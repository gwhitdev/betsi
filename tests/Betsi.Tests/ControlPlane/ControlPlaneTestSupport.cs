namespace Betsi.Tests.ControlPlane;

using Betsi.ControlPlane;
using Betsi.LicenseTool;
using Betsi.Licensing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;

/// <summary>A signing key generated per test run, trusted by validators built here.</summary>
public static class TestLicenses
{
    public const string KeyId = "test-2026";

    private static readonly Lazy<(RSA Rsa, string PublicPem)> Key = new(() =>
    {
        var rsa = RSA.Create(2048);
        return (rsa, rsa.ExportSubjectPublicKeyInfoPem());
    });

    public static string PublicKeyPem => Key.Value.PublicPem;

    public static LicenseIssuer Issuer => new(Key.Value.Rsa, KeyId);

    public static LicensingOptions Options() => new()
    {
        TrustedKeys = [new TrustedLicenseKey { KeyId = KeyId, PublicKeyPem = PublicKeyPem }]
    };

    public static LicenseValidator Validator() => new(Options(), isDevelopment: false);

    public static string ValidFor(Guid tenantId, DateTimeOffset? now = null, params string[] features) =>
        Issuer.Issue(
            tenantId,
            features.Length == 0 ? [LicenseFeatures.Core] : features,
            (now ?? DateTimeOffset.UtcNow).AddYears(1),
            issuedAt: (now ?? DateTimeOffset.UtcNow).AddDays(-1));
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Stands in for applying real migrations, and can be told to fail.</summary>
public sealed class FakeSchemaMigrator : ITenantSchemaMigrator
{
    public string LatestMigration { get; set; } = "20260912132740_AddOutboxDeliveryTracking";

    public HashSet<string> FailingDatabases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(Guid TenantId, string ConnectionString)> Calls { get; } = [];

    public Task<string?> MigrateAsync(Guid tenantId, string connectionString, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, connectionString));

        if (FailingDatabases.Any(db => connectionString.Contains($"Initial Catalog={db}", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cannot open server: login failed.");

        return Task.FromResult<string?>(LatestMigration);
    }
}

/// <summary>
/// An in-memory control plane with a registry and operations service wired over it.
/// </summary>
public sealed class ControlPlaneHarness : IAsyncDisposable, IDbContextFactory<ControlPlaneDbContext>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<ControlPlaneDbContext> _options;

    public ControlPlaneHarness(DateTimeOffset? now = null, LicensingOptions? licensing = null)
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options;

        using (var context = CreateDbContext())
            context.Database.EnsureCreated();

        Time = new ManualTimeProvider(now ?? new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        Servers = new DatabaseServerCatalog(new TenancyOptions
        {
            DatabaseServers = new(StringComparer.OrdinalIgnoreCase) { ["default"] = "Server=sql.test;User Id=betsi;Password=x" }
        });
        Validator = new LicenseValidator(licensing ?? TestLicenses.Options(), isDevelopment: false);

        Registry = new TenantRegistry(this, Servers, Validator, Migrator, Time, NullLogger<TenantRegistry>.Instance);
        Operations = new TenantOperations(this, Servers, Migrator, Validator, Registry, Time, NullLogger<TenantOperations>.Instance);
    }

    public ManualTimeProvider Time { get; }
    public DatabaseServerCatalog Servers { get; }
    public LicenseValidator Validator { get; }
    public FakeSchemaMigrator Migrator { get; } = new();
    public TenantRegistry Registry { get; }
    public TenantOperations Operations { get; }

    public ControlPlaneDbContext CreateDbContext() => new(_options);

    public async Task<List<ControlPlaneAuditRecord>> AuditFor(Guid tenantId)
    {
        await using var context = CreateDbContext();
        return await context.AuditLog.Where(a => a.TenantId == tenantId).OrderBy(a => a.Id).ToListAsync();
    }

    public async Task<TenantRecord> RecordFor(Guid tenantId)
    {
        await using var context = CreateDbContext();
        return await context.Tenants.SingleAsync(t => t.TenantId == tenantId);
    }

    public async Task UpdateRecord(Guid tenantId, Action<TenantRecord> change)
    {
        await using var context = CreateDbContext();
        var record = await context.Tenants.SingleAsync(t => t.TenantId == tenantId);
        change(record);
        await context.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
