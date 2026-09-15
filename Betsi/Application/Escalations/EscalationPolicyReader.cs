namespace Betsi.Application.Escalations;

using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>Finds the escalation policy revision in force at a given time.</summary>
public interface IEscalationPolicyReader
{
    /// <summary>The approved revision in effect at <paramref name="at"/>, or null if the site has none.</summary>
    Task<EscalationPolicy?> GetInForceAsync(DateTime at, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IEscalationPolicyReader"/>
public sealed class EscalationPolicyReader : IEscalationPolicyReader
{
    private readonly BetsiDbContext _context;

    public EscalationPolicyReader(BetsiDbContext context)
    {
        _context = context;
    }

    public Task<EscalationPolicy?> GetInForceAsync(DateTime at, CancellationToken cancellationToken) =>
        _context.EscalationPolicies
            .AsNoTracking()
            .Where(p => p.State == EscalationPolicy.PolicyState.Approved && p.EffectiveFrom <= at)
            // Latest effective time wins; if two share it, the later revision is the later decision.
            .OrderByDescending(p => p.EffectiveFrom)
            .ThenByDescending(p => p.Revision)
            .FirstOrDefaultAsync(cancellationToken);
}
