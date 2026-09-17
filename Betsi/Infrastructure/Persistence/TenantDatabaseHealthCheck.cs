namespace Betsi.Infrastructure.Persistence;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Reports unhealthy if the registry has never loaded or an available tenant's database
/// cannot be reached; degraded if any registered tenant is not serving.
/// </summary>
/// <remarks>
/// Tenant names, not reasons, appear in the result: reasons can include server names and
/// the health endpoint is unauthenticated.
/// </remarks>
public sealed class TenantDatabaseHealthCheck : IHealthCheck
{
    private readonly ITenantRegistry _registry;
    private readonly IServiceProvider _services;

    public TenantDatabaseHealthCheck(ITenantRegistry registry, IServiceProvider services)
    {
        _registry = registry;
        _services = services;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_registry.LastRefreshedAt is null)
            return HealthCheckResult.Unhealthy("The tenant registry has not loaded.");

        var unreachable = new List<string>();

        foreach (var tenant in _registry.Available)
        {
            await using var scope = _services.CreateAsyncScope();

            scope.ServiceProvider.GetRequiredService<TenantContext>()
                .Resolve(tenant.TenantId, Guid.Empty, "System");

            try
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<BetsiDbContext>();

                if (!await dbContext.Database.CanConnectAsync(cancellationToken))
                    unreachable.Add(tenant.Name);
            }
            catch (Exception)
            {
                unreachable.Add(tenant.Name);
            }
        }

        if (unreachable.Count > 0)
            return HealthCheckResult.Unhealthy($"Unreachable tenant databases: {string.Join(", ", unreachable)}.");

        var notServing = _registry.All.Where(t => t.Availability != TenantAvailability.Available).ToArray();

        return notServing.Length == 0
            ? HealthCheckResult.Healthy($"All {_registry.All.Count} tenants available.")
            : HealthCheckResult.Degraded(
                $"{_registry.Available.Count} of {_registry.All.Count} tenants available. " +
                $"Not serving: {string.Join(", ", notServing.Select(t => $"{t.Name} ({t.Availability})"))}.");
    }
}
