namespace Betsi.ControlPlane;

using Betsi.Licensing;
using Microsoft.EntityFrameworkCore;

/// <summary>Whether a tenant can serve requests right now.</summary>
public enum TenantAvailability
{
    Available = 1,
    /// <summary>An operator has suspended the tenant.</summary>
    Suspended = 2,
    /// <summary>Provisioning, failed, misconfigured or on an older schema than this build.</summary>
    NotReady = 3
}

/// <summary>A tenant as this instance currently understands it.</summary>
public sealed record TenantDescriptor
{
    public required Guid TenantId { get; init; }
    public required string Name { get; init; }
    public required TenantState State { get; init; }
    public required TenantAvailability Availability { get; init; }

    /// <summary>Why the tenant is not available, for operators. Never shown to API callers.</summary>
    public string? UnavailableReason { get; init; }

    public required string DatabaseServer { get; init; }
    public required string DatabaseName { get; init; }
    public string? ConnectionString { get; init; }
    public string? SchemaVersion { get; init; }
    public required LicenseEvaluation License { get; init; }
}

/// <summary>
/// The trusted source of which tenants exist and which database each uses (spec §3.3).
/// </summary>
public interface ITenantRegistry
{
    bool TryGet(Guid tenantId, out TenantDescriptor tenant);

    /// <summary>The connection for an available tenant. Throws for any other.</summary>
    string ResolveConnectionString(Guid tenantId);

    IReadOnlyCollection<TenantDescriptor> All { get; }

    /// <summary>Tenants that can serve requests: the set background work should iterate.</summary>
    IReadOnlyCollection<TenantDescriptor> Available { get; }

    DateTimeOffset? LastRefreshedAt { get; }

    Task RefreshAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITenantRegistry"/>
/// <remarks>
/// Requests are served from an in-memory snapshot, refreshed on a timer, so the request path
/// never waits on the control-plane database and a control-plane outage does not take down
/// tenants that were already serving. The cost is that a suspension takes up to one refresh
/// interval to reach every instance.
///
/// Until the first successful refresh the snapshot is empty, so every tenant is unknown:
/// the registry fails closed.
/// </remarks>
public sealed class TenantRegistry : ITenantRegistry
{
    // High-water marks are persisted at most this often, to avoid a control-plane write on
    // every refresh of every instance.
    private static readonly TimeSpan HighWaterMarkWriteInterval = TimeSpan.FromHours(1);

    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly DatabaseServerCatalog _servers;
    private readonly LicenseValidator _licenseValidator;
    private readonly ITenantSchemaMigrator _schema;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantRegistry> _logger;

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public TenantRegistry(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        DatabaseServerCatalog servers,
        LicenseValidator licenseValidator,
        ITenantSchemaMigrator schema,
        TimeProvider time,
        ILogger<TenantRegistry> logger)
    {
        _contextFactory = contextFactory;
        _servers = servers;
        _licenseValidator = licenseValidator;
        _schema = schema;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyCollection<TenantDescriptor> All => _snapshot.Tenants.Values.ToArray();

    public IReadOnlyCollection<TenantDescriptor> Available =>
        _snapshot.Tenants.Values.Where(t => t.Availability == TenantAvailability.Available).ToArray();

    public DateTimeOffset? LastRefreshedAt => _snapshot.RefreshedAt;

    public bool TryGet(Guid tenantId, out TenantDescriptor tenant) =>
        _snapshot.Tenants.TryGetValue(tenantId, out tenant!);

    public string ResolveConnectionString(Guid tenantId)
    {
        if (!TryGet(tenantId, out var tenant))
            throw new UnknownTenantException(tenantId);

        return tenant.Availability == TenantAvailability.Available
            ? tenant.ConnectionString!
            : throw new TenantUnavailableException(tenant);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var records = await context.Tenants.AsNoTracking().ToListAsync(cancellationToken);
        var now = _time.GetUtcNow();
        var previous = _snapshot;

        // A destroyed tenant is left out of the snapshot entirely, so a request naming it is
        // answered exactly as one naming a tenant that never existed: 404, not a 503 that
        // invites the caller to try again later. The control-plane record still holds it, which
        // is where `tenants list` reads from.
        var descriptors = records
            .Where(r => r.State != TenantState.Destroyed)
            .ToDictionary(r => r.TenantId, r => Describe(r, now));

        _snapshot = new Snapshot(descriptors, now);

        await RecordLicenseChangesAsync(context, records, descriptors, previous, now, cancellationToken);
    }

    private TenantDescriptor Describe(TenantRecord record, DateTimeOffset now)
    {
        var license = _licenseValidator.Validate(
            record.LicenseKey, record.TenantId, now, AsUtc(record.LicenseHighWaterMark));

        string? connectionString = null;
        string? notReady = null;

        if (_servers.Contains(record.DatabaseServer))
            connectionString = _servers.ConnectionStringFor(record.DatabaseServer, record.DatabaseName);
        else
            notReady = $"Database server profile '{record.DatabaseServer}' is not configured on this instance.";

        notReady ??= record.State switch
        {
            TenantState.Provisioning => "Provisioning has not completed.",
            TenantState.Failed => $"Provisioning or migration failed: {record.StateReason}",
            _ => null
        };

        // Migration ids are timestamp-prefixed, so ordinal comparison is chronological. A
        // database ahead of this build is allowed: expand/contract migrations keep the
        // previous application version working during a rolling deployment.
        if (notReady is null && record.State == TenantState.Active &&
            (record.SchemaVersion is null ||
             string.CompareOrdinal(record.SchemaVersion, _schema.LatestMigration) < 0))
        {
            notReady = $"Database schema '{record.SchemaVersion ?? "none"}' is older than this " +
                       $"build requires ('{_schema.LatestMigration}'). Run 'tenants migrate'.";
        }

        var availability = record.State == TenantState.Suspended
            ? TenantAvailability.Suspended
            : notReady is null ? TenantAvailability.Available : TenantAvailability.NotReady;

        return new TenantDescriptor
        {
            TenantId = record.TenantId,
            Name = record.Name,
            State = record.State,
            Availability = availability,
            UnavailableReason = availability == TenantAvailability.Suspended ? record.StateReason : notReady,
            DatabaseServer = record.DatabaseServer,
            DatabaseName = record.DatabaseName,
            ConnectionString = connectionString,
            SchemaVersion = record.SchemaVersion,
            License = license
        };
    }

    private async Task RecordLicenseChangesAsync(
        ControlPlaneDbContext context,
        List<TenantRecord> records,
        Dictionary<Guid, TenantDescriptor> descriptors,
        Snapshot previous,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var record in records)
        {
            // A destroyed tenant has no descriptor — it is deliberately absent from the
            // snapshot — and no licence left to evaluate.
            if (!descriptors.TryGetValue(record.TenantId, out var descriptor))
                continue;

            var license = descriptor.License;
            var status = license.Status.ToString();

            // Every evaluation is logged; only changes are audited. Auditing each refresh would
            // bury the transitions an investigation needs under thousands of identical rows.
            _logger.LogDebug(
                "Licence for tenant {TenantId} evaluated as {LicenseStatus}", record.TenantId, status);

            if (record.RecordedLicenseStatus != status || record.RecordedLicenseId != license.LicenseId)
            {
                // Compare-and-set against what this instance read, so when several instances
                // observe the same change at once exactly one of them audits it.
                var claimed = await context.Tenants
                    .Where(t => t.TenantId == record.TenantId &&
                                t.RecordedLicenseStatus == record.RecordedLicenseStatus &&
                                t.RecordedLicenseId == record.RecordedLicenseId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.RecordedLicenseStatus, status)
                        .SetProperty(t => t.RecordedLicenseId, license.LicenseId), cancellationToken);

                if (claimed == 1)
                {
                    context.AuditLog.Add(ControlPlaneAudit.Entry(
                        record.TenantId, "LicenseEvaluated", "system:registry",
                        license.Mode == LicenseMode.Full ? "Success" : "Restricted",
                        $"Status {status}; licence {license.LicenseId?.ToString() ?? "none"}; " +
                        $"expires {license.ExpiresAt:O}.", now));

                    if (license.Mode == LicenseMode.Restricted)
                    {
                        _logger.LogWarning(
                            "Tenant {TenantId} is in restricted mode: licence status {LicenseStatus}",
                            record.TenantId, status);
                    }
                }
            }

            // Audited on each instance that first notices it: a wound-back clock is an alarm,
            // and repeating it per process is preferable to one instance's audit hiding it.
            var previouslyDetected = previous.Tenants.TryGetValue(record.TenantId, out var before) &&
                                     before.License.ClockRollbackDetected;

            if (license.ClockRollbackDetected && !previouslyDetected)
            {
                context.AuditLog.Add(ControlPlaneAudit.Entry(
                    record.TenantId, "LicenseClockRollbackDetected", "system:registry", "Warning",
                    $"System clock {now:O} is behind the licence high-water mark " +
                    $"{record.LicenseHighWaterMark:O}. Evaluating against the high-water mark.", now));

                _logger.LogWarning(
                    "Clock rollback detected while evaluating tenant {TenantId}'s licence", record.TenantId);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        // Monotonic, and atomic in the database: a concurrent instance can only move the mark
        // further forward, never back.
        var nowUtc = now.UtcDateTime;
        var threshold = nowUtc - HighWaterMarkWriteInterval;
        var stale = records
            .Where(r => r.LicenseKey is not null && (r.LicenseHighWaterMark is null || r.LicenseHighWaterMark < threshold))
            .Select(r => r.TenantId)
            .ToArray();

        if (stale.Length > 0)
        {
            await context.Tenants
                .Where(t => stale.Contains(t.TenantId) &&
                            (t.LicenseHighWaterMark == null || t.LicenseHighWaterMark < nowUtc))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LicenseHighWaterMark, nowUtc), cancellationToken);
        }
    }

    private static DateTimeOffset? AsUtc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private sealed record Snapshot(IReadOnlyDictionary<Guid, TenantDescriptor> Tenants, DateTimeOffset? RefreshedAt)
    {
        public static readonly Snapshot Empty = new(new Dictionary<Guid, TenantDescriptor>(), null);
    }
}

/// <summary>Reloads the registry on a timer so every instance converges on control-plane state.</summary>
public sealed class TenantRegistryRefreshService : BackgroundService
{
    private readonly ITenantRegistry _registry;
    private readonly ControlPlaneOptions _options;
    private readonly ILogger<TenantRegistryRefreshService> _logger;

    public TenantRegistryRefreshService(
        ITenantRegistry registry, ControlPlaneOptions options, ILogger<TenantRegistryRefreshService> logger)
    {
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.RegistryRefreshInterval);

        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await _registry.RefreshAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Keep serving the last good snapshot. Dropping every tenant because the
                // control plane blipped would turn a control-plane incident into a clinical one.
                _logger.LogError(
                    exception,
                    "Tenant registry refresh failed; continuing with the snapshot from {LastRefreshedAt}",
                    _registry.LastRefreshedAt);
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Thrown when a request names a tenant this instance does not serve.</summary>
public sealed class UnknownTenantException : Exception
{
    public UnknownTenantException(Guid tenantId)
        : base($"Tenant '{tenantId}' is not registered on this instance.")
    {
        TenantId = tenantId;
    }

    public Guid TenantId { get; }
}

/// <summary>Thrown when a registered tenant cannot currently serve requests.</summary>
public sealed class TenantUnavailableException : Exception
{
    public TenantUnavailableException(TenantDescriptor tenant)
        : base($"Tenant '{tenant.TenantId}' is {tenant.Availability}: {tenant.UnavailableReason}")
    {
        Tenant = tenant;
    }

    public TenantDescriptor Tenant { get; }
}

internal static class ControlPlaneAudit
{
    public static ControlPlaneAuditRecord Entry(
        Guid? tenantId, string action, string actor, string outcome, string? detail, DateTimeOffset at) => new()
    {
        TenantId = tenantId,
        Action = action,
        Actor = actor,
        Outcome = outcome,
        Detail = detail is { Length: > 2000 } ? detail[..2000] : detail,
        OccurredAt = at.UtcDateTime
    };
}
