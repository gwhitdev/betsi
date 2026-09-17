namespace Betsi.Tests.ControlPlane;

using Betsi.ControlPlane;
using Betsi.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.MsSql;

/// <summary>
/// Provisions tenants end to end against a real SQL Server: the control-plane migration, the
/// database being created, tenant migrations applied, and the rowversion concurrency token.
/// </summary>
/// <remarks>
/// The unit suite uses SQLite and a fake migrator, which cannot prove that a database is
/// actually created or that the control-plane schema is valid on SQL Server.
/// </remarks>
public sealed class SqlServerProvisioningTests : IAsyncLifetime, IDbContextFactory<ControlPlaneDbContext>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MsSqlContainer? _container;
    private string? _skipReason;

    private TenantRegistry _registry = null!;
    private TenantOperations _operations = null!;

    public async ValueTask InitializeAsync()
    {
        if (!await Docker.IsAvailableAsync())
        {
            _skipReason = Docker.UnavailableReason;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();

        await using (var context = CreateDbContext())
            await context.Database.MigrateAsync();

        var servers = new DatabaseServerCatalog(new TenancyOptions
        {
            DatabaseServers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "" }.ConnectionString
            }
        });

        var migrator = new SqlServerTenantSchemaMigrator();
        var validator = TestLicenses.Validator();

        _registry = new TenantRegistry(this, servers, validator, migrator, TimeProvider.System, NullLogger<TenantRegistry>.Instance);
        _operations = new TenantOperations(this, servers, migrator, validator, _registry, TimeProvider.System, NullLogger<TenantOperations>.Instance);
    }

    public ControlPlaneDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(
                new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = "betsi_control" }.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "control"))
            .Options);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task A_tenant_database_is_created_migrated_and_served()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var id = Guid.NewGuid();
        var tenant = await _operations.ProvisionAsync(
            new ProvisionTenantRequest("Ysbyty Gwynedd", "default", "betsi_ysbyty_gwynedd", id, TestLicenses.ValidFor(id)),
            "operator:test", Ct);

        tenant.State.ShouldBe(TenantState.Active);
        tenant.SchemaVersion.ShouldBe(new SqlServerTenantSchemaMigrator().LatestMigration);

        _registry.TryGet(id, out var descriptor).ShouldBeTrue();
        descriptor.Availability.ShouldBe(TenantAvailability.Available);
        descriptor.License.Mode.ShouldBe(Betsi.Licensing.LicenseMode.Full);

        await using var connection = new SqlConnection(descriptor.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.PatientEpisodes", connection);
        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(0);
    }

    [Fact]
    public async Task Re_provisioning_and_re_migrating_are_no_ops()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var request = new ProvisionTenantRequest("Glan Clwyd", "default", "betsi_glan_clwyd");
        var first = await _operations.ProvisionAsync(request, "operator:test", Ct);
        var second = await _operations.ProvisionAsync(request, "operator:test", Ct);

        second.TenantId.ShouldBe(first.TenantId);

        var results = await _operations.MigrateAsync(null, "operator:test", Ct);
        results.ShouldAllBe(r => r.Succeeded);
    }

    [Fact]
    public async Task Suspension_round_trips_through_the_rowversion_column()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await _operations.ProvisionAsync(
            new ProvisionTenantRequest("Wrexham Maelor", "default", "betsi_wrexham_maelor"), "operator:test", Ct);

        await _operations.SuspendAsync(tenant.TenantId, "Test", "operator:test", Ct);
        await _operations.ResumeAsync(tenant.TenantId, "Test over", "operator:test", Ct);

        _registry.TryGet(tenant.TenantId, out var descriptor);
        descriptor.Availability.ShouldBe(TenantAvailability.Available);

        await using var context = CreateDbContext();
        (await context.AuditLog.CountAsync(a => a.TenantId == tenant.TenantId, Ct)).ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_database_on_an_unreachable_server_fails_without_affecting_others()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var healthy = await _operations.ProvisionAsync(
            new ProvisionTenantRequest("Healthy", "default", "betsi_healthy"), "operator:test", Ct);

        // A login that cannot succeed stands in for an unreachable tenant database.
        await using (var context = CreateDbContext())
        {
            context.Tenants.Add(new TenantRecord
            {
                TenantId = Guid.NewGuid(), Name = "Broken", State = TenantState.Provisioning,
                DatabaseServer = "default", DatabaseName = "betsi_broken",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync(Ct);
        }

        await using (var connection = new SqlConnection(_container!.GetConnectionString()))
        {
            await connection.OpenAsync(Ct);
            // A database that exists but is offline cannot be migrated.
            await using var create = new SqlCommand("CREATE DATABASE betsi_broken; ALTER DATABASE betsi_broken SET OFFLINE;", connection);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var results = await _operations.MigrateAsync(null, "operator:test", Ct);

        results.Single(r => r.Name == "Broken").Succeeded.ShouldBeFalse();
        results.Single(r => r.TenantId == healthy.TenantId).Succeeded.ShouldBeTrue();
        _registry.Available.Select(t => t.TenantId).ShouldContain(healthy.TenantId);
    }
}
