namespace Betsi.Application.Commands;

using Betsi.Licensing;
using MediatR;

/// <summary>
/// Base interface for all commands.
/// Commands represent a user's or system's intent to change state.
/// </summary>
public interface ICommand : IRequest<CommandResult>
{
}

public interface ICommand<TResponse> : IRequest<TResponse>
{
}

/// <summary>
/// What a command produced: the aggregate it affected and the version it left behind.
/// </summary>
/// <remarks>
/// There is no success flag. A command that did not succeed throws, and the exception is
/// translated into an RFC 9457 problem response by <c>ProblemDetailsExceptionHandler</c>.
/// Returning <c>Success = false</c> alongside an HTTP 200 would force every caller to
/// check twice and would make failures invisible to monitoring.
///
/// <see cref="Version"/> is the value to send back as <c>ExpectedVersion</c> on the next
/// command against this aggregate.
/// </remarks>
/// <param name="AggregateId">Identifier of the aggregate the command created or changed.</param>
/// <param name="Version">The aggregate's version after the command was applied.</param>
public sealed record CommandResult(Guid AggregateId, int Version);

// ============= Patient Episode Commands =============

/// <summary>
/// Command to register a new patient episode (patient arrival).
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class RegisterPatientCommand : ICommand
{
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTime DateOfBirth { get; set; }
    public string? NhsNumber { get; set; }
}

/// <summary>
/// Command to begin patient triage.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class BeginPatientTriageCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
}

/// <summary>
/// Command to complete patient triage and move to awaiting treatment.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class CompletePatientTriageCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
}

/// <summary>
/// Command to move patient to treatment area.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class BeginPatientTreatmentCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public Guid LocationId { get; set; }
    public int ExpectedVersion { get; set; }
}

/// <summary>
/// Command to discharge a patient.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class DischargePatientCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
    public string? DischargeNotes { get; set; }
}

/// <summary>
/// Command to cancel an episode, for example a patient who left without being seen.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class CancelPatientEpisodeCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
    public string? Reason { get; set; }
}

// ============= Location Commands =============

/// <summary>
/// Command to create a new location (bed/room).
/// </summary>
[RequiresLicense(LicenseFeatures.Core)]
public class CreateLocationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Capacity { get; set; }
    public string? Description { get; set; }
    public bool EscalationEnabled { get; set; } = true;
}

// ============= Queue Commands =============

/// <summary>
/// Command to create a new queue for a location.
/// </summary>
[RequiresLicense(LicenseFeatures.Core)]
public class CreateQueueCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Command to enqueue a patient in a queue.
/// </summary>
[AlwaysAvailable("Patient care: refusing it could delay treatment.")]
public class EnqueuePatientCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid QueueId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public int ExpectedVersion { get; set; }
}

// ============= Escalation Commands =============

/// <summary>
/// Command to trigger an escalation for a waiting-time threshold.
/// </summary>
[AlwaysAvailable("Escalation workflow: a safety function licensing must never disable (spec §5).")]
public class TriggerWaitingTimeEscalationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid PatientEpisodeId { get; set; }
    public Guid LocationId { get; set; }
    public string ResponsibleRole { get; set; } = string.Empty;
    public Guid? QueueId { get; set; }
}

/// <summary>
/// Command to acknowledge an escalation.
/// </summary>
[AlwaysAvailable("Escalation workflow: a safety function licensing must never disable (spec §5).")]
public class AcknowledgeEscalationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid EscalationId { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// Command to resolve an escalation.
/// </summary>
[AlwaysAvailable("Escalation workflow: a safety function licensing must never disable (spec §5).")]
public class ResolveEscalationCommand : ICommand
{
    public Guid TenantId { get; set; }
    public Guid EscalationId { get; set; }
    public string? Notes { get; set; }
}
