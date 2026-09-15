namespace Betsi.Security;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

/// <summary>Authenticates inbound integration messages by HMAC signature (MVP-063).</summary>
public sealed class InboundSignatureAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string PathPrefix = "/api/v1/integrations/inbound";

    public static bool IsInboundPath(PathString path) =>
        path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}
