namespace Betsi.Application.Escalations;

using Betsi.Application.Commands;
using Betsi.ControlPlane;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>What one evaluation of one tenant did.</summary>
public sealed record WaitingTimeEvaluation(
    bool PolicyInForce,
    int PatientsConsidered,
    int EscalationsRaised,
    int FollowUpExceptionsRaised,
    int Failures);

/// <summary>
/// Raises waiting-time escalations and missed-acknowledgement follow-up exceptions for the
/// current tenant (MVP-021, MVP-022).
/// </summary>
public interface IWaitingTimeMonitor
{
    Task<WaitingTimeEvaluation> EvaluateAsync(DateTime now, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWaitingTimeMonitor"/>
/// <remarks>
/// Follows spec Appendix A: the monitor reads, decides who needs attention, and submits
/// idempotent commands; the commands' handlers re-validate and are the only thing that writes.
/// That keeps every automatic escalation on the same audited, validated path as a human one.
///
/// Reading is two queries per tenant regardless of patient count — waiting patients past the
/// first threshold, and the tiers already raised for them — so evaluation cost grows with the
/// number of patients who actually need escalating, not the size of the department.
///
/// Each command runs in its own scope. One patient whose escalation fails (and is logged) does
/// not stop the others, and is retried on the next evaluation.
/// </remarks>
public sealed class WaitingTimeMonitor : IWaitingTimeMonitor
{
    /// <summary>
    /// Spec §2: patients "in the waiting room or awaiting admission to a bed". Time in triage
    /// or treatment is not waiting.
    /// </summary>
    public static readonly IReadOnlySet<PatientEpisode.PatientState> WaitingStates = new HashSet<PatientEpisode.PatientState>
    {
        PatientEpisode.PatientState.Waiting,
        PatientEpisode.PatientState.AwaitingTreatment
    };

    // An array for queries: EF translates Contains on arrays and lists, not on set interfaces.
    internal static readonly PatientEpisode.PatientState[] WaitingStateList = [.. WaitingStates];

    private readonly BetsiDbContext _context;
    private readonly IEscalationPolicyReader _policies;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<WaitingTimeMonitor> _logger;

    public WaitingTimeMonitor(
        BetsiDbContext context,
        IEscalationPolicyReader policies,
        ITenantContext tenantContext,
        IServiceScopeFactory scopes,
        ILogger<WaitingTimeMonitor> logger)
    {
        _context = context;
        _policies = policies;
        _tenantContext = tenantContext;
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<WaitingTimeEvaluation> EvaluateAsync(DateTime now, CancellationToken cancellationToken)
    {
        var failures = 0;

        var policy = await _policies.GetInForceAsync(now, cancellationToken);
        var (considered, raised) = policy is { Enabled: true }
            ? await RaiseEscalationsAsync(policy, now, () => failures++, cancellationToken)
            : (0, 0);

        // Missed deadlines are checked whether or not a policy is in force: an escalation a
        // member of staff raised by hand deserves the same follow-up when nobody picks it up.
        var followUps = await RaiseFollowUpExceptionsAsync(now, () => failures++, cancellationToken);

        return new WaitingTimeEvaluation(policy is { Enabled: true }, considered, raised, followUps, failures);
    }

    private async Task<(int Considered, int Raised)> RaiseEscalationsAsync(
        EscalationPolicy policy, DateTime now, Action onFailure, CancellationToken cancellationToken)
    {
        var firstThreshold = policy.Tiers[0].ThresholdMinutes;
        var arrivedBy = now.AddMinutes(-firstThreshold);

        var waiting = await _context.PatientEpisodes.AsNoTracking()
            .Where(e => WaitingStateList.Contains(e.State) && e.ArrivedAt <= arrivedBy)
            .Select(e => new { e.Id, e.ArrivedAt })
            .ToListAsync(cancellationToken);

        if (waiting.Count == 0)
            return (0, 0);

        var ids = waiting.Select(w => w.Id).ToList();

        var alreadyRaised = (await _context.Escalations.AsNoTracking()
                .Where(e => e.TierLevel != null && ids.Contains(e.PatientEpisodeId))
                .Select(e => new { e.PatientEpisodeId, Tier = e.TierLevel!.Value })
                .ToListAsync(cancellationToken))
            .Select(e => (e.PatientEpisodeId, e.Tier))
            .ToHashSet();

        var raised = 0;

        // Longest waits first, so if evaluation is interrupted the patients who have waited
        // longest are the ones already escalated.
        foreach (var patient in waiting.OrderBy(w => w.ArrivedAt))
        {
            var waitedMinutes = (int)Math.Floor((now - patient.ArrivedAt).TotalMinutes);

            foreach (var tier in policy.TiersReachedAfter(waitedMinutes))
            {
                if (alreadyRaised.Contains((patient.Id, tier.Level)))
                    continue;

                var sent = await SendAsync(
                    new RaisePolicyEscalationCommand { PatientEpisodeId = patient.Id, TierLevel = tier.Level, EvaluatedAt = now },
                    $"tier {tier.Level} escalation for episode {patient.Id}",
                    cancellationToken);

                if (sent) raised++; else onFailure();
            }
        }

        return (waiting.Count, raised);
    }

    private async Task<int> RaiseFollowUpExceptionsAsync(DateTime now, Action onFailure, CancellationToken cancellationToken)
    {
        var overdue = await _context.Escalations.AsNoTracking()
            .Where(e => e.State == Escalation.EscalationState.Created && e.AcknowledgementDueAt < now)
            .OrderBy(e => e.AcknowledgementDueAt)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        var raised = 0;

        foreach (var escalationId in overdue)
        {
            var sent = await SendAsync(
                new RaiseFollowUpExceptionCommand { EscalationId = escalationId, EvaluatedAt = now },
                $"follow-up exception for escalation {escalationId}",
                cancellationToken);

            if (sent) raised++; else onFailure();
        }

        return raised;
    }

    private async Task<bool> SendAsync(ICommand command, string description, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(_tenantContext.TenantId, Guid.Empty, "System");

        try
        {
            await scope.ServiceProvider.GetRequiredService<IMediator>().Send(command, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Could not raise {Description} for tenant {TenantId}; it will be retried on the next evaluation.",
                description, _tenantContext.TenantId);
            return false;
        }
    }
}

public sealed class EscalationMonitorOptions
{
    public const string SectionName = "EscalationMonitor";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often every tenant is evaluated. Bounds how late after a threshold or deadline an
    /// escalation or exception can appear.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>Runs <see cref="IWaitingTimeMonitor"/> for every available tenant on a timer.</summary>
public sealed class WaitingTimeMonitorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantRegistry _registry;
    private readonly EscalationMonitorOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<WaitingTimeMonitorService> _logger;

    // Tenants already warned about running with no policy, so the warning is not repeated every tick.
    private readonly HashSet<Guid> _warnedNoPolicy = [];

    public WaitingTimeMonitorService(
        IServiceScopeFactory scopes,
        ITenantRegistry registry,
        EscalationMonitorOptions options,
        TimeProvider time,
        ILogger<WaitingTimeMonitorService> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("The waiting-time escalation monitor is disabled by configuration. No escalations will be raised automatically.");
            return;
        }

        _logger.LogInformation("Waiting-time escalation monitor started; evaluating every {Interval}", _options.Interval);

        using var timer = new PeriodicTimer(_options.Interval, _time);

        do
        {
            foreach (var tenant in _registry.Available)
                await EvaluateTenantAsync(tenant.TenantId, stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task EvaluateTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Resolve(tenantId, Guid.Empty, "System");

            var result = await scope.ServiceProvider.GetRequiredService<IWaitingTimeMonitor>()
                .EvaluateAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);

            if (!result.PolicyInForce && _warnedNoPolicy.Add(tenantId))
            {
                _logger.LogWarning(
                    "Tenant {TenantId} has no enabled escalation policy in force. Waiting-time escalations are not being raised automatically.",
                    tenantId);
            }
            else if (result.PolicyInForce)
            {
                _warnedNoPolicy.Remove(tenantId);
            }

            if (result.EscalationsRaised > 0 || result.FollowUpExceptionsRaised > 0 || result.Failures > 0)
            {
                _logger.LogInformation(
                    "Tenant {TenantId}: {Escalations} escalations and {FollowUps} follow-up exceptions raised, {Failures} failures",
                    tenantId, result.EscalationsRaised, result.FollowUpExceptionsRaised, result.Failures);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The whole tenant is unreachable. Other tenants are unaffected; the next tick retries.
            _logger.LogError(exception, "Waiting-time evaluation failed for tenant {TenantId}", tenantId);
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
