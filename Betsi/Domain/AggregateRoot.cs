namespace Betsi.Domain;

/// <summary>
/// Base class for all aggregate roots in the Betsi domain model.
/// Aggregates are transactional consistency boundaries.
/// </summary>
public abstract class AggregateRoot
{
    /// <summary>
    /// Unique identifier for this aggregate instance (e.g., PatientEpisodeId, LocationId, etc).
    /// </summary>
    public Guid Id { get; protected set; }

    /// <summary>
    /// Sequence number for optimistic concurrency control.
    /// Incremented on every state change.
    /// </summary>
    public int Version { get; protected set; }

    /// <summary>
    /// Tenant ID for multi-tenant isolation.
    /// </summary>
    public Guid TenantId { get; protected set; }

    /// <summary>
    /// Collection of uncommitted domain events generated during this aggregate's lifecycle.
    /// Events are persisted to the event store and outbox in a single transaction.
    /// </summary>
    private readonly List<DomainEvent> _uncommittedEvents = new();

    /// <summary>
    /// Returns a snapshot of uncommitted events and clears the collection.
    /// Called by the repository after persisting events.
    /// </summary>
    public IReadOnlyList<DomainEvent> GetUncommittedEvents()
    {
        var events = _uncommittedEvents.ToList();
        _uncommittedEvents.Clear();
        return events;
    }

    /// <summary>
    /// Appends a domain event to the uncommitted events collection.
    /// Protected so only subclasses (aggregates) can raise events.
    /// </summary>
    protected void RaiseDomainEvent(DomainEvent @event)
    {
        // Version advances before the event is stamped so that every event carries the
        // version it produced. (AggregateId, Version) therefore uniquely identifies an
        // event and orders the log for replay. Version doubles as the EF concurrency
        // token: EF compares the value loaded from the database in the UPDATE predicate,
        // so incrementing here is what makes a concurrent write fail.
        Version++;

        @event.AggregateId = Id;
        @event.AggregateType = GetType().Name;
        @event.TenantId = TenantId;
        @event.Version = Version;

        _uncommittedEvents.Add(@event);
    }

}

/// <summary>
/// Base class for domain events.
/// Domain events represent something that happened in the domain and are immutable.
/// They are persisted to the event log for auditing and replay.
/// </summary>
public abstract class DomainEvent
{
    /// <summary>
    /// Unique event ID for deduplication in the outbox consumer.
    /// </summary>
    public Guid EventId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Aggregate ID that this event belongs to.
    /// </summary>
    public Guid AggregateId { get; set; }

    /// <summary>
    /// Aggregate type name (e.g., "PatientEpisode", "Location").
    /// </summary>
    public string AggregateType { get; set; } = string.Empty;

    /// <summary>
    /// Tenant ID for multi-tenant isolation.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Version of the aggregate at the time this event was generated.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Timestamp when this event occurred.
    /// </summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Actor ID (user or system) who triggered this event.
    /// </summary>
    public Guid ActorId { get; set; }

    /// <summary>
    /// Role of the actor (e.g., "Nurse", "Doctor", "System").
    /// </summary>
    public string ActorRole { get; set; } = string.Empty;
}
