namespace Betsi.Domain.Aggregates;

/// <summary>
/// Location (bed, room, waiting area) aggregate root.
/// Tracks physical space availability and occupancy.
/// </summary>
public class Location : AggregateRoot
{
    public enum LocationState
    {
        Available = 1,   // Empty and ready for patient
        Occupied = 2,    // Currently occupied by patient
        Full = 3,        // All beds occupied
        Overflowing = 4, // Past capacity (waiting area)
        Maintenance = 5  // Out of service
    }

    // Properties
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public LocationState State { get; private set; }

    /// <summary>
    /// Maximum capacity (number of beds/spaces).
    /// </summary>
    public int Capacity { get; private set; }

    /// <summary>
    /// Current occupancy count.
    /// </summary>
    public int CurrentOccupancy { get; private set; }

    /// <summary>
    /// Queue ID for patients waiting in this location.
    /// </summary>
    public Guid? QueueId { get; private set; }

    /// <summary>
    /// Whether automatic escalation is configured for this location.
    /// </summary>
    public bool EscalationEnabled { get; private set; }

    // ============= Constructors =============

    protected Location() { }

    private Location(
        Guid id,
        Guid tenantId,
        string name,
        int capacity,
        string? description = null,
        bool escalationEnabled = true)
    {
        Id = id;
        TenantId = tenantId;
        Name = name;
        Capacity = capacity;
        Description = description;
        EscalationEnabled = escalationEnabled;
        State = LocationState.Available;
        CurrentOccupancy = 0;
        Version = 0;
    }

    // ============= Factory Methods =============

    public static Location CreateNew(
        Guid tenantId,
        string name,
        int capacity,
        string? description = null,
        bool escalationEnabled = true,
        Guid? actorId = null,
        string actorRole = "System")
    {
        if (capacity <= 0)
            throw new ArgumentException("Location capacity must be greater than 0", nameof(capacity));

        var location = new Location(
            Guid.NewGuid(),
            tenantId,
            name,
            capacity,
            description,
            escalationEnabled);

        location.RaiseDomainEvent(new LocationCreated
        {
            Name = name,
            Capacity = capacity,
            Description = description,
            EscalationEnabled = escalationEnabled,
            ActorId = actorId ?? Guid.Empty,
            ActorRole = actorRole
        });

        return location;
    }

    // ============= Command Handlers =============

    /// <summary>
    /// Assigns a patient to this location, updating occupancy.
    /// </summary>
    public void OccupySpace(Guid patientEpisodeId, Guid actorId, string actorRole)
    {
        if (CurrentOccupancy >= Capacity)
            throw new DomainRuleViolationException($"Location {Name} is at capacity");

        CurrentOccupancy++;
        UpdateState();

        RaiseDomainEvent(new LocationOccupied
        {
            PatientEpisodeId = patientEpisodeId,
            NewOccupancy = CurrentOccupancy,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Releases space in this location (patient moved or discharged).
    /// </summary>
    public void ReleaseSpace(Guid patientEpisodeId, Guid actorId, string actorRole)
    {
        if (CurrentOccupancy > 0)
            CurrentOccupancy--;

        UpdateState();

        RaiseDomainEvent(new LocationSpaceReleased
        {
            PatientEpisodeId = patientEpisodeId,
            NewOccupancy = CurrentOccupancy,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Marks location as undergoing maintenance.
    /// </summary>
    public void BeginMaintenance(Guid actorId, string actorRole)
    {
        State = LocationState.Maintenance;

        RaiseDomainEvent(new LocationMaintenanceStarted
        {
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Returns location to available status after maintenance.
    /// </summary>
    public void EndMaintenance(Guid actorId, string actorRole)
    {
        CurrentOccupancy = 0;
        UpdateState();

        RaiseDomainEvent(new LocationMaintenanceEnded
        {
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    private void UpdateState()
    {
        if (CurrentOccupancy == 0)
            State = LocationState.Available;
        else if (CurrentOccupancy < Capacity)
            State = LocationState.Occupied;
        else if (CurrentOccupancy == Capacity)
            State = LocationState.Full;
        else
            State = LocationState.Overflowing;
    }
}

// ============= Domain Events =============

public class LocationCreated : DomainEvent
{
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Description { get; set; }
    public bool EscalationEnabled { get; set; }
}

public class LocationOccupied : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public int NewOccupancy { get; set; }
}

public class LocationSpaceReleased : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public int NewOccupancy { get; set; }
}

public class LocationMaintenanceStarted : DomainEvent
{
}

public class LocationMaintenanceEnded : DomainEvent
{
}
