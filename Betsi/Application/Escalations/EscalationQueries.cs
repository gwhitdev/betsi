namespace Betsi.Application.Escalations;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;

// ============= Board (MVP-023) =============

public sealed record PatientSummary(Guid EpisodeId, string Name, string State, DateTime ArrivedAt, int MinutesSinceArrival);

public sealed record EscalationCard(
    Guid Id,
    int Version,
    string State,
    string Trigger,
    string ResponsibleRole,
    int? TierLevel,
    int? PolicyRevision,
    string? RecommendedAction,
    int? WaitedMinutesWhenRaised,
    DateTime CreatedAt,
    DateTime? AcknowledgementDueAt,
    bool AcknowledgementOverdue,
    int MinutesSinceRaised,
    DateTime? AcknowledgedAt,
    string? AcknowledgedByRole,
    DateTime? ResolvedAt,
    string? ResolvedByRole,
    DateTime? ClosedAt,
    string? Notes,
    PatientSummary? Patient);

public sealed record FollowUpExceptionCard(
    Guid Id,
    int Version,
    Guid EscalationId,
    string EscalationResponsibleRole,
    string OwnerRole,
    DateTime MissedDeadline,
    DateTime RaisedAt,
    int MinutesOpen,
    PatientSummary? Patient);

public sealed record PolicyTierView(int Level, int ThresholdMinutes, string ResponsibleRole, int AcknowledgementDeadlineMinutes, string RecommendedAction);

/// <param name="Status">
/// <c>InForce</c>, <c>Disabled</c> (the site turned automatic escalation off), or
/// <c>NoApprovedPolicy</c> — shown prominently so nobody assumes escalations are being raised.
/// </param>
public sealed record PolicyStatusView(string Status, int? Revision, DateTime? EffectiveFrom, IReadOnlyList<PolicyTierView> Tiers);

public sealed record EscalationMetrics(
    int ActiveEscalations,
    int AwaitingAcknowledgement,
    int AcknowledgementOverdue,
    int OpenFollowUpExceptions,
    double? MedianMinutesToAcknowledge,
    int RaisedInLast24Hours,
    IReadOnlyDictionary<string, int> RaisedInLast24HoursByTrigger,
    IReadOnlyDictionary<string, int> RaisedInLast24HoursByTier);

/// <summary>Everything the escalation board shows, as of <see cref="GeneratedAt"/>.</summary>
/// <param name="Source">Where the data came from; the spec requires every view to say.</param>
/// <param name="Truncated">True if a list hit its limit; the board must say more exist.</param>
public sealed record EscalationBoard(
    DateTime GeneratedAt,
    string Source,
    int HistoryDays,
    PolicyStatusView Policy,
    EscalationMetrics Metrics,
    IReadOnlyList<EscalationCard> AwaitingAcknowledgement,
    IReadOnlyList<EscalationCard> Active,
    IReadOnlyList<FollowUpExceptionCard> FollowUpExceptions,
    IReadOnlyList<EscalationCard> History,
    bool Truncated);

// ============= Audit trail (MVP-024) =============

public sealed record AuditTrailEntry(
    long Sequence,
    DateTime OccurredAt,
    string AggregateType,
    Guid AggregateId,
    int Version,
    string EventType,
    Guid ActorId,
    string ActorRole,
    JsonElement Details);

// ============= Policy views (MVP-020) =============

public sealed record PolicyRevisionView(
    Guid Id,
    int Version,
    int Revision,
    string State,
    bool InForce,
    bool Enabled,
    IReadOnlyList<PolicyTierView> Tiers,
    string FollowUpOwnerRole,
    int? RestoresRevision,
    string ProposalReason,
    Guid ProposedByActorId,
    string ProposedByRole,
    DateTime ProposedAt,
    Guid? DecidedByActorId,
    string? DecidedByRole,
    DateTime? DecidedAt,
    string? DecisionReason,
    DateTime? EffectiveFrom);

public sealed record PolicyOverview(DateTime GeneratedAt, PolicyRevisionView? InForce, IReadOnlyList<PolicyRevisionView> Revisions);

public sealed record PolicyPreviewTier(int Level, int ThresholdMinutes, string ResponsibleRole, int PatientsAtOrPastThreshold, int WouldBeNewlyEscalated);

/// <summary>What a candidate set of tiers would do to the patients waiting right now.</summary>
public sealed record PolicyPreview(DateTime GeneratedAt, int PatientsWaiting, IReadOnlyList<PolicyPreviewTier> Tiers);

public sealed class EscalationQueries
{
    public const int MaxHistoryDays = 30;
    public const int ListLimit = 500;
    public const int HistoryListLimit = 50;

    private static readonly Escalation.EscalationState[] OpenStates =
    [
        Escalation.EscalationState.Created,
        Escalation.EscalationState.Acknowledged,
        Escalation.EscalationState.Escalated,
        Escalation.EscalationState.ManualFollowUp
    ];

    private static readonly Escalation.EscalationState[] UnacknowledgedStates =
    [
        Escalation.EscalationState.Created,
        Escalation.EscalationState.ManualFollowUp
    ];

    private readonly BetsiDbContext _context;
    private readonly IEscalationPolicyReader _policies;
    private readonly TimeProvider _time;

    public EscalationQueries(BetsiDbContext context, IEscalationPolicyReader policies, TimeProvider time)
    {
        _context = context;
        _policies = policies;
        _time = time;
    }

    /// <remarks>
    /// Read directly from the tenant database with no projection lag, which is how the board
    /// meets the spec's ten-second staleness limit: its staleness is however often the client
    /// polls. A separate projection store (ADR-003) becomes worthwhile when read load warrants
    /// it; the response contract does not change when it does.
    /// </remarks>
    public async Task<EscalationBoard> GetBoardAsync(int historyDays, CancellationToken cancellationToken)
    {
        historyDays = Math.Clamp(historyDays, 1, MaxHistoryDays);
        var now = _time.GetUtcNow().UtcDateTime;
        var historyFrom = now.AddDays(-historyDays);
        var dayAgo = now.AddDays(-1);

        var open = await _context.Escalations.AsNoTracking()
            .Where(e => OpenStates.Contains(e.State))
            .OrderBy(e => e.CreatedAt)
            .Take(ListLimit + 1)
            .ToListAsync(cancellationToken);

        var history = await _context.Escalations.AsNoTracking()
            .Where(e => !OpenStates.Contains(e.State) &&
                        ((e.ResolvedAt != null && e.ResolvedAt >= historyFrom) || (e.ClosedAt != null && e.ClosedAt >= historyFrom)))
            .OrderByDescending(e => e.ResolvedAt ?? e.ClosedAt)
            .Take(HistoryListLimit + 1)
            .ToListAsync(cancellationToken);

        var followUps = await _context.FollowUpExceptions.AsNoTracking()
            .Where(f => f.State == FollowUpException.FollowUpState.Open)
            .OrderBy(f => f.RaisedAt)
            .Take(ListLimit + 1)
            .ToListAsync(cancellationToken);

        var acknowledgementTimes = await _context.Escalations.AsNoTracking()
            .Where(e => e.AcknowledgedAt != null && e.AcknowledgedAt >= historyFrom)
            .Select(e => new { e.CreatedAt, e.AcknowledgedAt })
            .ToListAsync(cancellationToken);

        var recent = await _context.Escalations.AsNoTracking()
            .Where(e => e.CreatedAt >= dayAgo)
            .Select(e => new { e.Trigger, e.TierLevel })
            .ToListAsync(cancellationToken);

        var truncated = open.Count > ListLimit || history.Count > HistoryListLimit || followUps.Count > ListLimit;
        open = open.Take(ListLimit).ToList();
        history = history.Take(HistoryListLimit).ToList();
        followUps = followUps.Take(ListLimit).ToList();

        var patients = await PatientsAsync(
            open.Select(e => e.PatientEpisodeId).Concat(history.Select(e => e.PatientEpisodeId))
                .Concat(followUps.Select(f => f.PatientEpisodeId)),
            now, cancellationToken);

        var openCards = open.Select(e => Card(e, now, patients)).ToList();
        var awaiting = openCards
            .Where(c => c.AcknowledgedAt is null && UnacknowledgedStates.Select(s => s.ToString()).Contains(c.State))
            .OrderBy(c => c.AcknowledgementDueAt)
            .ToList();

        var metrics = new EscalationMetrics(
            ActiveEscalations: openCards.Count,
            AwaitingAcknowledgement: awaiting.Count,
            AcknowledgementOverdue: awaiting.Count(c => c.AcknowledgementOverdue),
            OpenFollowUpExceptions: followUps.Count,
            MedianMinutesToAcknowledge: Median(acknowledgementTimes
                .Select(a => (a.AcknowledgedAt!.Value - a.CreatedAt).TotalMinutes)
                .Where(m => m >= 0)),
            RaisedInLast24Hours: recent.Count,
            RaisedInLast24HoursByTrigger: recent.GroupBy(r => r.Trigger.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            RaisedInLast24HoursByTier: recent.Where(r => r.TierLevel != null)
                .GroupBy(r => $"tier{r.TierLevel}").ToDictionary(g => g.Key, g => g.Count()));

        return new EscalationBoard(
            GeneratedAt: now,
            Source: "tenant-database",
            HistoryDays: historyDays,
            Policy: PolicyStatus(await _policies.GetInForceAsync(now, cancellationToken)),
            Metrics: metrics,
            AwaitingAcknowledgement: awaiting,
            Active: openCards,
            FollowUpExceptions: followUps.Select(f => new FollowUpExceptionCard(
                f.Id, f.Version, f.EscalationId, f.EscalationResponsibleRole, f.OwnerRole, f.MissedDeadline, f.RaisedAt,
                MinutesBetween(f.RaisedAt, now), patients.GetValueOrDefault(f.PatientEpisodeId))).ToList(),
            History: history.Select(e => Card(e, now, patients)).ToList(),
            Truncated: truncated);
    }

    /// <summary>
    /// Every event for an escalation and its follow-up exceptions, in order, from the
    /// append-only event log.
    /// </summary>
    public async Task<IReadOnlyList<AuditTrailEntry>> GetAuditTrailAsync(Guid escalationId, CancellationToken cancellationToken)
    {
        if (!await _context.Escalations.AnyAsync(e => e.Id == escalationId, cancellationToken))
            throw new AggregateNotFoundException(nameof(Escalation), escalationId);

        var followUpIds = await _context.FollowUpExceptions
            .Where(f => f.EscalationId == escalationId)
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        var aggregateIds = followUpIds.Append(escalationId).ToList();

        var records = await _context.DomainEvents.AsNoTracking()
            .Where(e => aggregateIds.Contains(e.AggregateId))
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);

        return records.Select(r =>
        {
            using var document = JsonDocument.Parse(r.EventData);
            return new AuditTrailEntry(
                r.Id, r.OccurredAt, r.AggregateType, r.AggregateId, r.Version, r.EventType, r.ActorId, r.ActorRole,
                WithoutEnvelope(document.RootElement));
        }).ToList();
    }

    /// <summary>The audit trail as CSV for export (MVP-024).</summary>
    public static string ToCsv(IReadOnlyList<AuditTrailEntry> entries)
    {
        var csv = new StringBuilder("sequence,occurred_at_utc,aggregate_type,aggregate_id,version,event_type,actor_id,actor_role,details\r\n");

        foreach (var e in entries)
        {
            csv.AppendJoin(',',
                e.Sequence, e.OccurredAt.ToString("O"), Cell(e.AggregateType), e.AggregateId, e.Version,
                Cell(e.EventType), e.ActorId, Cell(e.ActorRole), Cell(e.Details.GetRawText()));
            csv.Append("\r\n");
        }

        return csv.ToString();
    }

    public async Task<PolicyOverview> GetPolicyOverviewAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var inForce = await _policies.GetInForceAsync(now, cancellationToken);

        var revisions = await _context.EscalationPolicies.AsNoTracking()
            .OrderByDescending(p => p.Revision)
            .ToListAsync(cancellationToken);

        var views = revisions.Select(p => View(p, p.Id == inForce?.Id)).ToList();
        return new PolicyOverview(now, views.SingleOrDefault(v => v.InForce), views);
    }

    public async Task<PolicyRevisionView?> GetPolicyRevisionAsync(int revision, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var policy = await _context.EscalationPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Revision == revision, cancellationToken);

        if (policy is null)
            return null;

        var inForce = await _policies.GetInForceAsync(now, cancellationToken);
        return View(policy, policy.Id == inForce?.Id);
    }

    /// <summary>
    /// Spec §9: threshold changes are previewable. Shows how many currently waiting patients each
    /// proposed tier would apply to, and how many of those have no escalation at that tier yet.
    /// </summary>
    public async Task<PolicyPreview> PreviewAsync(IReadOnlyList<WaitingTimeTier> tiers, CancellationToken cancellationToken)
    {
        EscalationPolicy.ValidateTiers(enabled: true, tiers);

        var now = _time.GetUtcNow().UtcDateTime;

        var waiting = await _context.PatientEpisodes.AsNoTracking()
            .Where(e => WaitingTimeMonitor.WaitingStateList.Contains(e.State))
            .Select(e => new { e.Id, e.ArrivedAt })
            .ToListAsync(cancellationToken);

        var ids = waiting.Select(w => w.Id).ToList();
        var raised = (await _context.Escalations.AsNoTracking()
                .Where(e => e.TierLevel != null && ids.Contains(e.PatientEpisodeId))
                .Select(e => new { e.PatientEpisodeId, Tier = e.TierLevel!.Value })
                .ToListAsync(cancellationToken))
            .Select(e => (e.PatientEpisodeId, e.Tier))
            .ToHashSet();

        return new PolicyPreview(now, waiting.Count, tiers.Select(t =>
        {
            var reached = waiting.Where(w => (now - w.ArrivedAt).TotalMinutes >= t.ThresholdMinutes).ToList();
            return new PolicyPreviewTier(t.Level, t.ThresholdMinutes, t.ResponsibleRole, reached.Count,
                reached.Count(w => !raised.Contains((w.Id, t.Level))));
        }).ToList());
    }

    private async Task<Dictionary<Guid, PatientSummary>> PatientsAsync(
        IEnumerable<Guid> episodeIds, DateTime now, CancellationToken cancellationToken)
    {
        var ids = episodeIds.Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var episodes = await _context.PatientEpisodes.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.FirstName, e.LastName, e.State, e.ArrivedAt })
            .ToListAsync(cancellationToken);

        return episodes.ToDictionary(
            e => e.Id,
            e => new PatientSummary(e.Id, $"{e.FirstName} {e.LastName}", e.State.ToString(), e.ArrivedAt, MinutesBetween(e.ArrivedAt, now)));
    }

    private static EscalationCard Card(Escalation e, DateTime now, IReadOnlyDictionary<Guid, PatientSummary> patients) => new(
        e.Id, e.Version, e.State.ToString(), e.Trigger.ToString(), e.ResponsibleRole, e.TierLevel, e.PolicyRevision,
        e.RecommendedAction, e.WaitedMinutes, e.CreatedAt, e.AcknowledgementDueAt,
        AcknowledgementOverdue: e.AcknowledgedAt is null && e.AcknowledgementDueAt < now && OpenStates.Contains(e.State),
        MinutesBetween(e.CreatedAt, now), e.AcknowledgedAt, e.AcknowledgedByRole, e.ResolvedAt, e.ResolvedByRole, e.ClosedAt,
        e.Notes, patients.GetValueOrDefault(e.PatientEpisodeId));

    private static PolicyStatusView PolicyStatus(EscalationPolicy? policy) => policy switch
    {
        null => new PolicyStatusView("NoApprovedPolicy", null, null, []),
        { Enabled: false } => new PolicyStatusView("Disabled", policy.Revision, policy.EffectiveFrom, []),
        _ => new PolicyStatusView("InForce", policy.Revision, policy.EffectiveFrom, Tiers(policy))
    };

    private static PolicyRevisionView View(EscalationPolicy p, bool inForce) => new(
        p.Id, p.Version, p.Revision, p.State.ToString(), inForce, p.Enabled, Tiers(p), p.FollowUpOwnerRole,
        p.RestoresRevision, p.ProposalReason, p.ProposedByActorId, p.ProposedByRole, p.ProposedAt, p.DecidedByActorId,
        p.DecidedByRole, p.DecidedAt, p.DecisionReason, p.EffectiveFrom);

    private static List<PolicyTierView> Tiers(EscalationPolicy p) =>
        p.Tiers.Select(t => new PolicyTierView(t.Level, t.ThresholdMinutes, t.ResponsibleRole, t.AcknowledgementDeadlineMinutes, t.RecommendedAction)).ToList();

    private static int MinutesBetween(DateTime from, DateTime to) => (int)Math.Max(0, Math.Floor((to - from).TotalMinutes));

    private static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return null;

        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return Math.Round(median, 1);
    }

    /// <summary>The event payload without the envelope fields already given as columns.</summary>
    private static JsonElement WithoutEnvelope(JsonElement root)
    {
        string[] envelope = ["EventId", "AggregateId", "AggregateType", "TenantId", "Version", "OccurredAt", "ActorId", "ActorRole"];

        var trimmed = root.EnumerateObject()
            .Where(p => !envelope.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Value.Clone());

        return JsonSerializer.SerializeToElement(trimmed);
    }

    private static string Cell(string value)
    {
        // A cell beginning with a formula character is executed by spreadsheet software when an
        // exported audit trail is opened. Free-text notes are user input, so neutralise it.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value.StartsWith('\'')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }
}
