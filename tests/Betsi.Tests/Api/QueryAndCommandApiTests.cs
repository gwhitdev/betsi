namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Application.Queries;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Episode and waiting-board queries (MVP-062) and the command envelope (MVP-061).</summary>
[Collection(ApiCollection.Name)]
public class QueryAndCommandApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BetsiApiFactory _factory;

    public QueryAndCommandApiTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // A tenant these tests have to themselves, so board contents are predictable.
    private static readonly Guid Tenant = BetsiApiFactory.QueryTenant;

    private HttpClient Nurse(Guid? actor = null) => _factory.ClientFor(Tenant, actor ?? Guid.NewGuid(), "Nurse");

    private async Task<CommandResult> RegisterAsync(string lastName, DateTime dateOfBirth, HttpClient? client = null) =>
        await (await (client ?? Nurse()).PostAsJsonAsync("/api/v1/patients/register",
            new RegisterPatientCommand { FirstName = "Query", LastName = lastName, DateOfBirth = dateOfBirth }, Ct))
            .ReadCommandResultAsync(Ct);

    private static HttpContent Envelope(object envelope) => JsonContent.Create(envelope, options: Json);

    [Collection(ApiCollection.Name)]
    public class Episodes(BetsiApiFixture fixture) : QueryAndCommandApiTests(fixture)
    {
        [Fact]
        public async Task An_episode_is_returned_with_its_escalations()
        {
            var patient = await RegisterAsync("Detail", new DateTime(1962, 4, 19));
            await Nurse().PostAsJsonAsync("/api/v1/escalations/waiting-time", new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = patient.AggregateId, LocationId = Guid.NewGuid(), ResponsibleRole = "Nurse in Charge"
            }, Ct);

            var episode = await Nurse().GetFromJsonAsync<EpisodeDetail>($"/api/v1/episodes/{patient.AggregateId}", Json, Ct);

            episode!.LastName.ShouldBe("Detail");
            episode.DateOfBirth.ShouldBe(new DateTime(1962, 4, 19));
            episode.State.ShouldBe("Waiting");
            episode.MinutesWaiting.ShouldNotBeNull();
            episode.Escalations.ShouldHaveSingleItem().ResponsibleRole.ShouldBe("Nurse in Charge");
        }

        [Fact]
        public async Task An_unknown_episode_is_404_and_another_tenants_episode_is_indistinguishable()
        {
            var inA = await RegisterAsync("OtherTenant", new DateTime(1990, 1, 1), _factory.ClientFor(BetsiApiFactory.TenantA));

            (await Nurse().GetAsync($"/api/v1/episodes/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await Nurse().GetAsync($"/api/v1/episodes/{inA.AggregateId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task Reading_an_episode_is_audited_against_that_episode()
        {
            var patient = await RegisterAsync("Audited", new DateTime(1980, 1, 1));
            var reader = Guid.NewGuid();

            (await Nurse(reader).GetAsync($"/api/v1/episodes/{patient.AggregateId}", Ct)).EnsureSuccessStatusCode();

            await using var db = _factory.DatabaseFor(Tenant);
            var read = await db.AuditLogs.SingleAsync(a => a.ActorId == reader, Ct);
            read.AffectedAggregateId.ShouldBe(patient.AggregateId);
            read.Outcome.ShouldBe("Read");
        }
    }

    [Collection(ApiCollection.Name)]
    public class WaitingBoard(BetsiApiFixture fixture) : QueryAndCommandApiTests(fixture)
    {
        [Fact]
        public async Task Pages_cover_every_waiting_patient_exactly_once_in_arrival_order()
        {
            for (var i = 0; i < 7; i++)
                await RegisterAsync($"Page{i}", new DateTime(1990, 1, 1));

            var seen = new List<WaitingBoardRow>();
            string? cursor = null;

            do
            {
                var url = "/api/v1/boards/waiting?pageSize=3" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
                var page = await Nurse().GetFromJsonAsync<WaitingBoardPage>(url, Json, Ct);
                page!.Items.Count.ShouldBeLessThanOrEqualTo(3);
                seen.AddRange(page.Items);
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            seen.Select(r => r.EpisodeId).ShouldBeUnique();
            seen.Select(r => r.ArrivedAt).ShouldBeInOrder(SortDirection.Ascending);

            await using var db = _factory.DatabaseFor(Tenant);
            var waiting = await db.PatientEpisodes.CountAsync(
                e => e.State == Betsi.Domain.Aggregates.PatientEpisode.PatientState.Waiting ||
                     e.State == Betsi.Domain.Aggregates.PatientEpisode.PatientState.AwaitingTreatment, Ct);
            seen.Count.ShouldBe(waiting);
        }

        [Fact]
        public async Task Age_filters_find_paediatric_patients()
        {
            var child = await RegisterAsync("Child", DateTime.UtcNow.Date.AddYears(-6));

            var page = await Nurse().GetFromJsonAsync<WaitingBoardPage>("/api/v1/boards/waiting?maxAgeYears=17&pageSize=200", Json, Ct);

            page!.Items.ShouldContain(r => r.EpisodeId == child.AggregateId && r.AgeYears == 6);
            page.Items.ShouldAllBe(r => r.AgeYears <= 17);
        }

        [Theory]
        [InlineData("pageSize=0")]
        [InlineData("pageSize=201")]
        [InlineData("state=InTreatment")]
        [InlineData("cursor=not-a-cursor")]
        public async Task Invalid_parameters_are_400_with_a_code(string query)
        {
            var response = await Nurse().GetAsync($"/api/v1/boards/waiting?{query}", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("\"code\":\"BAD_REQUEST\"");
        }
    }

    [Collection(ApiCollection.Name)]
    public class CommandEnvelopes(BetsiApiFixture fixture) : QueryAndCommandApiTests(fixture)
    {
        private static object RegisterEnvelope(string key, string lastName = "Envelope") => new
        {
            commandType = "RegisterPatient",
            commandId = Guid.NewGuid(),
            idempotencyKey = key,
            correlationId = "corr-123",
            payload = new { firstName = "Env", lastName, dateOfBirth = "1975-05-05" }
        };

        [Fact]
        public async Task A_command_is_dispatched_and_its_result_returned()
        {
            var response = await Nurse().PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(Guid.NewGuid().ToString())), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("corr-123");
            var result = await response.Content.ReadFromJsonAsync<CommandEnvelopeResult>(Json, Ct);
            result!.Status.ShouldBe("Succeeded");
            result.Result.AggregateId.ShouldNotBe(Guid.Empty);
            result.Replayed.ShouldBeFalse();
        }

        [Fact]
        public async Task A_retry_with_the_same_key_returns_the_original_result_without_acting_again()
        {
            var actor = Guid.NewGuid();
            var key = Guid.NewGuid().ToString();
            var lastName = $"Once{Guid.NewGuid():N}"[..20];

            var first = await (await Nurse(actor).PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(key, lastName)), Ct))
                .Content.ReadFromJsonAsync<CommandEnvelopeResult>(Json, Ct);
            var retry = await Nurse(actor).PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(key, lastName)), Ct);
            var second = await retry.Content.ReadFromJsonAsync<CommandEnvelopeResult>(Json, Ct);

            retry.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");
            second!.Result.ShouldBe(first!.Result);
            second.Replayed.ShouldBeTrue();

            await using var db = _factory.DatabaseFor(Tenant);
            (await db.PatientEpisodes.CountAsync(e => e.LastName == lastName, Ct)).ShouldBe(1);
        }

        [Fact]
        public async Task A_key_reused_for_a_different_request_or_by_another_actor_is_refused()
        {
            var actor = Guid.NewGuid();
            var key = Guid.NewGuid().ToString();
            (await Nurse(actor).PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(key, "First")), Ct)).EnsureSuccessStatusCode();

            var different = await Nurse(actor).PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(key, "Second")), Ct);
            different.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await different.Content.ReadAsStringAsync(Ct)).ShouldContain("IDEMPOTENCY_KEY_REUSED");

            var otherActor = await Nurse().PostAsync("/api/v1/commands", Envelope(RegisterEnvelope(key, "First")), Ct);
            otherActor.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        [Fact]
        public async Task A_failed_command_releases_its_key_so_a_corrected_retry_can_run()
        {
            var actor = Guid.NewGuid();
            var key = Guid.NewGuid().ToString();

            var invalid = await Nurse(actor).PostAsync("/api/v1/commands", Envelope(new
            {
                commandType = "RegisterPatient", commandId = Guid.NewGuid(), idempotencyKey = key,
                payload = new { firstName = "", lastName = "Broken", dateOfBirth = "1975-05-05" }
            }), Ct);

            invalid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await invalid.Content.ReadAsStringAsync(Ct)).ShouldContain("VALIDATION_ERROR");

            await using (var db = _factory.DatabaseFor(Tenant))
                (await db.IdempotencyRecords.AnyAsync(r => r.IdempotencyKey == key, Ct)).ShouldBeFalse();
        }

        [Fact]
        public async Task Expected_version_is_applied_and_conflicts_come_back_as_problems_with_the_command_id()
        {
            var patient = await RegisterAsync("Versioned", new DateTime(1980, 1, 1));
            var commandId = Guid.NewGuid();

            var stale = await Nurse().PostAsync("/api/v1/commands", Envelope(new
            {
                commandType = "BeginPatientTriage", commandId, expectedVersion = 7,
                payload = new { patientEpisodeId = patient.AggregateId }
            }), Ct);

            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            var body = await stale.Content.ReadAsStringAsync(Ct);
            body.ShouldContain("CONCURRENCY_CONFLICT");
            body.ShouldContain(commandId.ToString());
        }

        [Fact]
        public async Task The_envelope_enforces_the_same_permissions_as_the_resource_endpoints()
        {
            var response = await _factory.ClientFor(Tenant, Guid.NewGuid(), "Receptionist").PostAsync("/api/v1/commands", Envelope(new
            {
                commandType = "CreateLocation", commandId = Guid.NewGuid(),
                payload = new { name = "Sneaky", capacity = 1 }
            }), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        [Theory]
        [InlineData("RaisePolicyEscalation")]
        [InlineData("RaiseFollowUpException")]
        [InlineData("DropDatabase")]
        public async Task System_only_and_unknown_commands_are_not_accepted(string commandType)
        {
            var response = await Nurse().PostAsync("/api/v1/commands", Envelope(new
            {
                commandType, commandId = Guid.NewGuid(), payload = new { }
            }), Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("UNKNOWN_COMMAND_TYPE");
        }
    }
}
