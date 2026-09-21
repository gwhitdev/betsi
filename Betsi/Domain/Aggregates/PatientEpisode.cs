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
    /// Who is with the patient, when anyone is. Recorded for every patient, and the thing an
    /// unaccompanied-child alert turns on (MVP-034).
    /// </summary>
    public string? CarerName { get; private set; }

    /// <summary>Their relationship to the patient: "Mother", "Foster carer", "Neighbour".</summary>
    public string? CarerRelationship { get; private set; }

    /// <summary>
    /// Whether a carer is currently present. Distinct from <see cref="CarerName"/> being null:
    /// "nobody has asked" and "asked, and the patient is alone" are different states, and only
    /// the second one is a safeguarding fact.
    /// </summary>
    public bool? CarerPresent { get; private set; }

    public DateTime? CarerPresenceRecordedAt { get; private set; }

    /// <summary>
    /// A safeguarding concern has been raised on this episode (MVP-032). Never cleared by this
    /// aggregate: a concern that was raised stays raised, and its outcome is recorded on the
    /// escalation it created.
    /// </summary>
    public bool SafeguardingConcernRaised { get; private set; }

    /// <summary>
    /// A clinician has flagged this patient as deteriorating (MVP-041). Set by the flag and
    /// cleared by nothing: what follows is an escalation someone has to answer.
    /// </summary>
    public bool DeteriorationFlagged { get; private set; }

    public DateTime? DeteriorationFlaggedAt { get; private set; }

    /// <summary>The clinician currently assigned to this episode (MVP-033).</summary>
    public Guid? AssignedStaffActorId { get; private set; }
    public string? AssignedStaffName { get; private set; }
    public string? AssignedStaffRole { get; private set; }
    public bool? AssignedStaffPaediatricTrained { get; private set; }
    public DateTime? StaffAssignedAt { get; private set; }

    /// <summary>
    /// True after the current lack of paediatric competence has raised an alert. A trained
    /// assignment clears it so a later, distinct gap can alert again.
    /// </summary>
    public bool PaediatricSkillGapAlertOpen { get; private set; }

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

    /// <summary>The patient's age in completed years at <paramref name="now"/>.</summary>
    public int AgeYearsAt(DateTime now) => Clinical.AgeBands.YearsBetween(DateOfBirth, now);

    /// <summary>
    /// The age band that decides what is clinically normal for this patient (MVP-030, MVP-031).
    /// </summary>
    /// <remarks>
    /// Always derived, never stored: a child has a birthday during a long stay, and a band
    /// written down at registration would then be wrong for the rest of it.
    /// </remarks>
    public Clinical.AgeBand AgeBandAt(DateTime now) => Clinical.AgeBands.For(DateOfBirth, now);

    public bool IsPaediatricAt(DateTime now) => Clinical.AgeBands.IsPaediatric(AgeBandAt(now));

    // ============= Command Handlers =============

    /// <summary>
    /// Records who is with the patient, or that nobody is (MVP-034).
    /// </summary>
    /// <remarks>
    /// Allowed in any state including after discharge: a carer arriving late, or leaving, is a
    /// fact about the episode whatever the patient's clinical state. Every change is an event,
    /// because "when did we last know somebody was with this child" is the question a
    /// safeguarding review asks.
    /// </remarks>
    public void RecordCarerPresence(
        bool present, string? carerName, string? relationship, DateTime now, Guid actorId, string actorRole)
    {
        if (present && string.IsNullOrWhiteSpace(carerName))
            throw new DomainRuleViolationException("Recording a carer as present requires their name.");

        var wasPresent = CarerPresent;

        CarerPresent = present;
        CarerName = present ? carerName!.Trim() : null;
        CarerRelationship = present ? relationship?.Trim() : null;
        CarerPresenceRecordedAt = now;

        RaiseDomainEvent(new CarerPresenceRecorded
        {
            CarerPresent = present,
            CarerRelationship = present ? CarerRelationship : null,
            PreviouslyPresent = wasPresent,
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Records that a safeguarding concern has been raised (MVP-032).
    /// </summary>
    /// <remarks>
    /// The escalation that must follow is created by the handler, in the same transaction. This
    /// aggregate only records that the concern exists; it is deliberately not possible to
    /// withdraw one here.
    /// </remarks>
    public void RaiseSafeguardingConcern(DateTime now, Guid actorId, string actorRole)
    {
        if (SafeguardingConcernRaised)
        {
            // Not an error: two clinicians noticing the same thing is expected. The second is
            // recorded as an event and changes nothing, so nobody is told their concern was
            // rejected.
            RaiseDomainEvent(new SafeguardingConcernRepeated
            {
                OccurredAt = now, ActorId = actorId, ActorRole = actorRole
            });
            return;
        }

        SafeguardingConcernRaised = true;

        RaiseDomainEvent(new SafeguardingConcernRaised
        {
            OccurredAt = now, ActorId = actorId, ActorRole = actorRole
        });
    }

    /// <summary>
    /// A clinician's judgement that this patient is deteriorating (MVP-041).
    /// </summary>
    /// <remarks>
    /// Manual, always. The MVP raises nothing automatically from vital signs: a score is shown
    /// to a clinician, and a clinician decides. See hazard H-05 — an escalation engine driven by
    /// a transcription of a scoring table nobody has clinically verified would be worse than no
    /// engine at all.
    /// </remarks>
    public void FlagDeterioration(string reason, DateTime now, Guid actorId, string actorRole)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainRuleViolationException("A deterioration flag must say what was observed.");

        if (State is PatientState.Discharged or PatientState.Cancelled)
        {
            throw new DomainRuleViolationException(
                $"Cannot flag deterioration for a patient who is {State}.");
        }

        DeteriorationFlagged = true;
        DeteriorationFlaggedAt = now;

        RaiseDomainEvent(new PatientDeteriorationFlagged
        {
            Reason = reason.Trim(),
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>Records the clinician responsible for the episode and their declared competence.</summary>
    public void AssignClinicalStaff(
        Guid assignedActorId, string assignedName, string assignedRole, bool paediatricTrained,
        DateTime now, Guid actorId, string actorRole)
    {
        if (assignedActorId == Guid.Empty)
            throw new DomainRuleViolationException("Assigned staff must have a valid identity.");
        if (string.IsNullOrWhiteSpace(assignedName))
            throw new DomainRuleViolationException("Assigned staff must have a name.");
        if (string.IsNullOrWhiteSpace(assignedRole))
            throw new DomainRuleViolationException("Assigned staff must have a role.");

        AssignedStaffActorId = assignedActorId;
        AssignedStaffName = assignedName.Trim();
        AssignedStaffRole = assignedRole.Trim();
        AssignedStaffPaediatricTrained = paediatricTrained;
        StaffAssignedAt = now;
        if (paediatricTrained)
            PaediatricSkillGapAlertOpen = false;

        RaiseDomainEvent(new ClinicalStaffAssigned
        {
            AssignedStaffActorId = assignedActorId,
            AssignedStaffRole = AssignedStaffRole,
            PaediatricTrained = paediatricTrained,
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>Marks that the current paediatric competence gap has produced an alert.</summary>
    public void MarkPaediatricSkillGapAlerted(DateTime now, Guid actorId, string actorRole)
    {
        if (PaediatricSkillGapAlertOpen)
            return;

        PaediatricSkillGapAlertOpen = true;
        RaiseDomainEvent(new PaediatricSkillGapDetected
        {
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    /// <summary>
    /// Moves the patient from Waiting state to InTriage state.
    /// </summary>
    public void BeginTriage(Guid actorId, string actorRole)
    {
        if (State != PatientState.Waiting)
            throw new DomainRuleViolationException($"Cannot begin triage when patient is in state {State}");

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
            throw new DomainRuleViolationException($"Cannot complete triage when patient is in state {State}");

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
            throw new DomainRuleViolationException($"Cannot begin treatment when patient is in state {State}");

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
            throw new DomainRuleViolationException($"Cannot discharge patient in state {State}");

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
            throw new DomainRuleViolationException($"Cannot cancel patient in state {State}");

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
public class CarerPresenceRecorded : DomainEvent
{
    public bool CarerPresent { get; set; }

    /// <summary>The relationship, not the carer's name: an event is published to subscribers.</summary>
    public string? CarerRelationship { get; set; }

    public bool? PreviouslyPresent { get; set; }
}

public class SafeguardingConcernRaised : DomainEvent
{
}

/// <summary>A second concern on an episode that already has one. Recorded, changes nothing.</summary>
public class SafeguardingConcernRepeated : DomainEvent
{
}

public class PatientDeteriorationFlagged : DomainEvent
{
    /// <summary>What the clinician observed. Free text, and clinical: never published outward.</summary>
    public string Reason { get; set; } = string.Empty;
}

public class ClinicalStaffAssigned : DomainEvent
{
    public Guid AssignedStaffActorId { get; set; }
    public string AssignedStaffRole { get; set; } = string.Empty;
    public bool PaediatricTrained { get; set; }
}

public class PaediatricSkillGapDetected : DomainEvent
{
}

public class PatientEpisodeCancelled : DomainEvent
{
    public string? Reason { get; set; }
}
