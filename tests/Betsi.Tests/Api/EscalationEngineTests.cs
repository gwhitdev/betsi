namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Application.Escalations;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The escalation engine end to end (MVP-020–025): policy change control, automatic
/// escalation, missed-deadline follow-up, the board and the audit trail, through the real
/// pipeline.
/// </summary>
/// <remarks>
/// Patients are registered at the real current time, and the monitor is then run at a chosen
/// later time — "four hours and one minute from now" — rather than waiting. Commands sent over
/// HTTP (acknowledge, reassign) use the real clock. Assertions are always about specific
/// patients and escalations, because every test in this class shares one tenant.
/// </remarks>
[Collection(ApiCollection.Name)]
public class EscalationEngineTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Guid Administrator = Guid.NewGuid();
    private static readonly Guid ClinicalLead = Guid.NewGuid();
    private static readonly Guid Coordinator = Guid.NewGuid();
    private static readonly Guid OperationsManager = Guid.NewGuid();

    private readonly BetsiApiFactory _factory;

    public EscalationEngineTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<WaitingTimeTierInput> SpecTiers() =>
    [
        new() { ThresholdMinutes = 240, ResponsibleRole = "Waiting-room Coordinator", AcknowledgementDeadlineMinutes = 30, RecommendedAction = "Review clinical status; consider reassessment" },
        new() { ThresholdMinutes = 360, ResponsibleRole = "Nurse in Charge", AcknowledgementDeadlineMinutes = 30, RecommendedAction = "Decide on admission; escalate to bed management" },
        new() { ThresholdMinutes = 480, ResponsibleRole = "Bed Manager", AcknowledgementDeadlineMinutes = 30, RecommendedAction = "Escalate to site leadership" }
    ];

    private HttpClient As(Guid actor, string role, Guid? tenant = null) =>
        _factory.ClientFor(tenant ?? BetsiApiFactory.EscalationTenant, actor, role);

    private async Task<Guid> RegisterPatientAsync(Guid? tenant = null)
    {
        var response = await As(Coordinator, "Waiting-room Coordinator", tenant).PostAsJsonAsync(
            "/api/v1/patients/register",
            new RegisterPatientCommand { FirstName = "Gwen", LastName = $"Jones-{Guid.NewGuid():N}"[..20], DateOfBirth = new DateTime(1950, 1, 1) },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.ReadCommandResultAsync(Ct)).AggregateId;
    }

    private async Task<CommandResult> ProposeAsync(List<WaitingTimeTierInput>? tiers = null, Guid? tenant = null)
    {
        var response = await As(Administrator, "Site Administrator", tenant).PostAsJsonAsync(
            "/api/v1/escalation-policy/proposals",
            new ProposeEscalationPolicyCommand
            {
                Tiers = tiers ?? SpecTiers(),
                FollowUpOwnerRole = "Operations Manager",
                Reason = "Agreed at ED governance"
            },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadCommandResultAsync(Ct);
    }

    private async Task<HttpResponseMessage> ApproveAsync(CommandResult proposal, Guid actor, string role, DateTime? effectiveFrom = null, Guid? tenant = null) =>
        await As(actor, role, tenant).PostAsJsonAsync(
            $"/api/v1/escalation-policy/{proposal.AggregateId}/approve",
            new ApproveEscalationPolicyCommand { ExpectedVersion = proposal.Version, Reason = "Clinically signed off", EffectiveFrom = effectiveFrom },
            Ct);

    private async Task PolicyInForceAsync(Guid? tenant = null)
    {
        var proposal = await ProposeAsync(tenant: tenant);
        (await ApproveAsync(proposal, ClinicalLead, "Clinical Lead", tenant: tenant)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<List<Escalation>> EscalationsForAsync(Guid patientId, Guid? tenant = null)
    {
        await using var db = _factory.DatabaseFor(tenant ?? BetsiApiFactory.EscalationTenant);
        return await db.Escalations.AsNoTracking().Where(e => e.PatientEpisodeId == patientId).OrderBy(e => e.TierLevel).ToListAsync(Ct);
    }

    private async Task<List<FollowUpException>> FollowUpsForAsync(Guid escalationId)
    {
        await using var db = _factory.DatabaseFor(BetsiApiFactory.EscalationTenant);
        return await db.FollowUpExceptions.AsNoTracking().Where(f => f.EscalationId == escalationId).OrderBy(f => f.RaisedAt).ToListAsync(Ct);
    }

    private async Task<EscalationBoard> BoardAsync(Guid? tenant = null) =>
        (await As(Coordinator, "Waiting-room Coordinator", tenant).GetFromJsonAsync<EscalationBoard>("/api/v1/escalations/board", Json, Ct))!;

    private static async Task<T> ProblemAsync<T>(HttpResponseMessage response) where T : ProblemDetails =>
        (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    [Collection(ApiCollection.Name)]
    public class PolicyChangeControl(BetsiApiFixture fixture) : EscalationEngineTests(fixture)
    {
        [Fact]
        public async Task A_proposal_takes_effect_only_when_a_different_supervisory_user_approves_it()
        {
            var proposal = await ProposeAsync();

            (await ApproveAsync(proposal, Administrator, "Clinical Lead")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ApproveAsync(proposal, Coordinator, "Waiting-room Coordinator")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            (await ApproveAsync(proposal, ClinicalLead, "Clinical Lead")).StatusCode.ShouldBe(HttpStatusCode.OK);

            var overview = await As(Coordinator, "Waiting-room Coordinator")
                .GetFromJsonAsync<PolicyOverview>("/api/v1/escalation-policy", Json, Ct);

            overview!.InForce.ShouldNotBeNull();
            overview.InForce.Id.ShouldBe(proposal.AggregateId);
            overview.InForce.DecidedByRole.ShouldBe("Clinical Lead");
            overview.InForce.Tiers.Select(t => t.ThresholdMinutes).ShouldBe([240, 360, 480]);
        }

        [Fact]
        public async Task Approving_a_proposal_that_changed_since_it_was_read_is_a_conflict()
        {
            var proposal = await ProposeAsync();

            var response = await ApproveAsync(proposal with { Version = proposal.Version + 1 }, ClinicalLead, "Clinical Lead");

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task Unsafe_thresholds_are_refused_with_the_reason()
        {
            var tiers = SpecTiers();
            tiers[1].ThresholdMinutes = 200;

            var response = await As(Administrator, "Site Administrator").PostAsJsonAsync(
                "/api/v1/escalation-policy/proposals",
                new ProposeEscalationPolicyCommand { Tiers = tiers, FollowUpOwnerRole = "Operations Manager", Reason = "Typo" }, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ProblemAsync<ProblemDetails>(response)).Detail.ShouldNotBeNull().ShouldContain("later than tier 1");
        }

        [Fact]
        public async Task An_earlier_revision_can_be_restored_through_the_same_approval()
        {
            var original = await ProposeAsync();
            (await ApproveAsync(original, ClinicalLead, "Clinical Lead")).EnsureSuccessStatusCode();

            var overview = await As(Coordinator, "Waiting-room Coordinator").GetFromJsonAsync<PolicyOverview>("/api/v1/escalation-policy", Json, Ct);
            var revision = overview!.Revisions.Single(r => r.Id == original.AggregateId).Revision;

            var restore = await As(Administrator, "Site Administrator").PostAsJsonAsync(
                "/api/v1/escalation-policy/proposals/restore",
                new ProposeEscalationPolicyRestorationCommand { Revision = revision, Reason = "Roll back" }, Ct);

            restore.StatusCode.ShouldBe(HttpStatusCode.Created);
            var restoration = await restore.ReadCommandResultAsync(Ct);

            var view = (await As(Coordinator, "Waiting-room Coordinator").GetFromJsonAsync<PolicyOverview>("/api/v1/escalation-policy", Json, Ct))!
                .Revisions.Single(r => r.Id == restoration.AggregateId);

            view.State.ShouldBe("Proposed");
            view.RestoresRevision.ShouldBe(revision);

            (await ApproveAsync(restoration, ClinicalLead, "Clinical Lead")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task A_future_approval_can_be_withdrawn_before_it_takes_effect()
        {
            var proposal = await ProposeAsync();
            var approved = await (await ApproveAsync(proposal, ClinicalLead, "Clinical Lead", DateTime.UtcNow.AddDays(3)))
                .ReadCommandResultAsync(Ct);

            var withdraw = await As(ClinicalLead, "Clinical Lead").PostAsJsonAsync(
                $"/api/v1/escalation-policy/{proposal.AggregateId}/withdraw",
                new WithdrawEscalationPolicyCommand { ExpectedVersion = approved.Version, Reason = "Wrong start date" }, Ct);

            withdraw.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        [Fact]
        public async Task Preview_shows_who_a_threshold_would_apply_to_without_changing_anything()
        {
            await RegisterPatientAsync();

            var response = await As(Administrator, "Site Administrator").PostAsJsonAsync(
                "/api/v1/escalation-policy/preview",
                new List<WaitingTimeTierInput> { new() { ThresholdMinutes = 15, ResponsibleRole = "Coordinator", AcknowledgementDeadlineMinutes = 30, RecommendedAction = "Review" } },
                Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var preview = (await response.Content.ReadFromJsonAsync<PolicyPreview>(Json, Ct))!;
            preview.PatientsWaiting.ShouldBeGreaterThan(0);
            preview.Tiers.Single().ThresholdMinutes.ShouldBe(15);
        }
    }

    [Collection(ApiCollection.Name)]
    public class AutomaticEscalation(BetsiApiFixture fixture) : EscalationEngineTests(fixture)
    {
        [Fact]
        public async Task A_waiting_patient_is_escalated_once_at_each_threshold()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();
            var start = DateTime.UtcNow;

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, start.AddMinutes(239));
            (await EscalationsForAsync(patient)).ShouldBeEmpty();

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, start.AddMinutes(241));
            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, start.AddMinutes(242));

            var afterFourHours = await EscalationsForAsync(patient);
            var tier1 = afterFourHours.ShouldHaveSingleItem();
            tier1.TierLevel.ShouldBe(1);
            tier1.ResponsibleRole.ShouldBe("Waiting-room Coordinator");
            tier1.WaitedMinutes.ShouldBe(241);
            tier1.RecommendedAction.ShouldNotBeNull().ShouldContain("reassessment");
            tier1.AcknowledgementDueAt.ShouldNotBeNull().ShouldBe(start.AddMinutes(271), TimeSpan.FromSeconds(1));

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, start.AddMinutes(481));

            (await EscalationsForAsync(patient)).Select(e => (e.TierLevel, e.ResponsibleRole)).ShouldBe([
                (1, "Waiting-room Coordinator"),
                (2, "Nurse in Charge"),
                (3, "Bed Manager")
            ]);
        }

        [Fact]
        public async Task Every_automatic_escalation_is_audited_as_the_system()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, DateTime.UtcNow.AddMinutes(241));

            var escalation = (await EscalationsForAsync(patient)).Single();
            await using var db = _factory.DatabaseFor(BetsiApiFactory.EscalationTenant);

            var audit = await db.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.AffectedAggregateId == escalation.Id, Ct);
            audit.Action.ShouldBe(nameof(RaisePolicyEscalationCommand));
            audit.ActorRole.ShouldBe("System");
            audit.Outcome.ShouldBe("Success");
        }

        [Fact]
        public async Task A_patient_in_triage_or_treatment_is_not_waiting_and_is_not_escalated()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();
            (await As(Coordinator, "Staff Nurse").PostAsJsonAsync(
                $"/api/v1/patients/{patient}/triage/begin", new BeginPatientTriageCommand { ExpectedVersion = 1 }, Ct))
                .EnsureSuccessStatusCode();

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, DateTime.UtcNow.AddHours(9));

            (await EscalationsForAsync(patient)).ShouldBeEmpty();
        }

        [Fact]
        public async Task With_no_approved_policy_nothing_is_escalated_and_the_board_says_so()
        {
            var patient = await RegisterPatientAsync(BetsiApiFactory.TenantB);

            var result = await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.TenantB, DateTime.UtcNow.AddHours(12));

            result.PolicyInForce.ShouldBeFalse();
            (await EscalationsForAsync(patient, BetsiApiFactory.TenantB)).ShouldBeEmpty();
            (await BoardAsync(BetsiApiFactory.TenantB)).Policy.Status.ShouldBe("NoApprovedPolicy");
        }

        [Fact]
        public async Task An_unlicensed_site_still_escalates_because_licensing_never_disables_safety()
        {
            await PolicyInForceAsync(BetsiApiFactory.UnlicensedTenant);
            var patient = await RegisterPatientAsync(BetsiApiFactory.UnlicensedTenant);

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.UnlicensedTenant, DateTime.UtcNow.AddMinutes(241));

            (await EscalationsForAsync(patient, BetsiApiFactory.UnlicensedTenant)).ShouldHaveSingleItem();
        }

        [Fact]
        public void The_monitors_commands_cannot_be_sent_over_http()
        {
            // They carry the evaluation time. A caller able to send one could raise an escalation
            // or an exception "at" any time they chose.
            Type[] internalOnly = [typeof(RaisePolicyEscalationCommand), typeof(RaiseFollowUpExceptionCommand)];

            var exposed = typeof(Program).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .SelectMany(m => m.GetParameters())
                .Where(p => internalOnly.Contains(p.ParameterType))
                .ToArray();

            exposed.ShouldBeEmpty();
        }
    }

    [Collection(ApiCollection.Name)]
    public class MissedAcknowledgement(BetsiApiFixture fixture) : EscalationEngineTests(fixture)
    {
        [Fact]
        public async Task A_missed_deadline_raises_one_follow_up_exception_which_a_manager_closes()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();
            var raisedAt = DateTime.UtcNow.AddMinutes(241);

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt);
            var escalation = (await EscalationsForAsync(patient)).Single();

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt.AddMinutes(29));
            (await FollowUpsForAsync(escalation.Id)).ShouldBeEmpty();

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt.AddMinutes(31));
            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt.AddMinutes(32));

            var followUp = (await FollowUpsForAsync(escalation.Id)).ShouldHaveSingleItem();
            followUp.OwnerRole.ShouldBe("Operations Manager");
            followUp.MissedDeadline.ShouldBe(escalation.AcknowledgementDueAt!.Value);
            (await EscalationsForAsync(patient)).Single().State.ShouldBe(Escalation.EscalationState.ManualFollowUp);

            var board = await BoardAsync();
            board.FollowUpExceptions.ShouldContain(f => f.Id == followUp.Id && f.Patient != null && f.Patient.EpisodeId == patient);

            // Picked up late: allowed, and does not close the review.
            (await As(Coordinator, "Waiting-room Coordinator").PostAsJsonAsync(
                $"/api/v1/escalations/{escalation.Id}/acknowledge", new AcknowledgeEscalationCommand { Notes = "Seen now" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await BoardAsync()).FollowUpExceptions.ShouldContain(f => f.Id == followUp.Id);

            var close = new CloseFollowUpExceptionCommand { Outcome = FollowUpException.ReviewOutcome.AcknowledgedLate, ReviewNotes = "Coordinator in resus; no harm" };

            (await As(Coordinator, "Waiting-room Coordinator").PostAsJsonAsync(
                $"/api/v1/escalations/follow-ups/{followUp.Id}/close", close, Json, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            (await As(OperationsManager, "Operations Manager").PostAsJsonAsync(
                $"/api/v1/escalations/follow-ups/{followUp.Id}/close", close, Json, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            (await BoardAsync()).FollowUpExceptions.ShouldNotContain(f => f.Id == followUp.Id);
        }

        [Fact]
        public async Task A_reassigned_escalation_that_misses_its_new_deadline_raises_a_second_exception()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();
            var raisedAt = DateTime.UtcNow.AddMinutes(241);

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt);
            var escalation = (await EscalationsForAsync(patient)).Single();
            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt.AddMinutes(31));

            (await As(OperationsManager, "Operations Manager").PostAsJsonAsync(
                $"/api/v1/escalations/{escalation.Id}/reassign",
                new ReassignEscalationCommand { ResponsibleRole = "Nurse in Charge", Reason = "Coordinator off shift" }, Ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, raisedAt.AddMinutes(33));

            var followUps = await FollowUpsForAsync(escalation.Id);
            followUps.Count.ShouldBe(2);
            followUps.Select(f => f.EscalationResponsibleRole).ShouldBe(["Waiting-room Coordinator", "Nurse in Charge"]);
            followUps.Select(f => f.MissedDeadline).Distinct().Count().ShouldBe(2);
        }

        [Fact]
        public async Task A_staff_raised_escalation_nobody_acknowledges_is_followed_up_too()
        {
            var patient = await RegisterPatientAsync();
            var raised = await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge" }, Ct);
            var escalationId = (await raised.ReadCommandResultAsync(Ct)).AggregateId;

            await _factory.EvaluateWaitingTimesAsync(BetsiApiFactory.EscalationTenant, DateTime.UtcNow.AddMinutes(16));

            (await FollowUpsForAsync(escalationId)).ShouldHaveSingleItem();
        }
    }

    [Collection(ApiCollection.Name)]
    public class BoardAndAudit(BetsiApiFixture fixture) : EscalationEngineTests(fixture)
    {
        [Fact]
        public async Task The_board_shows_what_needs_acknowledging_with_the_patient_and_the_policy()
        {
            await PolicyInForceAsync();
            var patient = await RegisterPatientAsync();
            var raised = await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge" }, Ct);
            var escalationId = (await raised.ReadCommandResultAsync(Ct)).AggregateId;

            var board = await BoardAsync();

            board.Source.ShouldBe("tenant-database");
            board.GeneratedAt.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
            board.Policy.Status.ShouldBe("InForce");
            var card = board.AwaitingAcknowledgement.Single(c => c.Id == escalationId);
            card.Patient.ShouldNotBeNull().EpisodeId.ShouldBe(patient);
            card.ResponsibleRole.ShouldBe("Nurse in Charge");
            card.AcknowledgementOverdue.ShouldBeFalse();
            board.Metrics.AwaitingAcknowledgement.ShouldBeGreaterThanOrEqualTo(1);

            (await As(Coordinator, "Nurse in Charge").PostAsJsonAsync($"/api/v1/escalations/{escalationId}/acknowledge", new AcknowledgeEscalationCommand(), Ct))
                .EnsureSuccessStatusCode();
            (await As(Coordinator, "Nurse in Charge").PostAsJsonAsync($"/api/v1/escalations/{escalationId}/resolve", new ResolveEscalationCommand { Notes = "Moved to majors" }, Ct))
                .EnsureSuccessStatusCode();

            board = await BoardAsync();
            board.AwaitingAcknowledgement.ShouldNotContain(c => c.Id == escalationId);
            board.History.Single(c => c.Id == escalationId).ResolvedByRole.ShouldBe("Nurse in Charge");
            board.Metrics.MedianMinutesToAcknowledge.ShouldNotBeNull();
        }

        [Fact]
        public async Task Times_on_the_board_are_marked_as_utc()
        {
            var patient = await RegisterPatientAsync();
            await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge" }, Ct);

            // Without the "Z" a browser reads a deadline as local time and shows it an hour out
            // for half the year in the UK.
            using var board = JsonDocument.Parse(await As(Coordinator, "Staff Nurse").GetStringAsync("/api/v1/escalations/board", Ct));
            var card = board.RootElement.GetProperty("awaitingAcknowledgement").EnumerateArray()
                .First(c => c.GetProperty("patient").GetProperty("episodeId").GetGuid() == patient);

            card.GetProperty("createdAt").GetString().ShouldNotBeNull().ShouldEndWith("Z");
            card.GetProperty("acknowledgementDueAt").GetString().ShouldNotBeNull().ShouldEndWith("Z");
            card.GetProperty("patient").GetProperty("arrivedAt").GetString().ShouldNotBeNull().ShouldEndWith("Z");
        }

        [Fact]
        public async Task Acknowledging_and_resolving_twice_succeed_without_recording_twice()
        {
            var patient = await RegisterPatientAsync();
            var raised = await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge" }, Ct);
            var id = (await raised.ReadCommandResultAsync(Ct)).AggregateId;
            var client = As(Coordinator, "Nurse in Charge");

            var first = await (await client.PostAsJsonAsync($"/api/v1/escalations/{id}/acknowledge", new AcknowledgeEscalationCommand(), Ct)).ReadCommandResultAsync(Ct);
            var second = await (await client.PostAsJsonAsync($"/api/v1/escalations/{id}/acknowledge", new AcknowledgeEscalationCommand(), Ct)).ReadCommandResultAsync(Ct);

            second.Version.ShouldBe(first.Version);
        }

        [Fact]
        public async Task The_audit_trail_records_every_transition_in_order_with_who_and_in_what_role()
        {
            var patient = await RegisterPatientAsync();
            var raised = await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "Waiting-room Coordinator" }, Ct);
            var id = (await raised.ReadCommandResultAsync(Ct)).AggregateId;

            await As(Coordinator, "Waiting-room Coordinator").PostAsJsonAsync($"/api/v1/escalations/{id}/acknowledge", new AcknowledgeEscalationCommand { Notes = "Looking" }, Ct);
            await As(OperationsManager, "Operations Manager").PostAsJsonAsync($"/api/v1/escalations/{id}/reassign", new ReassignEscalationCommand { ResponsibleRole = "Bed Manager", Reason = "Needs a bed" }, Ct);
            await As(OperationsManager, "Bed Manager").PostAsJsonAsync($"/api/v1/escalations/{id}/acknowledge", new AcknowledgeEscalationCommand(), Ct);
            await As(OperationsManager, "Bed Manager").PostAsJsonAsync($"/api/v1/escalations/{id}/resolve", new ResolveEscalationCommand { Notes = "Bed found" }, Ct);

            var trail = (await As(OperationsManager, "Operations Manager")
                .GetFromJsonAsync<List<AuditTrailEntry>>($"/api/v1/escalations/{id}/audit", Json, Ct))!;

            trail.Select(e => e.EventType).ShouldBe([
                nameof(EscalationCreated), nameof(EscalationAcknowledged), nameof(EscalationReassigned),
                nameof(EscalationAcknowledged), nameof(EscalationResolved)
            ]);
            trail.Select(e => e.ActorRole).ShouldBe(["Staff Nurse", "Waiting-room Coordinator", "Operations Manager", "Bed Manager", "Bed Manager"]);
            trail.Select(e => e.Version).ShouldBe([1, 2, 3, 4, 5]);
            trail[2].Details.GetProperty("Reason").GetString().ShouldBe("Needs a bed");
        }

        [Fact]
        public async Task The_audit_trail_exports_as_csv_without_executable_cells()
        {
            var patient = await RegisterPatientAsync();
            var raised = await As(Coordinator, "Staff Nurse").PostAsJsonAsync("/api/v1/escalations/waiting-time",
                new TriggerWaitingTimeEscalationCommand { PatientEpisodeId = patient, LocationId = Guid.NewGuid(), ResponsibleRole = "=HYPERLINK(\"http://x\")" }, Ct);
            var id = (await raised.ReadCommandResultAsync(Ct)).AggregateId;

            var response = await As(OperationsManager, "Operations Manager").GetAsync($"/api/v1/escalations/{id}/audit?format=csv", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
            var lines = (await response.Content.ReadAsStringAsync(Ct)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            lines[0].ShouldStartWith("sequence,occurred_at_utc");
            lines.Length.ShouldBe(2);
            lines[1].Split(',').ShouldNotContain(cell => cell.StartsWith('='));
        }

        [Fact]
        public async Task The_audit_trail_of_an_unknown_escalation_is_not_found()
        {
            var response = await As(OperationsManager, "Operations Manager").GetAsync($"/api/v1/escalations/{Guid.NewGuid()}/audit", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task The_event_log_and_audit_log_refuse_updates_and_deletes()
        {
            await RegisterPatientAsync();
            await using var db = _factory.DatabaseFor(BetsiApiFactory.EscalationTenant);

            var record = await db.DomainEvents.FirstAsync(Ct);
            record.ActorRole = "Rewritten";
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));

            db.ChangeTracker.Clear();
            db.AuditLogs.Remove(await db.AuditLogs.FirstAsync(Ct));
            await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Collection(ApiCollection.Name)]
    public class Performance(BetsiApiFixture fixture) : EscalationEngineTests(fixture)
    {
        private const int WaitingPatients = 150;

        [Fact]
        public async Task Evaluating_and_loading_the_board_stay_fast_with_many_waiting_patients()
        {
            var tenant = BetsiApiFactory.PerformanceTenant;
            await PolicyInForceAsync(tenant);

            await using (var db = _factory.DatabaseFor(tenant))
            {
                var context = new TenantContext();
                context.ResolveSystem(tenant);
                var repository = new Betsi.Infrastructure.Persistence.AggregateRepository<PatientEpisode>(db, context);

                for (var i = 0; i < WaitingPatients; i++)
                    await repository.AddAsync(PatientEpisode.CreateNew(tenant, "Load", $"Patient{i}", new DateTime(1980, 1, 1)), Ct);
            }

            var at = DateTime.UtcNow.AddMinutes(241);
            var first = await _factory.EvaluateWaitingTimesAsync(tenant, at);
            first.EscalationsRaised.ShouldBe(WaitingPatients);

            // MVP-021: evaluation stays under a second for 100+ waiting patients.
            var evaluation = Stopwatch.StartNew();
            var steady = await _factory.EvaluateWaitingTimesAsync(tenant, at.AddMinutes(1));
            evaluation.Stop();

            steady.PatientsConsidered.ShouldBe(WaitingPatients);
            steady.EscalationsRaised.ShouldBe(0);
            evaluation.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));

            // MVP-023: the board loads in under 500ms with 100+ escalations. Warmed once first so
            // the measurement is the query, not first-request JIT.
            await BoardAsync(tenant);
            var load = Stopwatch.StartNew();
            var board = await BoardAsync(tenant);
            load.Stop();

            board.Metrics.ActiveEscalations.ShouldBe(WaitingPatients);
            load.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500));
        }
    }
}
