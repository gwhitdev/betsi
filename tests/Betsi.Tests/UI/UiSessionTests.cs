namespace Betsi.Tests.UI;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using Betsi.Security;
using Betsi.Tests.ControlPlane;
using Betsi.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

/// <summary>
/// The web interface's identity rules (Phase H).
/// </summary>
/// <remarks>
/// A Blazor circuit has no HTTP request, so it cannot use the tenant-resolution middleware. That
/// makes this the second place in the system where a tenant is derived from claims, and the one
/// a cross-tenant leak would come through. These tests hold it to the same rules as the
/// middleware: tenant from a verified claim only, unknown and suspended tenants refused, and a
/// session carrying several roles required to choose one.
/// </remarks>
public sealed class UiSessionTests : IAsyncLifetime
{
    private static readonly Guid Active = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Suspended = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Unregistered = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ControlPlaneHarness _controlPlane = new();
    private UiIdentityResolver _resolver = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var context = _controlPlane.CreateDbContext())
        {
            context.Tenants.AddRange(
                Tenant(Active, "Ysbyty Glan Clwyd", TenantState.Active),
                Tenant(Suspended, "Wrexham Maelor", TenantState.Suspended));

            await context.SaveChangesAsync(Ct);
        }

        await _controlPlane.Registry.RefreshAsync(Ct);

        _resolver = new UiIdentityResolver(
            _controlPlane.Registry, new BetsiClaimOptions(), NullLogger<UiIdentityResolver>.Instance);
    }

    public ValueTask DisposeAsync() => _controlPlane.DisposeAsync();

    private static TenantRecord Tenant(Guid id, string name, TenantState state) => new()
    {
        TenantId = id,
        Name = name,
        State = state,
        StateReason = state == TenantState.Suspended ? "Unpaid" : null,
        DatabaseServer = "default",
        DatabaseName = $"betsi_{id:N}",
        SchemaVersion = new FakeSchemaMigrator().LatestMigration,
        LicenseKey = TestLicenses.ValidFor(id),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Test"));

    [Fact]
    public void A_nurse_of_an_active_tenant_is_resolved()
    {
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Active.ToString()),
            ("sub", "nurse-1"),
            ("name", "Nia Roberts"),
            ("roles", "Nurse")));

        outcome.Reason.ShouldBeNull();
        outcome.Identity.ShouldNotBeNull();
        outcome.Identity.TenantId.ShouldBe(Active);
        outcome.Identity.TenantName.ShouldBe("Ysbyty Glan Clwyd");
        outcome.Identity.ActingRole.ShouldBe("Nurse");
        outcome.Identity.DisplayName.ShouldBe("Nia Roberts");

        // The same matrix the API enforces, so a screen cannot offer what a request would refuse.
        outcome.Identity.Can(Permissions.EscalationsRead).ShouldBeTrue();
        outcome.Identity.Can(Permissions.PolicyDecide).ShouldBeFalse();
    }

    [Fact]
    public void An_unauthenticated_visitor_is_refused()
    {
        var outcome = _resolver.Resolve(new ClaimsPrincipal(new ClaimsIdentity()));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldBe("You are not signed in.");
    }

    [Fact]
    public void Credentials_without_a_tenant_claim_are_refused()
    {
        // The identity provider authenticated them, but nothing says which department they work
        // in. Guessing one would be a cross-tenant leak waiting to happen.
        var outcome = _resolver.Resolve(User(("sub", "nurse-1"), ("roles", "Nurse")));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.ShouldContain("not associated with a department");
    }

    [Fact]
    public void An_unregistered_tenant_is_refused()
    {
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Unregistered.ToString()), ("sub", "x"), ("roles", "Nurse")));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.ShouldContain("not served by this system");
    }

    [Fact]
    public void A_suspended_tenant_is_refused()
    {
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Suspended.ToString()), ("sub", "x"), ("roles", "Nurse")));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.ShouldContain("suspended");
        // The operator's reason is not shown to a clinician: it can name commercial detail.
        outcome.Reason.ShouldNotContain("Unpaid");
    }

    [Fact]
    public void Several_roles_with_none_selected_is_refused()
    {
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Active.ToString()), ("sub", "x"),
            ("roles", "Nurse"), ("roles", "Clinical Lead")));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.ShouldContain("several roles");
    }

    [Fact]
    public void A_selected_acting_role_is_used_when_it_is_held()
    {
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Active.ToString()), ("sub", "x"),
            ("roles", "Nurse"), ("roles", "Clinical Lead"),
            ("betsi:acting_role", "Clinical Lead")));

        outcome.Identity!.ActingRole.ShouldBe("Clinical Lead");
        outcome.Identity.Can(Permissions.PolicyDecide).ShouldBeTrue();
    }

    [Fact]
    public void An_acting_role_the_account_does_not_hold_is_refused()
    {
        // Otherwise a claim the provider did not issue would be a promotion.
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Active.ToString()), ("sub", "x"),
            ("roles", "Nurse"),
            ("betsi:acting_role", "Clinical Lead")));

        outcome.Identity.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull();
        outcome.Reason.ShouldContain("several roles");
    }

    [Fact]
    public void A_session_with_no_role_resolves_with_no_permissions()
    {
        // Allowed through so each screen refuses in words, which says more than a blank page.
        var outcome = _resolver.Resolve(User(("betsi:tenant_id", Active.ToString()), ("sub", "x")));

        outcome.Identity.ShouldNotBeNull();
        outcome.Identity.ActingRole.ShouldBe("Unknown");
        foreach (var permission in Permissions.All)
            outcome.Identity.Can(permission).ShouldBeFalse();
    }

    [Fact]
    public void A_site_administrator_can_see_neither_board()
    {
        // The reason both board pages guard on a permission rather than only on being signed in.
        var outcome = _resolver.Resolve(User(
            ("betsi:tenant_id", Active.ToString()), ("sub", "x"), ("roles", "Site Administrator")));

        outcome.Identity!.Can(Permissions.EpisodesRead).ShouldBeFalse();
        outcome.Identity.Can(Permissions.EscalationsRead).ShouldBeFalse();
    }
}

public sealed class UiRouteTests
{
    [Theory]
    [InlineData("/api/v1/patients/register")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    [InlineData("/health/ready")]
    public void Machine_paths_are_judged_as_the_api(string path) =>
        UiRoutes.IsApiPath(path).ShouldBeTrue();

    [Theory]
    [InlineData("/")]
    [InlineData("/escalations")]
    [InlineData("/waiting")]
    [InlineData("/sign-in")]
    [InlineData("/_blazor")]
    public void Interface_paths_are_not(string path) =>
        UiRoutes.IsApiPath(path).ShouldBeFalse();

    [Theory]
    [InlineData("/escalations", "/escalations")]
    [InlineData("/waiting?filter=x", "/waiting?filter=x")]
    // An absolute URL, a protocol-relative one, or a backslash trick would each be an open
    // redirect: a link that signs a clinician in and lands them somewhere else, carrying the
    // trust of having just authenticated.
    [InlineData("https://elsewhere.example/steal", "/")]
    [InlineData("//elsewhere.example/steal", "/")]
    [InlineData("/\\elsewhere.example", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Only_local_return_paths_are_honoured(string? candidate, string expected) =>
        UiModule.SafeReturnUrl(candidate).ShouldBe(expected);
}
