namespace Betsi.Application.Commands.Handlers;

using Betsi.Application.Escalations;
using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;

public sealed class TriggerWaitingTimeEscalationCommandHandler
    : IRequestHandler<TriggerWaitingTimeEscalationCommand, CommandResult>
{
    private readonly IAggregateRepository<Escalation> _escalations;
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public TriggerWaitingTimeEscalationCommandHandler(
        IAggregateRepository<Escalation> escalations,
        IAggregateRepository<PatientEpisode> episodes,
        ITenantContext tenantContext,
        TimeProvider time)
    {
        _escalations = escalations;
        _episodes = episodes;
        _tenantContext = tenantContext;
        _time = time;
    }

    public async Task<CommandResult> Handle(
        TriggerWaitingTimeEscalationCommand command, CancellationToken cancellationToken)
    {
        // An escalation that names a patient who does not exist would appear on the
        // dashboard with no one to act on, so the episode is checked before creating it.
        _ = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        var escalation = Escalation.CreateFromWaitingTime(
            _tenantContext.TenantId,
            command.PatientEpisodeId,
            command.LocationId,
            command.ResponsibleRole,
            _time.GetUtcNow().UtcDateTime,
            command.QueueId,
            _tenantContext.ActorId,
            _tenantContext.ActorRole);

        await _escalations.AddAsync(escalation, cancellationToken);

        return new CommandResult(escalation.Id, escalation.Version);
    }
}

public sealed class AcknowledgeEscalationCommandHandler
    : AggregateCommandHandler<AcknowledgeEscalationCommand, Escalation>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public AcknowledgeEscalationCommandHandler(
        IAggregateRepository<Escalation> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(AcknowledgeEscalationCommand c) => c.EscalationId;

    // Acknowledgement is idempotent and is often sent from a dashboard that does not track
    // versions, so no expected version is required. The aggregate still rejects acknowledging
    // an already-resolved escalation.
    protected override int? GetExpectedVersion(AcknowledgeEscalationCommand c) => null;

    protected override void Apply(AcknowledgeEscalationCommand c, Escalation escalation) =>
        escalation.Acknowledge(_tenantContext.ActorId, _tenantContext.ActorRole, c.Notes, _time.GetUtcNow().UtcDateTime);
}

public sealed class ResolveEscalationCommandHandler
    : AggregateCommandHandler<ResolveEscalationCommand, Escalation>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public ResolveEscalationCommandHandler(
        IAggregateRepository<Escalation> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(ResolveEscalationCommand c) => c.EscalationId;
    protected override int? GetExpectedVersion(ResolveEscalationCommand c) => null;

    protected override void Apply(ResolveEscalationCommand c, Escalation escalation) =>
        escalation.Resolve(_tenantContext.ActorId, _tenantContext.ActorRole, c.Notes, _time.GetUtcNow().UtcDateTime);
}

public sealed class ReassignEscalationCommandHandler
    : AggregateCommandHandler<ReassignEscalationCommand, Escalation>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public ReassignEscalationCommandHandler(
        IAggregateRepository<Escalation> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(ReassignEscalationCommand c) => c.EscalationId;

    // Not version-checked, like acknowledgement: reassignment is taken from the board, and the
    // aggregate refuses reassigning a closed escalation or to the role it already has.
    protected override int? GetExpectedVersion(ReassignEscalationCommand c) => null;

    protected override void Apply(ReassignEscalationCommand c, Escalation escalation) =>
        escalation.Reassign(c.ResponsibleRole.Trim(), c.Reason.Trim(), _tenantContext.ActorId,
            _tenantContext.ActorRole, _time.GetUtcNow().UtcDateTime);
}

public sealed class CloseFollowUpExceptionCommandHandler
    : AggregateCommandHandler<CloseFollowUpExceptionCommand, FollowUpException>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public CloseFollowUpExceptionCommandHandler(
        IAggregateRepository<FollowUpException> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(CloseFollowUpExceptionCommand c) => c.FollowUpExceptionId;
    protected override int? GetExpectedVersion(CloseFollowUpExceptionCommand c) => null;

    protected override void Apply(CloseFollowUpExceptionCommand c, FollowUpException exception) =>
        exception.Close(c.Outcome, c.ReviewNotes, _tenantContext.ActorId, _tenantContext.ActorRole,
            _time.GetUtcNow().UtcDateTime);
}

/// <summary>
/// Raises the escalation for a policy tier (MVP-021). The monitor decides who to consider;
/// this re-checks everything against the database before acting, so a stale or duplicated
/// request cannot raise an escalation the policy does not call for.
/// </summary>
public sealed class RaisePolicyEscalationCommandHandler
    : IRequestHandler<RaisePolicyEscalationCommand, CommandResult>
{
    private readonly BetsiDbContext _context;
    private readonly IAggregateRepository<Escalation> _escalations;
    private readonly IEscalationPolicyReader _policies;
    private readonly ITenantContext _tenantContext;

    public RaisePolicyEscalationCommandHandler(
        BetsiDbContext context,
        IAggregateRepository<Escalation> escalations,
        IEscalationPolicyReader policies,
        ITenantContext tenantContext)
    {
        _context = context;
        _escalations = escalations;
        _policies = policies;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(RaisePolicyEscalationCommand command, CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(command, cancellationToken);
        if (existing is not null)
            return existing;

        var policy = await _policies.GetInForceAsync(command.EvaluatedAt, cancellationToken)
            ?? throw new DomainRuleViolationException("No escalation policy is in force.");

        var tier = policy.Enabled
            ? policy.Tiers.SingleOrDefault(t => t.Level == command.TierLevel)
            : null;

        if (tier is null)
            throw new DomainRuleViolationException($"Policy revision {policy.Revision} has no enabled tier {command.TierLevel}.");

        var episode = await _context.PatientEpisodes.AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        if (!WaitingTimeMonitor.WaitingStates.Contains(episode.State))
            throw new DomainRuleViolationException($"Patient episode is {episode.State}, not waiting.");

        var waitedMinutes = (int)Math.Floor((command.EvaluatedAt - episode.ArrivedAt).TotalMinutes);

        var escalation = Escalation.CreateFromWaitingTimePolicy(
            _tenantContext.TenantId, episode.Id, episode.LocationId, policy.Revision, tier, waitedMinutes,
            command.EvaluatedAt);

        try
        {
            await _escalations.AddAsync(escalation, cancellationToken);
        }
        catch (DbUpdateException) when (escalation.TierLevel is not null)
        {
            // Another instance may have raised it between our check and our insert, in which
            // case the unique index stopped the duplicate and the escalation exists — which is
            // what was asked for. Anything else is a genuine failure.
            var raced = await FindExistingInFreshReadAsync(command, cancellationToken);
            if (raced is null)
                throw;

            return raced;
        }

        return new CommandResult(escalation.Id, escalation.Version);
    }

    private async Task<CommandResult?> FindExistingAsync(RaisePolicyEscalationCommand command, CancellationToken cancellationToken) =>
        await _context.Escalations.AsNoTracking()
            .Where(e => e.PatientEpisodeId == command.PatientEpisodeId && e.TierLevel == command.TierLevel)
            .Select(e => new CommandResult(e.Id, e.Version))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<CommandResult?> FindExistingInFreshReadAsync(
        RaisePolicyEscalationCommand command, CancellationToken cancellationToken)
    {
        // The failed insert is still tracked; detach it so the query is not answered from it.
        foreach (var entry in _context.ChangeTracker.Entries().ToList())
            entry.State = EntityState.Detached;

        return await FindExistingAsync(command, cancellationToken);
    }
}

/// <summary>
/// Raises a follow-up exception for a missed acknowledgement deadline and moves the escalation
/// into manual follow-up, atomically (MVP-022).
/// </summary>
public sealed class RaiseFollowUpExceptionCommandHandler
    : IRequestHandler<RaiseFollowUpExceptionCommand, CommandResult>
{
    private readonly BetsiDbContext _context;
    private readonly IAggregateRepository<Escalation> _escalations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEscalationPolicyReader _policies;
    private readonly ITenantContext _tenantContext;

    public RaiseFollowUpExceptionCommandHandler(
        BetsiDbContext context,
        IAggregateRepository<Escalation> escalations,
        IUnitOfWork unitOfWork,
        IEscalationPolicyReader policies,
        ITenantContext tenantContext)
    {
        _context = context;
        _escalations = escalations;
        _unitOfWork = unitOfWork;
        _policies = policies;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(RaiseFollowUpExceptionCommand command, CancellationToken cancellationToken)
    {
        var escalation = await _escalations.GetByIdAsync(command.EscalationId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(Escalation), command.EscalationId);

        if (escalation.AcknowledgementDueAt is { } due)
        {
            var existing = await _context.FollowUpExceptions.AsNoTracking()
                .Where(f => f.EscalationId == escalation.Id && f.MissedDeadline == due)
                .Select(f => new CommandResult(f.Id, f.Version))
                .SingleOrDefaultAsync(cancellationToken);

            if (existing is not null)
                return existing;
        }

        // The owner comes from the policy in force now, not the one that raised the escalation:
        // who reviews missed acknowledgements today is a question about today's rota.
        var policy = await _policies.GetInForceAsync(command.EvaluatedAt, cancellationToken);
        var owner = policy?.FollowUpOwnerRole ?? EscalationAuthority.DefaultFollowUpOwnerRole;

        var exception = FollowUpException.Raise(_tenantContext.TenantId, escalation, owner, command.EvaluatedAt);
        escalation.MarkForManualFollowUp(exception.Id, Guid.Empty, "System", command.EvaluatedAt);

        _unitOfWork.Add(exception);

        // One transaction: an escalation in manual follow-up with no exception to review, or
        // an exception for an escalation still shown as merely pending, would each mislead the
        // person reading the board. The unique index on (escalation, deadline) and the
        // escalation's concurrency token make a racing second instance fail rather than duplicate.
        await _unitOfWork.CommitAsync([exception, escalation], cancellationToken);

        return new CommandResult(exception.Id, exception.Version);
    }
}
