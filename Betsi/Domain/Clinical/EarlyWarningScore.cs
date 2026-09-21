namespace Betsi.Domain.Clinical;

/// <summary>
/// How old a patient is, in the bands that change what is clinically normal.
/// </summary>
/// <remarks>
/// Derived from date of birth and never entered separately (hazard H-06): an age typed by hand
/// is an age that can be typed wrongly, and the consequence is a child assessed against adult
/// physiology.
/// </remarks>
public enum AgeBand
{
    /// <summary>Under one year.</summary>
    Infant = 1,

    /// <summary>One to four years.</summary>
    ToddlerOrPreschool = 2,

    /// <summary>Five to eleven years.</summary>
    SchoolAge = 3,

    /// <summary>Twelve to fifteen years.</summary>
    Adolescent = 4,

    /// <summary>Sixteen and over. NEWS2 is validated for this band and no other.</summary>
    Adult = 5
}

public static class AgeBands
{
    /// <summary>NEWS2 is validated for patients of sixteen and over (hazard H-06).</summary>
    public const int AdultFromYears = 16;

    /// <summary>Completed years between two dates. Birthdays, not 365-day arithmetic.</summary>
    public static int YearsBetween(DateTime dateOfBirth, DateTime now)
    {
        var years = now.Year - dateOfBirth.Year;

        if (now.Month < dateOfBirth.Month ||
            (now.Month == dateOfBirth.Month && now.Day < dateOfBirth.Day))
        {
            years--;
        }

        return Math.Max(0, years);
    }

    public static AgeBand For(int ageYears) => ageYears switch
    {
        < 1 => AgeBand.Infant,
        < 5 => AgeBand.ToddlerOrPreschool,
        < 12 => AgeBand.SchoolAge,
        < AdultFromYears => AgeBand.Adolescent,
        _ => AgeBand.Adult
    };

    public static AgeBand For(DateTime dateOfBirth, DateTime now) => For(YearsBetween(dateOfBirth, now));

    public static bool IsPaediatric(AgeBand band) => band != AgeBand.Adult;
}

/// <summary>Level of consciousness, as NEWS2 records it (ACVPU).</summary>
public enum Consciousness
{
    Alert = 1,
    NewConfusion = 2,
    RespondsToVoice = 3,
    RespondsToPain = 4,
    Unresponsive = 5
}

/// <summary>Which pain scale a score came from. A number without its scale means nothing.</summary>
public enum PainScale
{
    /// <summary>0–10, self-reported. For a patient who can give one.</summary>
    NumericRating = 1,

    /// <summary>Wong-Baker FACES, 0–10 in steps of two. Typically from about four years old.</summary>
    Faces = 2,

    /// <summary>FLACC, 0–10, observed. For a child too young or too unwell to self-report.</summary>
    Flacc = 3
}

/// <summary>What a clinician measured, in the units the scoring table expects.</summary>
/// <param name="RespiratoryRate">Breaths per minute.</param>
/// <param name="OxygenSaturation">Percentage.</param>
/// <param name="OnSupplementalOxygen">True if the patient is on oxygen.</param>
/// <param name="SystolicBloodPressure">mmHg.</param>
/// <param name="Pulse">Beats per minute.</param>
/// <param name="Temperature">Degrees Celsius.</param>
public readonly record struct VitalSigns(
    int? RespiratoryRate,
    int? OxygenSaturation,
    bool OnSupplementalOxygen,
    int? SystolicBloodPressure,
    int? Pulse,
    Consciousness? Consciousness,
    decimal? Temperature)
{
    public bool IsComplete =>
        RespiratoryRate is not null && OxygenSaturation is not null && SystolicBloodPressure is not null
        && Pulse is not null && Consciousness is not null && Temperature is not null;
}

/// <summary>Why a score could not be produced. Never a zero: absent is not normal.</summary>
public enum ScoreUnavailableReason
{
    None = 0,
    Incomplete = 1,
    NotValidatedForAge = 2,
    InvalidInput = 3
}

/// <param name="Total">The aggregate score, when one could be produced.</param>
/// <param name="HighestSingleParameter">The highest score from any one parameter. Three is significant on its own.</param>
public readonly record struct EarlyWarningResult(
    int? Total,
    int HighestSingleParameter,
    ScoreUnavailableReason Unavailable,
    IReadOnlyDictionary<string, int> Parameters)
{
    public bool HasScore => Total is not null;
}

/// <summary>
/// The National Early Warning Score 2 (NEWS2), as a lookup table.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The thresholds below were transcribed by the developer from the published NEWS2
/// specification and have NOT been verified by a clinician.</b> They must be checked value by
/// value before this system is used with real patients. See hazard H-05 in
/// <c>docs/HAZARD-LOG.md</c>, which records this as the most important open item in the log.
/// </para>
/// <para>
/// Nothing escalates from this score. A score is displayed beside the observation it came from,
/// so a clinician can check it against what they recorded; escalation is a human action
/// (MVP-041 is explicit that the MVP has no automatic vital-sign triggers). That is what keeps a
/// transcription error from becoming a missed or false alarm.
/// </para>
/// <para>
/// The table is expressed as ordered bands rather than nested conditionals so that a reviewer
/// can read it against the specification line by line, and so a correction is a change to data.
/// </para>
/// </remarks>
public static class NationalEarlyWarningScore
{
    /// <summary>A band of values and the points it scores. <c>null</c> bounds are open.</summary>
    private readonly record struct Band(decimal? From, decimal? To, int Points)
    {
        public bool Contains(decimal value) => (From is null || value >= From) && (To is null || value <= To);
    }

    private static int Score(IReadOnlyList<Band> bands, decimal value) =>
        bands.FirstOrDefault(b => b.Contains(value)).Points;

    // Respiration rate, breaths per minute.
    private static readonly Band[] RespiratoryRate =
    [
        new(null, 8, 3),
        new(9, 11, 1),
        new(12, 20, 0),
        new(21, 24, 2),
        new(25, null, 3)
    ];

    // Oxygen saturation, scale 1 (the default scale; scale 2 is for patients with hypercapnic
    // respiratory failure and a prescribed lower target range — deliberately not implemented,
    // because choosing the scale is a clinical decision this system has no way to make).
    private static readonly Band[] OxygenSaturation =
    [
        new(null, 91, 3),
        new(92, 93, 2),
        new(94, 95, 1),
        new(96, null, 0)
    ];

    private static readonly Band[] SystolicBloodPressure =
    [
        new(null, 90, 3),
        new(91, 100, 2),
        new(101, 110, 1),
        new(111, 219, 0),
        new(220, null, 3)
    ];

    private static readonly Band[] Pulse =
    [
        new(null, 40, 3),
        new(41, 50, 1),
        new(51, 90, 0),
        new(91, 110, 1),
        new(111, 130, 2),
        new(131, null, 3)
    ];

    private static readonly Band[] Temperature =
    [
        new(null, 35.0m, 3),
        new(35.1m, 36.0m, 1),
        new(36.1m, 38.0m, 0),
        new(38.1m, 39.0m, 1),
        new(39.1m, null, 2)
    ];

    /// <summary>Supplemental oxygen scores two; air scores nothing.</summary>
    private const int SupplementalOxygenPoints = 2;

    /// <summary>Anything other than alert scores three.</summary>
    private const int AlteredConsciousnessPoints = 3;

    /// <summary>
    /// Scores a set of observations, or explains why it will not.
    /// </summary>
    /// <remarks>
    /// Refuses in two cases, and returns no score rather than a low one in both. An incomplete
    /// set cannot be scored because a missing parameter is not a normal parameter. A patient
    /// under sixteen cannot be scored because NEWS2 is not validated for children, and a
    /// reassuring adult score on a sick child is the hazard this exists to avoid (H-06).
    /// </remarks>
    public static EarlyWarningResult Calculate(VitalSigns vitals, AgeBand band)
    {
        // Invalid values must never fall through a missing lookup band to a zero score.
        if (vitals.RespiratoryRate < 0 || vitals.Pulse < 0 || vitals.SystolicBloodPressure < 0 ||
            vitals.OxygenSaturation is < 0 or > 100 ||
            vitals.Consciousness is { } consciousness && !Enum.IsDefined(consciousness) ||
            vitals.Temperature is { } temperature &&
                (temperature < 0 || temperature > 99.9m || decimal.Round(temperature, 1) != temperature))
            return new(null, 0, ScoreUnavailableReason.InvalidInput, new Dictionary<string, int>());

        if (band != AgeBand.Adult)
            return new(null, 0, ScoreUnavailableReason.NotValidatedForAge, new Dictionary<string, int>());

        if (!vitals.IsComplete)
            return new(null, 0, ScoreUnavailableReason.Incomplete, new Dictionary<string, int>());

        var parameters = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["respiratoryRate"] = Score(RespiratoryRate, vitals.RespiratoryRate!.Value),
            ["oxygenSaturation"] = Score(OxygenSaturation, vitals.OxygenSaturation!.Value),
            ["supplementalOxygen"] = vitals.OnSupplementalOxygen ? SupplementalOxygenPoints : 0,
            ["systolicBloodPressure"] = Score(SystolicBloodPressure, vitals.SystolicBloodPressure!.Value),
            ["pulse"] = Score(Pulse, vitals.Pulse!.Value),
            ["consciousness"] = vitals.Consciousness == Clinical.Consciousness.Alert ? 0 : AlteredConsciousnessPoints,
            ["temperature"] = Score(Temperature, vitals.Temperature!.Value)
        };

        return new(
            parameters.Values.Sum(),
            parameters.Values.Max(),
            ScoreUnavailableReason.None,
            parameters);
    }
}

/// <summary>Which pain scale suits a patient of a given age.</summary>
/// <remarks>
/// A recommendation, not a rule: the clinician chooses, and a child who can self-report should.
/// The system records which scale was used, because a 6 on FLACC and a 6 self-reported are not
/// the same measurement and must not be charted as though they were.
/// </remarks>
public static class PainScales
{
    public static PainScale RecommendedFor(AgeBand band) => band switch
    {
        AgeBand.Infant or AgeBand.ToddlerOrPreschool => PainScale.Flacc,
        AgeBand.SchoolAge => PainScale.Faces,
        _ => PainScale.NumericRating
    };

    /// <summary>Every scale runs 0–10; FACES is recorded in steps of two.</summary>
    public static bool IsValid(PainScale scale, int score) =>
        Enum.IsDefined(scale) && score is >= 0 and <= 10 && (scale != PainScale.Faces || score % 2 == 0);
}
