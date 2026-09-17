namespace Betsi.LicenseTool;

using Betsi.Licensing;
using System.Security.Cryptography;

/// <summary>
/// Signs licence keys. The only code that ever holds a private key.
/// </summary>
/// <remarks>
/// Deliberately outside the product assembly (spec §5: validators contain public keys only).
/// In production this runs on the issuing workstation or an HSM-backed signing service, never
/// on a hospital's server.
/// </remarks>
public sealed class LicenseIssuer
{
    public const int KeySizeBits = 3072;

    private readonly RSA _privateKey;
    private readonly string _keyId;

    public LicenseIssuer(RSA privateKey, string keyId)
    {
        _privateKey = privateKey;
        _keyId = keyId;
    }

    public static LicenseIssuer FromPem(string privateKeyPem, string keyId)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return new LicenseIssuer(rsa, keyId);
    }

    /// <summary>A new signing key pair as (private PEM, public PEM).</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(KeySizeBits);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    public string Issue(
        Guid tenantId,
        IEnumerable<string> features,
        DateTimeOffset expiresAt,
        int gracePeriodDays = 30,
        string issuer = "Betsi",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? issuedAt = null,
        Guid? licenseId = null)
    {
        var now = DateTimeOffset.UtcNow;

        // A licence must carry the key id that verifies it, so the payload is built with it
        // rather than letting a caller sign under one key id and claim another.
        var payload = new LicensePayload
        {
            LicenseId = licenseId ?? Guid.NewGuid(),
            KeyId = _keyId,
            Issuer = issuer,
            TenantId = tenantId,
            Features = features.Select(f => f.Trim().ToLowerInvariant()).Distinct().ToArray(),
            IssuedAt = issuedAt ?? now,
            NotBefore = notBefore ?? issuedAt ?? now,
            ExpiresAt = expiresAt,
            GracePeriodDays = gracePeriodDays
        };

        return Sign(payload);
    }

    /// <summary>Signs a payload exactly as given. For tests that need malformed or unusual payloads.</summary>
    public string Sign(LicensePayload payload)
    {
        var encoded = LicenseFormat.EncodePayload(payload);
        var signature = _privateKey.SignData(
            LicenseFormat.SignedBytes(encoded), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        return LicenseFormat.Compose(encoded, signature);
    }
}
