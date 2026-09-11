namespace Betsi.Domain.Aggregates;

/// <summary>
/// Patient episode aggregate root.
/// Represents a patient's journey through the ED from arrival to discharge.
/// State machine: Waiting -> Triage -> Treatment -> Discharged
/// </summary>
public class PatientEpisode : AggregateRoot
{
    /// <summary>
    /// Possible states in the patient episode lifecycle.
    /// </summary>
    public enum PatientState
    {
        Waiting = 1,           // Newly registered, awaiting triage
        InTriage = 2,          // Currently undergoing triage
        AwaitingTreatment = 3, // Triaged, waiting for treatment area
        InTreatment = 4,       // Currently receiving treatment
        Discharged = 5,        // Episode concluded
        Cancelled = 6          // Episode cancelled (e.g., Left Without Being Seen)
    }

    // Properties
    public PatientState State { get; private set; }
    public string? NhsNumber { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public DateTime DateOfBirth { get; private set; }

    /// <summary>
    /// Resource ID if assigned to a location/bed (e.g., bed number, room ID).
    /// </summary>
    public Guid? LocationId { get; private set; }

    /// <summary>
    /// Queue position if currently in queue.
    /// </summary>
    public int? QueuePosition { get; private set; }

    /// <summary>
    /// Timestamp when this episode was created (patient arrived).
    /// </summary>
    public DateTime ArrivedAt { get; private set; }

    /// <summary>
    /// Timestamp when triage started (if applicable).
    /// </summary>
    public DateTime? TriageStartedAt { get; private set; }

    /// <summary>
    /// Timestamp when treatment started (if applicable).
    /// </summary>
    public DateTime? TreatmentStartedAt { get; private set; }

    /// <summary>
    /// Timestamp when episode ended (discharge or cancellation).
    /// </summary>
    public DateTime? EndedAt { get; private set; }

    /// <summary>
    /// Current waiting time (calculated as now - ArrivedAt or TreatmentStartedAt).
    /// Used to determine if escalation is needed.
    /// </summary>
    public TimeSpan CurrentWaitingTime => 
        State switch
        {
            PatientState.Discharged or PatientState.Cancelled => TimeSpan.Zero,
            PatientState.InTriage => TimeSpan.Zero, // Not waiting if in triage
            PatientState.InTreatment => TimeSpan.Zero, // Not waiting if in treatment
            _ => DateTime.UtcNow - ArrivedAt
        };

    // ============= Constructors =============

    /// <summary>
    /// Protected parameterless constructor for EF Core.
    /// </summary>
    protected PatientEpisode()
    {
    }

    /// <summary>
    /// Private constructor used by factory methods.
    /// </summary>
    private PatientEpisode(
        Guid id,
        Guid tenantId,
        string firstName,
        string lastName,
        DateTime dateOfBirth,
        string? nhsNumber = null)
    {
        Id = id;
        TenantId = tenantId;
        FirstName = firstName;
        LastName = lastName;
        DateOfBirth = dateOfBirth;
        NhsNumber = nhsNumber;
        State = PatientState.Waiting;
        ArrivedAt = DateTime.UtcNow;
        Version = 0;
    }

    // ============= Factory Methods =============

    /// <summary>
    /// Creates a new patient episode (patient registration).
    /// </summary>
    public static PatientEpisode CreateNew(
        Guid tenantId,
        string firstName,
        string lastName,
        DateTime dateOfBirth,
        string? nhsNumber = null,
        Guid? actorId = null,
        string actorRole = "System")
    {
        var episode = new PatientEpisode(
            Guid.NewGuid(),
            tenantId,
            firstName,
            lastName,
            dateOfBirth,
            nhsNumber);

        episode.RaiseDomainEvent(new PatientEpisodeCreated
        {
            FirstName = firstName,
            LastName = lastName,
            DateOfBirth = dateOfBirth,
            NhsNumber = nhsNumber,
            ActorId = actorId ?? Guid.Empty,
            ActorRole = actorRole
        });

        return episode;
    }

    // ============= Command Handlers =============

    /// <summary>
    /// Moves the patient from Waiting state to InTriage state.
    /// </summary>
    public void BeginTriage(Guid actorId, string actorRole)
    {
        if (State != PatientState.Waiting)
            throw new InvalidOperationException($"Cannot begin triage when patient is in state {State}");

        State = PatientState.InTriage;
        TriageStartedAt = DateTime.UtcNow;

        RaiseDomainEvent(new PatientTriageStarted
        {
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Completes triage and moves patient to AwaitingTreatment state.
    /// </summary>
    public void CompleteTriage(Guid actorId, string actorRole)
    {
        if (State != PatientState.InTriage)
            throw new InvalidOperationException($"Cannot complete triage when patient is in state {State}");

        State = PatientState.AwaitingTreatment;

        RaiseDomainEvent(new PatientTriageCompleted
        {
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Moves the patient to treatment.
    /// </summary>
    public void BeginTreatment(Guid locationId, Guid actorId, string actorRole)
    {
        if (State != PatientState.AwaitingTreatment && State != PatientState.InTriage)
            throw new InvalidOperationException($"Cannot begin treatment when patient is in state {State}");

        State = PatientState.InTreatment;
        LocationId = locationId;
        TreatmentStartedAt = DateTime.UtcNow;

        RaiseDomainEvent(new PatientTreatmentStarted
        {
            LocationId = locationId,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Discharges the patient and ends the episode.
    /// </summary>
    public void Discharge(Guid actorId, string actorRole, string? dischargeNotes = null)
    {
        if (State == PatientState.Discharged || State == PatientState.Cancelled)
            throw new InvalidOperationException($"Cannot discharge patient in state {State}");

        State = PatientState.Discharged;
        EndedAt = DateTime.UtcNow;

        RaiseDomainEvent(new PatientDischarged
        {
            DischargeNotes = dischargeNotes,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Cancels the episode (e.g., Left Without Being Seen).
    /// </summary>
    public void Cancel(Guid actorId, string actorRole, string? reason = null)
    {
        if (State == PatientState.Discharged || State == PatientState.Cancelled)
            throw new InvalidOperationException($"Cannot cancel patient in state {State}");

        State = PatientState.Cancelled;
        EndedAt = DateTime.UtcNow;

        RaiseDomainEvent(new PatientEpisodeCancelled
        {
            Reason = reason,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }
}

// ============= Domain Events =============

/// <summary>
/// Event raised when a new patient episode is created (patient registered).
/// </summary>
public class PatientEpisodeCreated : DomainEvent
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string? NhsNumber { get; set; }
}

/// <summary>
/// Event raised when patient triage begins.
/// </summary>
public class PatientTriageStarted : DomainEvent
{
}

/// <summary>
/// Event raised when patient triage is completed.
/// </summary>
public class PatientTriageCompleted : DomainEvent
{
}

/// <summary>
/// Event raised when patient moves into treatment.
/// </summary>
public class PatientTreatmentStarted : DomainEvent
{
    public Guid LocationId { get; set; }
}

/// <summary>
/// Event raised when patient is discharged.
/// </summary>
public class PatientDischarged : DomainEvent
{
    public string? DischargeNotes { get; set; }
}

/// <summary>
/// Event raised when patient episode is cancelled.
/// </summary>
public class PatientEpisodeCancelled : DomainEvent
{
    public string? Reason { get; set; }
}
