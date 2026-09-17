namespace Betsi.Tests.ControlPlane;

using Betsi.ControlPlane;
using Betsi.Licensing;

/// <summary>
/// The registry decides which database a request touches (ADR-001) and whether a tenant may
/// be served at all, so resolution must be exact and must fail closed.
/// </summary>
public class TenantRegistryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TenantRecord> Provision(ControlPlaneHarness harness, string database, bool licensed = true)
    {
        var id = Guid.NewGuid();
        return await harness.Operations.ProvisionAsync(
            new ProvisionTenantRequest(
                $"Hospital {database}", "default", database, id,
                licensed ? TestLicenses.ValidFor(id, harness.Time.Now) : null),
            "operator:test", Ct);
    }

    [Fact]
    public async Task Nothing_resolves_before_the_first_refresh()
    {
        await using var harness = new ControlPlaneHarness();
        await using (var context = harness.CreateDbContext())
        {
            context.Tenants.Add(new TenantRecord
            {
                TenantId = Guid.NewGuid(), Name = "Seeded", State = TenantState.Active,
                DatabaseServer = "default", DatabaseName = "betsi_seeded", SchemaVersion = harness.Migrator.LatestMigration
            });
            await context.SaveChangesAsync(Ct);
        }

        harness.Registry.All.ShouldBeEmpty();
        harness.Registry.LastRefreshedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Each_tenant_resolves_to_its_own_database_on_its_configured_server()
    {
        await using var harness = new ControlPlaneHarness();
        var a = await Provision(harness, "betsi_a");
        var b = await Provision(harness, "betsi_b");

        harness.Registry.ResolveConnectionString(a.TenantId).ShouldContain("Initial Catalog=betsi_a");
        harness.Registry.ResolveConnectionString(b.TenantId).ShouldContain("Initial Catalog=betsi_b");
        harness.Registry.ResolveConnectionString(a.TenantId).ShouldContain("Data Source=sql.test");
    }

    [Fact]
    public async Task An_unknown_tenant_is_rejected_rather_than_defaulted()
    {
        await using var harness = new ControlPlaneHarness();
        await Provision(harness, "betsi_a");

        Should.Throw<UnknownTenantException>(() => harness.Registry.ResolveConnectionString(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_suspended_tenant_does_not_resolve_a_connection()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        await harness.Operations.SuspendAsync(tenant.TenantId, "Contract ended", "operator:test", Ct);

        harness.Registry.TryGet(tenant.TenantId, out var descriptor).ShouldBeTrue();
        descriptor.Availability.ShouldBe(TenantAvailability.Suspended);
        Should.Throw<TenantUnavailableException>(() => harness.Registry.ResolveConnectionString(tenant.TenantId));
        harness.Registry.Available.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tenant_on_an_older_schema_than_this_build_is_not_served()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        harness.Migrator.LatestMigration = "20270101000000_SomethingNew";
        await harness.Registry.RefreshAsync(Ct);

        harness.Registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.NotReady);
        descriptor.UnavailableReason!.ShouldContain("tenants migrate");
    }

    [Fact]
    public async Task A_tenant_on_a_newer_schema_than_this_build_is_still_served()
    {
        // Expand/contract: during a rolling deployment the previous build runs against the new schema.
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        harness.Migrator.LatestMigration = "20200101000000_Older";
        await harness.Registry.RefreshAsync(Ct);

        harness.Registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.Available);
    }

    [Fact]
    public async Task A_tenant_on_a_server_profile_this_instance_lacks_is_not_ready()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");
        await harness.UpdateRecord(tenant.TenantId, r => r.DatabaseServer = "west-region");

        await harness.Registry.RefreshAsync(Ct);

        harness.Registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.NotReady);
        descriptor.ConnectionString.ShouldBeNull();
    }

    [Fact]
    public async Task An_unlicensed_tenant_is_served_in_restricted_mode()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a", licensed: false);

        harness.Registry.TryGet(tenant.TenantId, out var descriptor);

        // Restricted, not refused: patient care must continue without a licence.
        descriptor.Availability.ShouldBe(TenantAvailability.Available);
        descriptor.License.Status.ShouldBe(LicenseStatus.Missing);
        descriptor.License.Mode.ShouldBe(LicenseMode.Restricted);
    }

    [Fact]
    public async Task Licence_status_changes_are_audited_once_not_on_every_refresh()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        for (var i = 0; i < 5; i++)
            await harness.Registry.RefreshAsync(Ct);

        var evaluations = (await harness.AuditFor(tenant.TenantId)).Count(a => a.Action == "LicenseEvaluated");
        evaluations.ShouldBe(1);

        // Move past expiry and the grace period.
        harness.Time.Now = harness.Time.Now.AddYears(2);
        await harness.Registry.RefreshAsync(Ct);

        var audit = await harness.AuditFor(tenant.TenantId);
        audit.Count(a => a.Action == "LicenseEvaluated").ShouldBe(2);
        audit.Last(a => a.Action == "LicenseEvaluated").Outcome.ShouldBe("Restricted");
    }

    [Fact]
    public async Task The_licence_high_water_mark_only_moves_forward()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");
        var start = harness.Time.Now;

        (await harness.RecordFor(tenant.TenantId)).LicenseHighWaterMark.ShouldBe(start.UtcDateTime);

        harness.Time.Now = start.AddDays(-30);
        await harness.Registry.RefreshAsync(Ct);

        (await harness.RecordFor(tenant.TenantId)).LicenseHighWaterMark.ShouldBe(start.UtcDateTime);
        harness.Registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.License.ClockRollbackDetected.ShouldBeTrue();

        (await harness.AuditFor(tenant.TenantId)).ShouldContain(a => a.Action == "LicenseClockRollbackDetected");
    }

    [Fact]
    public async Task A_restarted_instance_does_not_re_audit_an_unchanged_licence()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        // A fresh registry over the same control plane: a new process, or an operator command.
        var restarted = new TenantRegistry(
            harness, harness.Servers, harness.Validator, harness.Migrator, harness.Time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantRegistry>.Instance);
        await restarted.RefreshAsync(Ct);

        (await harness.AuditFor(tenant.TenantId)).Count(a => a.Action == "LicenseEvaluated").ShouldBe(1);
    }

    [Fact]
    public async Task A_provisioned_tenant_with_a_licence_is_never_recorded_as_unlicensed()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await Provision(harness, "betsi_a");

        (await harness.AuditFor(tenant.TenantId))
            .Where(a => a.Action == "LicenseEvaluated")
            .ShouldAllBe(a => a.Outcome == "Success");
    }
}
