namespace Betsi.Infrastructure.Tenancy;

using Betsi.ControlPlane;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

/// <summary>
/// Establishes the tenant and actor for each request.
/// </summary>
/// <remarks>
/// Fails closed. A request that does not name a tenant this instance serves is rejected
/// before it can reach a handler, rather than being allowed through with an empty tenant.
///
/// Tenant and actor are read from claims when the request is authenticated, falling back to
/// headers otherwise. The header path exists so the API is usable before Phase G adds
/// OAuth2/OIDC, and <b>must be disabled in any environment holding real patient data</b> —
/// see <see cref="TenantResolutionOptions.AllowHeaderFallback"/>.
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    public const string TenantHeaderName = "X-Betsi-Tenant";
    public const string ActorHeaderName = "X-Betsi-Actor";
    public const string ActorRoleHeaderName = "X-Betsi-Actor-Role";

    public const string TenantClaimType = "betsi:tenant_id";
    public const string ActorRoleClaimType = ClaimTypes.Role;

    private readonly RequestDelegate _next;
    private readonly TenantResolutionOptions _options;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        TenantResolutionOptions options,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        TenantContext tenantContext,
        ITenantRegistry registry)
    {
        if (IsExempt(httpContext.Request.Path))
        {
            await _next(httpContext);
            return;
        }

        if (!TryReadTenantId(httpContext, out var tenantId))
        {
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status400BadRequest,
                "Tenant not specified",
                $"The request did not carry a tenant. Supply a '{TenantClaimType}' claim" +
                (_options.AllowHeaderFallback ? $" or a '{TenantHeaderName}' header." : "."));
            return;
        }

        // Checked here so an unknown or unavailable tenant is rejected at the edge rather than
        // surfacing later as a DbContext construction failure. The reason a tenant is
        // unavailable is logged for operators but not returned: it can name database servers.
        if (!registry.TryGet(tenantId, out var tenant))
        {
            _logger.LogWarning("Rejected request for unregistered tenant {TenantId}", tenantId);

            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status404NotFound,
                "Unknown tenant",
                "The requested tenant is not served by this instance.");
            return;
        }

        if (tenant.Availability == TenantAvailability.Suspended)
        {
            _logger.LogWarning("Rejected request for suspended tenant {TenantId}", tenantId);

            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status403Forbidden,
                "Tenant suspended",
                "This tenant has been suspended. Contact your system administrator.");
            return;
        }

        if (tenant.Availability == TenantAvailability.NotReady)
        {
            _logger.LogError(
                "Rejected request for tenant {TenantId}, which is not ready: {Reason}",
                tenantId, tenant.UnavailableReason);

            httpContext.Response.Headers.RetryAfter = "60";
            await WriteProblemAsync(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                "Tenant unavailable",
                "This tenant is temporarily unavailable. Try again shortly.");
            return;
        }

        var (actorId, actorRole) = ReadActor(httpContext);
        tenantContext.Resolve(tenantId, actorId, actorRole);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = tenantId,
            ["ActorId"] = actorId,
            ["ActorRole"] = actorRole
        }))
        {
            await _next(httpContext);
        }
    }

    private bool IsExempt(PathString path) =>
        _options.ExemptPathPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private bool TryReadTenantId(HttpContext httpContext, out Guid tenantId)
    {
        var claim = httpContext.User.FindFirst(TenantClaimType)?.Value;

        if (Guid.TryParse(claim, out tenantId))
            return true;

        if (!_options.AllowHeaderFallback)
            return false;

        var header = httpContext.Request.Headers[TenantHeaderName].FirstOrDefault();
        return Guid.TryParse(header, out tenantId);
    }

    private (Guid ActorId, string ActorRole) ReadActor(HttpContext httpContext)
    {
        var subject = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = httpContext.User.FindFirst(ActorRoleClaimType)?.Value;

        if (_options.AllowHeaderFallback)
        {
            subject ??= httpContext.Request.Headers[ActorHeaderName].FirstOrDefault();
            role ??= httpContext.Request.Headers[ActorRoleHeaderName].FirstOrDefault();
        }

        // An unidentified actor is recorded as Guid.Empty rather than rejected: the audit
        // trail keeps the row either way, and Phase G makes authentication mandatory.
        return (Guid.TryParse(subject, out var actorId) ? actorId : Guid.Empty,
                string.IsNullOrWhiteSpace(role) ? "Unknown" : role);
    }

    private static Task WriteProblemAsync(
        HttpContext httpContext, int statusCode, string title, string detail)
    {
        httpContext.Response.StatusCode = statusCode;
        return httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Instance = httpContext.Request.Path
            },
            options: null,
            contentType: "application/problem+json");
    }
}

/// <summary>
/// Controls how a request's tenant is established.
/// </summary>
public sealed class TenantResolutionOptions
{
    /// <summary>
    /// Whether a tenant may be supplied by request header instead of an authenticated claim.
    /// Development convenience only: anyone who can reach the API can name any tenant, so
    /// this must be false wherever real patient data is held.
    /// </summary>
    public bool AllowHeaderFallback { get; set; }

    /// <summary>
    /// Paths served without a tenant, such as health probes and API documentation.
    /// </summary>
    public List<string> ExemptPathPrefixes { get; set; } =
        ["/health", "/swagger", "/openapi"];
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(
        this IApplicationBuilder app, TenantResolutionOptions options) =>
        app.UseMiddleware<TenantResolutionMiddleware>(options);
}
