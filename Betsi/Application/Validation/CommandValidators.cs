namespace Betsi.Application.Validation;

using Betsi.Application.Commands;
using FluentValidation;

/// <summary>
/// Rules shared by commands that target an existing aggregate.
/// </summary>
internal static class CommonRules
{
    public static IRuleBuilderOptions<T, Guid> MustBeAnIdentifier<T>(
        this IRuleBuilder<T, Guid> rule, string subject) =>
        rule.NotEmpty().WithMessage($"{subject} is required.");

    public static IRuleBuilderOptions<T, int> MustBeAVersion<T>(this IRuleBuilder<T, int> rule) =>
        rule.GreaterThan(0)
            .WithMessage("Expected version must be the version returned by the previous command.");
}

public sealed class RegisterPatientCommandValidator : AbstractValidator<RegisterPatientCommand>
{
    public RegisterPatientCommandValidator()
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);

        RuleFor(x => x.DateOfBirth)
            .NotEmpty()
            .Must(dob => dob.Date <= DateTime.UtcNow.Date)
            .WithMessage("Date of birth cannot be in the future.")
            // A date of birth before this is a data-entry error rather than a supercentenarian.
            .Must(dob => dob.Date >= new DateTime(1890, 1, 1))
            .WithMessage("Date of birth is implausible; check the entry.");

        // NHS numbers are 10 digits with a modulus-11 check digit. Validating the check digit
        // catches transposed digits at the point of entry, which is where a mis-keyed number
        // is cheapest to fix and most dangerous to miss.
        RuleFor(x => x.NhsNumber)
            .Must(NhsNumber.IsValid)
            .When(x => !string.IsNullOrWhiteSpace(x.NhsNumber))
            .WithMessage("NHS number must be 10 digits with a valid check digit.");
    }
}

public sealed class BeginPatientTriageCommandValidator : AbstractValidator<BeginPatientTriageCommand>
{
    public BeginPatientTriageCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
    }
}

public sealed class CompletePatientTriageCommandValidator
    : AbstractValidator<CompletePatientTriageCommand>
{
    public CompletePatientTriageCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
    }
}

public sealed class BeginPatientTreatmentCommandValidator
    : AbstractValidator<BeginPatientTreatmentCommand>
{
    public BeginPatientTreatmentCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.LocationId).MustBeAnIdentifier("Location id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
    }
}

public sealed class DischargePatientCommandValidator : AbstractValidator<DischargePatientCommand>
{
    public DischargePatientCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
        RuleFor(x => x.DischargeNotes).MaximumLength(2000);
    }
}

public sealed class CancelPatientEpisodeCommandValidator
    : AbstractValidator<CancelPatientEpisodeCommand>
{
    public CancelPatientEpisodeCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
        RuleFor(x => x.Reason).MaximumLength(2000);
    }
}

public sealed class CreateLocationCommandValidator : AbstractValidator<CreateLocationCommand>
{
    public CreateLocationCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Capacity)
            .GreaterThan(0).WithMessage("Location capacity must be greater than 0.");
    }
}

public sealed class CreateQueueCommandValidator : AbstractValidator<CreateQueueCommand>
{
    public CreateQueueCommandValidator()
    {
        RuleFor(x => x.LocationId).MustBeAnIdentifier("Location id");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
    }
}

public sealed class EnqueuePatientCommandValidator : AbstractValidator<EnqueuePatientCommand>
{
    public EnqueuePatientCommandValidator()
    {
        RuleFor(x => x.QueueId).MustBeAnIdentifier("Queue id");
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
    }
}

public sealed class TriggerWaitingTimeEscalationCommandValidator
    : AbstractValidator<TriggerWaitingTimeEscalationCommand>
{
    public TriggerWaitingTimeEscalationCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.LocationId).MustBeAnIdentifier("Location id");
        RuleFor(x => x.ResponsibleRole)
            .NotEmpty().WithMessage("An escalation must name the role responsible for acting on it.")
            .MaximumLength(100);
    }
}

public sealed class AcknowledgeEscalationCommandValidator
    : AbstractValidator<AcknowledgeEscalationCommand>
{
    public AcknowledgeEscalationCommandValidator()
    {
        RuleFor(x => x.EscalationId).MustBeAnIdentifier("Escalation id");
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public sealed class ResolveEscalationCommandValidator : AbstractValidator<ResolveEscalationCommand>
{
    public ResolveEscalationCommandValidator()
    {
        RuleFor(x => x.EscalationId).MustBeAnIdentifier("Escalation id");
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public sealed class ReassignEscalationCommandValidator : AbstractValidator<ReassignEscalationCommand>
{
    public ReassignEscalationCommandValidator()
    {
        RuleFor(x => x.EscalationId).MustBeAnIdentifier("Escalation id");
        RuleFor(x => x.ResponsibleRole).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("A reassignment must record why.")
            .MaximumLength(1000);
    }
}

public sealed class CloseFollowUpExceptionCommandValidator : AbstractValidator<CloseFollowUpExceptionCommand>
{
    public CloseFollowUpExceptionCommandValidator()
    {
        RuleFor(x => x.FollowUpExceptionId).MustBeAnIdentifier("Follow-up exception id");
        RuleFor(x => x.Outcome).IsInEnum().WithMessage("Outcome must be one of the defined review outcomes.");
        RuleFor(x => x.ReviewNotes)
            .NotEmpty().WithMessage("Closing a follow-up exception must record what the review found.")
            .MaximumLength(2000);
    }
}

public sealed class RaisePolicyEscalationCommandValidator : AbstractValidator<RaisePolicyEscalationCommand>
{
    public RaisePolicyEscalationCommandValidator()
    {
        RuleFor(x => x.PatientEpisodeId).MustBeAnIdentifier("Patient episode id");
        RuleFor(x => x.TierLevel).InclusiveBetween(1, Betsi.Domain.Aggregates.EscalationPolicy.MaxTiers);
        RuleFor(x => x.EvaluatedAt).NotEmpty();
    }
}

public sealed class RaiseFollowUpExceptionCommandValidator : AbstractValidator<RaiseFollowUpExceptionCommand>
{
    public RaiseFollowUpExceptionCommandValidator()
    {
        RuleFor(x => x.EscalationId).MustBeAnIdentifier("Escalation id");
        RuleFor(x => x.EvaluatedAt).NotEmpty();
    }
}

public sealed class ProposeEscalationPolicyCommandValidator : AbstractValidator<ProposeEscalationPolicyCommand>
{
    // Structural checks only. The clinical-safety rules — thresholds in order, within bounds,
    // deadlines sane — live in EscalationPolicy so no other path can bypass them.
    public ProposeEscalationPolicyCommandValidator()
    {
        RuleFor(x => x.Tiers).NotNull();
        RuleForEach(x => x.Tiers).ChildRules(tier =>
        {
            tier.RuleFor(t => t.ResponsibleRole).NotEmpty().MaximumLength(100);
            tier.RuleFor(t => t.RecommendedAction).NotEmpty().MaximumLength(500);
        });
        RuleFor(x => x.FollowUpOwnerRole).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public sealed class ProposeEscalationPolicyRestorationCommandValidator
    : AbstractValidator<ProposeEscalationPolicyRestorationCommand>
{
    public ProposeEscalationPolicyRestorationCommandValidator()
    {
        RuleFor(x => x.Revision).GreaterThan(0);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public sealed class ApproveEscalationPolicyCommandValidator : AbstractValidator<ApproveEscalationPolicyCommand>
{
    public ApproveEscalationPolicyCommandValidator()
    {
        RuleFor(x => x.PolicyId).MustBeAnIdentifier("Policy id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public sealed class RejectEscalationPolicyCommandValidator : AbstractValidator<RejectEscalationPolicyCommand>
{
    public RejectEscalationPolicyCommandValidator()
    {
        RuleFor(x => x.PolicyId).MustBeAnIdentifier("Policy id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}

public sealed class WithdrawEscalationPolicyCommandValidator : AbstractValidator<WithdrawEscalationPolicyCommand>
{
    public WithdrawEscalationPolicyCommandValidator()
    {
        RuleFor(x => x.PolicyId).MustBeAnIdentifier("Policy id");
        RuleFor(x => x.ExpectedVersion).MustBeAVersion();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(1000);
    }
}
