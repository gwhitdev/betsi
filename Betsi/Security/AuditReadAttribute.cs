namespace Betsi.Security;

using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc.Filters;

/// <summary>
/// Records a successful read of patient data in the tenant's audit log (spec §6: every read of
/// PHI is audited). Only who read what and when is recorded — never the data itself.
/// </summary>
/// <param name="resource">What was read, e.g. "EscalationBoard".</param>
/// <param name="routeKey">Route value holding the id of the record read, if there is one.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditReadAttribute(string resource, string? routeKey = null) : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        var status = executed.HttpContext.Response.StatusCode;
        if (executed.Result is Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult { StatusCode: { } resultStatus })
            status = resultStatus;
        if (status >= 400)
            return;

        var services = context.HttpContext.RequestServices;
        var tenantContext = services.GetRequiredService<ITenantContext>();
        if (!tenantContext.IsResolved)
            return;

        var affected = routeKey is not null && Guid.TryParse(context.RouteData.Values[routeKey]?.ToString(), out var id)
            ? id
            : Guid.Empty;

        try
        {
            var db = services.GetRequiredService<BetsiDbContext>();
            db.AuditLogs.Add(new AuditLogRecord
            {
                TenantId = tenantContext.TenantId,
                Action = $"Read:{resource}",
                ActorId = tenantContext.ActorId,
                ActorRole = tenantContext.ActorRole,
                AffectedAggregateId = affected,
                AffectedAggregateType = resource,
                Outcome = "Read",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(context.HttpContext.RequestAborted);
        }
        catch (Exception exception)
        {
            services.GetRequiredService<ILogger<AuditReadAttribute>>()
                .LogError(exception, "Could not audit a read of {Resource} for tenant {TenantId}", resource, tenantContext.TenantId);
        }
    }
}
