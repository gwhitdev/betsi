namespace Betsi.Tests.Api;

using Betsi.Application.Behaviours;
using Betsi.Application.Commands;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Authentication and authorisation (MVP-065, spec §6): tokens are validated strictly, the
/// tenant and acting role come only from verified credentials, and every endpoint and command
/// requires a permission the acting role holds.
/// </summary>
[Collection(ApiCollection.Name)]
public class SecurityTests
{
    private readonly BetsiApiFactory _factory;

    public SecurityTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly RegisterPatientCommand APatient = new()
    {
        FirstName = "Token", LastName = "Holder", DateOfBirth = new DateTime(1970, 1, 1)
    };

    private async Task<HttpResponseMessage> RegisterWith(string token, Action<HttpClient>? configure = null)
    {
        var client = _factory.ClientWithToken(token);
        configure?.Invoke(client);
        return await client.PostAsJsonAsync("/api/v1/patients/register", APatient, Ct);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Collection(ApiCollection.Name)]
    public class Tokens(BetsiApiFixture fixture) : SecurityTests(fixture)
    {
        [Fact]
        public async Task A_valid_token_is_accepted_and_needs_no_development_headers()
        {
            var response = await RegisterWith(TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"]));

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        public static TheoryData<string, string> InvalidTokens => new()
        {
            { "expired", TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"], expires: DateTime.UtcNow.AddMinutes(-10)) },
            { "wrong audience", TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"], audience: "some-other-api") },
            { "wrong issuer", TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"], issuer: "https://evil.example") },
            { "untrusted signing key", TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"], signingKey: RSA.Create(2048)) },
            { "garbage", "not.a.token" }
        };

        [Theory]
        [MemberData(nameof(InvalidTokens))]
        public async Task An_invalid_token_is_401_with_a_problem_code(string _, string token)
        {
            var response = await RegisterWith(token);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await CodeOf(response)).ShouldBe("UNAUTHENTICATED");
        }

        [Fact]
        public async Task An_unsigned_token_is_refused()
        {
            var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
            var payload = Base64Url(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = TestTokens.Issuer, ["aud"] = TestTokens.Audience, ["sub"] = Guid.NewGuid(),
                ["betsi:tenant_id"] = BetsiApiFactory.TenantA, ["roles"] = "Nurse",
                ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()
            }));

            (await RegisterWith($"{header}.{payload}.")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task A_bad_token_does_not_fall_back_to_development_headers()
        {
            var response = await RegisterWith("not.a.token", client =>
            {
                client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeaderName, BetsiApiFactory.TenantA.ToString());
                client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.ActorRoleHeaderName, "Nurse");
            });

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task A_tenant_header_that_contradicts_the_token_is_refused()
        {
            var response = await RegisterWith(TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"]), client =>
                client.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeaderName, BetsiApiFactory.TenantB.ToString()));

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(response)).ShouldBe("TENANT_MISMATCH");
        }

        [Fact]
        public async Task Health_is_reachable_without_credentials()
        {
            (await _factory.CreateClient().GetAsync("/health", Ct)).StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task A_subject_that_is_not_a_guid_maps_to_a_stable_actor_id()
        {
            const string subject = "user-4471@nhs.example";
            var response = await RegisterWith(TestTokens.For(BetsiApiFactory.TenantA, ["Nurse"], subject: subject));
            var episode = await response.ReadCommandResultAsync(Ct);

            await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
            var created = await db.DomainEvents.SingleAsync(e => e.AggregateId == episode.AggregateId, Ct);

            // Scoped by issuer, so the same subject string from two providers is two different people.
            created.ActorId.ShouldBe(AuthenticationSetup.ActorIdFor(subject, TestTokens.Issuer));
            AuthenticationSetup.ActorIdFor(subject, "https://other-idp").ShouldNotBe(created.ActorId);
            created.ActorId.ShouldNotBe(Guid.Empty);
        }

        private static string Base64Url(string value) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Collection(ApiCollection.Name)]
    public class ActingRoles(BetsiApiFixture fixture) : SecurityTests(fixture)
    {
        [Fact]
        public async Task A_token_with_several_roles_must_select_one()
        {
            var token = TestTokens.For(BetsiApiFactory.TenantA, ["Nurse", "Site Administrator"]);

            var unselected = await RegisterWith(token);
            unselected.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(unselected)).ShouldBe("ACTING_ROLE_REQUIRED");

            var selected = await RegisterWith(token, c => c.DefaultRequestHeaders.Add(TenantResolutionMiddleware.ActingRoleHeaderName, "nurse"));
            selected.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        [Fact]
        public async Task The_acting_role_claim_selects_the_role_and_is_what_gets_audited()
        {
            var token = TestTokens.For(BetsiApiFactory.TenantA, ["Nurse", "Doctor"], actingRole: "Doctor");

            var episode = await (await RegisterWith(token)).ReadCommandResultAsync(Ct);

            await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
            (await db.AuditLogs.SingleAsync(a => a.AffectedAggregateId == episode.AggregateId, Ct)).ActorRole.ShouldBe("Doctor");
        }

        [Fact]
        public async Task A_role_the_token_does_not_hold_cannot_be_selected()
        {
            var response = await RegisterWith(TestTokens.For(BetsiApiFactory.TenantA, ["Receptionist"]),
                c => c.DefaultRequestHeaders.Add(TenantResolutionMiddleware.ActingRoleHeaderName, "Clinical Lead"));

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        [Theory]
        [InlineData("System")]
        [InlineData("Integration")]
        public async Task Reserved_roles_cannot_be_claimed_by_a_token(string role)
        {
            var response = await RegisterWith(TestTokens.For(BetsiApiFactory.TenantA, [role]));

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(response)).ShouldBe("RESERVED_ROLE");
        }

        [Fact]
        public async Task Reserved_roles_cannot_be_claimed_by_development_headers_either()
        {
            var response = await _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "System")
                .PostAsJsonAsync("/api/v1/patients/register", APatient, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [Collection(ApiCollection.Name)]
    public class Permissions(BetsiApiFixture fixture) : SecurityTests(fixture)
    {
        [Fact]
        public async Task A_role_without_the_endpoint_permission_is_refused_and_the_refusal_audited()
        {
            var actor = Guid.NewGuid();
            var response = await _factory.ClientWithToken(
                    TestTokens.For(BetsiApiFactory.TenantA, ["Site Administrator"], subject: actor.ToString()))
                .GetAsync("/api/v1/escalations/board", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(response)).ShouldBe("FORBIDDEN");

            await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
            var denial = await db.AuditLogs.SingleAsync(a => a.ActorId == actor, Ct);
            denial.Outcome.ShouldBe("Denied");
            denial.ErrorMessage.ShouldNotBeNull().ShouldContain(Betsi.Security.Permissions.EscalationsRead);
        }

        [Fact]
        public async Task A_role_without_the_command_permission_is_refused_and_audited()
        {
            var actor = Guid.NewGuid();
            var client = _factory.ClientFor(BetsiApiFactory.TenantA, Guid.NewGuid(), "Nurse");
            var patient = await (await client.PostAsJsonAsync("/api/v1/patients/register", APatient, Ct)).ReadCommandResultAsync(Ct);

            var response = await _factory.ClientFor(BetsiApiFactory.TenantA, actor, "Receptionist").PostAsJsonAsync(
                $"/api/v1/patients/{patient.AggregateId}/triage/begin", new BeginPatientTriageCommand { ExpectedVersion = 1 }, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(response)).ShouldBe("FORBIDDEN");

            await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
            (await db.AuditLogs.SingleAsync(a => a.ActorId == actor, Ct)).Outcome.ShouldBe("Failure");
        }

        [Fact]
        public async Task An_unknown_role_has_no_permissions()
        {
            var response = await _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Visitor")
                .PostAsJsonAsync("/api/v1/patients/register", APatient, Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Reading_patient_data_is_audited()
        {
            var actor = Guid.NewGuid();

            (await _factory.ClientFor(BetsiApiFactory.TenantA, actor, "Nurse in Charge").GetAsync("/api/v1/escalations/board", Ct))
                .EnsureSuccessStatusCode();

            await using var db = _factory.DatabaseFor(BetsiApiFactory.TenantA);
            var read = await db.AuditLogs.SingleAsync(a => a.ActorId == actor, Ct);
            read.Outcome.ShouldBe("Read");
            read.Action.ShouldBe("Read:EscalationBoard");
        }

        [Fact]
        public void Every_endpoint_requires_a_permission_or_dispatches_a_command_that_does()
        {
            var unprotected = typeof(Program).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(m => (Controller: t, Action: m)))
                .Where(x => !IsProtected(x.Controller, x.Action))
                .Select(x => $"{x.Controller.Name}.{x.Action.Name}")
                .ToArray();

            unprotected.ShouldBeEmpty();
        }

        [Fact]
        public void Every_command_declares_its_permission_or_is_system_only()
        {
            var commands = typeof(RegisterPatientCommand).Assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ICommand).IsAssignableFrom(t));

            var unclassified = commands.Where(t =>
            {
                try { AuthorizationBehaviour<ICommand, CommandResult>.RequirementOf(t); return false; }
                catch (InvalidOperationException) { return true; }
            }).Select(t => t.Name).ToArray();

            unclassified.ShouldBeEmpty();
        }

        [Fact]
        public void Site_administration_roles_cannot_see_patient_data()
        {
            // Least privilege (spec §6): configuring the site does not require reading records.
            foreach (var permission in new[] { Betsi.Security.Permissions.EpisodesRead, Betsi.Security.Permissions.EscalationsRead })
                RoleMatrix.Grants("Site Administrator", permission).ShouldBeFalse();
        }

        private static bool IsProtected(Type controller, MethodInfo action)
        {
            if (action.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy is not null) ||
                action.GetCustomAttribute<AllowAnonymousAttribute>() is not null ||
                action.GetCustomAttribute<DispatchesAuthorizedCommandsAttribute>() is not null ||
                controller.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy is not null))
            {
                return true;
            }

            return action.GetParameters().Any(p =>
                p.GetCustomAttribute<FromBodyAttribute>() is not null &&
                p.ParameterType.GetCustomAttribute<RequiresPermissionAttribute>() is not null);
        }
    }
}
