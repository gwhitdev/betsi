namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// The failure contract (MVP-065). Callers act on status codes, so each kind of failure must
/// map to a distinct, correct one — and a failure must never arrive as a 200.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProblemDetailsTests
{
    private readonly BetsiApiFactory _factory;

    public ProblemDetailsTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _factory.ClientFor(BetsiApiFactory.TenantA);

    private HttpClient Administrator => _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Site Administrator");

    private async Task<CommandResult> ARegisteredPatientAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/patients/register", new RegisterPatientCommand
        {
            FirstName = "Problem",
            LastName = "Details",
            DateOfBirth = new DateTime(1975, 3, 3)
        }, Ct);

        return await response.ReadCommandResultAsync(Ct);
    }

    [Fact]
    public async Task An_invalid_command_is_rejected_with_422_and_field_level_errors()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/patients/register",
            new RegisterPatientCommand { FirstName = "", LastName = "", DateOfBirth = default }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("title").GetString().ShouldBe("The request failed validation");
        problem.TryGetProperty("errors", out var errors).ShouldBeTrue();
        errors.EnumerateObject().Select(p => p.Name)
            .ShouldContain(nameof(RegisterPatientCommand.FirstName));
    }

    [Fact]
    public async Task A_problem_response_uses_the_problem_json_content_type()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/patients/register",
            new RegisterPatientCommand { FirstName = "", LastName = "", DateOfBirth = default }, Ct);

        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task A_problem_response_carries_a_type_and_a_trace_id()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/patients/register",
            new RegisterPatientCommand { FirstName = "", LastName = "", DateOfBirth = default }, Ct);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        problem.GetProperty("type").GetString().ShouldStartWith("https://betsi.nhs.uk/problems/");
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unknown_patient_gives_404()
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/patients/{Guid.NewGuid()}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_stale_expected_version_gives_409_with_the_current_version()
    {
        var registered = await ARegisteredPatientAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/patients/{registered.AggregateId}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = 99 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        // The caller needs the real version to retry without another round trip.
        problem.GetProperty("actualVersion").GetInt32().ShouldBe(registered.Version);
        problem.GetProperty("expectedVersion").GetInt32().ShouldBe(99);
    }

    [Fact]
    public async Task An_illegal_state_transition_gives_422_not_500()
    {
        var registered = await ARegisteredPatientAsync();

        // Treating a patient who has not been triaged is a clinical-workflow error, not a
        // server fault.
        var response = await Client.PostAsJsonAsync(
            $"/api/v1/patients/{registered.AggregateId}/treatment/begin",
            new BeginPatientTreatmentCommand
            {
                LocationId = Guid.NewGuid(),
                ExpectedVersion = registered.Version
            }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("title").GetString().ShouldBe("Operation not valid in the current state");
    }

    [Fact]
    public async Task An_escalation_for_an_unknown_patient_gives_404()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/escalations/waiting-time",
            new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = Guid.NewGuid(),
                LocationId = Guid.NewGuid(),
                ResponsibleRole = "Senior Clinician"
            }, Ct);

        // An escalation naming a patient who does not exist would sit on the dashboard with
        // nobody able to act on it.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_escalation_with_no_responsible_role_is_rejected()
    {
        var registered = await ARegisteredPatientAsync();

        var response = await Client.PostAsJsonAsync("/api/v1/escalations/waiting-time",
            new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = registered.AggregateId,
                LocationId = Guid.NewGuid(),
                ResponsibleRole = ""
            }, Ct);

        // An escalation nobody owns is the failure mode the ED report describes.
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_escalation_can_be_raised_acknowledged_and_resolved()
    {
        var registered = await ARegisteredPatientAsync();

        var raised = await Client.PostAsJsonAsync("/api/v1/escalations/waiting-time",
            new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = registered.AggregateId,
                LocationId = Guid.NewGuid(),
                ResponsibleRole = "Senior Clinician"
            }, Ct);

        raised.StatusCode.ShouldBe(HttpStatusCode.Created);
        var escalation = await raised.ReadCommandResultAsync(Ct);

        var acknowledged = await Client.PostAsJsonAsync(
            $"/api/v1/escalations/{escalation.AggregateId}/acknowledge",
            new AcknowledgeEscalationCommand { Notes = "On my way" }, Ct);
        acknowledged.StatusCode.ShouldBe(HttpStatusCode.OK);

        var resolved = await Client.PostAsJsonAsync(
            $"/api/v1/escalations/{escalation.AggregateId}/resolve",
            new ResolveEscalationCommand { Notes = "Patient moved to majors" }, Ct);
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_escalation_cannot_be_resolved_before_it_is_acknowledged()
    {
        var registered = await ARegisteredPatientAsync();

        var raised = await Client.PostAsJsonAsync("/api/v1/escalations/waiting-time",
            new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = registered.AggregateId,
                LocationId = Guid.NewGuid(),
                ResponsibleRole = "Senior Clinician"
            }, Ct);

        var escalation = await raised.ReadCommandResultAsync(Ct);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/escalations/{escalation.AggregateId}/resolve",
            new ResolveEscalationCommand(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_location_with_no_capacity_is_rejected_before_it_reaches_the_domain()
    {
        var response = await Administrator.PostAsJsonAsync("/api/v1/locations",
            new CreateLocationCommand { Name = "Nowhere", Capacity = 0 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_location_and_queue_can_be_created_and_a_patient_queued()
    {
        var registered = await ARegisteredPatientAsync();

        var location = await (await Administrator.PostAsJsonAsync("/api/v1/locations",
            new CreateLocationCommand { Name = $"Bay {Guid.NewGuid()}", Capacity = 4 }, Ct))
            .ReadCommandResultAsync(Ct);

        var queue = await (await Administrator.PostAsJsonAsync("/api/v1/queues",
            new CreateQueueCommand { LocationId = location.AggregateId, Name = $"Queue {Guid.NewGuid()}" }, Ct))
            .ReadCommandResultAsync(Ct);

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/queues/{queue.AggregateId}/patients",
            new EnqueuePatientCommand
            {
                PatientEpisodeId = registered.AggregateId,
                ExpectedVersion = queue.Version
            }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
