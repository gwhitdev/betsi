namespace Betsi.Infrastructure.Outbox;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Polls every tenant's outbox on a fixed interval.
/// </summary>
/// <remarks>
/// Each tenant is drained in its own scope, so one tenant's unreachable database cannot stop
/// the others from draining.
/// </remarks>
public sealed class OutboxBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ITenantRegistry _registry;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxBackgroundService> _logger;

    public OutboxBackgroundService(
        IServiceProvider services,
        ITenantRegistry registry,
        OutboxOptions options,
        ILogger<OutboxBackgroundService> logger)
    {
        _services = services;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Outbox processor started; polling every {PollInterval}", _options.PollInterval);

        using var timer = new PeriodicTimer(_options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Suspended and not-ready tenants are skipped: their databases may be unreachable or
            // on a schema this build cannot write to.
            foreach (var tenant in _registry.Available)
                await DrainTenantAsync(tenant.TenantId, stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Outbox processor stopped");
    }

    private async Task DrainTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _services.CreateAsyncScope();

            scope.ServiceProvider.GetRequiredService<TenantContext>()
                .ResolveSystem(tenantId);

            var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
            var result = await processor.DrainAsync(tenantId, cancellationToken);

            if (result.Total > 0)
            {
                _logger.LogInformation(
                    "Drained outbox for tenant {TenantId}: {Published} published, " +
                    "{Failed} retrying, {DeadLettered} dead-lettered",
                    tenantId, result.Published, result.Failed, result.DeadLettered);
            }
        }
        catch (Exception exception)
        {
            // A failure here is the whole tenant being unreachable. Log and carry on; the
            // next tick retries, and the other tenants are unaffected.
            _logger.LogError(
                exception, "Failed to drain the outbox for tenant {TenantId}", tenantId);
        }
    }
}
