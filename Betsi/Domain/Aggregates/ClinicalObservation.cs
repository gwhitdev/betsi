namespace Betsi.Domain.Aggregates;

using Betsi.Domain.Clinical;

/// <summary>
/// One set of observations recorded for a patient at one moment (MVP-040, MVP-042).
/// </summary>
/// <remarks>
/// <para>
/// Its own aggregate rather than a collection on <see cref="PatientEpisode"/>. Observations are
/// written often and read as a series; holding them inside the episode would mean loading every
/// observation of a twelve-hour stay to record one more, and would put an unrelated write in
/// contention with every triage and discharge for the same episode's version.
/// </para>
/// <para>
/// <b>Append-only.</b> An observation is never edited. A correction is a new observation that
/// names the one it supersedes, and both remain in the record with who recorded each and when —
/// which is what an audit and, eventually, a coroner require (MVP-044). Normal persistence
/// rejects measurement updates and deletion; the database also permits only one replacement
/// per original through a unique index. These guards do not prevent privileged raw SQL writes.
/// </para>
/// <para>
/// Structured fields, not free text (MVP-040): a respiratory rate has to be a number in a known
/// unit for anything to score it or chart it. The one free-text field is the clinician's note,
/// which exists because no set of fields anticipates everything worth saying.
/// </para>
/// </remarks>
public class ClinicalObservation : AggregateRoot
{
    public enum ObservationSource
    {
        NursingAssessment = 1,
        MedicalReview = 2,
        TriageAssessment = 3,
        ClinicalHandover = 4,
        OtherClinicalAssessment = 5
    }

    /// <summary>
    /// A deliberately non-diagnostic structured finding. Site-approved clinical detail remains
    /// in the adjacent text field; this enum must not be interpreted as a score or trigger.
    /// </summary>
    public enum AssessmentFinding
    {
        NoConcern = 1,
        Concern = 2,
        UnableToAssess = 3
    }

    /// <summary>Why this set was taken.</summary>
    public enum ObservationKind
    {
        /// <summary>Routine observations.</summary>
        Routine = 1,

        /// <summary>Taken at triage.</summary>
        Triage = 2,

        /// <summary>Taken because someone was concerned.</summary>
        Concern = 3,

        /// <summary>A correction to an earlier set.</summary>
        Correction = 4
    }

    public Guid PatientEpisodeId { get; private set; }
    public ObservationKind Kind { get; private set; }

    public int? RespiratoryRate { get; private set; }
    public int? OxygenSaturation { get; private set; }
    public bool OnSupplementalOxygen { get; private set; }
    public int? SystolicBloodPressure { get; private set; }
    public int? Pulse { get; private set; }
    public Consciousness? Consciousness { get; private set; }
    public decimal? Temperature { get; private set; }

    public int? PainScore { get; private set; }
    public PainScale? PainScale { get; private set; }
    public string? PainLocation { get; private set; }
    public string? PainCharacter { get; private set; }
    public DateTime? PainOnsetAt { get; private set; }

    public ObservationSource? Source { get; private set; }
    public string? SbarSituation { get; private set; }
    public string? SbarBackground { get; private set; }
    public string? SbarAssessment { get; private set; }
    public string? SbarRecommendation { get; private set; }
    public AssessmentFinding? BreathingFinding { get; private set; }
    public string? BreathingDetails { get; private set; }
    public AssessmentFinding? CirculationFinding { get; private set; }
    public string? CirculationDetails { get; private set; }
    public AssessmentFinding? MobilityFinding { get; private set; }
    public string? MobilityDetails { get; private set; }

    /// <summary>The clinician's own words. Everything else is structured.</summary>
    public string? Notes { get; private set; }

    /// <summary>The age band the patient was in when this was recorded, for scoring and charting.</summary>
    public AgeBand AgeBand { get; private set; }

    /// <summary>
    /// The early warning score, where one could be produced. Null is not zero: see
    /// <see cref="ScoreUnavailableReason"/>.
    /// </summary>
    public int? EarlyWarningScore { get; private set; }

    public int? HighestSingleParameter { get; private set; }

    public ScoreUnavailableReason ScoreUnavailable { get; private set; }

    public DateTime RecordedAt { get; private set; }
    public Guid RecordedBy { get; private set; }
    public string RecordedByRole { get; private set; } = string.Empty;

    /// <summary>The observation this one corrects, if any. The corrected one is never removed.</summary>
    public Guid? SupersedesObservationId { get; private set; }

    /// <summary>Set when a later observation corrects this one, so a reader sees which is current.</summary>
    public Guid? SupersededByObservationId { get; private set; }

    private ClinicalObservation()
    {
    }

    public static ClinicalObservation Record(
        Guid tenantId,
        Guid patientEpisodeId,
        AgeBand ageBand,
        ObservationKind kind,
        VitalSigns vitals,
        int? painScore,
        PainScale? painScale,
        string? notes,
        DateTime now,
        Guid actorId,
        string actorRole,
        Guid? supersedes = null,
        string? painLocation = null,
        string? painCharacter = null,
        DateTime? painOnsetAt = null,
        ObservationSource? source = null,
        string? sbarSituation = null,
        string? sbarBackground = null,
        string? sbarAssessment = null,
        string? sbarRecommendation = null,
        AssessmentFinding? breathingFinding = null,
        string? breathingDetails = null,
        AssessmentFinding? circulationFinding = null,
        string? circulationDetails = null,
        AssessmentFinding? mobilityFinding = null,
        string? mobilityDetails = null)
    {
        if (patientEpisodeId == Guid.Empty)
            throw new DomainRuleViolationException("An observation must name the episode it belongs to.");

        // A pain score without its scale is not a measurement: 6 on FLACC and 6 self-reported
        // are different things and must never be charted as the same number.
        if (painScore is not null && painScale is null)
            throw new DomainRuleViolationException("A pain score must say which scale it was measured on.");

        if (painScore is not null && !PainScales.IsValid(painScale!.Value, painScore.Value))
        {
            throw new DomainRuleViolationException(
                $"{painScore} is not a valid score on the {painScale} scale.");
        }

        if (painScore is not null &&
            (string.IsNullOrWhiteSpace(painLocation) || string.IsNullOrWhiteSpace(painCharacter) || painOnsetAt is null))
        {
            throw new DomainRuleViolationException(
                "A pain assessment requires location, character and onset.");
        }

        if (painOnsetAt > now)
            throw new DomainRuleViolationException("Pain onset cannot be in the future.");

        if (!HasAnyMeasurement(vitals, painScore, notes, sbarSituation, sbarBackground,
                sbarAssessment, sbarRecommendation, breathingFinding, breathingDetails,
                circulationFinding, circulationDetails, mobilityFinding, mobilityDetails))
            throw new DomainRuleViolationException("An observation must record something.");

        var score = NationalEarlyWarningScore.Calculate(vitals, ageBand);

        var observation = new ClinicalObservation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PatientEpisodeId = patientEpisodeId,
            AgeBand = ageBand,
            Kind = supersedes is null ? kind : ObservationKind.Correction,
            RespiratoryRate = vitals.RespiratoryRate,
            OxygenSaturation = vitals.OxygenSaturation,
            OnSupplementalOxygen = vitals.OnSupplementalOxygen,
            SystolicBloodPressure = vitals.SystolicBloodPressure,
            Pulse = vitals.Pulse,
            Consciousness = vitals.Consciousness,
            Temperature = vitals.Temperature,
            PainScore = painScore,
            PainScale = painScale,
            PainLocation = Normalise(painLocation),
            PainCharacter = Normalise(painCharacter),
            PainOnsetAt = painOnsetAt,
            Source = source,
            SbarSituation = Normalise(sbarSituation),
            SbarBackground = Normalise(sbarBackground),
            SbarAssessment = Normalise(sbarAssessment),
            SbarRecommendation = Normalise(sbarRecommendation),
            BreathingFinding = breathingFinding,
            BreathingDetails = Normalise(breathingDetails),
            CirculationFinding = circulationFinding,
            CirculationDetails = Normalise(circulationDetails),
            MobilityFinding = mobilityFinding,
            MobilityDetails = Normalise(mobilityDetails),
            Notes = notes,
            EarlyWarningScore = score.Total,
            HighestSingleParameter = score.HasScore ? score.HighestSingleParameter : null,
            ScoreUnavailable = score.Unavailable,
            RecordedAt = now,
            RecordedBy = actorId,
            RecordedByRole = actorRole,
            SupersedesObservationId = supersedes
        };

        observation.RaiseDomainEvent(new ObservationRecorded
        {
            PatientEpisodeId = patientEpisodeId,
            Kind = observation.Kind.ToString(),
            AgeBand = ageBand.ToString(),
            EarlyWarningScore = score.Total,
            HighestSingleParameter = observation.HighestSingleParameter,
            ScoreUnavailable = score.Unavailable.ToString(),
            PainScore = painScore,
            PainScale = painScale?.ToString(),
            PainLocation = observation.PainLocation,
            PainCharacter = observation.PainCharacter,
            PainOnsetAt = observation.PainOnsetAt,
            Source = source?.ToString(),
            HasSbar = observation.SbarSituation is not null || observation.SbarBackground is not null ||
                observation.SbarAssessment is not null || observation.SbarRecommendation is not null,
            BreathingFinding = breathingFinding?.ToString(),
            CirculationFinding = circulationFinding?.ToString(),
            MobilityFinding = mobilityFinding?.ToString(),
            SupersedesObservationId = supersedes,
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });

        return observation;
    }

    /// <summary>
    /// Marks this observation as corrected by a later one.
    /// </summary>
    /// <remarks>
    /// The only state change an observation ever undergoes, and it changes no measurement: the
    /// original values stay exactly as they were recorded. Without this a reader has two
    /// contradictory sets of numbers and no way to know which one the clinician stands behind.
    /// </remarks>
    public void SupersededBy(Guid observationId, DateTime now, Guid actorId, string actorRole)
    {
        if (SupersededByObservationId is not null)
        {
            throw new DomainRuleViolationException(
                "This observation has already been corrected. Correct the current one instead.");
        }

        if (observationId == Id)
            throw new DomainRuleViolationException("An observation cannot supersede itself.");

        SupersededByObservationId = observationId;

        RaiseDomainEvent(new ObservationSuperseded
        {
            PatientEpisodeId = PatientEpisodeId,
            SupersededByObservationId = observationId,
            OccurredAt = now,
            ActorId = actorId,
            ActorRole = actorRole
        });
    }

    private static bool HasAnyMeasurement(VitalSigns vitals, int? painScore, string? notes,
        string? sbarSituation, string? sbarBackground, string? sbarAssessment,
        string? sbarRecommendation, AssessmentFinding? breathingFinding, string? breathingDetails,
        AssessmentFinding? circulationFinding, string? circulationDetails,
        AssessmentFinding? mobilityFinding, string? mobilityDetails) =>
        vitals.RespiratoryRate is not null || vitals.OxygenSaturation is not null
        || vitals.SystolicBloodPressure is not null || vitals.Pulse is not null
        || vitals.Consciousness is not null || vitals.Temperature is not null
        || painScore is not null || !string.IsNullOrWhiteSpace(notes)
        || !string.IsNullOrWhiteSpace(sbarSituation) || !string.IsNullOrWhiteSpace(sbarBackground)
        || !string.IsNullOrWhiteSpace(sbarAssessment) || !string.IsNullOrWhiteSpace(sbarRecommendation)
        || breathingFinding is not null || !string.IsNullOrWhiteSpace(breathingDetails)
        || circulationFinding is not null || !string.IsNullOrWhiteSpace(circulationDetails)
        || mobilityFinding is not null || !string.IsNullOrWhiteSpace(mobilityDetails);

    private static string? Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public class ObservationRecorded : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string AgeBand { get; set; } = string.Empty;
    public int? EarlyWarningScore { get; set; }
    public int? HighestSingleParameter { get; set; }
    public string ScoreUnavailable { get; set; } = string.Empty;
    public int? PainScore { get; set; }
    public string? PainScale { get; set; }
    public string? PainLocation { get; set; }
    public string? PainCharacter { get; set; }
    public DateTime? PainOnsetAt { get; set; }
    public string? Source { get; set; }
    public bool HasSbar { get; set; }
    public string? BreathingFinding { get; set; }
    public string? CirculationFinding { get; set; }
    public string? MobilityFinding { get; set; }
    public Guid? SupersedesObservationId { get; set; }
}

public class ObservationSuperseded : DomainEvent
{
    public Guid PatientEpisodeId { get; set; }
    public Guid SupersededByObservationId { get; set; }
}
