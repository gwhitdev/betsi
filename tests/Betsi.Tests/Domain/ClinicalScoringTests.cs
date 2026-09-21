namespace Betsi.Tests.Domain;

using Betsi.Domain.Clinical;

/// <summary>Independent boundary examples from RCP NEWS2 (2017), Chart 1, scale 1.
/// Reference: https://www.rcp.ac.uk/media/a4ibkkbf/news2-final-report_0_0.pdf.
/// Engineering checks do not constitute clinical approval.</summary>
public class ClinicalScoringTests
{
    private static readonly VitalSigns Normal = new(16, 98, false, 120, 70, Consciousness.Alert, 37m);

    [Theory]
    [InlineData("respiratoryRate", 8, 3)]
    [InlineData("respiratoryRate", 9, 1)]
    [InlineData("respiratoryRate", 11, 1)]
    [InlineData("respiratoryRate", 12, 0)]
    [InlineData("respiratoryRate", 20, 0)]
    [InlineData("respiratoryRate", 21, 2)]
    [InlineData("respiratoryRate", 24, 2)]
    [InlineData("respiratoryRate", 25, 3)]
    [InlineData("oxygenSaturation", 91, 3)]
    [InlineData("oxygenSaturation", 92, 2)]
    [InlineData("oxygenSaturation", 93, 2)]
    [InlineData("oxygenSaturation", 94, 1)]
    [InlineData("oxygenSaturation", 95, 1)]
    [InlineData("oxygenSaturation", 96, 0)]
    [InlineData("systolicBloodPressure", 90, 3)]
    [InlineData("systolicBloodPressure", 91, 2)]
    [InlineData("systolicBloodPressure", 100, 2)]
    [InlineData("systolicBloodPressure", 101, 1)]
    [InlineData("systolicBloodPressure", 110, 1)]
    [InlineData("systolicBloodPressure", 111, 0)]
    [InlineData("systolicBloodPressure", 219, 0)]
    [InlineData("systolicBloodPressure", 220, 3)]
    [InlineData("pulse", 40, 3)]
    [InlineData("pulse", 41, 1)]
    [InlineData("pulse", 50, 1)]
    [InlineData("pulse", 51, 0)]
    [InlineData("pulse", 90, 0)]
    [InlineData("pulse", 91, 1)]
    [InlineData("pulse", 110, 1)]
    [InlineData("pulse", 111, 2)]
    [InlineData("pulse", 130, 2)]
    [InlineData("pulse", 131, 3)]
    public void Integer_boundaries_match_the_reference(string parameter, int value, int expected)
    {
        var vitals = parameter switch
        {
            "respiratoryRate" => Normal with { RespiratoryRate = value },
            "oxygenSaturation" => Normal with { OxygenSaturation = value },
            "systolicBloodPressure" => Normal with { SystolicBloodPressure = value },
            "pulse" => Normal with { Pulse = value },
            _ => throw new ArgumentException(parameter)
        };
        var result = NationalEarlyWarningScore.Calculate(vitals, AgeBand.Adult);
        result.Parameters[parameter].ShouldBe(expected);
        result.Total.ShouldBe(expected);
        result.HighestSingleParameter.ShouldBe(expected);
    }

    [Theory]
    [InlineData("35.0", 3)]
    [InlineData("35.1", 1)]
    [InlineData("36.0", 1)]
    [InlineData("36.1", 0)]
    [InlineData("38.0", 0)]
    [InlineData("38.1", 1)]
    [InlineData("39.0", 1)]
    [InlineData("39.1", 2)]
    public void Temperature_boundaries_match_the_reference(string value, int expected)
    {
        var temperature = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        NationalEarlyWarningScore.Calculate(Normal with { Temperature = temperature }, AgeBand.Adult)
            .Total.ShouldBe(expected);
    }

    [Theory]
    [InlineData(Consciousness.Alert, 2)]
    [InlineData(Consciousness.NewConfusion, 5)]
    [InlineData(Consciousness.RespondsToVoice, 5)]
    [InlineData(Consciousness.RespondsToPain, 5)]
    [InlineData(Consciousness.Unresponsive, 5)]
    public void Oxygen_and_consciousness_are_scored(Consciousness consciousness, int total)
    {
        NationalEarlyWarningScore.Calculate(Normal with
            { Consciousness = consciousness, OnSupplementalOxygen = true }, AgeBand.Adult).Total.ShouldBe(total);
    }

    [Fact]
    public void Missing_invalid_and_paediatric_inputs_do_not_produce_a_score()
    {
        foreach (var vitals in new[] { Normal with { RespiratoryRate = null }, Normal with { Pulse = null },
                     Normal with { OxygenSaturation = null }, Normal with { SystolicBloodPressure = null },
                     Normal with { Temperature = null }, Normal with { Consciousness = null } })
        {
            var result = NationalEarlyWarningScore.Calculate(vitals, AgeBand.Adult);
            result.Total.ShouldBeNull();
            result.Unavailable.ShouldBe(ScoreUnavailableReason.Incomplete);
        }
        foreach (var vitals in new[] { Normal with { Temperature = 35.05m }, Normal with { Pulse = -1 },
                     Normal with { RespiratoryRate = -1 }, Normal with { SystolicBloodPressure = -1 },
                     Normal with { OxygenSaturation = 101 }, Normal with { Consciousness = (Consciousness)99 } })
        {
            var result = NationalEarlyWarningScore.Calculate(vitals, AgeBand.Adult);
            result.Total.ShouldBeNull();
            result.Unavailable.ShouldBe(ScoreUnavailableReason.InvalidInput);
        }
        foreach (var band in new[] { AgeBand.Infant, AgeBand.ToddlerOrPreschool, AgeBand.SchoolAge, AgeBand.Adolescent })
            NationalEarlyWarningScore.Calculate(Normal, band).Unavailable.ShouldBe(ScoreUnavailableReason.NotValidatedForAge);
    }

    [Fact]
    public void Age_boundary_uses_the_sixteenth_birthday()
    {
        var dob = new DateTime(2010, 9, 18);
        AgeBands.For(dob, new DateTime(2026, 9, 17)).ShouldBe(AgeBand.Adolescent);
        AgeBands.For(dob, new DateTime(2026, 9, 18)).ShouldBe(AgeBand.Adult);
    }
}
