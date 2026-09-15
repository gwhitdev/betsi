namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Domain.Aggregates;
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

    [Fact]
    public async Task A_patient_can_be_registered()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA);

        var result = await RegisterAsync(client);

        result.AggregateId.ShouldNotBe(Guid.Empty);
        result.Version.ShouldBe(1);
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

        var afterTreatment = await PostAsync(
            client, $"/api/v1/patients/{patientId}/treatment/begin",
            new BeginPatientTreatmentCommand
            {
                LocationId = Guid.NewGuid(),
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
