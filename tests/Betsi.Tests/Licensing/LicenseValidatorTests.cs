namespace Betsi.Tests.Licensing;

using Betsi.LicenseTool;
using Betsi.Licensing;
using Betsi.Tests.ControlPlane;
using System.Security.Cryptography;

/// <summary>
/// The offline licence validator (MVP-008). A licence is a commercial control, but getting it
/// wrong in the permissive direction gives the product away, and in the restrictive direction
/// switches off a hospital. Both directions are tested.
/// </summary>
public class LicenseValidatorTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static string Issue(DateTimeOffset expires, int graceDays = 30, Guid? tenant = null, DateTimeOffset? notBefore = null) =>
        TestLicenses.Issuer.Issue(tenant ?? Tenant, [LicenseFeatures.Core], expires, graceDays,
            notBefore: notBefore ?? Now.AddDays(-10), issuedAt: Now.AddDays(-10));

    [Fact]
    public void A_current_licence_is_valid_and_grants_its_features()
    {
        var result = TestLicenses.Validator().Validate(Issue(Now.AddDays(100)), Tenant, Now);

        result.Status.ShouldBe(LicenseStatus.Valid);
        result.Mode.ShouldBe(LicenseMode.Full);
        result.Grants(LicenseFeatures.Core).ShouldBeTrue();
        result.Grants("addon.portal").ShouldBeFalse();
    }

    [Fact]
    public void An_expired_licence_keeps_full_function_during_the_grace_period()
    {
        var result = TestLicenses.Validator().Validate(Issue(Now.AddDays(-5), graceDays: 30), Tenant, Now);

        result.Status.ShouldBe(LicenseStatus.GracePeriod);
        result.Mode.ShouldBe(LicenseMode.Full);
        result.GracePeriodEndsAt.ShouldBe(Now.AddDays(25));
    }

    [Fact]
    public void An_expired_licence_past_its_grace_period_is_restricted()
    {
        var result = TestLicenses.Validator().Validate(Issue(Now.AddDays(-31), graceDays: 30), Tenant, Now);

        result.Status.ShouldBe(LicenseStatus.Expired);
        result.Mode.ShouldBe(LicenseMode.Restricted);
        result.Grants(LicenseFeatures.Core).ShouldBeFalse();
    }

    [Fact]
    public void A_licence_before_its_start_date_is_not_yet_valid()
    {
        var key = Issue(Now.AddDays(400), notBefore: Now.AddDays(3));

        TestLicenses.Validator().Validate(key, Tenant, Now).Status.ShouldBe(LicenseStatus.NotYetValid);
    }

    [Fact]
    public void A_licence_for_another_tenant_is_refused()
    {
        var key = Issue(Now.AddDays(100), tenant: Guid.NewGuid());

        TestLicenses.Validator().Validate(key, Tenant, Now).Status.ShouldBe(LicenseStatus.WrongTenant);
    }

    [Fact]
    public void Changing_any_part_of_the_payload_breaks_the_signature()
    {
        var key = Issue(Now.AddDays(-400));
        LicenseFormat.TryParse(key, out _, out var payload, out var signature).ShouldBeTrue();

        // Extend the expiry, keep the original signature.
        var forged = LicenseFormat.Compose(
            LicenseFormat.EncodePayload(payload! with { ExpiresAt = Now.AddYears(10) }), signature);

        var result = TestLicenses.Validator().Validate(forged, Tenant, Now);

        result.Status.ShouldBe(LicenseStatus.InvalidSignature);
        result.Mode.ShouldBe(LicenseMode.Restricted);
        // Nothing from an unverified payload is reported back.
        result.ExpiresAt.ShouldBeNull();
        result.Features.ShouldBeEmpty();
    }

    [Fact]
    public void A_licence_signed_by_an_untrusted_key_claiming_a_trusted_key_id_is_refused()
    {
        using var attackerKey = RSA.Create(2048);
        var forged = new LicenseIssuer(attackerKey, TestLicenses.KeyId).Issue(Tenant, ["core"], Now.AddYears(1));

        TestLicenses.Validator().Validate(forged, Tenant, Now).Status.ShouldBe(LicenseStatus.InvalidSignature);
    }

    [Fact]
    public void A_licence_naming_an_unknown_key_id_is_refused()
    {
        using var otherKey = RSA.Create(2048);
        var key = new LicenseIssuer(otherKey, "retired-2019").Issue(Tenant, ["core"], Now.AddYears(1));

        TestLicenses.Validator().Validate(key, Tenant, Now).Status.ShouldBe(LicenseStatus.UnknownSigningKey);
    }

    [Theory]
    [InlineData(null, LicenseStatus.Missing)]
    [InlineData("", LicenseStatus.Missing)]
    [InlineData("not a licence", LicenseStatus.Malformed)]
    [InlineData("BETSI1.e30.!!!", LicenseStatus.Malformed)]
    [InlineData("BETSI9.e30.AAAA", LicenseStatus.Malformed)]
    public void Missing_and_malformed_keys_are_restricted(string? key, LicenseStatus expected)
    {
        var result = TestLicenses.Validator().Validate(key, Tenant, Now);

        result.Status.ShouldBe(expected);
        result.Mode.ShouldBe(LicenseMode.Restricted);
    }

    [Fact]
    public void A_future_format_version_is_refused_even_if_correctly_signed()
    {
        var key = TestLicenses.Issuer.Sign(new LicensePayload
        {
            FormatVersion = 2,
            LicenseId = Guid.NewGuid(),
            KeyId = TestLicenses.KeyId,
            TenantId = Tenant,
            Features = ["core"],
            NotBefore = Now.AddDays(-1),
            ExpiresAt = Now.AddYears(1)
        });

        TestLicenses.Validator().Validate(key, Tenant, Now).Status.ShouldBe(LicenseStatus.UnsupportedFormat);
    }

    [Fact]
    public void Winding_the_clock_back_does_not_revive_an_expired_licence()
    {
        var key = Issue(Now.AddDays(-60), graceDays: 30);

        // The licence was last evaluated today; the server clock now claims it is two months ago.
        var result = TestLicenses.Validator().Validate(key, Tenant, now: Now.AddDays(-70), highWaterMark: Now);

        result.ClockRollbackDetected.ShouldBeTrue();
        result.EffectiveTime.ShouldBe(Now);
        result.Status.ShouldBe(LicenseStatus.Expired);
    }

    [Fact]
    public void A_small_clock_correction_is_not_treated_as_tampering()
    {
        var result = TestLicenses.Validator().Validate(
            Issue(Now.AddDays(100)), Tenant, now: Now.AddMinutes(-2), highWaterMark: Now);

        result.ClockRollbackDetected.ShouldBeFalse();
        result.Status.ShouldBe(LicenseStatus.Valid);
    }

    [Fact]
    public void Development_signing_keys_are_refused_outside_development()
    {
        var options = new LicensingOptions
        {
            TrustedKeys = [new TrustedLicenseKey { KeyId = "dev-2026-09", PublicKeyPem = TestLicenses.PublicKeyPem }]
        };

        Should.Throw<InvalidOperationException>(() => new LicenseValidator(options, isDevelopment: false))
            .Message.ShouldContain("development key");

        Should.NotThrow(() => new LicenseValidator(options, isDevelopment: true));
    }

    [Fact]
    public void A_trusted_key_that_is_not_a_public_key_fails_at_startup()
    {
        var options = new LicensingOptions
        {
            TrustedKeys = [new TrustedLicenseKey { KeyId = "prod-1", PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nnope\n-----END PUBLIC KEY-----" }]
        };

        Should.Throw<InvalidOperationException>(() => new LicenseValidator(options, isDevelopment: false));
    }
}
