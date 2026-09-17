namespace Betsi.Tests.Infrastructure;

using Betsi.ControlPlane;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

/// <summary>
/// Forward and rollback migrations against a real SQL Server (MVP-104).
/// </summary>
/// <remarks>
/// <para>
/// A migration's <c>Down</c> is written by the tooling and, in most projects, never executed
/// until the night someone needs it. These tests execute every one: each migration is applied,
/// rolled back, and applied again. A <c>Down</c> that cannot run is a release that cannot be
/// undone, and it is better to find that here than at two in the morning.
/// </para>
/// <para>
/// The deployment runbook is explicit that a rollback of the *application* does not roll back
/// the schema — the previous build tolerates a newer schema by design, and reversing a
/// migration under a live database is how data is lost. These tests exist for the other case:
/// a migration that must be withdrawn before a database is in service, and the release rehearsal
/// that proves it can be.
/// </para>
/// </remarks>
public sealed class MigrationRollbackTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Guid _tenantId = Guid.NewGuid();

    private MsSqlContainer? _container;
    private string? _skipReason;

    public async ValueTask InitializeAsync()
    {
        if (!await Docker.IsAvailableAsync())
        {
            _skipReason = Docker.UnavailableReason;
            return;
        }

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private BetsiDbContext TenantContextFor(string database)
    {
        var tenantContext = new TenantContext();
        tenantContext.ResolveSystem(_tenantId);

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(new SqlConnectionStringBuilder(_container!.GetConnectionString())
            {
                InitialCatalog = database
            }.ConnectionString)
            .Options;

        return new BetsiDbContext(options, tenantContext);
    }

    private static IReadOnlyList<string> MigrationsOf(DbContext context) =>
        context.GetInfrastructure().GetRequiredService<IMigrationsAssembly>()
            .Migrations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task Every_tenant_migration_can_be_rolled_back_and_reapplied()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using var context = TenantContextFor("betsi_rollback");
        var migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();

        var migrations = MigrationsOf(context);
        migrations.Count.ShouldBeGreaterThan(1, "there is nothing to roll back to");

        await context.Database.MigrateAsync(Ct);

        // Backwards one at a time, then forwards again: a Down that only works when run as part
        // of a full teardown is not a rollback, it is a drop.
        for (var i = migrations.Count - 1; i >= 1; i--)
        {
            var target = migrations[i - 1];

            await migrator.MigrateAsync(target, Ct);
            (await context.Database.GetAppliedMigrationsAsync(Ct)).Last().ShouldBe(target);

            await migrator.MigrateAsync(migrations[i], Ct);
            (await context.Database.GetAppliedMigrationsAsync(Ct)).Last().ShouldBe(migrations[i]);
        }

        // The loop finishes with the second migration applied, so bring the database back to the
        // head before asserting: the point is that a round trip through every Down leaves a
        // database that can still be migrated to current.
        await context.Database.MigrateAsync(Ct);
        (await context.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Rolling_the_newest_migration_back_and_forward_keeps_the_data_that_predates_it()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        await using var context = TenantContextFor("betsi_rollback_data");
        var migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = MigrationsOf(context);

        await context.Database.MigrateAsync(Ct);

        var episode = PatientEpisode.CreateNew(_tenantId, "Rhodri", "Emlyn", new DateTime(1954, 7, 19));
        context.PatientEpisodes.Add(episode);
        await context.SaveChangesAsync(Ct);

        var eventsBefore = await context.DomainEvents.CountAsync(Ct);

        // Down then Up on the newest migration. The tables it introduced are dropped and
        // recreated; everything older must be untouched, because a withdrawn release must not
        // take a department's existing record with it.
        await migrator.MigrateAsync(migrations[^2], Ct);
        await migrator.MigrateAsync(migrations[^1], Ct);

        await using var reopened = TenantContextFor("betsi_rollback_data");
        var survivor = await reopened.PatientEpisodes.SingleOrDefaultAsync(p => p.Id == episode.Id, Ct);

        survivor.ShouldNotBeNull();
        survivor.LastName.ShouldBe("Emlyn");
        (await reopened.DomainEvents.CountAsync(Ct)).ShouldBe(eventsBefore);
    }

    [Fact]
    public async Task Every_control_plane_migration_can_be_rolled_back_and_reapplied()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(
                new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = "betsi_control_rollback" }.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "control"))
            .Options;

        await using var context = new ControlPlaneDbContext(options);
        var migrator = context.GetInfrastructure().GetRequiredService<IMigrator>();
        var migrations = MigrationsOf(context);

        await context.Database.MigrateAsync(Ct);

        // The control plane holds the Data Protection key ring as well as the registry, so a
        // rollback that dropped the wrong table would make every stored integration secret
        // unreadable. Worth proving, not assuming.
        for (var i = migrations.Count - 1; i >= 1; i--)
        {
            await migrator.MigrateAsync(migrations[i - 1], Ct);
            await migrator.MigrateAsync(migrations[i], Ct);
        }

        (await context.Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_database_migrated_from_empty_matches_one_migrated_step_by_step()
    {
        Assert.SkipWhen(_skipReason is not null, _skipReason ?? string.Empty);

        // Two ways of arriving at the same schema: a fresh site provisioned today, and a site
        // that has been upgraded release by release. If they differ, one of them is running on a
        // schema nothing was tested against.
        await using var allAtOnce = TenantContextFor("betsi_all_at_once");
        await allAtOnce.Database.MigrateAsync(Ct);

        await using var stepByStep = TenantContextFor("betsi_step_by_step");
        var migrator = stepByStep.GetInfrastructure().GetRequiredService<IMigrator>();
        foreach (var migration in MigrationsOf(stepByStep))
            await migrator.MigrateAsync(migration, Ct);

        var first = await DescribeSchemaAsync("betsi_all_at_once");
        var second = await DescribeSchemaAsync("betsi_step_by_step");

        second.ShouldBe(first);
    }

    /// <summary>Every column of every table, as the database reports them.</summary>
    private async Task<List<string>> DescribeSchemaAsync(string database)
    {
        await using var connection = new SqlConnection(
            new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = database }.ConnectionString);

        await connection.OpenAsync(Ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(TABLE_SCHEMA, '.', TABLE_NAME, '.', COLUMN_NAME, ' ', DATA_TYPE, ' ',
                          COALESCE(CAST(CHARACTER_MAXIMUM_LENGTH AS nvarchar(20)), ''), ' ', IS_NULLABLE)
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME <> '__EFMigrationsHistory'
            ORDER BY TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
            """;

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            columns.Add(reader.GetString(0));

        return columns;
    }
}
