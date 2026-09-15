namespace Betsi.Tests.Domain;

using Betsi.Domain;

/// <summary>
/// Tests for the aggregate versioning contract.
///
/// Version is the optimistic-concurrency token and the ordering key for the event log.
/// Every event raised must therefore advance the version by exactly one and carry the
/// version it produced, so that (AggregateId, Version) uniquely identifies an event and
/// events can be replayed in order.
/// </summary>
public class AggregateRootTests
{
    private sealed class TestEvent : DomainEvent
    {
        public string Payload { get; set; } = string.Empty;
    }

    private sealed class TestAggregate : AggregateRoot
    {
        public static TestAggregate Create(Guid tenantId)
        {
            var aggregate = new TestAggregate { Id = Guid.NewGuid(), TenantId = tenantId };
            aggregate.Raise("created");
            return aggregate;
        }

        public void Raise(string payload) => RaiseDomainEvent(new TestEvent { Payload = payload });

        private TestAggregate()
        {
        }
    }

    [Fact]
    public void Raising_an_event_advances_the_version_by_one()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());

        aggregate.Version.ShouldBe(1);

        aggregate.Raise("second");

        aggregate.Version.ShouldBe(2);
    }

    [Fact]
    public void Each_event_carries_the_version_it_produced()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());
        aggregate.Raise("second");
        aggregate.Raise("third");

        var versions = aggregate.GetUncommittedEvents().Select(e => e.Version).ToArray();

        versions.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Events_raised_in_one_unit_of_work_have_distinct_versions()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());
        aggregate.Raise("second");
        aggregate.Raise("third");

        var versions = aggregate.GetUncommittedEvents().Select(e => e.Version).ToArray();

        versions.Distinct().Count().ShouldBe(versions.Length);
    }

    [Fact]
    public void Events_are_stamped_with_aggregate_identity_and_tenant()
    {
        var tenantId = Guid.NewGuid();
        var aggregate = TestAggregate.Create(tenantId);

        var raised = aggregate.GetUncommittedEvents().Single();

        raised.AggregateId.ShouldBe(aggregate.Id);
        raised.AggregateType.ShouldBe(nameof(TestAggregate));
        raised.TenantId.ShouldBe(tenantId);
        raised.EventId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void Collecting_uncommitted_events_clears_them()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());

        aggregate.GetUncommittedEvents().Count.ShouldBe(1);
        aggregate.GetUncommittedEvents().ShouldBeEmpty();
    }

    [Fact]
    public void Collecting_uncommitted_events_does_not_change_the_version()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());
        aggregate.Raise("second");

        var versionBeforeCollection = aggregate.Version;
        aggregate.GetUncommittedEvents();

        aggregate.Version.ShouldBe(versionBeforeCollection);
    }

    [Fact]
    public void Every_event_has_a_unique_identifier_for_outbox_deduplication()
    {
        var aggregate = TestAggregate.Create(Guid.NewGuid());
        aggregate.Raise("second");

        var eventIds = aggregate.GetUncommittedEvents().Select(e => e.EventId).ToArray();

        eventIds.Distinct().Count().ShouldBe(eventIds.Length);
    }
}
