namespace Betsi.Tests.Infrastructure;

using Betsi.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The repository is the only place that writes aggregate state, the event log and the
/// outbox, and it must write all three together or none.
/// </summary>
public class AggregateRepositoryTests
{
    /// <summary>The active test's cancellation token, so a hung test can be cut short.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TenantDatabase NewDatabase() =>
        new(Guid.NewGuid(), Guid.NewGuid());

    private static PatientEpisode ARegisteredPatient(TenantDatabase database) =>
        PatientEpisode.CreateNew(
            database.TenantId, "Gwen", "Jones", new DateTime(1962, 4, 19),
            "9434765919", database.ActorId, database.ActorRole);

    [Fact]
    public async Task An_aggregate_can_be_saved_and_read_back()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        await using var readContext = database.NewContext();
        var reloaded = await database.NewRepository<PatientEpisode>(readContext)
            .GetByIdAsync(episode.Id, Ct);

        reloaded.ShouldNotBeNull();
        reloaded.FirstName.ShouldBe("Gwen");
        reloaded.State.ShouldBe(PatientEpisode.PatientState.Waiting);
        reloaded.TenantId.ShouldBe(database.TenantId);
    }

    [Fact]
    public async Task Saving_writes_every_raised_event_to_the_event_log()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);
        episode.BeginTriage(database.ActorId, database.ActorRole);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        await using var readContext = database.NewContext();
        var events = await readContext.DomainEvents
            .Where(e => e.AggregateId == episode.Id)
            .OrderBy(e => e.Version)
            .ToListAsync(Ct);

        events.Select(e => e.EventType)
            .ShouldBe([nameof(PatientEpisodeCreated), nameof(PatientTriageStarted)]);
        events.Select(e => e.Version).ShouldBe([1, 2]);
        events.ShouldAllBe(e => e.TenantId == database.TenantId);
    }

    [Fact]
    public async Task Saving_queues_every_event_in_the_outbox_for_delivery()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        await using var readContext = database.NewContext();
        var outbox = await readContext.OutboxMessages.ToListAsync(Ct);

        outbox.Count.ShouldBe(1);
        outbox[0].EventType.ShouldBe(nameof(PatientEpisodeCreated));
        outbox[0].ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task The_event_log_and_the_outbox_agree_on_event_identity()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);
        episode.BeginTriage(database.ActorId, database.ActorRole);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        await using var readContext = database.NewContext();
        var loggedIds = await readContext.DomainEvents.Select(e => e.EventId).ToListAsync(Ct);
        var outboxIds = await readContext.OutboxMessages.Select(e => e.EventId).ToListAsync(Ct);

        // The event id is the consumer's deduplication key, so a mismatch here would make
        // redelivery indistinguishable from a new event.
        outboxIds.ShouldBe(loggedIds, ignoreOrder: true);
    }

    [Fact]
    public async Task Events_carry_the_actor_who_caused_them()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        await using var readContext = database.NewContext();
        var logged = await readContext.DomainEvents.SingleAsync(Ct);

        logged.ActorId.ShouldBe(database.ActorId);
        logged.ActorRole.ShouldBe(database.ActorRole);
    }

    [Fact]
    public async Task Nothing_is_written_when_the_save_fails()
    {
        await using var database = NewDatabase();
        var first = ARegisteredPatient(database);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(first, Ct);

        // The same NHS number twice violates the unique index. The second save must leave no
        // trace at all — a half-written event log is worse than a failed command.
        var duplicate = ARegisteredPatient(database);

        await using (var context = database.NewContext())
        {
            var repository = database.NewRepository<PatientEpisode>(context);
            await Should.ThrowAsync<DbUpdateException>(() => repository.AddAsync(duplicate, Ct));
        }

        await using var readContext = database.NewContext();
        (await readContext.PatientEpisodes.CountAsync(Ct)).ShouldBe(1);
        (await readContext.DomainEvents.CountAsync(Ct)).ShouldBe(1);
        (await readContext.OutboxMessages.CountAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_concurrent_write_is_rejected_rather_than_silently_overwriting()
    {
        await using var database = NewDatabase();
        var episode = ARegisteredPatient(database);

        await using (var context = database.NewContext())
            await database.NewRepository<PatientEpisode>(context).AddAsync(episode, Ct);

        // Two scopes load the same episode, then both act on it.
        await using var firstContext = database.NewContext();
        await using var secondContext = database.NewContext();

        var firstCopy = await database.NewRepository<PatientEpisode>(firstContext)
            .GetByIdAsync(episode.Id, Ct);
        var secondCopy = await database.NewRepository<PatientEpisode>(secondContext)
            .GetByIdAsync(episode.Id, Ct);

        firstCopy!.BeginTriage(database.ActorId, database.ActorRole);
        await database.NewRepository<PatientEpisode>(firstContext).SaveAsync(firstCopy, Ct);

        secondCopy!.BeginTriage(database.ActorId, database.ActorRole);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => database.NewRepository<PatientEpisode>(secondContext).SaveAsync(secondCopy, Ct));
    }

    [Fact]
    public async Task A_queues_patient_list_survives_a_round_trip()
    {
        await using var database = NewDatabase();
        var queue = Queue.CreateNew(
            database.TenantId, Guid.NewGuid(), "Main Waiting Room",
            database.ActorId, database.ActorRole);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        queue.EnqueuePatient(first, database.ActorId, database.ActorRole);
        queue.EnqueuePatient(second, database.ActorId, database.ActorRole);

        await using (var context = database.NewContext())
            await database.NewRepository<Queue>(context).AddAsync(queue, Ct);

        await using var readContext = database.NewContext();
        var reloaded = await database.NewRepository<Queue>(readContext).GetByIdAsync(queue.Id, Ct);

        // The list is stored as a JSON column; order is the whole point of a queue.
        reloaded.ShouldNotBeNull();
        reloaded.Patients.ShouldBe([first, second]);
    }

    [Fact]
    public async Task An_empty_queue_round_trips_without_becoming_null()
    {
        await using var database = NewDatabase();
        var queue = Queue.CreateNew(
            database.TenantId, Guid.NewGuid(), "Empty Queue", database.ActorId, database.ActorRole);

        await using (var context = database.NewContext())
            await database.NewRepository<Queue>(context).AddAsync(queue, Ct);

        await using var readContext = database.NewContext();
        var reloaded = await database.NewRepository<Queue>(readContext).GetByIdAsync(queue.Id, Ct);

        reloaded!.Patients.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_aggregate_reads_as_null()
    {
        await using var database = NewDatabase();

        await using var context = database.NewContext();
        var missing = await database.NewRepository<PatientEpisode>(context)
            .GetByIdAsync(Guid.NewGuid(), Ct);

        missing.ShouldBeNull();
    }
}
