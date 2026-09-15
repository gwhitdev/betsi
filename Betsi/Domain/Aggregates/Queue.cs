namespace Betsi.Domain.Aggregates;

/// <summary>
/// Queue aggregate root.
/// Maintains an ordered list of waiting patients and threshold tracking for escalation.
/// </summary>
public class Queue : AggregateRoot
{
    /// <summary>
    /// Location this queue is associated with (e.g., waiting room, triage area).
    /// </summary>
    public Guid LocationId { get; private set; }

    /// <summary>
    /// Name of this queue (e.g., "Main Waiting Room", "Triage Queue").
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Ordered list of patient episode IDs currently in this queue.
    /// </summary>
    private readonly List<Guid> _patients = new();

    /// <summary>
    /// Public read-only access to patient queue.
    /// </summary>
    public IReadOnlyList<Guid> Patients => _patients.AsReadOnly();

    /// <summary>
    /// Whether this queue has exceeded the escalation threshold (typically 4 hours).
    /// </summary>
    public bool HasExceededEscalationThreshold { get; private set; }

    /// <summary>
    /// Timestamp when the queue first exceeded escalation threshold (if applicable).
    /// </summary>
    public DateTime? ExceededThresholdAt { get; private set; }

    // ============= Constructors =============

    protected Queue() { }

    private Queue(
        Guid id,
        Guid tenantId,
        Guid locationId,
        string name)
    {
        Id = id;
        TenantId = tenantId;
        LocationId = locationId;
        Name = name;
        HasExceededEscalationThreshold = false;
        Version = 0;
    }

    // ============= Factory Methods =============

    public static Queue CreateNew(
        Guid tenantId,
        Guid locationId,
        string name,
        Guid? actorId = null,
        string actorRole = "System")
    {
        var queue = new Queue(
            Guid.NewGuid(),
            tenantId,
            locationId,
            name);

        queue.RaiseDomainEvent(new QueueCreated
        {
            LocationId = locationId,
            Name = name,
            ActorId = actorId ?? Guid.Empty,
            ActorRole = actorRole
        });

        return queue;
    }

    // ============= Command Handlers =============

    /// <summary>
    /// Adds a patient to the end of the queue.
    /// </summary>
    public void EnqueuePatient(Guid patientEpisodeId, Guid actorId, string actorRole)
    {
        if (_patients.Contains(patientEpisodeId))
            throw new DomainRuleViolationException($"Patient {patientEpisodeId} is already in queue");

        _patients.Add(patientEpisodeId);

        RaiseDomainEvent(new PatientEnqueued
        {
            PatientEpisodeId = patientEpisodeId,
            QueuePosition = _patients.Count,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Removes a patient from the queue (e.g., moved to treatment or left).
    /// </summary>
    public void DequeuePatient(Guid patientEpisodeId, Guid actorId, string actorRole)
    {
        var index = _patients.IndexOf(patientEpisodeId);
        if (index == -1)
            throw new DomainRuleViolationException($"Patient {patientEpisodeId} not found in queue");

        _patients.RemoveAt(index);

        RaiseDomainEvent(new PatientDequeued
        {
            PatientEpisodeId = patientEpisodeId,
            PreviousQueuePosition = index + 1,
            NewQueueSize = _patients.Count,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Marks that the queue has exceeded escalation threshold.
    /// Used to trigger automatic escalation events.
    /// </summary>
    public void MarkEscalationThresholdExceeded(Guid escalationId, Guid actorId, string actorRole)
    {
        if (!HasExceededEscalationThreshold)
        {
            HasExceededEscalationThreshold = true;
            ExceededThresholdAt = DateTime.UtcNow;

            RaiseDomainEvent(new QueueEscalationThresholdExceeded
            {
                EscalationId = escalationId,
                QueueSize = _patients.Count,
                ActorId = actorId,
                ActorRole = actorRole
            });
        }
    }

    /// <summary>
    /// Resets the escalation threshold (e.g., when queue size reduces significantly).
    /// </summary>
    public void ResetEscalationThreshold(Guid actorId, string actorRole)
    {
        if (HasExceededEscalationThreshold)
        {
            HasExceededEscalationThreshold = false;
            ExceededThresholdAt = null;

            RaiseDomainEvent(new QueueEscalationThresholdReset
            {
                QueueSize = _patients.Count,
                ActorId = actorId,
                ActorRole = actorRole
            });
        }
    }

    /// <summary>
    /// Gets the current position of a patient in the queue (1-based index, 0 if not in queue).
    /// </summary>
    public int GetPatientQueuePosition(Guid patientEpisodeId)
    {
        var index = _patients.IndexOf(patientEpisodeId);
        return index >= 0 ? index + 1 : 0;
    }
}

// ============= Domain Events =============

public class QueueCreated : DomainEvent
{
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class PatientEnqueued : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public int QueuePosition { get; set; }
}

public class PatientDequeued : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public int PreviousQueuePosition { get; set; }
    public int NewQueueSize { get; set; }
}

public class QueueEscalationThresholdExceeded : DomainEvent
{
    public Guid EscalationId { get; set; }
    public int QueueSize { get; set; }
}

public class QueueEscalationThresholdReset : DomainEvent
{
    public int QueueSize { get; set; }
}
