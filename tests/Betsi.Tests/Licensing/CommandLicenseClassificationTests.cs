namespace Betsi.Tests.Licensing;

using Betsi.Application.Behaviours;
using Betsi.Application.Commands;
using Betsi.Licensing;

/// <summary>
/// Every command is explicitly either licence-gated or always available.
/// </summary>
/// <remarks>
/// A new command without a classification would otherwise be decided by whichever default
/// someone chose — and either default is wrong for some command. This forces the decision to
/// be made, visibly, when the command is written.
/// </remarks>
public class CommandLicenseClassificationTests
{
    private static readonly Type[] Commands = typeof(RegisterPatientCommand).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ICommand).IsAssignableFrom(t))
        .ToArray();

    [Fact]
    public void Every_command_is_classified_exactly_once()
    {
        Commands.ShouldNotBeEmpty();

        var unclassified = Commands
            .Where(t => !IsClassified(t))
            .Select(t => t.Name)
            .ToArray();

        unclassified.ShouldBeEmpty();
    }

    [Fact]
    public void No_patient_or_escalation_command_can_be_switched_off_by_a_licence()
    {
        // Spec §5: licensing must never disable clinically necessary safety functions.
        var gatedClinical = Commands
            .Where(t => t.Name.Contains("Patient") || t.Name.Contains("Escalation"))
            .Where(t => LicenseBehaviour<ICommand, CommandResult>.RequiredFeatureOf(t) is not null)
            .Select(t => t.Name)
            .ToArray();

        gatedClinical.ShouldBeEmpty();
    }

    [Fact]
    public void A_command_with_both_or_neither_classification_is_rejected()
    {
        Should.Throw<InvalidOperationException>(() =>
            LicenseBehaviour<ICommand, CommandResult>.RequiredFeatureOf(typeof(Unclassified)));

        Should.Throw<InvalidOperationException>(() =>
            LicenseBehaviour<ICommand, CommandResult>.RequiredFeatureOf(typeof(DoublyClassified)));
    }

    private static bool IsClassified(Type commandType)
    {
        try
        {
            LicenseBehaviour<ICommand, CommandResult>.RequiredFeatureOf(commandType);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Unclassified : ICommand;

    [RequiresLicense(LicenseFeatures.Core)]
    [AlwaysAvailable("contradiction")]
    private sealed class DoublyClassified : ICommand;
}
