namespace Betsi.Licensing;

/// <summary>Licensed features. Addons will add their own.</summary>
public static class LicenseFeatures
{
    /// <summary>Site administration: locations, queues and other configuration.</summary>
    public const string Core = "core";
}

/// <summary>
/// Marks a command as usable only while the tenant's licence is in force and grants
/// <see cref="Feature"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresLicenseAttribute : Attribute
{
    public RequiresLicenseAttribute(string feature) => Feature = feature;

    public string Feature { get; }
}

/// <summary>
/// Marks a command as available regardless of licence state, because refusing it could
/// harm a patient.
/// </summary>
/// <remarks>
/// Every command must carry exactly one of this or <see cref="RequiresLicenseAttribute"/>;
/// a test enforces it. There is deliberately no default. Defaulting to "gated" would let a
/// forgotten clinical command be switched off by an expired licence; defaulting to
/// "available" would hide a commercial decision in an omission. The classification is a
/// clinical safety decision and belongs in the DCB0129 hazard log.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AlwaysAvailableAttribute : Attribute
{
    public AlwaysAvailableAttribute(string reason) => Reason = reason;

    public string Reason { get; }
}

/// <summary>
/// Thrown when a licence-gated operation is attempted without a licence that grants it.
/// </summary>
public sealed class LicenseRestrictedException : Exception
{
    public LicenseRestrictedException(string feature, LicenseEvaluation evaluation)
        : base($"This operation requires the '{feature}' licence feature. " +
               $"The tenant's licence status is {evaluation.Status}.")
    {
        Feature = feature;
        Evaluation = evaluation;
    }

    public string Feature { get; }
    public LicenseEvaluation Evaluation { get; }
}
