namespace Betsi.Application.Behaviours;

using Betsi.Infrastructure.Observability;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using System.Diagnostics;

/// <summary>
/// Counts and times every command, and opens a trace span for it (MVP-109).
/// </summary>
/// <remarks>
/// Outermost in the pipeline, so the duration recorded is the one a caller experiences,
/// including validation, authorisation and the licence check. Tags carry the command name,
/// tenant and outcome — never a patient identifier, which would put clinical data into a
/// metrics store that has no audit trail.
/// </remarks>
public sealed class MetricsBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly BetsiMetrics _metrics;
    private readonly ITenantContext _tenantContext;

    public MetricsBehaviour(BetsiMetrics metrics, ITenantContext tenantContext)
    {
        _metrics = metrics;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TRequest).Name;
        var tenantId = _tenantContext.IsResolved ? _tenantContext.TenantId : Guid.Empty;

        using var activity = MediatRTracing.Source.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("betsi.tenant_id", tenantId);
        activity?.SetTag("betsi.command", name);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next();
            _metrics.CommandHandled(name, tenantId, succeeded: true, stopwatch.Elapsed.TotalMilliseconds);
            return response;
        }
        catch (Exception exception)
        {
            _metrics.CommandHandled(name, tenantId, succeeded: false, stopwatch.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }
}
