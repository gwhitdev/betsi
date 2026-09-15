namespace Betsi.Application.Behaviours;

using Betsi.Infrastructure.Tenancy;
using MediatR;
using System.Diagnostics;

/// <summary>
/// Logs the name, tenant, actor and duration of every command.
/// </summary>
public sealed class LoggingBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehaviour<TRequest, TResponse>> _logger;
    private readonly ITenantContext _tenantContext;

    public LoggingBehaviour(
        ILogger<LoggingBehaviour<TRequest, TResponse>> logger, ITenantContext tenantContext)
    {
        _logger = logger;
        _tenantContext = tenantContext;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var stopwatch = Stopwatch.StartNew();

        // Patient-identifying fields are never logged: the request name, tenant and actor are
        // enough to trace a call, and structured logs are not held to the same standard as
        // the clinical record.
        _logger.LogInformation(
            "Handling {RequestName} for tenant {TenantId} by actor {ActorId} ({ActorRole})",
            requestName,
            _tenantContext.IsResolved ? _tenantContext.TenantId : Guid.Empty,
            _tenantContext.IsResolved ? _tenantContext.ActorId : Guid.Empty,
            _tenantContext.IsResolved ? _tenantContext.ActorRole : "Unresolved");

        try
        {
            var response = await next();

            _logger.LogInformation(
                "Handled {RequestName} in {ElapsedMilliseconds}ms",
                requestName, stopwatch.ElapsedMilliseconds);

            return response;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "{RequestName} failed after {ElapsedMilliseconds}ms: {ExceptionType}",
                requestName, stopwatch.ElapsedMilliseconds, exception.GetType().Name);

            throw;
        }
    }
}
