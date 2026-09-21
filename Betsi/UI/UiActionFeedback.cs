using Betsi.Domain;
using Betsi.Security;
using Betsi.UI.Resources;
using FluentValidation;
using Microsoft.Extensions.Localization;

namespace Betsi.UI;

/// <summary>Maps known command failures to safe, actionable UI messages.</summary>
public static class UiActionFeedback
{
    public static string For(Exception exception, string subject, IStringLocalizer<Strings> text)
    {
        var reason = exception switch
        {
            ValidationException or DomainRuleViolationException => text["ActionValidationRecovery"],
            AggregateConcurrencyException or ConflictException => text["ActionConflictRecovery"],
            PermissionDeniedException => text["ActionPermissionRecovery"],
            _ => text["ActionServiceRecovery"]
        };
        return text["ActionFailureFor", subject, reason];
    }
}
