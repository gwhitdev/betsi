namespace Betsi.Tests.Api;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;

/// <summary>
/// Tenant isolation is the property that matters most here: a leak means one health board
/// reading another's patient records.
/// </summary>
/// <remarks>
/// The original implementation leaked. Query filters closed over an instance field, and EF
/// caches the built model per options, so whichever tenant was resolved first in the process
/// had its id compiled into the model for every tenant thereafter. Isolation now comes from
/// the connection each tenant resolves to, and these tests hold that line.
/// </remarks>
[Collection(ApiCollection.Name)]
public class TenantIsolationTests
{
    private readonly BetsiApiFactory _factory;

    public TenantIsolationTests(BetsiApiFixture fixture)
    {
        _factory = fixture.Factory;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RegisterPatientCommand APatientNamed(string lastName) => new()
    {
        FirstName = "Test",
        LastName = lastName,
        DateOfBirth = new DateTime(1980, 6, 1)
    };

    [Fact]
    public async Task A_patient_registered_by_one_tenant_is_invisible_to_the_other()
    {
        var clientA = _factory.ClientFor(BetsiApiFactory.TenantA);
        var clientB = _factory.ClientFor(BetsiApiFactory.TenantB);

        var responseA = await clientA.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("OnlyInTenantA"), Ct);
        var registeredInA = await responseA.ReadCommandResultAsync(Ct);

        await clientB.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("OnlyInTenantB"), Ct);

        await using var databaseB = _factory.DatabaseFor(BetsiApiFactory.TenantB);
        var leaked = await databaseB.PatientEpisodes
            .AnyAsync(e => e.Id == registeredInA.AggregateId, Ct);

        leaked.ShouldBeFalse();
    }

    [Fact]
    public async Task Tenant_B_cannot_act_on_tenant_As_patient()
    {
        var clientA = _factory.ClientFor(BetsiApiFactory.TenantA);
        var clientB = _factory.ClientFor(BetsiApiFactory.TenantB);

        var response = await clientA.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("BelongsToA"), Ct);
        var registeredInA = await response.ReadCommandResultAsync(Ct);

        var attempt = await clientB.PostAsJsonAsync(
            $"/api/v1/patients/{registeredInA.AggregateId}/triage/begin",
            new BeginPatientTriageCommand { ExpectedVersion = registeredInA.Version },
            Ct);

        // Not found rather than forbidden: tenant B should not learn that the record exists.
        attempt.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Each_tenants_event_log_holds_only_its_own_events()
    {
        var clientA = _factory.ClientFor(BetsiApiFactory.TenantA);
        var clientB = _factory.ClientFor(BetsiApiFactory.TenantB);

        await clientA.PostAsJsonAsync("/api/v1/patients/register", APatientNamed("EventsA"), Ct);
        await clientB.PostAsJsonAsync("/api/v1/patients/register", APatientNamed("EventsB"), Ct);

        await using var databaseA = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        await using var databaseB = _factory.DatabaseFor(BetsiApiFactory.TenantB);

        (await databaseA.DomainEvents.ToListAsync(Ct))
            .ShouldAllBe(e => e.TenantId == BetsiApiFactory.TenantA);
        (await databaseB.DomainEvents.ToListAsync(Ct))
            .ShouldAllBe(e => e.TenantId == BetsiApiFactory.TenantB);
    }

    [Fact]
    public async Task Each_tenants_audit_trail_holds_only_its_own_entries()
    {
        var clientA = _factory.ClientFor(BetsiApiFactory.TenantA);

        await clientA.PostAsJsonAsync("/api/v1/patients/register", APatientNamed("AuditA"), Ct);

        await using var databaseB = _factory.DatabaseFor(BetsiApiFactory.TenantB);
        (await databaseB.AuditLogs.ToListAsync(Ct))
            .ShouldAllBe(a => a.TenantId == BetsiApiFactory.TenantB);
    }

    [Fact]
    public async Task Whichever_tenant_is_served_first_does_not_capture_the_others_data()
    {
        // The specific shape of the original bug: the first tenant through the process won,
        // so this asserts both orderings within one process.
        var clientB = _factory.ClientFor(BetsiApiFactory.TenantB);
        var clientA = _factory.ClientFor(BetsiApiFactory.TenantA);

        var inB = await (await clientB.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("OrderingB"), Ct)).ReadCommandResultAsync(Ct);
        var inA = await (await clientA.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("OrderingA"), Ct)).ReadCommandResultAsync(Ct);

        await using var databaseA = _factory.DatabaseFor(BetsiApiFactory.TenantA);
        await using var databaseB = _factory.DatabaseFor(BetsiApiFactory.TenantB);

        (await databaseA.PatientEpisodes.AnyAsync(e => e.Id == inA.AggregateId, Ct)).ShouldBeTrue();
        (await databaseA.PatientEpisodes.AnyAsync(e => e.Id == inB.AggregateId, Ct)).ShouldBeFalse();
        (await databaseB.PatientEpisodes.AnyAsync(e => e.Id == inB.AggregateId, Ct)).ShouldBeTrue();
        (await databaseB.PatientEpisodes.AnyAsync(e => e.Id == inA.AggregateId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_request_with_no_tenant_is_refused()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("NoTenant"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_request_naming_an_unregistered_tenant_is_refused()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TenantResolutionMiddleware.TenantHeaderName, Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync(
            "/api/v1/patients/register", APatientNamed("UnknownTenant"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Health_checks_are_reachable_without_a_tenant()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health", Ct);

        // Probes have no tenant to offer, so the exempt-path list must let them through.
        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest);
    }
}
