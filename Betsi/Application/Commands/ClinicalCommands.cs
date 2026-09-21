namespace Betsi.Application.Commands;

using Betsi.Domain.Clinical;
using Betsi.Domain.Aggregates;
using Betsi.Licensing;
using Betsi.Security;

/// <summary>
/// Records a set of observations for a patient (MVP-040, MVP-042).
/// </summary>
/// <remarks>
/// Always available: recording what a clinician has measured is not a feature a licence may
/// switch off (spec §5). Every field is optional individually, but the command must carry
/// something; the domain refuses an observation that records nothing.
/// </remarks>
[AlwaysAvailable("Clinical observation: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.ObservationsRecord)]
public class RecordObservationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }

    /// <summary>Routine, Triage or Concern. A correction is implied by <see cref="SupersedesObservationId"/>.</summary>
    public string Kind { get; set; } = "Routine";

    public int? RespiratoryRate { get; set; }
    public int? OxygenSaturation { get; set; }
    public bool OnSupplementalOxygen { get; set; }
    public int? SystolicBloodPressure { get; set; }
    public int? Pulse { get; set; }

    /// <summary>ACVPU: Alert, NewConfusion, RespondsToVoice, RespondsToPain, Unresponsive.</summary>
    public Consciousness? Consciousness { get; set; }

    public decimal? Temperature { get; set; }

    public int? PainScore { get; set; }

    /// <summary>NumericRating, Faces or Flacc. Required whenever a pain score is given.</summary>
    public PainScale? PainScale { get; set; }

    /// <summary>Where the patient reports or displays pain.</summary>
    public string? PainLocation { get; set; }

    /// <summary>Clinical description such as sharp, burning or cramping.</summary>
    public string? PainCharacter { get; set; }

    /// <summary>When the current pain began, when known.</summary>
    public DateTime? PainOnsetAt { get; set; }

    /// <summary>Where this observation originated, when known.</summary>
    public ClinicalObservation.ObservationSource? Source { get; set; }

    public string? SbarSituation { get; set; }
    public string? SbarBackground { get; set; }
    public string? SbarAssessment { get; set; }
    public string? SbarRecommendation { get; set; }
    public ClinicalObservation.AssessmentFinding? BreathingFinding { get; set; }
    public string? BreathingDetails { get; set; }
    public ClinicalObservation.AssessmentFinding? CirculationFinding { get; set; }
    public string? CirculationDetails { get; set; }
    public ClinicalObservation.AssessmentFinding? MobilityFinding { get; set; }
    public string? MobilityDetails { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// The observation this one corrects. The earlier one is kept and marked superseded;
    /// nothing is ever edited or deleted.
    /// </summary>
    public Guid? SupersedesObservationId { get; set; }

    /// <summary>Required for corrections: the version of the observation being superseded.</summary>
    public int? ExpectedVersion { get; set; }

}

/// <summary>
/// Records who is with the patient, or that nobody is (MVP-034).
/// </summary>
/// <remarks>
/// A child recorded as unaccompanied escalates to the safeguarding role. The age at which that
/// happens is configurable per site, because "child" is a local safeguarding policy decision.
/// </remarks>
[AlwaysAvailable("Safeguarding: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.PatientsCare)]
public class RecordCarerPresenceCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }

    public bool CarerPresent { get; set; }

    /// <summary>Required when a carer is present.</summary>
    public string? CarerName { get; set; }

    public string? CarerRelationship { get; set; }
}

/// <summary>Assigns a clinician to an episode and records paediatric competence (MVP-033).</summary>
[AlwaysAvailable("Clinical staffing: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.PatientsCare)]
public class AssignClinicalStaffCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
    public Guid AssignedStaffActorId { get; set; }
    public string AssignedStaffName { get; set; } = string.Empty;
    public string AssignedStaffRole { get; set; } = string.Empty;
    public bool PaediatricTrained { get; set; }
}

/// <summary>
/// Raises a safeguarding concern, which escalates immediately (MVP-032).
/// </summary>
[AlwaysAvailable("Safeguarding: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.SafeguardingRaise)]
public class RaiseSafeguardingConcernCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }

    /// <summary>What was observed. Clinical free text; never leaves the tenant database.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Overrides the site's default safeguarding role, when one is known locally.</summary>
    public string? ResponsibleRole { get; set; }
}

/// <summary>
/// A clinician's judgement that a patient is deteriorating, which escalates (MVP-041).
/// </summary>
/// <remarks>
/// Manual by design. The MVP raises nothing automatically from vital signs — a score informs a
/// clinician, and the clinician decides (hazard H-05).
/// </remarks>
[AlwaysAvailable("Deterioration: a safety function licensing must never disable (spec §5).")]
[RequiresPermission(Permissions.PatientsCare)]
public class FlagPatientDeteriorationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }

    /// <summary>What the clinician observed: "declining consciousness", "respiratory distress".</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Who should respond. Defaults to the site's senior clinical role.</summary>
    public string? ResponsibleRole { get; set; }
}
