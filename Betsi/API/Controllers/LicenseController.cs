namespace Betsi.API.Controllers;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

/// <summary>The calling tenant's licence status and enabled features (spec §4.1).</summary>
[ApiController]
[Route("api/v1/license")]
[Produces("application/json")]
public sealed class LicenseController : ControllerBase
{
    private readonly ITenantRegistry _registry;
    private readonly ITenantContext _tenantContext;

    public LicenseController(ITenantRegistry registry, ITenantContext tenantContext)
    {
        _registry = registry;
        _tenantContext = tenantContext;
    }

    /// <summary>Licence status for the tenant named on the request.</summary>
    [HttpGet]
    [Authorize(Policy = Permissions.LicenseRead)]
    [ProducesResponseType<LicenseStatusResponse>(StatusCodes.Status200OK)]
    public ActionResult<LicenseStatusResponse> Get()
    {
        // The middleware has already established the tenant exists and is available.
        _registry.TryGet(_tenantContext.TenantId, out var tenant);
        var license = tenant.License;

        return new LicenseStatusResponse(
            license.Status.ToString(),
            license.Mode.ToString(),
            license.LicenseId,
            license.Mode == Licensing.LicenseMode.Full ? license.Features : [],
            license.ExpiresAt,
            license.GracePeriodEndsAt,
            license.EvaluatedAt);
    }
}

/// <param name="Status">Why the licence is or is not in force, e.g. Valid, GracePeriod, Expired.</param>
/// <param name="Mode">Full, or Restricted — licence-gated operations refused; patient care continues.</param>
/// <param name="Features">Features currently usable. Empty in restricted mode.</param>
public sealed record LicenseStatusResponse(
    string Status,
    string Mode,
    Guid? LicenseId,
    IReadOnlyList<string> Features,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GracePeriodEndsAt,
    DateTimeOffset EvaluatedAt);
