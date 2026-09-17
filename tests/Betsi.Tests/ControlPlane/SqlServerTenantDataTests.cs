namespace Betsi.Tests.ControlPlane;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Testcontainers.MsSql;

/// <summary>
/// Backup, restore, export and destruction against a real SQL Server (MVP-108).
/// </summary>
/// <remarks>
/// These operations are almost entirely T-SQL that the server either accepts or does not:
/// BACKUP, RESTORE VERIFYONLY, RESTORE, ALTER DATABASE … SINGLE_USER, DROP DATABASE. A SQLite
/// or in-memory test would assert that this code builds strings, which is not the thing that
/// can be wrong. The backup directory is inside the container, because the server writes it.
/// </remarks>
public sealed class SqlServerTenantDataTests : IAsyncLifetime, IDbContextFactory<ControlPlaneDbContext>
{
    private const string BackupDirectory = "/var/opt/mssql/backups";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MsSqlContainer? _container;
    private string? _skipReason;

    private DatabaseServerCatalog _servers = null!;
    private TenantRegistry _registry = null!;
    private TenantOperations _operations = null!;
    private TenantDataOperations _data = null!;

    public async ValueTask InitializeAsync()
    {
        if (!await Docker.IsAvailableAsync())
        {
            _skipReason = Docker.UnavailableReason;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
        await _container.ExecAsync(["mkdir", "-p", BackupDirectory]);

        await using (var context = CreateDbContext())
            await context.Database.MigrateAsync();

        _servers = new DatabaseServerCatalog(new TenancyOptions
        {
            DatabaseServers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["default"] = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "" }.ConnectionString
            }
        });

        var migrator = new SqlServerTenantSchemaMigrator();
        var validator = TestLicenses.Validator();

        _registry = new TenantRegistry(this, _servers, validator, migrator, TimeProvider.System, NullLogger<TenantRegistry>.Instance);
        _operations = new TenantOperations(this, _servers, migrator, validator, _registry, TimeProvider.System, NullLogger<TenantOperations>.Instance);
        _data = new TenantDataOperations(this, _servers, _registry, TimeProvider.System, NullLogger<TenantDataOperations>.Instance);
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

    private async Task<TenantRecord> ProvisionAsync(string name, string database)
    {
        var id = Guid.NewGuid();
        return await _operations.ProvisionAsync(
            new ProvisionTenantRequest(name, "default", database, id, TestLicenses.ValidFor(id)), "operator:test", Ct);
    }

    /// <summary>Puts one episode in the tenant's database, so a restore or export has something to prove.</summary>
    private async Task<Guid> RegisterAnEpisodeAsync(TenantRecord tenant, string surname)
    {
        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(tenant.TenantId);

        await using var context = new BetsiDbContext(
            new DbContextOptionsBuilder<BetsiDbContext>()
                .UseSqlServer(_servers.ConnectionStringFor(tenant.DatabaseServer, tenant.DatabaseName))
                .Options,
            tenantContext);

        var episode = Betsi.Domain.Aggregates.PatientEpisode.CreateNew(
            tenant.TenantId, "Test", surname, new DateTime(1980, 1, 1));

        context.PatientEpisodes.Add(episode);
        await context.SaveChangesAsync(Ct);
        return episode.Id;
    }

    private async Task<int> EpisodeCountAsync(string database)
    {
        await using var connection = new SqlConnection(_servers.ConnectionStringFor("default", database));
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM dbo.PatientEpisodes", connection);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_backup_is_written_verified_and_restores_into_a_new_database()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await ProvisionAsync("Backup Site", "betsi_backup_site");
        await RegisterAnEpisodeAsync(tenant, "Prichard");

        var backup = await _data.BackupAsync(
            tenant.TenantId, new BackupRequest(BackupDirectory), "operator:test", Ct);

        backup.Kind.ShouldBe(BackupKind.Full);
        // Verified by RESTORE VERIFYONLY inside the operation: a backup that cannot be read is
        // reported when it is taken, not on the night it is needed.
        backup.Verified.ShouldBeTrue();
        backup.Path.ShouldStartWith($"{BackupDirectory}/betsi_backup_site_");
        backup.Path.ShouldEndWith(".bak");

        // A second episode after the backup, so the restore is provably the earlier state.
        await RegisterAnEpisodeAsync(tenant, "Vaughan");
        (await EpisodeCountAsync("betsi_backup_site")).ShouldBe(2);

        await _data.RestoreAsync(
            new RestoreRequest(tenant.TenantId, backup.Path, "betsi_backup_site_restored"), "operator:test", Ct);

        (await EpisodeCountAsync("betsi_backup_site_restored")).ShouldBe(1);
        // The live database is untouched by a restore that does not repoint.
        (await EpisodeCountAsync("betsi_backup_site")).ShouldBe(2);

        await using var control = CreateDbContext();
        var audit = await control.AuditLog.Where(a => a.TenantId == tenant.TenantId).ToListAsync(Ct);
        audit.ShouldContain(a => a.Action == "BackupTenant" && a.Outcome == "Success");
        audit.ShouldContain(a => a.Action == "RestoreTenant" && a.Outcome == "Success");
    }

    [Fact]
    public async Task Restoring_over_a_live_tenant_is_refused_until_it_is_suspended()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await ProvisionAsync("Live Site", "betsi_live_site");
        var backup = await _data.BackupAsync(tenant.TenantId, new BackupRequest(BackupDirectory), "operator:test", Ct);

        var refusal = await Should.ThrowAsync<TenantOperationException>(() => _data.RestoreAsync(
            new RestoreRequest(tenant.TenantId, backup.Path, "betsi_live_site"), "operator:test", Ct));

        refusal.Message.ShouldContain("Suspend it");

        await _operations.SuspendAsync(tenant.TenantId, "Restoring", "operator:test", Ct);
        await _data.RestoreAsync(
            new RestoreRequest(tenant.TenantId, backup.Path, "betsi_live_site"), "operator:test", Ct);

        (await EpisodeCountAsync("betsi_live_site")).ShouldBe(0);
    }

    [Fact]
    public async Task Repointing_moves_the_tenant_to_the_restored_database()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await ProvisionAsync("Repoint Site", "betsi_repoint_site");
        await RegisterAnEpisodeAsync(tenant, "Meredith");

        var backup = await _data.BackupAsync(tenant.TenantId, new BackupRequest(BackupDirectory), "operator:test", Ct);
        await _operations.SuspendAsync(tenant.TenantId, "Restoring", "operator:test", Ct);

        await _data.RestoreAsync(
            new RestoreRequest(tenant.TenantId, backup.Path, "betsi_repoint_site_new", Repoint: true), "operator:test", Ct);

        await using var control = CreateDbContext();
        var record = await control.Tenants.SingleAsync(t => t.TenantId == tenant.TenantId, Ct);
        record.DatabaseName.ShouldBe("betsi_repoint_site_new");

        await _operations.ResumeAsync(tenant.TenantId, "Verified", "operator:test", Ct);
        _registry.TryGet(tenant.TenantId, out var descriptor).ShouldBeTrue();
        descriptor.DatabaseName.ShouldBe("betsi_repoint_site_new");
    }

    [Fact]
    public async Task An_export_writes_every_table_and_audits_the_row_counts_only()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await ProvisionAsync("Export Site", "betsi_export_site");
        var episodeId = await RegisterAnEpisodeAsync(tenant, "Llewelyn");

        var path = Path.Combine(Path.GetTempPath(), $"betsi-export-{Guid.NewGuid():N}.json");

        try
        {
            var export = await _data.ExportAsync(tenant.TenantId, path, "operator:test", Ct);

            export.RowCounts["patientEpisodes"].ShouldBe(1);
            export.RowCounts.ShouldContainKey("domainEvents");
            export.RowCounts.ShouldContainKey("auditLogs");

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, Ct));
            document.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(tenant.TenantId);

            var episodes = document.RootElement.GetProperty("data").GetProperty("patientEpisodes");
            episodes.GetArrayLength().ShouldBe(1);
            episodes[0].GetProperty("id").GetGuid().ShouldBe(episodeId);
            // The export *is* the clinical record — unlike a webhook payload, it carries names.
            episodes[0].GetProperty("lastName").GetString().ShouldBe("Llewelyn");

            await using var control = CreateDbContext();
            var audit = await control.AuditLog
                .Where(a => a.TenantId == tenant.TenantId && a.Action == "ExportTenant")
                .SingleAsync(Ct);

            audit.Outcome.ShouldBe("Success");
            audit.Detail.ShouldNotBeNull();
            audit.Detail.ShouldContain("patientEpisodes 1");
            // The audit log is read by people not entitled to the clinical record.
            audit.Detail.ShouldNotContain("Llewelyn");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Destruction_needs_a_suspended_tenant_and_its_name_then_drops_the_database()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var tenant = await ProvisionAsync("Doomed Site", "betsi_doomed_site");
        await RegisterAnEpisodeAsync(tenant, "Hughes");

        var active = await Should.ThrowAsync<TenantOperationException>(() =>
            _data.DestroyAsync(tenant.TenantId, "Doomed Site", "operator:test", Ct));
        active.Message.ShouldContain("Suspend it first");

        await _operations.SuspendAsync(tenant.TenantId, "Contract ended", "operator:test", Ct);

        var wrongName = await Should.ThrowAsync<TenantOperationException>(() =>
            _data.DestroyAsync(tenant.TenantId, "doomed site", "operator:test", Ct));
        wrongName.Message.ShouldContain("does not match the tenant name");

        await _data.DestroyAsync(tenant.TenantId, "Doomed Site", "operator:test", Ct);

        await using var control = CreateDbContext();
        var record = await control.Tenants.SingleAsync(t => t.TenantId == tenant.TenantId, Ct);

        // The tombstone stays: it is the evidence the tenant existed, and it stops the database
        // name being reissued.
        record.State.ShouldBe(TenantState.Destroyed);
        record.LicenseKey.ShouldBeNull();

        await using var connection = new SqlConnection(_servers.ConnectionStringFor("default", "master"));
        await connection.OpenAsync(Ct);
        await using var exists = new SqlCommand("SELECT DB_ID('betsi_doomed_site')", connection);
        (await exists.ExecuteScalarAsync(Ct)).ShouldBe(DBNull.Value);

        // Absent from the registry, so a request naming it is a 404 and not a 503 inviting a retry.
        _registry.TryGet(tenant.TenantId, out _).ShouldBeFalse();

        // Idempotent: destroying a destroyed tenant is recorded and changes nothing.
        await _data.DestroyAsync(tenant.TenantId, "Doomed Site", "operator:test", Ct);
        (await control.AuditLog.CountAsync(
            a => a.TenantId == tenant.TenantId && a.Action == "DestroyTenant" && a.Outcome == "NoChange", Ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task The_data_protection_key_ring_survives_a_restart_through_the_control_plane()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        // Two repositories over the same database stand in for two instances, or for one
        // instance before and after a restart: the key ring must be shared, or a secret written
        // by one is unreadable by the other.
        var first = new Betsi.Infrastructure.DataProtection.ControlPlaneXmlRepository(
            this, TimeProvider.System,
            NullLogger<Betsi.Infrastructure.DataProtection.ControlPlaneXmlRepository>.Instance);

        first.StoreElement(new System.Xml.Linq.XElement("key", new System.Xml.Linq.XAttribute("id", "abc")), "key-abc");

        var second = new Betsi.Infrastructure.DataProtection.ControlPlaneXmlRepository(
            this, TimeProvider.System,
            NullLogger<Betsi.Infrastructure.DataProtection.ControlPlaneXmlRepository>.Instance);

        var elements = second.GetAllElements();
        elements.ShouldContain(element => element.Attribute("id")!.Value == "abc");
    }
}
