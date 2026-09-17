namespace Betsi.Tests.Api;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

/// <summary>Mints tokens as a test OIDC provider would, signed by a key generated per run.</summary>
public static class TestTokens
{
    public const string Issuer = "https://idp.betsi.test";
    public const string Audience = "betsi-api";
    public const string KeyId = "test-idp-1";

    private static readonly RSA Key = RSA.Create(2048);

    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    public static string For(
        Guid tenantId,
        string[] roles,
        string? subject = null,
        string? actingRole = null,
        DateTime? expires = null,
        string issuer = Issuer,
        string audience = Audience,
        RSA? signingKey = null,
        Dictionary<string, object>? extraClaims = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject ?? Guid.NewGuid().ToString(),
            ["betsi:tenant_id"] = tenantId.ToString(),
            ["roles"] = roles
        };

        if (actingRole is not null)
            claims["betsi:acting_role"] = actingRole;

        foreach (var (key, value) in extraClaims ?? [])
            claims[key] = value;

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = expiry.AddHours(-1),
            NotBefore = expiry.AddHours(-1),
            Expires = expiry,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(signingKey ?? Key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)
        });
    }
}
