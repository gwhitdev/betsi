namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Domain.Aggregates;
using Betsi.Domain.Clinical;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;

/// <summary>
/// Drives a patient through the API exactly as a caller would, against the real pipeline.
/// </summary>
[Collection(ApiCollection.Name)]
public class PatientJourneyTests
{
    private readonly BetsiApiFactory _factory;

    public PatientJourneyTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegisterPatientCommand ANewPatient() => new()
    {
        FirstName = "Gwen",
        LastName = "Jones",
        DateOfBirth = new DateTime(1962, 4, 19)
    };

    private async Task<CommandResult> RegisterAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/patients/register", ANewPatient(), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadCommandResultAsync(Ct);
    }

    private async Task<CommandResult> RegisterChildAsync(HttpClient client, int age = 8)
    {
        var response = await client.PostAsJsonAsync("/api/v1/patients/register", new RegisterPatientCommand
        {
            FirstName = "Morgan",
            LastName = "Child",
            DateOfBirth = DateTime.UtcNow.Date.AddYears(-age)
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await response.ReadCommandResultAsync(Ct);
    }

    [Fact]
    public async Task A_patient_can_be_registered()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);

        var result = await RegisterAsync(client);

        result.AggregateId.ShouldNotBe(Guid.Empty);
        result.Version.ShouldBe(1);
    }

    [Fact]
    public async Task An_observation_can_be_recorded_corrected_and_read_with_history()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);
        var observation = new RecordObservationCommand
        {
            Kind = "Routine",
            RespiratoryRate = 16,
            OxygenSaturation = 98,
            SystolicBloodPressure = 120,
            Pulse = 70,
            Consciousness = Betsi.Domain.Clinical.Consciousness.Alert,
            Temperature = 37.0m,
            Notes = "Initial set"
        };

        var recorded = await PostAsync(client,
            $"/api/v1/episodes/{registered.AggregateId}/observations", observation);

        var correction = new RecordObservationCommand
        {
            SupersedesObservationId = recorded.AggregateId,
            ExpectedVersion = recorded.Version,
            RespiratoryRate = 18,
            Notes = "Corrected respiratory rate"
        };
        var corrected = await PostAsync(client,
            $"/api/v1/episodes/{registered.AggregateId}/observations", correction);

        corrected.Version.ShouldBe(1);
        var response = await client.GetAsync(
            $"/api/v1/episodes/{registered.AggregateId}/observations", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var history = await response.Content.ReadFromJsonAsync<List<Betsi.Application.Queries.ObservationView>>(cancellationToken: Ct);
        history.ShouldNotBeNull();
        history!.Count.ShouldBe(2);
        history[0].SupersededByObservationId.ShouldBe(corrected.AggregateId);
        history[1].SupersedesObservationId.ShouldBe(recorded.AggregateId);
        history[0].RespiratoryRate.ShouldBe(16);
        history[0].Notes.ShouldBe("Initial set");
        history[1].RespiratoryRate.ShouldBe(18);
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Read:ObservationHistory" &&
            a.AffectedAggregateId == registered.AggregateId, Ct);
        audit.ActorRole.ShouldBe("Nurse");
        audit.Outcome.ShouldBe("Read");
    }

    [Fact]
    public async Task Structured_assessment_templates_round_trip_through_the_API_and_correction_history()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        var recorded = await PostAsync(client,
            $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand
            {
                Kind = "Concern",
                Source = ClinicalObservation.ObservationSource.MedicalReview,
                SbarSituation = "New shortness of breath",
                SbarBackground = "Awaiting imaging",
                SbarAssessment = "Clinician remains concerned",
                SbarRecommendation = "Repeat observations and senior review",
                BreathingFinding = ClinicalObservation.AssessmentFinding.Concern,
                BreathingDetails = "Increased work observed",
                CirculationFinding = ClinicalObservation.AssessmentFinding.NoConcern,
                MobilityFinding = ClinicalObservation.AssessmentFinding.UnableToAssess,
                MobilityDetails = "Resting in bed"
            });

        await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand
            {
                SupersedesObservationId = recorded.AggregateId,
                ExpectedVersion = recorded.Version,
                Source = ClinicalObservation.ObservationSource.MedicalReview,
                SbarSituation = "Breathing improved",
                BreathingFinding = ClinicalObservation.AssessmentFinding.NoConcern
            });

        var history = await client.GetFromJsonAsync<List<Betsi.Application.Queries.ObservationView>>(
            $"/api/v1/episodes/{episode.AggregateId}/observations", Ct);
        history.ShouldNotBeNull();
        history!.Count.ShouldBe(2);
        history[0].Source.ShouldBe("MedicalReview");
        history[0].SbarRecommendation.ShouldBe("Repeat observations and senior review");
        history[0].BreathingFinding.ShouldBe("Concern");
        history[0].CirculationFinding.ShouldBe("NoConcern");
        history[0].MobilityDetails.ShouldBe("Resting in bed");
        history[0].SupersededByObservationId.ShouldNotBeNull();
        history[1].SbarSituation.ShouldBe("Breathing improved");
        history[1].BreathingFinding.ShouldBe("NoConcern");
    }

    [Fact]
    public async Task Observation_history_is_tenant_isolated_and_role_protected()
    {
        var nurse = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(nurse);
        await PostAsync(nurse, $"/api/v1/episodes/{registered.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Synthetic observation" });

        var otherTenant = _factory.ClientFor(BetsiApiFactory.TenantB);
        var otherResponse = await otherTenant.GetAsync(
            $"/api/v1/episodes/{registered.AggregateId}/observations", Ct);
        otherResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var administrator = _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Site Administrator");
        var forbidden = await administrator.GetAsync(
            $"/api/v1/episodes/{registered.AggregateId}/observations", Ct);
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_correction_with_a_stale_observation_version_is_rejected()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);
        var recorded = await PostAsync(client,
            $"/api/v1/episodes/{registered.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Synthetic observation" });

        var response = await client.PostAsJsonAsync(
            $"/api/v1/episodes/{registered.AggregateId}/observations",
            new RecordObservationCommand
            {
                SupersedesObservationId = recorded.AggregateId,
                ExpectedVersion = recorded.Version + 1,
                Notes = "Stale correction"
            }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retried_observation_envelope_records_exactly_one_observation_and_event(bool correction)
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        CommandResult? corrected = correction
            ? await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
                new RecordObservationCommand { Notes = "Original to correct" })
            : null;
        var envelope = new
        {
            commandType = "RecordObservation", commandId = Guid.NewGuid(),
            idempotencyKey = Guid.NewGuid().ToString(),
            payload = new { patientEpisodeId = episode.AggregateId, notes = "Measured once",
                supersedesObservationId = corrected?.AggregateId, expectedVersion = corrected?.Version }
        };
        var first = await client.PostAsJsonAsync("/api/v1/commands", envelope, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var original = await first.Content.ReadFromJsonAsync<CommandEnvelopeResult>(cancellationToken: Ct);
        var retry = await client.PostAsJsonAsync("/api/v1/commands", envelope, Ct);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        var replay = await retry.Content.ReadFromJsonAsync<CommandEnvelopeResult>(cancellationToken: Ct);
        replay!.Replayed.ShouldBeTrue();
        replay.Result.ShouldBe(original!.Result);
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        (await db.ClinicalObservations.CountAsync(o => o.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(correction ? 2 : 1);
        (await db.DomainEvents.CountAsync(e => e.AggregateId == original.Result.AggregateId, Ct)).ShouldBe(1);
        (await db.OutboxMessages.CountAsync(e => e.AggregateId == original.Result.AggregateId, Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(e => e.AffectedAggregateId == original.Result.AggregateId && e.Outcome == "Success", Ct)).ShouldBe(1);
        if (corrected is not null)
        {
            var source = await db.ClinicalObservations.SingleAsync(o => o.Id == corrected.AggregateId, Ct);
            source.SupersededByObservationId.ShouldBe(original.Result.AggregateId);
            source.Version.ShouldBe(2);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pulse\":-1}")]
    [InlineData("{\"oxygenSaturation\":101}")]
    [InlineData("{\"temperature\":35.05}")]
    [InlineData("{\"kind\":\"typo\",\"notes\":\"Observation\"}")]
    [InlineData("{\"painScore\":3,\"painScale\":\"Faces\"}")]
    [InlineData("{\"consciousness\":99}")]
    [InlineData("{\"source\":99,\"notes\":\"Observation\"}")]
    [InlineData("{\"breathingFinding\":99}")]
    public async Task Invalid_observations_are_rejected_without_creating_records(string json)
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        var response = await client.PostAsync($"/api/v1/episodes/{episode.AggregateId}/observations",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        (await db.ClinicalObservations.AnyAsync(o => o.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Severe_pain_records_structured_details_and_raises_one_analgesic_review()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        var onset = DateTime.UtcNow.AddMinutes(-20);
        var recorded = await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand
            {
                PainScore = 7,
                PainScale = PainScale.NumericRating,
                PainLocation = "Right lower abdomen",
                PainCharacter = "Sharp",
                PainOnsetAt = onset
            });

        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var observation = await db.ClinicalObservations.SingleAsync(o => o.Id == recorded.AggregateId, Ct);
        observation.PainLocation.ShouldBe("Right lower abdomen");
        observation.PainCharacter.ShouldBe("Sharp");
        observation.PainOnsetAt.ShouldNotBeNull();
        Math.Abs((observation.PainOnsetAt.Value - onset).TotalSeconds).ShouldBeLessThan(1);
        var alert = await db.Escalations.SingleAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct);
        alert.Trigger.ShouldBe(Escalation.EscalationTrigger.SystemAlert);
        alert.ResponsibleRole.ShouldBe("Nurse in Charge");
        alert.Notes.ShouldNotBeNull();
        alert.Notes!.ShouldContain("Pain management review: score 7");

        await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand
            {
                SupersedesObservationId = recorded.AggregateId,
                ExpectedVersion = recorded.Version,
                PainScore = 8,
                PainScale = PainScale.NumericRating,
                PainLocation = "Right lower abdomen",
                PainCharacter = "Sharp",
                PainOnsetAt = onset,
                Notes = "Corrected score"
            });
        db.ChangeTracker.Clear();
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task An_untrained_paediatric_assignment_is_audited_and_raises_one_skill_gap_alert()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterChildAsync(client);
        var staffId = Guid.NewGuid();

        var assigned = await PostAsync(client,
            $"/api/v1/episodes/{episode.AggregateId}/staff-assignment",
            new AssignClinicalStaffCommand
            {
                ExpectedVersion = episode.Version,
                AssignedStaffActorId = staffId,
                AssignedStaffName = "Alex Nurse",
                AssignedStaffRole = "Nurse",
                PaediatricTrained = false
            });

        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var stored = await db.PatientEpisodes.SingleAsync(e => e.Id == episode.AggregateId, Ct);
        stored.AssignedStaffActorId.ShouldBe(staffId);
        stored.AssignedStaffName.ShouldBe("Alex Nurse");
        stored.AssignedStaffPaediatricTrained.ShouldBe(false);
        stored.PaediatricSkillGapAlertOpen.ShouldBeTrue();
        stored.Version.ShouldBe(assigned.Version);
        var alert = await db.Escalations.SingleAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct);
        alert.Trigger.ShouldBe(Escalation.EscalationTrigger.SystemAlert);
        alert.ResponsibleRole.ShouldBe("Nurse in Charge");
        alert.Notes!.ShouldContain("Paediatric competence required");
        (await db.DomainEvents.CountAsync(e => e.AggregateId == episode.AggregateId &&
            e.EventType == nameof(ClinicalStaffAssigned), Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(a => a.Action == nameof(AssignClinicalStaffCommand) &&
            a.Outcome == "Success" && a.AffectedAggregateId == episode.AggregateId, Ct)).ShouldBe(1);

        await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Paediatric review" });
        db.ChangeTracker.Clear();
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(1);

        await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/staff-assignment",
            new AssignClinicalStaffCommand
            {
                ExpectedVersion = assigned.Version,
                AssignedStaffActorId = staffId,
                AssignedStaffName = "Alex Nurse",
                AssignedStaffRole = "Nurse",
                PaediatricTrained = true
            });
        db.ChangeTracker.Clear();
        (await db.PatientEpisodes.SingleAsync(e => e.Id == episode.AggregateId, Ct))
            .PaediatricSkillGapAlertOpen.ShouldBeFalse();
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_paediatric_observation_without_trained_staff_raises_one_alert_but_an_adult_does_not()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var child = await RegisterChildAsync(client);
        var adult = await RegisterAsync(client);

        await PostAsync(client, $"/api/v1/episodes/{child.AggregateId}/observations",
            new RecordObservationCommand { Notes = "First child observation" });
        await PostAsync(client, $"/api/v1/episodes/{child.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Second child observation" });
        await PostAsync(client, $"/api/v1/episodes/{adult.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Adult observation" });

        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == child.AggregateId, Ct)).ShouldBe(1);
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == adult.AggregateId, Ct)).ShouldBe(0);
        (await db.PatientEpisodes.SingleAsync(e => e.Id == child.AggregateId, Ct))
            .PaediatricSkillGapAlertOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_paediatric_skill_gap_alert_rolls_back_the_staff_assignment()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterChildAsync(client);
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailStaffingAlert BEFORE INSERT ON DomainEvents
            WHEN NEW.AggregateType = 'Escalation'
            BEGIN SELECT RAISE(ABORT, 'Injected staffing alert failure'); END;
            """, Ct);
        try
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/episodes/{episode.AggregateId}/staff-assignment",
                new AssignClinicalStaffCommand
                {
                    ExpectedVersion = episode.Version,
                    AssignedStaffActorId = Guid.NewGuid(),
                    AssignedStaffName = "Untrained Nurse",
                    AssignedStaffRole = "Nurse"
                }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailStaffingAlert;", Ct);
        }

        db.ChangeTracker.Clear();
        var stored = await db.PatientEpisodes.SingleAsync(e => e.Id == episode.AggregateId, Ct);
        stored.AssignedStaffActorId.ShouldBeNull();
        stored.PaediatricSkillGapAlertOpen.ShouldBeFalse();
        stored.Version.ShouldBe(episode.Version);
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_failed_pain_alert_rolls_back_the_observation_and_its_events()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailPainAlert BEFORE INSERT ON DomainEvents
            WHEN NEW.AggregateType = 'Escalation'
            BEGIN SELECT RAISE(ABORT, 'Injected pain alert failure'); END;
            """, Ct);
        try
        {
            var response = await client.PostAsJsonAsync(
                $"/api/v1/episodes/{episode.AggregateId}/observations",
                new RecordObservationCommand
                {
                    PainScore = 6,
                    PainScale = PainScale.NumericRating,
                    PainLocation = "Chest",
                    PainCharacter = "Burning",
                    PainOnsetAt = DateTime.UtcNow.AddMinutes(-5)
                }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailPainAlert;", Ct);
        }

        db.ChangeTracker.Clear();
        (await db.ClinicalObservations.CountAsync(o => o.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(0);
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == episode.AggregateId, Ct)).ShouldBe(0);
        (await db.DomainEvents.CountAsync(e => e.AggregateId != episode.AggregateId &&
            e.EventData.Contains(episode.AggregateId.ToString()), Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Corrections_cannot_cross_episodes_or_tenants_and_writes_require_a_clinical_role()
    {
        var nurse = _factory.ClientFor(BetsiApiFactory.TenantA);
        var first = await RegisterAsync(nurse);
        var second = await RegisterAsync(nurse);
        var recorded = await PostAsync(nurse, $"/api/v1/episodes/{first.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Original" });
        var correction = new RecordObservationCommand
            { Notes = "Correction", SupersedesObservationId = recorded.AggregateId, ExpectedVersion = 1 };
        (await nurse.PostAsJsonAsync($"/api/v1/episodes/{second.AggregateId}/observations", correction, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var other = _factory.ClientFor(BetsiApiFactory.TenantB);
        var otherEpisode = await RegisterAsync(other);
        (await other.PostAsJsonAsync($"/api/v1/episodes/{otherEpisode.AggregateId}/observations", correction, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var admin = _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Site Administrator");
        (await admin.PostAsJsonAsync($"/api/v1/episodes/{first.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Forbidden" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_failed_linked_correction_does_not_leave_partial_observations_or_events()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);
        var recorded = await PostAsync(client,
            $"/api/v1/episodes/{registered.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Original" });

        await using var database = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var tenant = new TenantContext();
        tenant.Resolve(BetsiApiFactory.TenantA, Guid.NewGuid(), "Nurse");
        var unit = new UnitOfWork(database, tenant);
        var now = DateTime.UtcNow;
        var first = ClinicalObservation.Record(BetsiApiFactory.TenantA, registered.AggregateId,
            AgeBand.Adult, ClinicalObservation.ObservationKind.Correction, default,
            null, null, "First", now, tenant.ActorId, tenant.ActorRole, recorded.AggregateId);
        var second = ClinicalObservation.Record(BetsiApiFactory.TenantA, registered.AggregateId,
            AgeBand.Adult, ClinicalObservation.ObservationKind.Correction, default,
            null, null, "Second", now, tenant.ActorId, tenant.ActorRole, recorded.AggregateId);
        unit.Add(first);
        unit.Add(second);

        await Should.ThrowAsync<DbUpdateException>(() => unit.CommitAsync([first, second], Ct));

        await using var verification = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        (await verification.ClinicalObservations.CountAsync(o => o.PatientEpisodeId == registered.AggregateId, Ct)).ShouldBe(1);
        (await verification.DomainEvents.CountAsync(e => e.AggregateId == recorded.AggregateId, Ct)).ShouldBe(1);
        (await verification.OutboxMessages.CountAsync(e => e.AggregateId == recorded.AggregateId, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_database_failure_during_correction_preserves_the_original_and_audits_failure()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var episode = await RegisterAsync(client);
        var recorded = await PostAsync(client, $"/api/v1/episodes/{episode.AggregateId}/observations",
            new RecordObservationCommand { Notes = "Original" });
        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var auditCount = await db.AuditLogs.CountAsync(a => a.Action == "RecordObservationCommand" && a.Outcome == "Failure", Ct);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailCorrectionEvent BEFORE INSERT ON DomainEvents
            WHEN NEW.EventType = 'ObservationSuperseded'
            BEGIN SELECT RAISE(ABORT, 'Injected correction failure'); END;
            """, Ct);
        try
        {
            var response = await client.PostAsJsonAsync($"/api/v1/episodes/{episode.AggregateId}/observations",
                new RecordObservationCommand { Notes = "Replacement", SupersedesObservationId = recorded.AggregateId,
                    ExpectedVersion = recorded.Version }, Ct);
            response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailCorrectionEvent;", Ct);
        }
        var original = await db.ClinicalObservations.SingleAsync(o => o.PatientEpisodeId == episode.AggregateId, Ct);
        original.Id.ShouldBe(recorded.AggregateId);
        original.Notes.ShouldBe("Original");
        original.Version.ShouldBe(1);
        original.SupersededByObservationId.ShouldBeNull();
        (await db.DomainEvents.CountAsync(e => e.AggregateType == "ClinicalObservation" &&
            e.EventData.Contains(episode.AggregateId.ToString()), Ct)).ShouldBe(1);
        (await db.OutboxMessages.CountAsync(e => e.AggregateType == "ClinicalObservation" &&
            e.EventData.Contains(episode.AggregateId.ToString()), Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(a => a.Action == "RecordObservationCommand" && a.Outcome == "Failure", Ct))
            .ShouldBe(auditCount + 1);
    }

    [Fact]
    public async Task A_failed_unaccompanied_child_alert_leaves_neither_episode_change_nor_escalation()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var response = await client.PostAsJsonAsync("/api/v1/patients/register", new RegisterPatientCommand
        {
            FirstName = "Synthetic",
            LastName = "Child",
            DateOfBirth = DateTime.UtcNow.Date.AddYears(-10)
        }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var registered = await response.ReadCommandResultAsync(Ct);

        await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER FailClinicalEscalation BEFORE INSERT ON DomainEvents
            WHEN NEW.AggregateType = 'Escalation'
            BEGIN SELECT RAISE(ABORT, 'Injected linked escalation failure'); END;
            """, Ct);
        try
        {
            var failed = await client.PostAsJsonAsync(
                $"/api/v1/episodes/{registered.AggregateId}/carer-presence",
                new RecordCarerPresenceCommand { CarerPresent = false }, Ct);
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailClinicalEscalation;", Ct);
        }

        db.ChangeTracker.Clear();
        var episode = await db.PatientEpisodes.SingleAsync(e => e.Id == registered.AggregateId, Ct);
        episode.CarerPresent.ShouldBeNull();
        episode.SafeguardingConcernRaised.ShouldBeFalse();
        episode.Version.ShouldBe(registered.Version);
        (await db.Escalations.CountAsync(e => e.PatientEpisodeId == registered.AggregateId, Ct)).ShouldBe(0);
        (await db.DomainEvents.CountAsync(e => e.AggregateId == registered.AggregateId, Ct)).ShouldBe(1);
        (await db.OutboxMessages.CountAsync(e => e.AggregateId == registered.AggregateId, Ct)).ShouldBe(1);
        (await db.AuditLogs.CountAsync(a => a.Action == nameof(RecordCarerPresenceCommand) &&
            a.Outcome == "Failure" && a.AffectedAggregateId == Guid.Empty, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_patient_can_be_taken_from_arrival_to_discharge()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);
        var patientId = registered.AggregateId;

        var afterTriageBegan = await PostAsync(
            client, $"/api/v1/patients/{patientId}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = registered.Version });

        var afterTriageDone = await PostAsync(
            client, $"/api/v1/patients/{patientId}/triage/complete",
            new CompletePatientTriageCommand { ExpectedVersion = afterTriageBegan.Version });

        var administrator = _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Site Administrator");
        var location = await PostAsync(administrator, "/api/v1/locations", new CreateLocationCommand
        {
            Name = $"Treatment {Guid.NewGuid():N}",
            Capacity = 1
        });

        var afterTreatment = await PostAsync(
            client, $"/api/v1/patients/{patientId}/treatment/begin",
            new BeginPatientTreatmentCommand
            {
                LocationId = location.AggregateId,
                ExpectedVersion = afterTriageDone.Version
            });

        var afterDischarge = await PostAsync(
            client, $"/api/v1/patients/{patientId}/discharge",
            new DischargePatientCommand
            {
                ExpectedVersion = afterTreatment.Version,
                DischargeNotes = "Advised to rest"
            });

        // Each step returns the version to send with the next, and the version advances once
        // per event.
        afterDischarge.Version.ShouldBe(5);

        await using var database = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var episode = await database.PatientEpisodes.SingleAsync(e => e.Id == patientId, Ct);
        episode.State.ShouldBe(PatientEpisode.PatientState.Discharged);
        var treatmentLocation = await database.Locations.SingleAsync(l => l.Id == location.AggregateId, Ct);
        treatmentLocation.CurrentOccupancy.ShouldBe(0);
        treatmentLocation.Version.ShouldBe(3);
    }

    [Fact]
    public async Task The_journey_is_written_to_the_event_log_and_the_outbox()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);

        await PostAsync(
            client, $"/api/v1/patients/{registered.AggregateId}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = registered.Version });

        await using var database = _factory.DatabaseFor(BetsiApiFactory.TenantA);

        var events = await database.DomainEvents
            .Where(e => e.AggregateId == registered.AggregateId)
            .OrderBy(e => e.Version)
            .ToListAsync(Ct);

        events.Select(e => e.EventType)
            .ShouldBe([nameof(PatientEpisodeCreated), nameof(PatientTriageStarted)]);

        var queued = await database.OutboxMessages
            .CountAsync(m => m.AggregateId == registered.AggregateId, Ct);

        queued.ShouldBe(2);
    }

    [Fact]
    public async Task Every_command_is_audited_with_the_actor_who_sent_it()
    {
        var actorId = Guid.NewGuid();
        var client = _factory.ClientFor(BetsiApiFactory.TenantA, actorId, "Consultant");

        var registered = await RegisterAsync(client);

        await using var database = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var audit = await database.AuditLogs
            .Where(a => a.AffectedAggregateId == registered.AggregateId)
            .SingleAsync(Ct);

        audit.Action.ShouldBe(nameof(RegisterPatientCommand));
        audit.ActorId.ShouldBe(actorId);
        audit.ActorRole.ShouldBe("Consultant");
        audit.Outcome.ShouldBe("Success");
    }

    [Fact]
    public async Task A_rejected_command_is_audited_as_a_failure()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);
        var registered = await RegisterAsync(client);

        // Completing triage that never began. A refused clinical action is exactly what an
        // investigation needs to see, so it must reach the audit trail too.
        var response = await client.PostAsJsonAsync(
            $"/api/v1/patients/{registered.AggregateId}/triage/complete",
            new CompletePatientTriageCommand { ExpectedVersion = registered.Version },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await using var database = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        var failures = await database.AuditLogs
            .Where(a => a.Action == nameof(CompletePatientTriageCommand) && a.Outcome == "Failure")
            .ToListAsync(Ct);

        failures.ShouldNotBeEmpty();
    }

    private async Task<CommandResult> PostAsync<T>(HttpClient client, string url, T body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        response.EnsureSuccessStatusCode();
        return await response.ReadCommandResultAsync(Ct);
    }
}
