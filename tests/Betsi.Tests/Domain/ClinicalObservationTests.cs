namespace Betsi.Tests.Domain;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Domain.Clinical;

public class ClinicalObservationTests
{
    private static ClinicalObservation Record(int? pain = null, PainScale? scale = null, string? notes = "Synthetic") =>
        ClinicalObservation.Record(Guid.NewGuid(), Guid.NewGuid(), AgeBand.Adolescent,
            ClinicalObservation.ObservationKind.Routine, new(16, 98, false, 120, 70, Consciousness.Alert, 37m),
            pain, scale, notes, DateTime.UtcNow, Guid.NewGuid(), "Nurse");

    [Fact]
    public void Paediatric_observations_retain_measurements_without_an_adult_score()
    {
        var observation = Record();
        observation.RespiratoryRate.ShouldBe(16);
        observation.EarlyWarningScore.ShouldBeNull();
        observation.HighestSingleParameter.ShouldBeNull();
        observation.ScoreUnavailable.ShouldBe(ScoreUnavailableReason.NotValidatedForAge);
        observation.Version.ShouldBe(1);
        var recorded = observation.GetUncommittedEvents().Single().ShouldBeOfType<ObservationRecorded>();
        recorded.EarlyWarningScore.ShouldBeNull();
        recorded.ActorId.ShouldBe(observation.RecordedBy);
        recorded.PatientEpisodeId.ShouldBe(observation.PatientEpisodeId);
    }

    [Theory]
    [InlineData(3, null)]
    [InlineData(3, PainScale.Faces)]
    [InlineData(-1, PainScale.NumericRating)]
    [InlineData(11, PainScale.Flacc)]
    [InlineData(2, (PainScale)99)]
    public void Pain_requires_a_valid_scale_and_score(int pain, PainScale? scale) =>
        Should.Throw<DomainRuleViolationException>(() => Record(pain, scale));

    [Fact]
    public void A_pain_assessment_requires_and_retains_structured_details()
    {
        var now = DateTime.UtcNow;
        var onset = now.AddMinutes(-15);
        var observation = ClinicalObservation.Record(Guid.NewGuid(), Guid.NewGuid(), AgeBand.Adult,
            ClinicalObservation.ObservationKind.Routine, default, 6, PainScale.NumericRating,
            notes: null, now, Guid.NewGuid(), "Nurse", painLocation: " Chest ",
            painCharacter: " Burning ", painOnsetAt: onset);

        observation.PainLocation.ShouldBe("Chest");
        observation.PainCharacter.ShouldBe("Burning");
        observation.PainOnsetAt.ShouldBe(onset);

        Should.Throw<DomainRuleViolationException>(() => ClinicalObservation.Record(
            Guid.NewGuid(), Guid.NewGuid(), AgeBand.Adult, ClinicalObservation.ObservationKind.Routine,
            default, 6, PainScale.NumericRating, null, now, Guid.NewGuid(), "Nurse"));
    }

    [Fact]
    public void Structured_templates_retain_source_SBAR_and_assessment_findings()
    {
        var observation = ClinicalObservation.Record(
            Guid.NewGuid(), Guid.NewGuid(), AgeBand.Adult,
            ClinicalObservation.ObservationKind.Concern, default, null, null, null,
            DateTime.UtcNow, Guid.NewGuid(), "Nurse",
            source: ClinicalObservation.ObservationSource.ClinicalHandover,
            sbarSituation: " New breathlessness ",
            sbarBackground: " Awaiting review ",
            sbarAssessment: " Concern remains ",
            sbarRecommendation: " Senior review requested ",
            breathingFinding: ClinicalObservation.AssessmentFinding.Concern,
            breathingDetails: " Increased effort ",
            circulationFinding: ClinicalObservation.AssessmentFinding.NoConcern,
            mobilityFinding: ClinicalObservation.AssessmentFinding.UnableToAssess,
            mobilityDetails: " Assessment deferred ");

        observation.Source.ShouldBe(ClinicalObservation.ObservationSource.ClinicalHandover);
        observation.SbarSituation.ShouldBe("New breathlessness");
        observation.SbarBackground.ShouldBe("Awaiting review");
        observation.SbarAssessment.ShouldBe("Concern remains");
        observation.SbarRecommendation.ShouldBe("Senior review requested");
        observation.BreathingFinding.ShouldBe(ClinicalObservation.AssessmentFinding.Concern);
        observation.BreathingDetails.ShouldBe("Increased effort");
        observation.CirculationFinding.ShouldBe(ClinicalObservation.AssessmentFinding.NoConcern);
        observation.MobilityFinding.ShouldBe(ClinicalObservation.AssessmentFinding.UnableToAssess);
        observation.MobilityDetails.ShouldBe("Assessment deferred");
        var recorded = observation.GetUncommittedEvents().Single().ShouldBeOfType<ObservationRecorded>();
        recorded.Source.ShouldBe("ClinicalHandover");
        recorded.HasSbar.ShouldBeTrue();
        recorded.BreathingFinding.ShouldBe("Concern");
    }

    [Fact]
    public void Supersession_preserves_measurements_and_cannot_be_repeated_or_self_referential()
    {
        var original = Record();
        Should.Throw<DomainRuleViolationException>(() => original.SupersededBy(original.Id, DateTime.UtcNow, Guid.NewGuid(), "Nurse"));
        var replacement = Guid.NewGuid();
        original.SupersededBy(replacement, DateTime.UtcNow, Guid.NewGuid(), "Nurse");
        original.SupersededByObservationId.ShouldBe(replacement);
        original.RespiratoryRate.ShouldBe(16);
        original.Notes.ShouldBe("Synthetic");
        original.Version.ShouldBe(2);
        original.GetUncommittedEvents().Select(e => e.Version).ShouldBe([1, 2]);
        Should.Throw<DomainRuleViolationException>(() => original.SupersededBy(Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), "Nurse"));
    }
}
