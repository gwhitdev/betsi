namespace Betsi.Tests.Infrastructure.Outbox;

using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Outbox;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The outbox is the delivery guarantee. A message must be published exactly once on the
/// happy path, retried when delivery fails, and eventually abandoned rather than blocking
/// everything behind it.
/// </summary>
public class OutboxProcessorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A publisher whose behaviour each test dictates.</summary>
    private sealed class StubPublisher : IOutboxPublisher
    {
        private readonly Func<OutboxMessage, bool> _shouldFail;

        public StubPublisher(Func<OutboxMessage, bool>? shouldFail = null)
        {
            _shouldFail = shouldFail ?? (_ => false);
        }

        public List<Guid> Published { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (_shouldFail(message))
                throw new InvalidOperationException("Downstream unavailable");

            Published.Add(message.EventId);
            return Task.CompletedTask;
        }
    }

    private static async Task<TenantDatabase> ADatabaseWithQueuedEvents(int patientCount)
    {
        var database = new TenantDatabase(Guid.NewGuid(), Guid.NewGuid());

        await using var context = database.NewContext();
        var repository = database.NewRepository<PatientEpisode>(context);

        for (var i = 0; i < patientCount; i++)
        {
            var episode = PatientEpisode.CreateNew(
                database.TenantId, "Patient", $"Number{i}", new DateTime(1970, 1, 1),
                nhsNumber: null, database.ActorId, database.ActorRole);

            await repository.AddAsync(episode, Ct);
        }

        return database;
    }

    private static OutboxProcessor NewProcessor(
        TenantDatabase database,
        BetsiDbContext context,
        IOutboxPublisher publisher,
        OutboxOptions? options = null) =>
        new(context, publisher, options ?? new OutboxOptions(), TestMetrics.Instance, NullLogger<OutboxProcessor>.Instance);

    [Fact]
    public async Task Draining_an_empty_outbox_does_nothing()
    {
        await using var database = new TenantDatabase(Guid.NewGuid(), Guid.NewGuid());
        await using var context = database.NewContext();

        var result = await NewProcessor(database, context, new StubPublisher())
            .DrainAsync(database.TenantId, Ct);

        result.Total.ShouldBe(0);
    }

    [Fact]
    public async Task Queued_messages_are_published_and_marked_processed()
    {
        await using var database = await ADatabaseWithQueuedEvents(3);
        var publisher = new StubPublisher();

        await using (var context = database.NewContext())
        {
            var result = await NewProcessor(database, context, publisher)
                .DrainAsync(database.TenantId, Ct);

            result.Published.ShouldBe(3);
        }

        publisher.Published.Count.ShouldBe(3);

        await using var readContext = database.NewContext();
        (await readContext.OutboxMessages.CountAsync(m => m.ProcessedAt == null, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_message_is_never_published_twice()
    {
        await using var database = await ADatabaseWithQueuedEvents(2);
        var publisher = new StubPublisher();

        await using (var context = database.NewContext())
            await NewProcessor(database, context, publisher).DrainAsync(database.TenantId, Ct);

        await using (var context = database.NewContext())
        {
            var second = await NewProcessor(database, context, publisher)
                .DrainAsync(database.TenantId, Ct);

            // The second pass finds nothing left: ProcessedAt is what makes the drain
            // idempotent across restarts.
            second.Total.ShouldBe(0);
        }

        publisher.Published.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Messages_are_published_in_the_order_they_were_written()
    {
        await using var database = await ADatabaseWithQueuedEvents(5);
        var publisher = new StubPublisher();

        await using var context = database.NewContext();
        await NewProcessor(database, context, publisher).DrainAsync(database.TenantId, Ct);

        await using var readContext = database.NewContext();
        var writtenOrder = await readContext.OutboxMessages
            .OrderBy(m => m.Id).Select(m => m.EventId).ToListAsync(Ct);

        publisher.Published.ShouldBe(writtenOrder);
    }

    [Fact]
    public async Task A_failed_delivery_leaves_the_message_queued_for_retry()
    {
        await using var database = await ADatabaseWithQueuedEvents(1);
        var publisher = new StubPublisher(shouldFail: _ => true);

        await using (var context = database.NewContext())
        {
            var result = await NewProcessor(database, context, publisher)
                .DrainAsync(database.TenantId, Ct);

            result.Failed.ShouldBe(1);
            result.Published.ShouldBe(0);
        }

        await using var readContext = database.NewContext();
        var message = await readContext.OutboxMessages.SingleAsync(Ct);

        message.ProcessedAt.ShouldBeNull();
        message.Attempts.ShouldBe(1);
        message.LastError.ShouldNotBeNull().ShouldContain("Downstream unavailable");
    }

    [Fact]
    public async Task A_message_that_recovers_is_published_on_a_later_pass()
    {
        await using var database = await ADatabaseWithQueuedEvents(1);
        var downstreamIsDown = true;
        var publisher = new StubPublisher(shouldFail: _ => downstreamIsDown);

        await using (var context = database.NewContext())
            await NewProcessor(database, context, publisher).DrainAsync(database.TenantId, Ct);

        downstreamIsDown = false;

        await using (var context = database.NewContext())
        {
            var result = await NewProcessor(database, context, publisher)
                .DrainAsync(database.TenantId, Ct);

            result.Published.ShouldBe(1);
        }

        publisher.Published.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_message_that_never_succeeds_is_dead_lettered_rather_than_retried_forever()
    {
        await using var database = await ADatabaseWithQueuedEvents(1);
        var publisher = new StubPublisher(shouldFail: _ => true);
        var options = new OutboxOptions { MaxAttempts = 3 };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = database.NewContext();
            await NewProcessor(database, context, publisher, options)
                .DrainAsync(database.TenantId, Ct);
        }

        await using var readContext = database.NewContext();
        var message = await readContext.OutboxMessages.SingleAsync(Ct);

        message.Attempts.ShouldBe(3);
        message.ProcessedAt.ShouldNotBeNull();
        message.ProcessedBy.ShouldBe("dead-letter");
    }

    [Fact]
    public async Task A_poison_message_does_not_block_the_ones_behind_it()
    {
        await using var database = await ADatabaseWithQueuedEvents(3);
        var options = new OutboxOptions { MaxAttempts = 1 };

        Guid poisonEventId;
        await using (var context = database.NewContext())
        {
            poisonEventId = (await context.OutboxMessages.OrderBy(m => m.Id).FirstAsync(Ct)).EventId;
        }

        var publisher = new StubPublisher(shouldFail: m => m.EventId == poisonEventId);

        await using (var context = database.NewContext())
        {
            var result = await NewProcessor(database, context, publisher, options)
                .DrainAsync(database.TenantId, Ct);

            result.DeadLettered.ShouldBe(1);
            result.Published.ShouldBe(2);
        }

        publisher.Published.ShouldNotContain(poisonEventId);
    }

    [Fact]
    public async Task A_drain_pass_takes_no_more_than_the_batch_size()
    {
        await using var database = await ADatabaseWithQueuedEvents(5);
        var publisher = new StubPublisher();
        var options = new OutboxOptions { BatchSize = 2 };

        await using var context = database.NewContext();
        var result = await NewProcessor(database, context, publisher, options)
            .DrainAsync(database.TenantId, Ct);

        result.Published.ShouldBe(2);
    }

    [Fact]
    public async Task One_tenants_drain_does_not_touch_anothers_messages()
    {
        await using var database = await ADatabaseWithQueuedEvents(2);
        var publisher = new StubPublisher();

        await using var context = database.NewContext();
        var result = await NewProcessor(database, context, publisher)
            .DrainAsync(Guid.NewGuid(), Ct);

        result.Total.ShouldBe(0);
        publisher.Published.ShouldBeEmpty();
    }
}
