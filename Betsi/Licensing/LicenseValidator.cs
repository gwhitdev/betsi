namespace Betsi.Licensing;

using System.Security.Cryptography;

/// <summary>Why a licence is or is not in force.</summary>
public enum LicenseStatus
{
    Valid = 1,
    /// <summary>Expired, but inside the grace period. Full function continues.</summary>
    GracePeriod = 2,
    Expired = 3,
    NotYetValid = 4,
    Missing = 5,
    Malformed = 6,
    UnsupportedFormat = 7,
    UnknownSigningKey = 8,
    InvalidSignature = 9,
    WrongTenant = 10
}

/// <summary>What the tenant may do as a result of its licence.</summary>
public enum LicenseMode
{
    /// <summary>Everything the licence's features allow.</summary>
    Full = 1,

    /// <summary>
    /// Licence-gated operations are refused. Operations classified as always available —
    /// every patient-care and escalation command — continue. Spec §5: licensing must never
    /// disable clinically necessary safety functions.
    /// </summary>
    Restricted = 2
}

/// <summary>The outcome of validating one licence for one tenant at one moment.</summary>
public sealed record LicenseEvaluation
{
    public required LicenseStatus Status { get; init; }
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>
    /// The time the evaluation was made against. Normally the clock; the high-water mark
    /// instead if the clock has been wound back.
    /// </summary>
    public required DateTimeOffset EffectiveTime { get; init; }

    public bool ClockRollbackDetected { get; init; }
    public Guid? LicenseId { get; init; }
    public string? KeyId { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? GracePeriodEndsAt { get; init; }

    public LicenseMode Mode =>
        Status is LicenseStatus.Valid or LicenseStatus.GracePeriod
            ? LicenseMode.Full
            : LicenseMode.Restricted;

    /// <summary>Whether a feature is usable now. Always false in restricted mode.</summary>
    public bool Grants(string feature) =>
        Mode == LicenseMode.Full && Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A public key the validator trusts, identified by the key id licences name.</summary>
public sealed class TrustedLicenseKey
{
    public string KeyId { get; set; } = string.Empty;
    public string PublicKeyPem { get; set; } = string.Empty;
}

public sealed class LicensingOptions
{
    public const string SectionName = "Licensing";

    /// <summary>
    /// Key ids with this prefix sign development licences. They are refused outside the
    /// Development environment, so a development licence can never unlock production.
    /// </summary>
    public const string DevelopmentKeyPrefix = "dev-";

    public List<TrustedLicenseKey> TrustedKeys { get; set; } = [];

    /// <summary>
    /// How far the clock may appear to move backwards before it is treated as tampering.
    /// Covers ordinary NTP corrections between registry refreshes.
    /// </summary>
    public TimeSpan ClockRollbackTolerance { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Validates licence keys offline (MVP-008). Holds public keys only.
/// </summary>
/// <remarks>
/// Checks run cheapest-and-least-trusting first: shape, format version, signing key,
/// signature, then — only once the content is proven authentic — tenant and dates.
///
/// Clock safety: the caller supplies the latest time a licence was previously evaluated at
/// for this tenant (a high-water mark persisted in the control plane). Evaluation uses
/// whichever is later, so setting the server clock back cannot revive an expired licence.
/// </remarks>
public sealed class LicenseValidator
{
    private readonly Dictionary<string, RSA> _keys;
    private readonly TimeSpan _rollbackTolerance;

    public LicenseValidator(LicensingOptions options, bool isDevelopment)
    {
        _rollbackTolerance = options.ClockRollbackTolerance;
        _keys = new Dictionary<string, RSA>(StringComparer.Ordinal);

        foreach (var key in options.TrustedKeys)
        {
            if (string.IsNullOrWhiteSpace(key.KeyId))
                throw new InvalidOperationException("A trusted licence key has no key id.");

            if (!isDevelopment &&
                key.KeyId.StartsWith(LicensingOptions.DevelopmentKeyPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Trusted licence key '{key.KeyId}' is a development key and must not be " +
                    "trusted outside Development: anyone with the repository can sign with it.");
            }

            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(key.PublicKeyPem);
            }
            catch (Exception exception) when (exception is ArgumentException or CryptographicException)
            {
                rsa.Dispose();
                throw new InvalidOperationException(
                    $"Trusted licence key '{key.KeyId}' is not a valid PEM public key.", exception);
            }

            if (!_keys.TryAdd(key.KeyId, rsa))
                throw new InvalidOperationException($"Trusted licence key '{key.KeyId}' is listed twice.");
        }
    }

    public LicenseEvaluation Validate(
        string? key, Guid tenantId, DateTimeOffset now, DateTimeOffset? highWaterMark = null)
    {
        var rollback = highWaterMark is { } mark && now < mark - _rollbackTolerance;
        var effective = highWaterMark is { } hw && hw > now ? hw : now;

        LicenseEvaluation Result(LicenseStatus status, LicensePayload? payload = null) => new()
        {
            Status = status,
            EvaluatedAt = now,
            EffectiveTime = effective,
            ClockRollbackDetected = rollback,
            LicenseId = payload?.LicenseId,
            KeyId = payload?.KeyId,
            Features = payload?.Features ?? [],
            ExpiresAt = payload?.ExpiresAt,
            GracePeriodEndsAt = payload?.ExpiresAt.AddDays(payload.GracePeriodDays)
        };

        if (string.IsNullOrWhiteSpace(key))
            return Result(LicenseStatus.Missing);

        if (!LicenseFormat.TryParse(key, out var encodedPayload, out var payload, out var signature))
            return Result(LicenseStatus.Malformed);

        if (payload!.FormatVersion != LicenseFormat.CurrentFormatVersion)
            return Result(LicenseStatus.UnsupportedFormat);

        if (!_keys.TryGetValue(payload.KeyId, out var rsa))
            return Result(LicenseStatus.UnknownSigningKey);

        var authentic = rsa.VerifyData(
            LicenseFormat.SignedBytes(encodedPayload),
            signature,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        // Nothing from an unverified payload is echoed back: it is attacker-controlled.
        if (!authentic)
            return Result(LicenseStatus.InvalidSignature);

        if (payload.TenantId != tenantId)
            return Result(LicenseStatus.WrongTenant, payload);

        if (effective < payload.NotBefore)
            return Result(LicenseStatus.NotYetValid, payload);

        if (effective <= payload.ExpiresAt)
            return Result(LicenseStatus.Valid, payload);

        return effective <= payload.ExpiresAt.AddDays(payload.GracePeriodDays)
            ? Result(LicenseStatus.GracePeriod, payload)
            : Result(LicenseStatus.Expired, payload);
    }
}
