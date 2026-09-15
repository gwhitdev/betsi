namespace Betsi.Tests.Api;

using Betsi.API.Controllers;
using Betsi.Application.Commands;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Net.Http.Json;

/// <summary>
/// What the API does for tenants that are suspended, not ready, or unlicensed.
/// </summary>
[Collection(ApiCollection.Name)]
public class TenantLifecycleApiTests
{
    private readonly BetsiApiFactory _factory;

    public TenantLifecycleApiTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegisterPatientCommand APatient() => new()
    {
        FirstName = "Gwen",
        LastName = "Jones",
        DateOfBirth = new DateTime(1962, 4, 19)
    };

    [Fact]
    public async Task A_suspended_tenant_is_refused_with_403()
    {
        var client = _factory.ClientFor(BetsiApiFactory.SuspendedTenant);

        var response = await client.PostAsJsonAsync("/api/v1/patients/register", APatient(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        problem!.Title.ShouldBe("Tenant suspended");
    }

    [Fact]
    public async Task A_tenant_on_an_outdated_schema_is_refused_with_503_and_no_internal_detail()
    {
        var client = _factory.ClientFor(BetsiApiFactory.OutdatedSchemaTenant);

        var response = await client.PostAsJsonAsync("/api/v1/patients/register", APatient(), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.ShouldNotBeNull();

        // The operator-facing reason names migrations and servers; callers get none of it.
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("migrat", Case.Insensitive);
        body.ShouldNotContain("schema", Case.Insensitive);
    }

    [Fact]
    public async Task Without_a_licence_patient_care_continues()
    {
        var client = _factory.ClientFor(BetsiApiFactory.UnlicensedTenant);

        var register = await client.PostAsJsonAsync("/api/v1/patients/register", APatient(), Ct);
        register.StatusCode.ShouldBe(HttpStatusCode.Created);
        var patient = await register.ReadCommandResultAsync(Ct);

        var triage = await client.PostAsJsonAsync(
            $"/api/v1/patients/{patient.AggregateId}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = patient.Version }, Ct);
        triage.StatusCode.ShouldBe(HttpStatusCode.OK);

        var escalation = await client.PostAsJsonAsync("/api/v1/escalations/waiting-time",
            new TriggerWaitingTimeEscalationCommand
            {
                PatientEpisodeId = patient.AggregateId,
                LocationId = Guid.NewGuid(),
                ResponsibleRole = "Nurse in Charge"
            }, Ct);
        escalation.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Without_a_licence_administration_is_refused_with_403()
    {
        var client = _factory.ClientFor(BetsiApiFactory.UnlicensedTenant, actorRole: "Site Administrator");

        var response = await client.PostAsJsonAsync("/api/v1/locations",
            new CreateLocationCommand { Name = "Majors bay 4", Capacity = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        problem!.Type.ShouldEndWith("/license-restricted");
        problem.Extensions["licenseStatus"]!.ToString().ShouldBe("Missing");
    }

    [Fact]
    public async Task A_refused_licence_gated_command_is_audited()
    {
        var name = $"Resus {Guid.NewGuid():N}";
        var client = _factory.ClientFor(BetsiApiFactory.UnlicensedTenant, actorRole: "Site Administrator");

        await client.PostAsJsonAsync("/api/v1/locations", new CreateLocationCommand { Name = name, Capacity = 2 }, Ct);

        await using var db = _factory.DatabaseFor(BetsiApiFactory.UnlicensedTenant);
        db.AuditLogs.ShouldContain(a => a.Action == nameof(CreateLocationCommand) && a.Outcome == "Failure" &&
                                        a.ErrorMessage!.Contains("licence"));
    }

    [Fact]
    public async Task With_a_licence_administration_is_allowed()
    {
        var client = _factory.ClientFor(BetsiApiFactory.TenantA, actorRole: "Site Administrator");

        var response = await client.PostAsJsonAsync("/api/v1/locations",
            new CreateLocationCommand { Name = $"Majors bay {Guid.NewGuid():N}", Capacity = 1 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task The_licence_endpoint_reports_the_calling_tenants_licence()
    {
        var licensed = await _factory.ClientFor(BetsiApiFactory.TenantA)
            .GetFromJsonAsync<LicenseStatusResponse>("/api/v1/license", Ct);

        licensed!.Status.ShouldBe("Valid");
        licensed.Mode.ShouldBe("Full");
        licensed.Features.ShouldBe(["core"]);

        var unlicensed = await _factory.ClientFor(BetsiApiFactory.UnlicensedTenant)
            .GetFromJsonAsync<LicenseStatusResponse>("/api/v1/license", Ct);

        unlicensed!.Status.ShouldBe("Missing");
        unlicensed.Mode.ShouldBe("Restricted");
        unlicensed.Features.ShouldBeEmpty();
    }
}
