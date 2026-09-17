namespace Betsi.Licensing;

using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// What a licence grants. This is the signed content of a licence key.
/// </summary>
/// <remarks>
/// Every field the validator relies on is inside the signature, including the key id and the
/// tenant. A licence cannot be moved to another tenant or re-attributed to another signing
/// key without invalidating it.
/// </remarks>
public sealed record LicensePayload
{
    /// <summary>Payload schema version. Validators reject versions they do not understand.</summary>
    public int FormatVersion { get; init; } = LicenseFormat.CurrentFormatVersion;

    public Guid LicenseId { get; init; }

    /// <summary>Which trusted public key verifies this licence, so keys can be rotated.</summary>
    public string KeyId { get; init; } = string.Empty;

    public string Issuer { get; init; } = string.Empty;

    /// <summary>The only tenant this licence is valid for.</summary>
    public Guid TenantId { get; init; }

    public IReadOnlyList<string> Features { get; init; } = [];

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset NotBefore { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Days after <see cref="ExpiresAt"/> during which the licence keeps full function while
    /// a renewal is arranged. Offline sites cannot always renew on the day.
    /// </summary>
    public int GracePeriodDays { get; init; }
}

/// <summary>
/// The wire format of a licence key: <c>BETSI1.&lt;payload&gt;.&lt;signature&gt;</c>.
/// </summary>
/// <remarks>
/// Payload and signature are base64url. The signature is RSA-PSS with SHA-256 over the ASCII
/// bytes of <c>BETSI1.&lt;payload&gt;</c>, so the prefix is signed too and a key cannot be
/// replayed under a future format with different semantics.
///
/// RSA-PSS rather than Ed25519 (both permitted by spec §5) because it is in the .NET base
/// class library, which keeps a cryptographic dependency out of the validator.
///
/// This type only encodes and decodes. Signing lives in <c>tools/Betsi.LicenseTool</c>; the
/// product holds public keys only.
/// </remarks>
public static class LicenseFormat
{
    public const string Prefix = "BETSI1";
    public const int CurrentFormatVersion = 1;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>The bytes a signature covers for a given encoded payload.</summary>
    public static byte[] SignedBytes(string encodedPayload) =>
        Encoding.ASCII.GetBytes($"{Prefix}.{encodedPayload}");

    public static string EncodePayload(LicensePayload payload) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));

    public static string Compose(string encodedPayload, byte[] signature) =>
        $"{Prefix}.{encodedPayload}.{Base64Url.EncodeToString(signature)}";

    /// <summary>
    /// Splits a key into its parts without trusting any of them.
    /// </summary>
    public static bool TryParse(
        string key, out string encodedPayload, out LicensePayload? payload, out byte[] signature)
    {
        encodedPayload = string.Empty;
        payload = null;
        signature = [];

        var parts = key.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
            return false;

        try
        {
            encodedPayload = parts[1];
            signature = Base64Url.DecodeFromChars(parts[2]);
            payload = JsonSerializer.Deserialize<LicensePayload>(
                Base64Url.DecodeFromChars(parts[1]), JsonOptions);
            return payload is not null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return false;
        }
    }
}
