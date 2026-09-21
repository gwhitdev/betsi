namespace Betsi.Application.Behaviours;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;

/// <summary>
/// Writes an audit row for every command, whether it succeeded or failed.
/// </summary>
/// <remarks>
/// This is the DSPT and DCB0129 evidence trail: who did what, to which record, when, and
/// whether it worked. Failures are recorded too — a rejected attempt to discharge a patient
/// is exactly the kind of event an investigation needs to see.
///
/// The audit row is written in its own SaveChanges after the handler has committed, so a
/// failure to audit cannot roll back clinical work. The trade-off is that a process crash
/// between the two leaves an unaudited command; that is preferable to the alternative, and
/// the event log in the same transaction as the change remains the primary record.
/// </remarks>
public sealed class AuditBehaviour<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly BetsiDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AuditBehaviour<TRequest, TResponse>> _logger;

    public AuditBehaviour(
        BetsiDbContext context,
        ITenantContext tenantContext,
        ILogger<AuditBehaviour<TRequest, TResponse>> logger)
    {
        _context = context;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        // Only state-changing commands are audited here. Reads that carry patient data are
        // audited at the endpoint instead (AuditReadAttribute), so the trail records which
        // records were looked at without a row for every list query.
        if (request is not ICommand)
            return await next();

        try
        {
            var response = await next();

            await WriteAuditAsync(
                request,
                outcome: "Success",
                affectedAggregateId: (response as CommandResult)?.AggregateId ?? Guid.Empty,
                errorMessage: null,
                cancellationToken);

            return response;
        }
        catch (Exception exception)
        {
            // A failed multi-aggregate commit can leave tracked changes behind. Never flush
            // those changes when saving the separate failure audit record.
            _context.ChangeTracker.Clear();
            await WriteAuditAsync(
                request,
                outcome: "Failure",
                affectedAggregateId: Guid.Empty,
                errorMessage: Truncate(exception.Message, 500),
                cancellationToken);

            throw;
        }
    }

    private async Task WriteAuditAsync(
        TRequest request,
        string outcome,
        Guid affectedAggregateId,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            _context.AuditLogs.Add(new AuditLogRecord
            {
                TenantId = _tenantContext.TenantId,
                Action = typeof(TRequest).Name,
                ActorId = _tenantContext.ActorId,
                ActorRole = _tenantContext.ActorRole,
                AffectedAggregateId = affectedAggregateId,
                AffectedAggregateType = AggregateTypeFor(typeof(TRequest).Name),
                Outcome = outcome,
                ErrorMessage = errorMessage,
                Context = null,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception auditException)
        {
            // Never let an audit failure mask the command's own outcome.
            _logger.LogError(
                auditException,
                "Failed to write audit record for {Action}. The command outcome was {Outcome}.",
                typeof(TRequest).Name, outcome);
        }
    }

    private static string AggregateTypeFor(string commandName) => commandName switch
    {
        // Most specific first: "RaisePolicyEscalation" and "EscalationPolicy" both mention an escalation.
        var n when n.Contains("Observation", StringComparison.Ordinal) => "ClinicalObservation",
        var n when n.Contains("EscalationPolicy", StringComparison.Ordinal) => "EscalationPolicy",
        var n when n.Contains("FollowUpException", StringComparison.Ordinal) => "FollowUpException",
        var n when n.Contains("Escalation", StringComparison.Ordinal) => "Escalation",
        var n when n.Contains("Patient", StringComparison.Ordinal) => "PatientEpisode",
        var n when n.Contains("Location", StringComparison.Ordinal) => "Location",
        var n when n.Contains("Queue", StringComparison.Ordinal) => "Queue",
        _ => "Unknown"
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
