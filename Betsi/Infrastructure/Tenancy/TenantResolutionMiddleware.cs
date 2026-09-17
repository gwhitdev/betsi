namespace Betsi.Infrastructure.Tenancy;

using Betsi.ControlPlane;
using Betsi.API.Problems;
using Betsi.Security;
using System.Security.Claims;

/// <summary>
/// Establishes the tenant and actor for each request.
/// </summary>
/// <remarks>
/// Fails closed. A request that does not name a tenant this instance serves is rejected
/// before it can reach a handler, rather than being allowed through with an empty tenant.
///
/// Tenant, actor and acting role are read from the authenticated principal: a validated OIDC
/// token, a signed inbound integration message, or — in Development only — the
/// <c>X-Betsi-*</c> headers, which <see cref="DevelopmentHeaderAuthenticationHandler"/> turns
/// into the same claims. Nothing here reads identity from a raw header.
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    public const string TenantHeaderName = "X-Betsi-Tenant";
    public const string ActorHeaderName = "X-Betsi-Actor";
    public const string ActorRoleHeaderName = "X-Betsi-Actor-Role";

    /// <summary>Selects one of the token's roles when it carries several and no acting-role claim.</summary>
    public const string ActingRoleHeaderName = "X-Betsi-Acting-Role";

    private readonly RequestDelegate _next;
    private readonly TenantResolutionOptions _options;
    private readonly BetsiClaimOptions _claims;
    private readonly ILogger<TenantResolutionMiddleware> _logger;

    public TenantResolutionMiddleware(
        RequestDelegate next,
        TenantResolutionOptions options,
        BetsiClaimOptions claims,
        ILogger<TenantResolutionMiddleware> logger)
    {
        _next = next;
        _options = options;
        _claims = claims;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext httpContext,
        TenantContext tenantContext,
        ITenantRegistry registry)
    {
        var user = httpContext.User;

        // Unauthenticated requests pass through unresolved; the authorisation fallback policy
        // turns them into a 401. Exempt paths (health, API documentation) need no tenant.
        if (IsExempt(httpContext.Request.Path) || user.Identity?.IsAuthenticated != true)
        {
            await _next(httpContext);
            return;
        }

        if (!Guid.TryParse(user.FindFirst(_claims.Tenant)?.Value, out var tenantId))
        {
            await RefuseAsync(httpContext, StatusCodes.Status400BadRequest, ProblemCodes.TenantNotSpecified,
                "Tenant not specified",
                $"The credentials did not carry a '{_claims.Tenant}' claim" +
                (_options.AllowHeaderFallback ? $" and no '{TenantHeaderName}' header was sent." : "."));
            return;
        }

        // A tenant named in a header must agree with the one in the token (MVP-065): a client
        // that believes it is talking to one hospital must not silently act in another.
        var headerTenant = httpContext.Request.Headers[TenantHeaderName].ToString();
        if (!string.IsNullOrEmpty(headerTenant) &&
            (!Guid.TryParse(headerTenant, out var named) || named != tenantId))
        {
            _logger.LogWarning("Refused request whose tenant header does not match its credentials");
            await RefuseAsync(httpContext, StatusCodes.Status403Forbidden, ProblemCodes.TenantMismatch,
                "Tenant mismatch", "The tenant named in the request does not match the tenant in the credentials.");
            return;
        }

        if (!TryResolveActingRole(httpContext, user, out var actingRole, out var refusal))
        {
            await RefuseAsync(httpContext, StatusCodes.Status403Forbidden, refusal.Code, refusal.Title, refusal.Detail);
            return;
        }

        // Checked here so an unknown or unavailable tenant is rejected at the edge rather than
        // surfacing later as a DbContext construction failure. The reason a tenant is
        // unavailable is logged for operators but not returned: it can name database servers.
        if (!registry.TryGet(tenantId, out var tenant))
        {
            _logger.LogWarning("Rejected request for unregistered tenant {TenantId}", tenantId);
            await RefuseAsync(httpContext, StatusCodes.Status404NotFound, ProblemCodes.UnknownTenant,
                "Unknown tenant", "The requested tenant is not served by this instance.");
            return;
        }

        if (tenant.Availability == TenantAvailability.Suspended)
        {
            _logger.LogWarning("Rejected request for suspended tenant {TenantId}", tenantId);
            await RefuseAsync(httpContext, StatusCodes.Status403Forbidden, ProblemCodes.TenantSuspended,
                "Tenant suspended", "This tenant has been suspended. Contact your system administrator.");
            return;
        }

        if (tenant.Availability == TenantAvailability.NotReady)
        {
            _logger.LogError(
                "Rejected request for tenant {TenantId}, which is not ready: {Reason}",
                tenantId, tenant.UnavailableReason);

            httpContext.Response.Headers.RetryAfter = "60";
            await RefuseAsync(httpContext, StatusCodes.Status503ServiceUnavailable, ProblemCodes.TenantUnavailable,
                "Tenant unavailable", "This tenant is temporarily unavailable. Try again shortly.");
            return;
        }

        var actorId = AuthenticationSetup.ActorIdFor(
            user.FindFirst(_claims.Subject)?.Value, user.FindFirst("iss")?.Value);

        tenantContext.Resolve(tenantId, actorId, actingRole);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = tenantId,
            ["ActorId"] = actorId,
            ["ActorRole"] = actingRole
        }))
        {
            await _next(httpContext);
        }
    }

    private bool TryResolveActingRole(
        HttpContext httpContext, ClaimsPrincipal user, out string actingRole, out (string Code, string Title, string Detail) refusal)
    {
        refusal = default;

        var roles = user.FindAll(_claims.Role).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        var selected = user.FindFirst(_claims.ActingRole)?.Value;
        if (string.IsNullOrWhiteSpace(selected))
            selected = httpContext.Request.Headers[ActingRoleHeaderName].ToString();

        if (!string.IsNullOrWhiteSpace(selected))
        {
            actingRole = roles.FirstOrDefault(r => string.Equals(r, selected, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (actingRole.Length == 0)
            {
                refusal = (ProblemCodes.Forbidden, "Role not held", $"The credentials do not hold the role '{selected}'.");
                return false;
            }
        }
        else if (roles.Count == 1)
        {
            actingRole = roles[0];
        }
        else if (roles.Count == 0)
        {
            // Authenticated but with no role: allowed through so the permission check refuses
            // it (and audits the refusal) rather than failing here without a record.
            actingRole = "Unknown";
        }
        else
        {
            actingRole = string.Empty;
            refusal = (ProblemCodes.ActingRoleRequired, "Acting role required",
                $"The credentials carry several roles. Select one with the '{_claims.ActingRole}' claim or the '{ActingRoleHeaderName}' header.");
            return false;
        }

        // The platform's own roles cannot be claimed. Integration is granted only to a message
        // authenticated by its signature, never to a token or header that says so.
        if (RoleMatrix.IsReserved(actingRole) &&
            !(string.Equals(actingRole, RoleMatrix.IntegrationRole, StringComparison.OrdinalIgnoreCase) &&
              user.Identity?.AuthenticationType == BetsiAuthenticationSchemes.InboundSignature))
        {
            _logger.LogWarning("Refused credentials claiming reserved role {Role}", actingRole);
            refusal = (ProblemCodes.ReservedRole, "Reserved role", $"The role '{actingRole}' cannot be claimed.");
            return false;
        }

        return true;
    }

    private bool IsExempt(PathString path) =>
        _options.ExemptPathPrefixes.Any(prefix =>
            path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));

    private static Task RefuseAsync(HttpContext httpContext, int status, string code, string title, string detail) =>
        ProblemCodes.WriteAsync(httpContext, ProblemCodes.Create(status, code, title, detail));
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
