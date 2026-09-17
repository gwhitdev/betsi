namespace Betsi.ControlPlane;

using Microsoft.EntityFrameworkCore;

/// <summary>Brings the control plane and tenant registry up before the application serves requests.</summary>
public interface IPlatformStartup
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPlatformStartup"/>
/// <remarks>
/// Unlike the single-database initialiser it replaces, a tenant that cannot be migrated does
/// not stop the application: under database-per-tenant, one hospital's unreachable database
/// must not take every other hospital offline. That tenant is refused (503) until an operator
/// fixes it, and the reason is in the control-plane audit log.
/// </remarks>
public sealed class PlatformStartup : IPlatformStartup
{
    private readonly IDbContextFactory<ControlPlaneDbContext> _contextFactory;
    private readonly ITenantOperations _operations;
    private readonly ITenantRegistry _registry;
    private readonly ControlPlaneOptions _controlPlaneOptions;
    private readonly TenancyOptions _tenancyOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<PlatformStartup> _logger;

    public PlatformStartup(
        IDbContextFactory<ControlPlaneDbContext> contextFactory,
        ITenantOperations operations,
        ITenantRegistry registry,
        ControlPlaneOptions controlPlaneOptions,
        TenancyOptions tenancyOptions,
        IHostEnvironment environment,
        ILogger<PlatformStartup> logger)
    {
        _contextFactory = contextFactory;
        _operations = operations;
        _registry = registry;
        _controlPlaneOptions = controlPlaneOptions;
        _tenancyOptions = tenancyOptions;
        _environment = environment;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_controlPlaneOptions.MigrateOnStartup)
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            _logger.LogInformation("Applying control-plane migrations");
            await context.Database.MigrateAsync(cancellationToken);
        }

        await SeedDevelopmentTenantsAsync(cancellationToken);

        await _registry.RefreshAsync(cancellationToken);

        if (_tenancyOptions.MigrateTenantsOnStartup)
        {
            foreach (var result in await _operations.MigrateAsync(null, "system:startup", cancellationToken))
            {
                if (!result.Succeeded)
                    _logger.LogError("Tenant {TenantName} could not be migrated: {Error}", result.Name, result.Error);
            }
        }

        foreach (var tenant in _registry.All)
        {
            if (tenant.Availability != TenantAvailability.Available)
            {
                _logger.LogWarning(
                    "Tenant {TenantName} ({TenantId}) will not serve requests: {Availability} — {Reason}",
                    tenant.Name, tenant.TenantId, tenant.Availability, tenant.UnavailableReason);
            }
        }

        _logger.LogInformation(
            "Tenant registry loaded: {Available} of {Total} tenants available",
            _registry.Available.Count, _registry.All.Count);
    }

    private async Task SeedDevelopmentTenantsAsync(CancellationToken cancellationToken)
    {
        if (_tenancyOptions.SeedTenants.Count == 0)
            return;

        // Seeding creates databases from configuration. Outside Development, tenants are
        // provisioned deliberately by an operator, with an audit trail naming who did it.
        if (!_environment.IsDevelopment())
        {
            _logger.LogWarning(
                "Tenancy:SeedTenants is configured but ignored outside Development. Use 'tenants provision'.");
            return;
        }

        foreach (var seed in _tenancyOptions.SeedTenants)
        {
            try
            {
                var key = seed.LicenseFile is null
                    ? null
                    : (await File.ReadAllTextAsync(
                        Path.Combine(_environment.ContentRootPath, seed.LicenseFile), cancellationToken)).Trim();

                // The licence goes in with the provisioning request so a new tenant is never
                // briefly unlicensed; an existing tenant gets it only if it has changed.
                var tenant = await _operations.ProvisionAsync(
                    new ProvisionTenantRequest(seed.Name, seed.DatabaseServer, seed.DatabaseName, seed.TenantId, key),
                    "system:development-seed",
                    cancellationToken);

                if (key is not null && tenant.LicenseKey != key)
                    await _operations.InstallLicenseAsync(tenant.TenantId, key, "system:development-seed", cancellationToken);
            }
            catch (TenantOperationException exception)
            {
                _logger.LogError("Could not seed development tenant {TenantName}: {Error}", seed.Name, exception.Message);
            }
        }
    }
}
