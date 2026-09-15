namespace Betsi.Security;

using Betsi.API.Problems;
using Betsi.ControlPlane;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Integrations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;

/// <summary>
/// Authenticates inbound integration messages by HMAC signature (MVP-063).
/// </summary>
/// <remarks>
/// Route: <c>/api/v1/integrations/inbound/{tenantId}/{sourceId}</c>. The source's secret, held
/// encrypted in the tenant database, must have signed <c>"{timestamp}.{raw body}"</c> within the
/// last five minutes. A verified message becomes a principal in the reserved Integration role
/// for that tenant only; nothing about it is trusted before the signature is checked.
/// </remarks>
public sealed class InboundSignatureAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITenantRegistry registry,
    IServiceScopeFactory scopes,
    TimeProvider time,
    BetsiClaimOptions claims)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string PathPrefix = "/api/v1/integrations/inbound";
    public const string MessageIdHeader = "Betsi-Message-Id";
    public const string SourceIdClaim = "betsi:inbound_source";
    public const int MaxBodyBytes = 1024 * 1024;

    public static bool IsInboundPath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase, out var remaining))
            return AuthenticateResult.NoResult();

        var segments = remaining.Value?.Trim('/').Split('/') ?? [];
        if (segments.Length != 2 || !Guid.TryParse(segments[0], out var tenantId) || !Guid.TryParse(segments[1], out var sourceId))
            return AuthenticateResult.Fail("The inbound URL must name a tenant and a source.");

        // Unknown or unavailable tenants fail the same way as a bad signature: nothing about which
        // tenants or sources exist is revealed to an unauthenticated caller.
        if (!registry.TryGet(tenantId, out var tenant) || tenant.Availability != TenantAvailability.Available)
            return AuthenticateResult.Fail("Signature verification failed.");

        if (Request.ContentLength is > MaxBodyBytes)
            return AuthenticateResult.Fail("The message is too large.");

        if (string.IsNullOrWhiteSpace(Request.Headers[MessageIdHeader]))
            return AuthenticateResult.Fail($"The {MessageIdHeader} header is required.");

        string? secret;
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(tenantId);
            var db = scope.ServiceProvider.GetRequiredService<BetsiDbContext>();

            var source = await db.InboundSources.AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == sourceId && s.Active, Context.RequestAborted);

            secret = source is null ? null : scope.ServiceProvider.GetRequiredService<IntegrationSecrets>().Unprotect(source.ProtectedSecret);
        }

        if (secret is null)
            return AuthenticateResult.Fail("Signature verification failed.");

        Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: MaxBodyBytes);
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
            body = await reader.ReadToEndAsync(Context.RequestAborted);
        Request.Body.Position = 0;

        if (!WebhookSignature.Verify(secret, Request.Headers[WebhookSignature.SignatureHeader], body, time.GetUtcNow(), out var failure))
        {
            Logger.LogWarning("Inbound message for source {SourceId} refused: {Failure}", sourceId, failure);
            return AuthenticateResult.Fail("Signature verification failed.");
        }

        var identity = new ClaimsIdentity(Scheme.Name, claims.Subject, claims.Role);
        identity.AddClaim(new Claim(claims.Tenant, tenantId.ToString()));
        identity.AddClaim(new Claim(claims.Subject, sourceId.ToString()));
        identity.AddClaim(new Claim(claims.Role, RoleMatrix.IntegrationRole));
        identity.AddClaim(new Claim(SourceIdClaim, sourceId.ToString()));

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemCodes.WriteAsync(Context, ProblemCodes.Create(
            StatusCodes.Status401Unauthorized, ProblemCodes.InvalidSignature, "Message not authenticated",
            "The message signature could not be verified."));
}
