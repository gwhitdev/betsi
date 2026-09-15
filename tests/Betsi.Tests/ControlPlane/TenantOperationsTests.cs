namespace Betsi.Tests.ControlPlane;

using Betsi.ControlPlane;
using Betsi.Licensing;

/// <summary>
/// Provisioning, suspension, migration and licence installation (MVP-009). Every operation
/// must be idempotent, resumable after a failure, and leave an audit record.
/// </summary>
public class TenantOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Operator = "operator:test";

    [Fact]
    public async Task Provisioning_registers_migrates_and_activates_a_tenant()
    {
        await using var harness = new ControlPlaneHarness();

        var tenant = await harness.Operations.ProvisionAsync(
            new ProvisionTenantRequest("Ysbyty Gwynedd", "default", "betsi_ysbyty_gwynedd"), Operator, Ct);

        tenant.State.ShouldBe(TenantState.Active);
        tenant.SchemaVersion.ShouldBe(harness.Migrator.LatestMigration);
        harness.Migrator.Calls.ShouldHaveSingleItem().ConnectionString.ShouldContain("Initial Catalog=betsi_ysbyty_gwynedd");

        harness.Registry.TryGet(tenant.TenantId, out var descriptor).ShouldBeTrue();
        descriptor.Availability.ShouldBe(TenantAvailability.Available);

        var audit = await harness.AuditFor(tenant.TenantId);
        audit.ShouldContain(a => a.Action == "RegisterTenant" && a.Actor == Operator);
        audit.ShouldContain(a => a.Action == "ProvisionTenant" && a.Outcome == "Success");
    }

    [Fact]
    public async Task Provisioning_the_same_tenant_twice_changes_nothing()
    {
        await using var harness = new ControlPlaneHarness();
        var request = new ProvisionTenantRequest("Ysbyty Gwynedd", "default", "betsi_ysbyty_gwynedd");

        var first = await harness.Operations.ProvisionAsync(request, Operator, Ct);
        var second = await harness.Operations.ProvisionAsync(request, Operator, Ct);

        second.TenantId.ShouldBe(first.TenantId);
        harness.Migrator.Calls.Count.ShouldBe(1);
        (await harness.AuditFor(first.TenantId)).ShouldContain(a => a.Outcome == "NoChange");
    }

    [Fact]
    public async Task A_failed_provision_is_recorded_and_resumes_when_re_run()
    {
        await using var harness = new ControlPlaneHarness();
        var request = new ProvisionTenantRequest("Ysbyty Gwynedd", "default", "betsi_ysbyty_gwynedd");
        harness.Migrator.FailingDatabases.Add("betsi_ysbyty_gwynedd");

        await Should.ThrowAsync<TenantOperationException>(() => harness.Operations.ProvisionAsync(request, Operator, Ct));

        var failed = (await harness.Operations.ListAsync(Ct)).Single();
        failed.State.ShouldBe(TenantState.Failed);
        failed.StateReason!.ShouldContain("login failed");
        harness.Registry.TryGet(failed.TenantId, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.NotReady);

        harness.Migrator.FailingDatabases.Clear();
        var resumed = await harness.Operations.ProvisionAsync(request, Operator, Ct);

        resumed.TenantId.ShouldBe(failed.TenantId);
        resumed.State.ShouldBe(TenantState.Active);
        (await harness.AuditFor(failed.TenantId)).Select(a => a.Outcome).ShouldContain("Failure");
    }

    [Fact]
    public async Task Two_tenants_cannot_share_a_database()
    {
        await using var harness = new ControlPlaneHarness();
        await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("First", "default", "betsi_shared"), Operator, Ct);

        var exception = await Should.ThrowAsync<TenantOperationException>(() =>
            harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Second", "default", "betsi_shared"), Operator, Ct));

        exception.Message.ShouldContain("ADR-001");
    }

    [Fact]
    public async Task A_registered_tenant_cannot_be_re_provisioned_onto_a_different_database()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("First", "default", "betsi_first"), Operator, Ct);

        await Should.ThrowAsync<TenantOperationException>(() => harness.Operations.ProvisionAsync(
            new ProvisionTenantRequest("First", "default", "betsi_elsewhere", tenant.TenantId), Operator, Ct));
    }

    [Theory]
    [InlineData("betsi-hyphen")]
    [InlineData("1starts_with_digit")]
    [InlineData("betsi];DROP DATABASE master;--")]
    [InlineData("")]
    public async Task Database_names_that_are_not_plain_identifiers_are_refused(string databaseName)
    {
        await using var harness = new ControlPlaneHarness();

        await Should.ThrowAsync<TenantOperationException>(() => harness.Operations.ProvisionAsync(
            new ProvisionTenantRequest("Hospital", "default", databaseName), Operator, Ct));

        harness.Migrator.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unconfigured_server_profile_is_refused()
    {
        await using var harness = new ControlPlaneHarness();

        await Should.ThrowAsync<TenantOperationException>(() => harness.Operations.ProvisionAsync(
            new ProvisionTenantRequest("Hospital", "no-such-server", "betsi_hospital"), Operator, Ct));
    }

    [Fact]
    public async Task Suspending_and_resuming_require_a_reason_and_are_audited()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Hospital", "default", "betsi_h"), Operator, Ct);

        await Should.ThrowAsync<TenantOperationException>(() =>
            harness.Operations.SuspendAsync(tenant.TenantId, " ", Operator, Ct));

        await harness.Operations.SuspendAsync(tenant.TenantId, "Security incident INC-42", Operator, Ct);
        (await harness.RecordFor(tenant.TenantId)).State.ShouldBe(TenantState.Suspended);

        await harness.Operations.ResumeAsync(tenant.TenantId, "INC-42 closed", Operator, Ct);
        (await harness.RecordFor(tenant.TenantId)).State.ShouldBe(TenantState.Active);

        var audit = await harness.AuditFor(tenant.TenantId);
        audit.ShouldContain(a => a.Action == "SuspendTenant" && a.Detail == "Security incident INC-42");
        audit.ShouldContain(a => a.Action == "ResumeTenant" && a.Detail == "INC-42 closed");
    }

    [Fact]
    public async Task A_suspended_tenant_must_be_resumed_not_re_provisioned()
    {
        await using var harness = new ControlPlaneHarness();
        var request = new ProvisionTenantRequest("Hospital", "default", "betsi_h");
        var tenant = await harness.Operations.ProvisionAsync(request, Operator, Ct);
        await harness.Operations.SuspendAsync(tenant.TenantId, "Contract ended", Operator, Ct);

        await Should.ThrowAsync<TenantOperationException>(() => harness.Operations.ProvisionAsync(request, Operator, Ct));
    }

    [Fact]
    public async Task Migrating_all_tenants_continues_past_a_failure()
    {
        await using var harness = new ControlPlaneHarness();
        await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Alpha", "default", "betsi_alpha"), Operator, Ct);
        await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Bravo", "default", "betsi_bravo"), Operator, Ct);
        await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Charlie", "default", "betsi_charlie"), Operator, Ct);

        harness.Migrator.LatestMigration = "20270101000000_Next";
        harness.Migrator.FailingDatabases.Add("betsi_bravo");

        var results = await harness.Operations.MigrateAsync(null, Operator, Ct);

        results.Select(r => (r.Name, r.Succeeded)).ShouldBe([("Alpha", true), ("Bravo", false), ("Charlie", true)]);

        // Bravo stays Active but is not served by this build until its schema catches up.
        var bravo = results.Single(r => r.Name == "Bravo").TenantId;
        (await harness.RecordFor(bravo)).State.ShouldBe(TenantState.Active);
        harness.Registry.TryGet(bravo, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.NotReady);
        harness.Registry.Available.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_valid_licence_can_be_installed()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Hospital", "default", "betsi_h"), Operator, Ct);

        var evaluation = await harness.Operations.InstallLicenseAsync(
            tenant.TenantId, TestLicenses.ValidFor(tenant.TenantId, harness.Time.Now), Operator, Ct);

        evaluation.Status.ShouldBe(LicenseStatus.Valid);
        harness.Registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.License.Grants(LicenseFeatures.Core).ShouldBeTrue();
        (await harness.AuditFor(tenant.TenantId)).ShouldContain(a => a.Action == "InstallLicense" && a.Outcome == "Success");
    }

    [Fact]
    public async Task An_invalid_licence_is_refused_and_the_current_one_kept()
    {
        await using var harness = new ControlPlaneHarness();
        var tenant = await harness.Operations.ProvisionAsync(new ProvisionTenantRequest("Hospital", "default", "betsi_h"), Operator, Ct);
        var good = TestLicenses.ValidFor(tenant.TenantId, harness.Time.Now);
        await harness.Operations.InstallLicenseAsync(tenant.TenantId, good, Operator, Ct);

        var otherTenants = TestLicenses.ValidFor(Guid.NewGuid(), harness.Time.Now);
        await Should.ThrowAsync<TenantOperationException>(() =>
            harness.Operations.InstallLicenseAsync(tenant.TenantId, otherTenants, Operator, Ct));

        (await harness.RecordFor(tenant.TenantId)).LicenseKey.ShouldBe(good);
        (await harness.AuditFor(tenant.TenantId)).ShouldContain(a => a.Action == "InstallLicense" && a.Outcome == "Refused");
    }
}
