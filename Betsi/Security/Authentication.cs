namespace Betsi.Security;

using Betsi.API.Problems;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

/// <summary>Claim names read from tokens. Configurable so any OIDC provider's claims can be mapped.</summary>
public sealed class BetsiClaimOptions
{
    public string Tenant { get; set; } = "betsi:tenant_id";
    public string Subject { get; set; } = "sub";
    public string Role { get; set; } = "roles";

    /// <summary>
    /// The role the user selected for this session, where the provider supports role selection
    /// (as NHS CIS2 does). Required when a token carries more than one role.
    /// </summary>
    public string ActingRole { get; set; } = "betsi:acting_role";
}

public sealed class TrustedSigningKey
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
}

/// <summary>OIDC / JWT bearer configuration (MVP-065).</summary>
public sealed class JwtOptions
{
    /// <summary>OIDC issuer URL. Signing keys are discovered from its metadata. Preferred in production.</summary>
    public string? Authority { get; set; }

    /// <summary>Required. Tokens issued for any other audience are refused.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Required when <see cref="Authority"/> is not set.</summary>
    public string? Issuer { get; set; }

    /// <summary>Static public keys, for providers without discovery and for tests.</summary>
    public List<TrustedSigningKey> SigningKeys { get; set; } = [];

    public bool RequireHttpsMetadata { get; set; } = true;

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) || SigningKeys.Count > 0;
}

/// <summary>
/// Interactive sign-in for the web interface (Phase H).
/// </summary>
/// <remarks>
/// A browser gets a cookie, not a token. The authorization-code flow runs server-side and the
/// access token never reaches the browser, so an injected script cannot read one — which for a
/// system holding patient data is the difference between a configuration and a threat model.
/// </remarks>
public sealed class InteractiveSignInOptions
{
    /// <summary>OIDC authority for interactive sign-in. Usually the same provider the API trusts.</summary>
    public string? Authority { get; set; }

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Confidential-client secret. A <c>secret:</c> reference in a deployment.</summary>
    public string? ClientSecret { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Scopes beyond openid/profile. The tenant and role claims usually need one.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>How long a signed-in session lasts before the user must authenticate again.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class BetsiAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public JwtOptions Jwt { get; set; } = new();
    public BetsiClaimOptions Claims { get; set; } = new();
    public InteractiveSignInOptions Interactive { get; set; } = new();
}

public static class BetsiAuthenticationSchemes
{
    public const string Default = "Betsi";
    public const string Bearer = JwtBearerDefaults.AuthenticationScheme;
    public const string DevelopmentHeaders = "DevelopmentHeaders";
    public const string InboundSignature = "InboundSignature";

    /// <summary>The web interface's sign-in cookie.</summary>
    public const string Cookie = "BetsiCookie";

    /// <summary>The OIDC challenge that issues that cookie.</summary>
    public const string Oidc = "BetsiOidc";
}

public static class AuthenticationSetup
{
    public static IServiceCollection AddBetsiAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment,
        TenantResolutionOptions tenantResolution)
    {
        var options = Bind(configuration);
        var interactive = options.Interactive;

        // Resolved lazily from the final configuration, so providers added after registration
        // (a test host, a secret store) are honoured. Startup validation below uses what is
        // known now, which in a deployed host is everything.
        services.AddSingleton(sp => Bind(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => sp.GetRequiredService<BetsiAuthenticationOptions>().Claims);

        // Outside Development there must be a way to authenticate someone. Failing at startup is
        // clearer than an API that answers every request with 401.
        if (!environment.IsDevelopment() && !options.Jwt.IsConfigured)
        {
            throw new InvalidOperationException(
                "Authentication:Jwt is not configured. Set Authority (OIDC discovery) or SigningKeys, plus Audience.");
        }

        if (options.Jwt.IsConfigured && string.IsNullOrWhiteSpace(options.Jwt.Audience))
            throw new InvalidOperationException("Authentication:Jwt:Audience is required.");

        if (string.IsNullOrWhiteSpace(options.Jwt.Authority) && options.Jwt.SigningKeys.Count > 0 &&
            string.IsNullOrWhiteSpace(options.Jwt.Issuer))
        {
            throw new InvalidOperationException("Authentication:Jwt:Issuer is required when signing keys are configured statically.");
        }

        services.AddAuthentication(BetsiAuthenticationSchemes.Default)
            .AddPolicyScheme(BetsiAuthenticationSchemes.Default, "Betsi", policy =>
            {
                policy.ForwardDefaultSelector = context =>
                {
                    if (InboundSignatureAuthenticationHandler.IsInboundPath(context.Request.Path))
                        return BetsiAuthenticationSchemes.InboundSignature;

                    var authorization = context.Request.Headers.Authorization.ToString();
                    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return BetsiAuthenticationSchemes.Bearer;

                    // Anything that is not the API is the web interface, which authenticates
                    // with a cookie. Checked after the bearer header so a token always wins:
                    // a request that presents one is asking to be judged on it.
                    if (interactive.IsConfigured && !UI.UiRoutes.IsApiPath(context.Request.Path))
                        return BetsiAuthenticationSchemes.Cookie;

                    return tenantResolution.AllowHeaderFallback
                        ? BetsiAuthenticationSchemes.DevelopmentHeaders
                        : BetsiAuthenticationSchemes.Bearer;
                };
            })
            .AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>(BetsiAuthenticationSchemes.DevelopmentHeaders, null)
            .AddScheme<AuthenticationSchemeOptions, InboundSignatureAuthenticationHandler>(BetsiAuthenticationSchemes.InboundSignature, null)
            .AddJwtBearer(BetsiAuthenticationSchemes.Bearer, _ => { });

        services.AddOptions<JwtBearerOptions>(BetsiAuthenticationSchemes.Bearer)
            .Configure<BetsiAuthenticationOptions>(ConfigureJwt);

        if (interactive.IsConfigured)
            AddInteractiveSignIn(services, interactive, options.Claims, environment);

        services.AddAuthorization(authorization =>
        {
            // Every endpoint requires an authenticated caller unless it explicitly opts out.
            authorization.FallbackPolicy = new AuthorizationPolicyBuilder(BetsiAuthenticationSchemes.Default)
                .RequireAuthenticatedUser()
                .Build();

            foreach (var permission in Permissions.All)
            {
                authorization.AddPolicy(permission, policy => policy
                    .AddAuthenticationSchemes(BetsiAuthenticationSchemes.Default)
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(permission)));
            }
        });

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();

        return services;
    }

    /// <summary>Cookie plus authorization-code sign-in for the web interface.</summary>
    private static void AddInteractiveSignIn(
        IServiceCollection services, InteractiveSignInOptions interactive, BetsiClaimOptions claims,
        IHostEnvironment environment)
    {
        services.AddAuthentication()
            .AddCookie(BetsiAuthenticationSchemes.Cookie, cookie =>
            {
                cookie.Cookie.Name = "betsi.session";
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                // Always secure outside Development: this cookie is a signed-in clinician.
                cookie.Cookie.SecurePolicy = environment.IsDevelopment()
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;

                // A fixed lifetime, not a sliding one. A shared ward computer left logged in all
                // week is how the wrong person's name ends up on a clinical action.
                cookie.ExpireTimeSpan = interactive.SessionLifetime;
                cookie.SlidingExpiration = false;

                cookie.LoginPath = UI.UiRoutes.SignIn;
                cookie.LogoutPath = UI.UiRoutes.SignOut;
                cookie.AccessDeniedPath = UI.UiRoutes.Forbidden;
            })
            .AddOpenIdConnect(BetsiAuthenticationSchemes.Oidc, oidc =>
            {
                oidc.Authority = interactive.Authority;
                oidc.ClientId = interactive.ClientId;
                oidc.ClientSecret = interactive.ClientSecret;
                oidc.RequireHttpsMetadata = interactive.RequireHttpsMetadata;
                oidc.SignInScheme = BetsiAuthenticationSchemes.Cookie;

                // Authorization code with PKCE. The tokens stay on the server.
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.MapInboundClaims = false;

                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                foreach (var scope in interactive.Scopes)
                    oidc.Scope.Add(scope);

                oidc.TokenValidationParameters.NameClaimType = "name";
                oidc.TokenValidationParameters.RoleClaimType = claims.Role;

                oidc.CallbackPath = UI.UiRoutes.SignInCallback;
                oidc.SignedOutCallbackPath = UI.UiRoutes.SignOutCallback;
                oidc.SignedOutRedirectUri = UI.UiRoutes.SignedOut;
            });
    }

    private static BetsiAuthenticationOptions Bind(IConfiguration configuration)
    {
        var options = new BetsiAuthenticationOptions();
        configuration.GetSection(BetsiAuthenticationOptions.SectionName).Bind(options);
        return options;
    }

    private static void ConfigureJwt(JwtBearerOptions jwt, BetsiAuthenticationOptions options)
    {
        // Claim names exactly as the provider sent them: "sub" stays "sub", not a SOAP URI.
        jwt.MapInboundClaims = false;
        jwt.Audience = options.Jwt.Audience;
        jwt.RequireHttpsMetadata = options.Jwt.RequireHttpsMetadata;

        if (!string.IsNullOrWhiteSpace(options.Jwt.Authority))
            jwt.Authority = options.Jwt.Authority;

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = options.Jwt.ClockSkew,
            // Asymmetric only. A shared-secret algorithm would let anyone holding the verification
            // key mint tokens, and "none" must never be accepted.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.EcdsaSha256],
            NameClaimType = options.Claims.Subject,
            RoleClaimType = options.Claims.Role,
            IssuerSigningKeys = options.Jwt.SigningKeys.Select(ToSecurityKey).ToList()
        };

        // Responses are written by ProblemAuthorizationResultHandler, so a failed token and a
        // missing one both produce the same RFC 9457 401.
        jwt.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return ProblemCodes.WriteAsync(context.HttpContext, ProblemCodes.Create(
                    StatusCodes.Status401Unauthorized, ProblemCodes.Unauthenticated, "Authentication required",
                    "Supply a valid bearer token."));
            }
        };
    }

    private static SecurityKey ToSecurityKey(TrustedSigningKey key)
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            return new RsaSecurityKey(rsa) { KeyId = key.KeyId };
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            return new ECDsaSecurityKey(ecdsa) { KeyId = key.KeyId };
        }
    }

    /// <summary>
    /// A stable actor id for a subject. Providers use their own subject formats (CIS2 UUIDs,
    /// Entra object ids, opaque strings), so a non-GUID subject is hashed with its issuer into one.
    /// </summary>
    public static Guid ActorIdFor(string? subject, string? issuer)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return Guid.Empty;

        if (Guid.TryParse(subject, out var id))
            return id;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}|{subject}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}

/// <summary>
/// Development-only authentication from <c>X-Betsi-*</c> headers, so the API is usable without
/// an identity provider. Refused at startup outside Development.
/// </summary>
public sealed class DevelopmentHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    BetsiClaimOptions claims)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers;
        var tenant = headers[TenantResolutionMiddleware.TenantHeaderName].ToString();
        var role = headers[TenantResolutionMiddleware.ActorRoleHeaderName].ToString();

        if (string.IsNullOrWhiteSpace(tenant) && string.IsNullOrWhiteSpace(role))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity(Scheme.Name, claims.Subject, claims.Role);

        if (!string.IsNullOrWhiteSpace(tenant))
            identity.AddClaim(new Claim(claims.Tenant, tenant));

        var actor = headers[TenantResolutionMiddleware.ActorHeaderName].ToString();
        if (!string.IsNullOrWhiteSpace(actor))
            identity.AddClaim(new Claim(claims.Subject, actor));

        if (!string.IsNullOrWhiteSpace(role))
            identity.AddClaim(new Claim(claims.Role, role));

        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemCodes.WriteAsync(Context, ProblemCodes.Create(
            StatusCodes.Status401Unauthorized, ProblemCodes.Unauthenticated, "Authentication required",
            $"Supply a bearer token, or in Development the {TenantResolutionMiddleware.TenantHeaderName} and {TenantResolutionMiddleware.ActorRoleHeaderName} headers."));
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Grants a permission if the resolved acting role holds it in <see cref="RoleMatrix"/>.</summary>
public sealed class PermissionAuthorizationHandler(ITenantContext tenantContext) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (tenantContext.IsResolved && !tenantContext.IsSystem &&
            RoleMatrix.Grants(tenantContext.ActorRole, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes 401 and 403 as problem details, and records every refused authorisation in the
/// tenant's audit log (spec §6: every authorisation decision is audited).
/// </summary>
public sealed class ProblemAuthorizationResultHandler(ILogger<ProblemAuthorizationResultHandler> logger)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            if (InboundSignatureAuthenticationHandler.IsInboundPath(context.Request.Path))
            {
                await ProblemCodes.WriteAsync(context, ProblemCodes.Create(
                    StatusCodes.Status401Unauthorized, ProblemCodes.InvalidSignature, "Message not authenticated",
                    "The message signature could not be verified."));
                return;
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            await ProblemCodes.WriteAsync(context, ProblemCodes.Create(
                StatusCodes.Status401Unauthorized, ProblemCodes.Unauthenticated, "Authentication required",
                "Supply a valid bearer token."));
            return;
        }

        if (authorizeResult.Forbidden)
        {
            var permissions = policy.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission).ToArray();
            await AuditDenialAsync(context, permissions);

            var problem = ProblemCodes.Create(
                StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Not permitted",
                "Your role does not permit this operation.");
            if (permissions.Length > 0)
                problem.Extensions["permission"] = permissions[0];

            await ProblemCodes.WriteAsync(context, problem);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private async Task AuditDenialAsync(HttpContext context, string[] permissions)
    {
        var tenantContext = context.RequestServices.GetRequiredService<ITenantContext>();
        if (!tenantContext.IsResolved)
            return;

        try
        {
            var db = context.RequestServices.GetRequiredService<BetsiDbContext>();
            db.AuditLogs.Add(new AuditLogRecord
            {
                TenantId = tenantContext.TenantId,
                Action = Truncate($"{context.Request.Method} {context.GetEndpoint()?.DisplayName ?? context.Request.Path}", 100),
                ActorId = tenantContext.ActorId,
                ActorRole = tenantContext.ActorRole,
                AffectedAggregateId = Guid.Empty,
                AffectedAggregateType = "Authorization",
                Outcome = "Denied",
                ErrorMessage = Truncate($"Requires {string.Join(", ", permissions)}", 500),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception exception)
        {
            // A failure to audit must not turn a refusal into a success or a 500.
            logger.LogError(exception, "Could not audit an authorisation denial for tenant {TenantId}", tenantContext.TenantId);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
