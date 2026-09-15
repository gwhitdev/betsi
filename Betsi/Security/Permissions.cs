namespace Betsi.Security;

/// <summary>What an actor may do. Endpoints and commands require permissions, never roles.</summary>
public static class Permissions
{
    public const string PatientsRegister = "patients.register";
    public const string PatientsCare = "patients.care";
    public const string PatientsDischarge = "patients.discharge";
    public const string EpisodesRead = "episodes.read";
    public const string QueuesManage = "queues.manage";

    public const string EscalationsRaise = "escalations.raise";
    public const string EscalationsRespond = "escalations.respond";
    public const string EscalationsRead = "escalations.read";

    public const string PolicyRead = "policy.read";
    public const string PolicyPropose = "policy.propose";
    public const string PolicyDecide = "policy.decide";

    public const string LocationsManage = "locations.manage";
    public const string LicenseRead = "license.read";
    public const string WebhooksManage = "webhooks.manage";
    public const string IntegrationsManage = "integrations.manage";
    public const string IntegrationIngest = "integration.ingest";

    public static readonly IReadOnlyList<string> All =
    [
        PatientsRegister, PatientsCare, PatientsDischarge, EpisodesRead, QueuesManage,
        EscalationsRaise, EscalationsRespond, EscalationsRead,
        PolicyRead, PolicyPropose, PolicyDecide,
        LocationsManage, LicenseRead, WebhooksManage, IntegrationsManage, IntegrationIngest
    ];
}

/// <summary>
/// The MVP default role matrix (spec §2 and §6). Site-specific role mappings are v1.1.
/// </summary>
/// <remarks>
/// Least privilege: site administration roles do not see patient data, and patient-facing roles
/// cannot change configuration. Role names are matched case-insensitively against the acting
/// role on the token. A role not listed here has no permissions at all.
///
/// This matrix is a clinical safety and information governance artefact. Changes to it belong
/// in the DCB0129 hazard log and the DPIA.
/// </remarks>
public static class RoleMatrix
{
    /// <summary>
    /// The role inbound integrations act in. Reserved: it is granted only to requests
    /// authenticated by a signed inbound message, never taken from a token or header.
    /// </summary>
    public const string IntegrationRole = "Integration";

    private static readonly string[] Clinical =
        ["Nurse", "Staff Nurse", "Nurse in Charge", "Senior Clinician", "Doctor", "Consultant"];

    private static readonly string[] Supervisory =
        ["Clinical Lead", "Operations Manager", "Site Manager", "Matron"];

    private static readonly string[] Flow = ["Waiting-room Coordinator", "Bed Manager"];

    private static readonly Dictionary<string, HashSet<string>> Matrix = Build();

    public static IReadOnlyCollection<string> Roles => Matrix.Keys;

    public static bool Grants(string? role, string permission) =>
        role is not null && Matrix.TryGetValue(role, out var permissions) && permissions.Contains(permission);

    public static IReadOnlySet<string> PermissionsOf(string role) =>
        Matrix.TryGetValue(role, out var permissions) ? permissions : new HashSet<string>();

    public static bool IsReserved(string role) =>
        string.Equals(role, IntegrationRole, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, Infrastructure.Tenancy.TenantContext.SystemRole, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, HashSet<string>> Build()
    {
        var matrix = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Grant(IEnumerable<string> roles, params string[] permissions)
        {
            foreach (var role in roles)
            {
                if (!matrix.TryGetValue(role, out var set))
                    matrix[role] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(permissions);
            }
        }

        string[] everyStaffRole = [.. Clinical, .. Supervisory, .. Flow, "Receptionist", "Site Administrator"];

        Grant(everyStaffRole, Permissions.PolicyRead, Permissions.LicenseRead);

        // Anyone who meets patients can register an arrival and raise a concern.
        Grant([.. Clinical, .. Flow, "Receptionist", "Matron", "Clinical Lead"],
            Permissions.PatientsRegister, Permissions.EscalationsRaise, Permissions.EpisodesRead);

        Grant([.. Clinical, "Clinical Lead", "Matron"], Permissions.PatientsCare, Permissions.PatientsDischarge);
        Grant([.. Clinical, .. Flow, "Matron"], Permissions.QueuesManage);

        // Everyone an escalation can be assigned to must be able to see and act on it.
        Grant([.. Clinical, .. Supervisory, .. Flow], Permissions.EscalationsRead, Permissions.EscalationsRespond, Permissions.EpisodesRead);

        Grant(["Site Administrator", "Clinical Lead", "Operations Manager", "Matron"], Permissions.PolicyPropose);
        Grant(Supervisory, Permissions.PolicyDecide);

        Grant(["Site Administrator", "Bed Manager", "Operations Manager"], Permissions.LocationsManage);
        Grant(["Site Administrator"], Permissions.WebhooksManage, Permissions.IntegrationsManage);

        // Integrations bring arrivals and discharges from the EPR. Nothing else.
        Grant([IntegrationRole], Permissions.IntegrationIngest, Permissions.PatientsRegister, Permissions.PatientsDischarge);

        return matrix;
    }
}

/// <summary>The permission a command requires. Every command carries this or <see cref="SystemOnlyAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

/// <summary>A command only the platform may send, from a background job. Refused for every person and integration.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemOnlyAttribute : Attribute;

/// <summary>Thrown when the acting role lacks the permission an operation requires.</summary>
public sealed class PermissionDeniedException(string permission, string role)
    : Exception($"The role '{role}' does not have the '{permission}' permission.")
{
    public string Permission { get; } = permission;
    public string Role { get; } = role;
}

/// <summary>
/// Marks an endpoint whose authorisation is enforced per command by the MediatR pipeline,
/// because which permission applies depends on the command in the body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DispatchesAuthorizedCommandsAttribute : Attribute;
