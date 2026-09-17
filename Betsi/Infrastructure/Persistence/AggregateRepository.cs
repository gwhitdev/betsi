namespace Betsi.Infrastructure.Persistence;

using Betsi.Domain;
using Betsi.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

/// <summary>
/// Loads and saves aggregates, and persists the events they raise.
/// </summary>
public interface IAggregateRepository<T> where T : AggregateRoot
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(T aggregate, CancellationToken cancellationToken = default);

    Task SaveAsync(T aggregate, CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Core implementation of <see cref="IAggregateRepository{T}"/>.
/// </summary>
/// <remarks>
/// Aggregate state, the event log and the outbox are written in one transaction. That
/// atomicity is the whole point of the outbox pattern: an event that is visible in the log
/// but not queued for delivery (or vice versa) is a silently lost escalation.
/// </remarks>
public class AggregateRepository<T> : IAggregateRepository<T> where T : AggregateRoot
{
    private readonly BetsiDbContext _context;
    private readonly ITenantContext _tenantContext;

    public AggregateRepository(BetsiDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Set<T>().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public async Task AddAsync(T aggregate, CancellationToken cancellationToken = default)
    {
        _context.Set<T>().Add(aggregate);
        await SaveAsync(aggregate, cancellationToken);
    }

    /// <summary>
    /// Persists pending changes to the aggregate together with its uncommitted events.
    /// </summary>
    /// <exception cref="DbUpdateConcurrencyException">
    /// The aggregate was modified by another writer since it was loaded.
    /// </exception>
    public async Task SaveAsync(T aggregate, CancellationToken cancellationToken = default)
    {
        AggregatePersistence.StageEvents(_context, _tenantContext.TenantId, aggregate);

        // One SaveChangesAsync, so aggregate + events + outbox share a transaction without
        // an explicit one. An explicit transaction would break the SQLite in-memory and
        // in-memory providers used by the unit tests for no gain here.
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Commits changes to more than one aggregate atomically.
/// </summary>
/// <remarks>
/// For the few operations that span aggregates by design — raising a follow-up exception and
/// marking its escalation — where committing one without the other would leave the board
/// showing an escalation in follow-up with no exception to review, or the reverse.
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>Tracks a newly created aggregate so that <see cref="CommitAsync"/> inserts it.</summary>
    void Add(AggregateRoot aggregate);

    /// <summary>Saves every tracked change and the events of the given aggregates in one transaction.</summary>
    Task CommitAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IUnitOfWork"/>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly BetsiDbContext _context;
    private readonly ITenantContext _tenantContext;

    public UnitOfWork(BetsiDbContext context, ITenantContext tenantContext)
    {
        _context = context;
        _tenantContext = tenantContext;
    }

    public void Add(AggregateRoot aggregate) => _context.Add(aggregate);

    public async Task CommitAsync(IEnumerable<AggregateRoot> aggregates, CancellationToken cancellationToken)
    {
        foreach (var aggregate in aggregates)
            AggregatePersistence.StageEvents(_context, _tenantContext.TenantId, aggregate);

        await _context.SaveChangesAsync(cancellationToken);
    }
}

internal static class AggregatePersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>Adds an aggregate's uncommitted events to the event log and the outbox.</summary>
    public static void StageEvents(BetsiDbContext context, Guid tenantId, AggregateRoot aggregate)
    {
        foreach (var domainEvent in aggregate.GetUncommittedEvents())
        {
            var eventType = domainEvent.GetType().Name;
            var data = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonOptions);

            context.DomainEvents.Add(new DomainEventRecord
            {
                TenantId = tenantId,
                EventId = domainEvent.EventId,
                AggregateId = domainEvent.AggregateId,
                AggregateType = domainEvent.AggregateType,
                EventType = eventType,
                EventData = data,
                Version = domainEvent.Version,
                ActorId = domainEvent.ActorId,
                ActorRole = domainEvent.ActorRole,
                OccurredAt = domainEvent.OccurredAt,
                CreatedAt = DateTime.UtcNow
            });

            context.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                EventId = domainEvent.EventId,
                AggregateId = domainEvent.AggregateId,
                AggregateType = domainEvent.AggregateType,
                EventType = eventType,
                EventData = data,
                CreatedAt = DateTime.UtcNow,
                ProcessedAt = null,
                ProcessedBy = null
            });
        }
    }
}
