namespace Betsi.Application.Commands.Handlers;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;

public sealed class ProposeEscalationPolicyCommandHandler
    : IRequestHandler<ProposeEscalationPolicyCommand, CommandResult>
{
    private readonly BetsiDbContext _context;
    private readonly IAggregateRepository<EscalationPolicy> _policies;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public ProposeEscalationPolicyCommandHandler(
        BetsiDbContext context,
        IAggregateRepository<EscalationPolicy> policies,
        ITenantContext tenantContext,
        TimeProvider time)
    {
        _context = context;
        _policies = policies;
        _tenantContext = tenantContext;
        _time = time;
    }

    public async Task<CommandResult> Handle(ProposeEscalationPolicyCommand command, CancellationToken cancellationToken)
    {
        var tiers = command.Tiers
            .Select((t, i) => new WaitingTimeTier(
                i + 1, t.ThresholdMinutes, t.ResponsibleRole.Trim(), t.AcknowledgementDeadlineMinutes, t.RecommendedAction.Trim()))
            .ToList();

        var policy = EscalationPolicy.Propose(
            _tenantContext.TenantId,
            await PolicyRevisions.NextAsync(_context, cancellationToken),
            command.Enabled,
            tiers,
            command.FollowUpOwnerRole,
            command.Reason,
            _tenantContext.ActorId,
            _tenantContext.ActorRole,
            _time.GetUtcNow().UtcDateTime);

        await PolicyRevisions.AddAsync(_context, _policies, policy, cancellationToken);
        return new CommandResult(policy.Id, policy.Version);
    }
}

public sealed class ProposeEscalationPolicyRestorationCommandHandler
    : IRequestHandler<ProposeEscalationPolicyRestorationCommand, CommandResult>
{
    private readonly BetsiDbContext _context;
    private readonly IAggregateRepository<EscalationPolicy> _policies;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public ProposeEscalationPolicyRestorationCommandHandler(
        BetsiDbContext context,
        IAggregateRepository<EscalationPolicy> policies,
        ITenantContext tenantContext,
        TimeProvider time)
    {
        _context = context;
        _policies = policies;
        _tenantContext = tenantContext;
        _time = time;
    }

    public async Task<CommandResult> Handle(
        ProposeEscalationPolicyRestorationCommand command, CancellationToken cancellationToken)
    {
        var source = await _context.EscalationPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Revision == command.Revision, cancellationToken)
            ?? throw new DomainRuleViolationException($"Policy revision {command.Revision} does not exist.");

        var restoration = source.ProposeRestoration(
            await PolicyRevisions.NextAsync(_context, cancellationToken),
            command.Reason,
            _tenantContext.ActorId,
            _tenantContext.ActorRole,
            _time.GetUtcNow().UtcDateTime);

        await PolicyRevisions.AddAsync(_context, _policies, restoration, cancellationToken);
        return new CommandResult(restoration.Id, restoration.Version);
    }
}

public sealed class ApproveEscalationPolicyCommandHandler
    : AggregateCommandHandler<ApproveEscalationPolicyCommand, EscalationPolicy>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public ApproveEscalationPolicyCommandHandler(
        IAggregateRepository<EscalationPolicy> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(ApproveEscalationPolicyCommand c) => c.PolicyId;

    // Version-checked: the approver must be approving the proposal they read, not one that
    // changed underneath them.
    protected override int? GetExpectedVersion(ApproveEscalationPolicyCommand c) => c.ExpectedVersion;

    protected override void Apply(ApproveEscalationPolicyCommand c, EscalationPolicy policy) =>
        policy.Approve(_tenantContext.ActorId, _tenantContext.ActorRole, c.Reason,
            c.EffectiveFrom is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
            _time.GetUtcNow().UtcDateTime);
}

public sealed class RejectEscalationPolicyCommandHandler
    : AggregateCommandHandler<RejectEscalationPolicyCommand, EscalationPolicy>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public RejectEscalationPolicyCommandHandler(
        IAggregateRepository<EscalationPolicy> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(RejectEscalationPolicyCommand c) => c.PolicyId;
    protected override int? GetExpectedVersion(RejectEscalationPolicyCommand c) => c.ExpectedVersion;

    protected override void Apply(RejectEscalationPolicyCommand c, EscalationPolicy policy) =>
        policy.Reject(_tenantContext.ActorId, _tenantContext.ActorRole, c.Reason, _time.GetUtcNow().UtcDateTime);
}

public sealed class WithdrawEscalationPolicyCommandHandler
    : AggregateCommandHandler<WithdrawEscalationPolicyCommand, EscalationPolicy>
{
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public WithdrawEscalationPolicyCommandHandler(
        IAggregateRepository<EscalationPolicy> repository, ITenantContext tenantContext, TimeProvider time)
        : base(repository)
    {
        _tenantContext = tenantContext;
        _time = time;
    }

    protected override Guid GetAggregateId(WithdrawEscalationPolicyCommand c) => c.PolicyId;
    protected override int? GetExpectedVersion(WithdrawEscalationPolicyCommand c) => c.ExpectedVersion;

    protected override void Apply(WithdrawEscalationPolicyCommand c, EscalationPolicy policy) =>
        policy.Withdraw(_tenantContext.ActorId, _tenantContext.ActorRole, c.Reason, _time.GetUtcNow().UtcDateTime);
}

internal static class PolicyRevisions
{
    public static async Task<int> NextAsync(BetsiDbContext context, CancellationToken cancellationToken) =>
        (await context.EscalationPolicies.MaxAsync(p => (int?)p.Revision, cancellationToken) ?? 0) + 1;

    public static async Task AddAsync(
        BetsiDbContext context,
        IAggregateRepository<EscalationPolicy> policies,
        EscalationPolicy policy,
        CancellationToken cancellationToken)
    {
        try
        {
            await policies.AddAsync(policy, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            foreach (var entry in context.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            var revisionTaken = await context.EscalationPolicies
                .AnyAsync(p => p.Revision == policy.Revision, cancellationToken);

            if (!revisionTaken)
                throw;

            // Another proposal took this number first. Not retried silently: the proposer may
            // want to see the other change before submitting theirs.
            throw new ConflictException(
                $"Revision {policy.Revision} was proposed by someone else at the same moment. Review it, then resubmit.",
                exception);
        }
    }
}
