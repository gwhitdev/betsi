namespace Betsi.Tests.UI;

using Betsi.Domain;
using Betsi.Security;
using Betsi.UI;
using Betsi.UI.Resources;
using FluentValidation;
using Microsoft.Extensions.Localization;

public sealed class UiActionFeedbackTests
{
    [Theory]
    [InlineData("validation", "ActionValidationRecovery")]
    [InlineData("conflict", "ActionConflictRecovery")]
    [InlineData("permission", "ActionPermissionRecovery")]
    [InlineData("service", "ActionServiceRecovery")]
    public void Failure_feedback_names_the_task_without_exposing_exception_details(string kind, string recovery)
    {
        Exception failure = kind switch
        {
            "validation" => new DomainRuleViolationException("private validation detail"),
            "conflict" => new AggregateConcurrencyException("PatientEpisode", Guid.NewGuid(), 1, 2),
            "permission" => new PermissionDeniedException("patients.care", "private role"),
            _ => new InvalidOperationException("private infrastructure detail")
        };

        var feedback = UiActionFeedback.For(failure, "Synthetic patient", new KeyLocalizer());

        feedback.ShouldContain("Synthetic patient");
        feedback.ShouldContain(recovery);
        feedback.ShouldNotContain("private");
        feedback.ShouldNotContain("PatientEpisode");
    }

    private sealed class KeyLocalizer : IStringLocalizer<Strings>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] =>
            new(name, $"{name}: {string.Join(" ", arguments)}");
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
