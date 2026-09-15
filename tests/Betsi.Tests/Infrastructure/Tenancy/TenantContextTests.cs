namespace Betsi.Tests.Infrastructure.Tenancy;

using Betsi.Infrastructure.Tenancy;

/// <summary>
/// The tenant context must fail closed: an unresolved context is never usable, because a
/// silently-empty tenant id would read or write another organisation's patient records.
/// </summary>
public class TenantContextTests
{
    [Fact]
    public void A_new_context_is_not_resolved()
    {
        var context = new TenantContext();

        context.IsResolved.ShouldBeFalse();
    }

    [Fact]
    public void Reading_the_tenant_id_before_resolution_throws()
    {
        var context = new TenantContext();

        Should.Throw<TenantNotResolvedException>(() => _ = context.TenantId);
    }

    [Fact]
    public void Reading_the_actor_before_resolution_throws()
    {
        var context = new TenantContext();

        Should.Throw<TenantNotResolvedException>(() => _ = context.ActorId);
    }

    [Fact]
    public void Resolving_makes_the_tenant_and_actor_available()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var context = new TenantContext();

        context.Resolve(tenantId, actorId, "Nurse");

        context.IsResolved.ShouldBeTrue();
        context.TenantId.ShouldBe(tenantId);
        context.ActorId.ShouldBe(actorId);
        context.ActorRole.ShouldBe("Nurse");
    }

    [Fact]
    public void An_empty_tenant_id_is_not_a_valid_resolution()
    {
        var context = new TenantContext();

        Should.Throw<ArgumentException>(() => context.Resolve(Guid.Empty, Guid.NewGuid(), "Nurse"));
    }

    [Fact]
    public void A_context_cannot_be_re_resolved_to_a_different_tenant()
    {
        var context = new TenantContext();
        context.Resolve(Guid.NewGuid(), Guid.NewGuid(), "Nurse");

        Should.Throw<InvalidOperationException>(
            () => context.Resolve(Guid.NewGuid(), Guid.NewGuid(), "Doctor"));
    }
}
