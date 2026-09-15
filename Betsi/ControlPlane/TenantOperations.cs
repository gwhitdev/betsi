namespace Betsi.ControlPlane;

using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.RegularExpressions;

/// <summary>Brings a tenant database up to the schema this build requires.</summary>
public interface ITenantSchemaMigrator
{
    /// <summary>The newest tenant migration this build contains.</summary>
    string LatestMigration { get; }

    /// <summary>Creates the database if needed, applies pending migrations, returns the latest applied.</summary>
    Task<string?> MigrateAsync(Guid tenantId, string connectionString, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITenantSchemaMigrator"/>
public sealed class SqlServerTenantSchemaMigrator : ITenantSchemaMigrator
{
    public SqlServerTenantSchemaMigrator()
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext([]);
        LatestMigration = context.GetInfrastructure()
            .GetRequiredService<IMigrationsAssembly>()
            .Migrations.Keys.Max(StringComparer.Ordinal)
            ?? throw new InvalidOperationException("This build contains no tenant migrations.");
    }

    public string LatestMigration { get; }

    public async Task<string?> MigrateAsync(
        Guid tenantId, string connectionString, CancellationToken cancellationToken)
    {
        var tenantContext = new TenantContext();
        tenantContext.Resolve(tenantId, Guid.Empty, "System");

        var options = new DbContextOptionsBuilder<BetsiDbContext>()
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                sql.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .Options;

        await using var context = new BetsiDbContext(options, tenantContext);
        await context.Database.MigrateAsync(cancellationToken);

        return (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault();
    }
}

public sealed record ProvisionTenantRequest(
    string Name, string DatabaseServer, string DatabaseName, Guid? TenantId = null, string? LicenseKey = null);

public sealed record TenantMigrationResult(Guid TenantId, string Name, bool Succeeded, string? SchemaVersion, string? Error);

/// <summary>
/// Operator actions on tenants (MVP-009): provision, suspend, resume, migrate, install licence.
/// </summary>
public interface ITenantOperations
{
    Task<TenantRecord> ProvisionAsync(ProvisionTenantRequest request, string actor, CancellationToken cancellationToken);
    Task SuspendAsync(Guid tenantId, string reason, string actor, CancellationToken cancellationToken);
    Task ResumeAsync(Guid tenantId, string reason, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantMigrationResult>> MigrateAsync(Guid? tenantId, string actor, CancellationToken cancellationToken);
    Task<LicenseEvaluation> InstallLicenseAsync(Guid tenantId, string licenseKey, string actor, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>Thrown when an operator action is refused. The message is written for the operator.</summary>
public sealed class TenantOperationException : Exception
{
    public TenantOperationException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <inheritdoc cref="ITenantOperations"/>
/// <remarks>
/// Every operation is idempotent and resumable: re-running a provision that failed halfway
/// continues from where it stopped, and re-running one that succeeded changes nothing. Every
/// attempt, successful or not, is written to the control-plane audit log.
/// </remarks>
public sealed partial class TenantOperations : ITenantOperations
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly DatabaseServerCatalog _servers;
    private readonly ITenantSchemaMigrator _migrator;
    private readonly LicenseValidator _licenseValidator;
    private readonly ITenantRegistry _registry;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantOperations> _logger;

    public TenantOperations(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        DatabaseServerCatalog servers,
        ITenantSchemaMigrator migrator,
        LicenseValidator licenseValidator,
        ITenantRegistry registry,
        TimeProvider time,
        ILogger<TenantOperations> logger)
    {
        _contextFactory = contextFactory;
        _servers = servers;
        _migrator = migrator;
        _licenseValidator = licenseValidator;
        _registry = registry;
        _time = time;
        _logger = logger;
    }

    // Starts with a letter so it is a valid unquoted SQL Server identifier; no punctuation,
    // so it cannot escape the identifier when a database is created from it.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial Regex DatabaseNamePattern();

    public async Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancellationToken);
    }

    public async Task<TenantRecord> ProvisionAsync(
        ProvisionTenantRequest request, string actor, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();

        if (name.Length is 0 or > 200)
            throw new TenantOperationException("A tenant name is required and must be at most 200 characters.");

        if (!DatabaseNamePattern().IsMatch(request.DatabaseName))
        {
            throw new TenantOperationException(
                $"'{request.DatabaseName}' is not a valid database name. Use letters, digits and " +
                "underscores, starting with a letter, at most 100 characters.");
        }

        if (!_servers.Contains(request.DatabaseServer))
        {
            throw new TenantOperationException(
                $"Database server profile '{request.DatabaseServer}' is not configured under Tenancy:DatabaseServers.");
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var now = _time.GetUtcNow();

        var existing = request.TenantId is { } requestedId
            ? await context.Tenants.SingleOrDefaultAsync(t => t.TenantId == requestedId, cancellationToken)
            : await context.Tenants.SingleOrDefaultAsync(t => t.Name == name, cancellationToken);

        var tenant = existing ?? await RegisterAsync(context, request, name, actor, now, cancellationToken);

        if (existing is not null)
        {
            if (!string.Equals(existing.DatabaseServer, request.DatabaseServer, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.DatabaseName, request.DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                throw new TenantOperationException(
                    $"Tenant '{existing.Name}' ({existing.TenantId}) is already registered against " +
                    $"{existing.DatabaseServer}/{existing.DatabaseName}. Moving a tenant's database is not a provisioning operation.");
            }

            if (existing.State == TenantState.Suspended)
            {
                throw new TenantOperationException(
                    $"Tenant '{existing.Name}' is suspended. Resume it rather than re-provisioning.");
            }

            if (existing.State == TenantState.Active)
            {
                await AuditAsync(context, existing.TenantId, "ProvisionTenant", actor, "NoChange",
                    "Tenant is already provisioned.", cancellationToken);
                return existing;
            }
        }

        try
        {
            if (request.LicenseKey is not null)
                ApplyLicense(context, tenant, request.LicenseKey, actor, now);

            await MigrateTenantAsync(context, tenant, actor, "ProvisionTenant", cancellationToken);
        }
        finally
        {
            // Refreshed on failure too, so this instance reports the tenant as failed rather
            // than not knowing it exists.
            await _registry.RefreshAsync(cancellationToken);
        }

        return tenant;
    }

    private async Task<TenantRecord> RegisterAsync(
        ControlPlaneDbContext context, ProvisionTenantRequest request, string name, string actor,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Checked explicitly for a clear message; the unique index is the real guarantee.
        var clash = await context.Tenants.AnyAsync(
            t => t.DatabaseServer == request.DatabaseServer && t.DatabaseName == request.DatabaseName,
            cancellationToken);

        if (clash)
        {
            throw new TenantOperationException(
                $"Database {request.DatabaseServer}/{request.DatabaseName} already belongs to another tenant. " +
                "Each tenant must have its own database (ADR-001).");
        }

        if (await context.Tenants.AnyAsync(t => t.Name == name, cancellationToken))
            throw new TenantOperationException($"A tenant named '{name}' already exists.");

        var tenant = new TenantRecord
        {
            TenantId = request.TenantId ?? Guid.NewGuid(),
            Name = name,
            State = TenantState.Provisioning,
            DatabaseServer = request.DatabaseServer,
            DatabaseName = request.DatabaseName,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime
        };

        context.Tenants.Add(tenant);
        context.AuditLog.Add(ControlPlaneAudit.Entry(
            tenant.TenantId, "RegisterTenant", actor, "Success",
            $"Registered against {tenant.DatabaseServer}/{tenant.DatabaseName}.", now));

        // Committed before the database is touched, so a crash mid-provision leaves a
        // Provisioning record that the next run can find and resume.
        await context.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    public async Task<IReadOnlyList<TenantMigrationResult>> MigrateAsync(
        Guid? tenantId, string actor, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var tenants = await context.Tenants
            .Where(t => tenantId == null || t.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        if (tenantId is not null && tenants.Count == 0)
            throw new TenantOperationException($"Tenant '{tenantId}' is not registered.");

        var results = new List<TenantMigrationResult>();

        // Sequential and continuing past failures: one tenant's unreachable database must not
        // leave every other tenant on an old schema.
        foreach (var tenant in tenants)
        {
            try
            {
                await MigrateTenantAsync(context, tenant, actor, "MigrateTenant", cancellationToken);
                results.Add(new TenantMigrationResult(tenant.TenantId, tenant.Name, true, tenant.SchemaVersion, null));
            }
            catch (TenantOperationException exception)
            {
                results.Add(new TenantMigrationResult(
                    tenant.TenantId, tenant.Name, false, tenant.SchemaVersion, exception.Message));
            }
        }

        await _registry.RefreshAsync(cancellationToken);
        return results;
    }

    private async Task MigrateTenantAsync(
        ControlPlaneDbContext context, TenantRecord tenant, string actor, string action,
        CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = _servers.ConnectionStringFor(tenant.DatabaseServer, tenant.DatabaseName);

            _logger.LogInformation("Migrating tenant {TenantId} ({TenantName})", tenant.TenantId, tenant.Name);
            tenant.SchemaVersion = await _migrator.MigrateAsync(tenant.TenantId, connectionString, cancellationToken);

            // Completing a migration completes provisioning. A suspended tenant is kept
            // current so it can be resumed, but stays suspended.
            if (tenant.State is TenantState.Provisioning or TenantState.Failed)
            {
                tenant.State = TenantState.Active;
                tenant.StateReason = null;
            }

            tenant.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            await AuditAsync(context, tenant.TenantId, action, actor, "Success",
                $"Schema at {tenant.SchemaVersion}.", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TenantOperationException)
        {
            _logger.LogError(exception, "Migration failed for tenant {TenantId}", tenant.TenantId);

            // An active tenant stays active: its existing schema may still serve the running
            // build, and the registry independently refuses it if this build needs the newer
            // schema. A tenant that was never active is marked failed so the reason is visible.
            var reason = $"{exception.GetType().Name}: {exception.Message}";
            if (tenant.State is TenantState.Provisioning or TenantState.Failed)
            {
                tenant.State = TenantState.Failed;
                tenant.StateReason = reason.Length > 1000 ? reason[..1000] : reason;
            }

            tenant.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            await AuditAsync(context, tenant.TenantId, action, actor, "Failure", reason, cancellationToken);

            throw new TenantOperationException(
                $"Could not migrate tenant '{tenant.Name}': {exception.Message}", exception);
        }
    }

    public Task SuspendAsync(Guid tenantId, string reason, string actor, CancellationToken cancellationToken) =>
        ChangeStateAsync(tenantId, reason, actor, "SuspendTenant",
            from: [TenantState.Active, TenantState.Failed], to: TenantState.Suspended, cancellationToken);

    public Task ResumeAsync(Guid tenantId, string reason, string actor, CancellationToken cancellationToken) =>
        ChangeStateAsync(tenantId, reason, actor, "ResumeTenant",
            from: [TenantState.Suspended], to: TenantState.Active, cancellationToken);

    private async Task ChangeStateAsync(
        Guid tenantId, string reason, string actor, string action,
        TenantState[] from, TenantState to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new TenantOperationException("A reason is required. It is recorded in the audit log.");

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, tenantId, cancellationToken);

        if (tenant.State == to)
        {
            await AuditAsync(context, tenantId, action, actor, "NoChange", reason, cancellationToken);
            return;
        }

        if (!from.Contains(tenant.State))
        {
            await AuditAsync(context, tenantId, action, actor, "Refused",
                $"Tenant is {tenant.State}. {reason}", cancellationToken);
            throw new TenantOperationException($"Tenant '{tenant.Name}' is {tenant.State} and cannot be moved to {to}.");
        }

        tenant.State = to;
        tenant.StateReason = to == TenantState.Active ? null : reason;
        tenant.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await AuditAsync(context, tenantId, action, actor, "Success", reason, cancellationToken);
        await _registry.RefreshAsync(cancellationToken);
    }

    public async Task<LicenseEvaluation> InstallLicenseAsync(
        Guid tenantId, string licenseKey, string actor, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var tenant = await FindAsync(context, tenantId, cancellationToken);

        var evaluation = ApplyLicense(context, tenant, licenseKey, actor, _time.GetUtcNow());

        await context.SaveChangesAsync(cancellationToken);
        await _registry.RefreshAsync(cancellationToken);
        return evaluation;
    }

    private LicenseEvaluation ApplyLicense(
        ControlPlaneDbContext context, TenantRecord tenant, string licenseKey, string actor, DateTimeOffset now)
    {
        var evaluation = _licenseValidator.Validate(
            licenseKey, tenant.TenantId, now,
            tenant.LicenseHighWaterMark is { } mark ? new DateTimeOffset(mark, TimeSpan.Zero) : null);

        // Only a licence that is in force now may replace the current one. Installing a
        // not-yet-valid renewal early would switch the tenant into restricted mode until it starts.
        if (evaluation.Mode != LicenseMode.Full)
        {
            context.AuditLog.Add(ControlPlaneAudit.Entry(
                tenant.TenantId, "InstallLicense", actor, "Refused", $"Licence status {evaluation.Status}.", now));
            context.SaveChanges();

            throw new TenantOperationException(
                $"Licence not installed for '{tenant.Name}': status {evaluation.Status}. The existing licence is unchanged.");
        }

        tenant.LicenseKey = licenseKey.Trim();
        tenant.UpdatedAt = now.UtcDateTime;

        context.AuditLog.Add(ControlPlaneAudit.Entry(
            tenant.TenantId, "InstallLicense", actor, "Success",
            $"Licence {evaluation.LicenseId} ({evaluation.Status}); features {string.Join(",", evaluation.Features)}; " +
            $"expires {evaluation.ExpiresAt:O}.", now));

        return evaluation;
    }

    private static async Task<TenantRecord> FindAsync(
        ControlPlaneDbContext context, Guid tenantId, CancellationToken cancellationToken) =>
        await context.Tenants.SingleOrDefaultAsync(t => t.TenantId == tenantId, cancellationToken)
        ?? throw new TenantOperationException($"Tenant '{tenantId}' is not registered.");

    private async Task AuditAsync(
        ControlPlaneDbContext context, Guid tenantId, string action, string actor, string outcome,
        string? detail, CancellationToken cancellationToken)
    {
        context.AuditLog.Add(ControlPlaneAudit.Entry(tenantId, action, actor, outcome, detail, _time.GetUtcNow()));
        await context.SaveChangesAsync(cancellationToken);
    }
}
