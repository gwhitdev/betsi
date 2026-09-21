namespace Betsi.Application.Validation;

using Betsi.Application.Commands;
using Betsi.Domain.Aggregates;
using Betsi.Domain.Clinical;
using FluentValidation;

public sealed class RecordObservationCommandValidator : AbstractValidator<RecordObservationCommand>
{
    public RecordObservationCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).NotEmpty();
        RuleFor(x => x.Kind).Must(k => new[] { "Routine", "Triage", "Concern" }
            .Contains(k, StringComparer.OrdinalIgnoreCase)).WithMessage("Kind must be Routine, Triage or Concern.");
        RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.RespiratoryRate).GreaterThanOrEqualTo(0);
        RuleFor(x => x.OxygenSaturation).InclusiveBetween(0, 100);
        RuleFor(x => x.SystolicBloodPressure).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Pulse).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Temperature).InclusiveBetween(0m, 99.9m)
            .Must(t => t is null || decimal.Round(t.Value, 1) == t.Value)
            .WithMessage("Temperature must have at most one decimal place.");
        RuleFor(x => x.Consciousness).IsInEnum();
        RuleFor(x => x.PainScale).IsInEnum();
        RuleFor(x => x.PainScale).NotNull().When(x => x.PainScore is not null);
        RuleFor(x => x.PainScore).NotNull().When(x => x.PainScale is not null);
        RuleFor(x => x.PainLocation).NotEmpty().MaximumLength(200).When(x => x.PainScore is not null);
        RuleFor(x => x.PainCharacter).NotEmpty().MaximumLength(200).When(x => x.PainScore is not null);
        RuleFor(x => x.PainOnsetAt).NotNull().When(x => x.PainScore is not null);
        RuleFor(x => x.PainOnsetAt).LessThanOrEqualTo(DateTime.UtcNow)
            .When(x => x.PainOnsetAt is not null).WithMessage("Pain onset cannot be in the future.");
        RuleFor(x => x.PainLocation).Empty().When(x => x.PainScore is null);
        RuleFor(x => x.PainCharacter).Empty().When(x => x.PainScore is null);
        RuleFor(x => x.PainOnsetAt).Null().When(x => x.PainScore is null);
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.BreathingFinding).IsInEnum();
        RuleFor(x => x.CirculationFinding).IsInEnum();
        RuleFor(x => x.MobilityFinding).IsInEnum();
        RuleFor(x => x.SbarSituation).MaximumLength(2000);
        RuleFor(x => x.SbarBackground).MaximumLength(2000);
        RuleFor(x => x.SbarAssessment).MaximumLength(2000);
        RuleFor(x => x.SbarRecommendation).MaximumLength(2000);
        RuleFor(x => x.BreathingDetails).MaximumLength(1000);
        RuleFor(x => x.CirculationDetails).MaximumLength(1000);
        RuleFor(x => x.MobilityDetails).MaximumLength(1000);
        RuleFor(x => x).Must(x => x.PainScore is null || (x.PainScale is { } scale &&
            PainScales.IsValid(scale, x.PainScore.Value))).WithMessage("Pain score is invalid for its scale.");
        RuleFor(x => x.SupersedesObservationId).NotEqual(Guid.Empty);
        RuleFor(x => x.ExpectedVersion).NotNull().GreaterThan(0).When(x => x.SupersedesObservationId is not null);
        RuleFor(x => x.ExpectedVersion).Null().When(x => x.SupersedesObservationId is null);
        RuleFor(x => x).Must(x => x.RespiratoryRate is not null || x.OxygenSaturation is not null ||
            x.SystolicBloodPressure is not null || x.Pulse is not null || x.Temperature is not null ||
            x.Consciousness is not null || x.PainScore is not null || !string.IsNullOrWhiteSpace(x.Notes) ||
            !string.IsNullOrWhiteSpace(x.SbarSituation) || !string.IsNullOrWhiteSpace(x.SbarBackground) ||
            !string.IsNullOrWhiteSpace(x.SbarAssessment) || !string.IsNullOrWhiteSpace(x.SbarRecommendation) ||
            x.BreathingFinding is not null || !string.IsNullOrWhiteSpace(x.BreathingDetails) ||
            x.CirculationFinding is not null || !string.IsNullOrWhiteSpace(x.CirculationDetails) ||
            x.MobilityFinding is not null || !string.IsNullOrWhiteSpace(x.MobilityDetails))
            .WithMessage("An observation must record something.");
    }
}

public sealed class RecordCarerPresenceCommandValidator : AbstractValidator<RecordCarerPresenceCommand>
{
    public RecordCarerPresenceCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).NotEmpty();
        RuleFor(x => x.CarerName)
            .NotEmpty().When(x => x.CarerPresent)
            .MaximumLength(200);
        RuleFor(x => x.CarerRelationship).MaximumLength(200);
        RuleFor(x => x.CarerName).Empty().When(x => !x.CarerPresent)
            .WithMessage("Carer name must be omitted when no carer is present.");
        RuleFor(x => x.CarerRelationship).Empty().When(x => !x.CarerPresent)
            .WithMessage("Carer relationship must be omitted when no carer is present.");
    }
}

public sealed class AssignClinicalStaffCommandValidator : AbstractValidator<AssignClinicalStaffCommand>
{
    public AssignClinicalStaffCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).NotEmpty();
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
        RuleFor(x => x.AssignedStaffActorId).NotEmpty();
        RuleFor(x => x.AssignedStaffName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.AssignedStaffRole).NotEmpty().MaximumLength(100);
    }
}

public sealed class RaiseSafeguardingConcernCommandValidator : AbstractValidator<RaiseSafeguardingConcernCommand>
{
    public RaiseSafeguardingConcernCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ResponsibleRole).MaximumLength(100);
    }
}

public sealed class FlagPatientDeteriorationCommandValidator : AbstractValidator<FlagPatientDeteriorationCommand>
{
    public FlagPatientDeteriorationCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ResponsibleRole).MaximumLength(100);
    }
}
