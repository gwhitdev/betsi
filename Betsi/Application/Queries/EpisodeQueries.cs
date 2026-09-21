namespace Betsi.Application.Queries;

using Betsi.Application.Escalations;
using Betsi.Application.Commands.Handlers;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Text;
using System.Text;

public sealed record EpisodeEscalationSummary(
    Guid Id, string State, string Trigger, string ResponsibleRole, int? TierLevel, DateTime CreatedAt,
    DateTime? AcknowledgementDueAt, bool AcknowledgementOverdue, DateTime? AcknowledgedAt, DateTime? ResolvedAt);

/// <summary>The canonical view of one episode (spec §4.1: <c>GET /episodes/{id}</c>).</summary>
public sealed record EpisodeDetail(
    Guid Id,
    int Version,
    string FirstName,
    string LastName,
    DateTime DateOfBirth,
    int AgeYears,
    string? NhsNumber,
    bool? CarerPresent,
    string? CarerName,
    string? CarerRelationship,
    DateTime? CarerPresenceRecordedAt,
    bool SafeguardingConcernRaised,
    bool DeteriorationFlagged,
    DateTime? DeteriorationFlaggedAt,
    bool RequiresPaediatricPathway,
    Guid? AssignedStaffActorId,
    string? AssignedStaffName,
    string? AssignedStaffRole,
    bool? AssignedStaffPaediatricTrained,
    DateTime? StaffAssignedAt,
    bool PaediatricSkillGapAlertOpen,
    string State,
    Guid? LocationId,
    DateTime ArrivedAt,
    DateTime? TriageStartedAt,
    DateTime? TreatmentStartedAt,
    DateTime? EndedAt,
    int? MinutesWaiting,
    IReadOnlyList<EpisodeEscalationSummary> Escalations,
    int OpenFollowUpExceptions,
    DateTime GeneratedAt);

public sealed record WaitingBoardRow(
    Guid EpisodeId,
    int Version,
    string Name,
    int AgeYears,
    string State,
    Guid? LocationId,
    DateTime ArrivedAt,
    int MinutesWaiting,
    int OpenEscalations,
    int? HighestTierRaised,
    bool AcknowledgementOverdue);

/// <param name="NextCursor">Pass as <c>cursor</c> for the next page; null on the last page.</param>
public sealed record WaitingBoardPage(DateTime GeneratedAt, string Source, IReadOnlyList<WaitingBoardRow> Items, string? NextCursor);

public sealed record WaitingBoardFilter(
    Guid? LocationId = null,
    string? State = null,
    int? MinWaitingMinutes = null,
    int? MinAgeYears = null,
    int? MaxAgeYears = null,
    string? Cursor = null,
    int PageSize = WaitingBoardFilter.DefaultPageSize)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
}

/// <summary>Thrown for a query parameter that cannot be honoured; becomes a 400.</summary>
public sealed class InvalidQueryException(string message) : Exception(message);

/// <summary>Episode detail and the waiting board (MVP-062).</summary>
public sealed class EpisodeQueries
{
    private readonly BetsiDbContext _context;
    private readonly TimeProvider _time;
    private readonly ClinicalOptions _clinical;

    public EpisodeQueries(BetsiDbContext context, TimeProvider time, ClinicalOptions clinical)
    {
        _context = context;
        _time = time;
        _clinical = clinical;
    }

    public async Task<EpisodeDetail?> GetEpisodeAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var episode = await _context.PatientEpisodes.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (episode is null)
            return null;

        var escalations = await _context.Escalations.AsNoTracking()
            .Where(e => e.PatientEpisodeId == id)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var openFollowUps = await _context.FollowUpExceptions
            .CountAsync(f => f.PatientEpisodeId == id && f.State == FollowUpException.FollowUpState.Open, cancellationToken);

        return new EpisodeDetail(
            episode.Id, episode.Version, episode.FirstName, episode.LastName, episode.DateOfBirth,
            AgeOn(episode.DateOfBirth, now), episode.NhsNumber,
            episode.CarerPresent, episode.CarerName, episode.CarerRelationship, episode.CarerPresenceRecordedAt,
            episode.SafeguardingConcernRaised, episode.DeteriorationFlagged, episode.DeteriorationFlaggedAt,
            AgeOn(episode.DateOfBirth, now) < _clinical.PaediatricPathwayAgeYears,
            episode.AssignedStaffActorId, episode.AssignedStaffName, episode.AssignedStaffRole,
            episode.AssignedStaffPaediatricTrained, episode.StaffAssignedAt, episode.PaediatricSkillGapAlertOpen,
            episode.State.ToString(), episode.LocationId,
            episode.ArrivedAt, episode.TriageStartedAt, episode.TreatmentStartedAt, episode.EndedAt,
            WaitingTimeMonitor.WaitingStates.Contains(episode.State) ? Minutes(episode.ArrivedAt, now) : null,
            escalations.Select(e => new EpisodeEscalationSummary(
                e.Id, e.State.ToString(), e.Trigger.ToString(), e.ResponsibleRole, e.TierLevel, e.CreatedAt,
                e.AcknowledgementDueAt, e.HasExceededAcknowledgementDeadline(now), e.AcknowledgedAt, e.ResolvedAt)).ToList(),
            openFollowUps,
            now);
    }

    /// <remarks>
    /// Keyset pagination on (arrival, id): stable while patients arrive and leave between pages,
    /// and each page is an index seek however deep the client pages — unlike offset paging,
    /// which skips or repeats patients as the board changes underneath it.
    /// </remarks>
    public async Task<WaitingBoardPage> GetWaitingBoardAsync(WaitingBoardFilter filter, CancellationToken cancellationToken)
    {
        if (filter.PageSize is < 1 or > WaitingBoardFilter.MaxPageSize)
            throw new InvalidQueryException($"pageSize must be between 1 and {WaitingBoardFilter.MaxPageSize}.");

        var now = _time.GetUtcNow().UtcDateTime;
        var states = WaitingTimeMonitor.WaitingStateList;

        if (filter.State is not null)
        {
            if (!Enum.TryParse<PatientEpisode.PatientState>(filter.State, ignoreCase: true, out var state) ||
                !WaitingTimeMonitor.WaitingStates.Contains(state))
            {
                throw new InvalidQueryException("state must be Waiting or AwaitingTreatment.");
            }

            states = [state];
        }

        var query = _context.PatientEpisodes.AsNoTracking().Where(e => states.Contains(e.State));

        if (filter.LocationId is { } location)
            query = query.Where(e => e.LocationId == location);

        if (filter.MinWaitingMinutes is { } minWait)
        {
            var arrivedBy = now.AddMinutes(-minWait);
            query = query.Where(e => e.ArrivedAt <= arrivedBy);
        }

        // Age filters become date-of-birth bounds so they stay in the database query.
        if (filter.MinAgeYears is { } minAge)
        {
            var bornOnOrBefore = now.Date.AddYears(-minAge);
            query = query.Where(e => e.DateOfBirth <= bornOnOrBefore);
        }

        if (filter.MaxAgeYears is { } maxAge)
        {
            var bornAfter = now.Date.AddYears(-(maxAge + 1));
            query = query.Where(e => e.DateOfBirth > bornAfter);
        }

        if (filter.Cursor is not null)
        {
            var (arrivedAt, id) = DecodeCursor(filter.Cursor);
            query = query.Where(e => e.ArrivedAt > arrivedAt || (e.ArrivedAt == arrivedAt && e.Id.CompareTo(id) > 0));
        }

        var page = await query
            .OrderBy(e => e.ArrivedAt).ThenBy(e => e.Id)
            .Take(filter.PageSize + 1)
            .Select(e => new { e.Id, e.Version, e.FirstName, e.LastName, e.DateOfBirth, e.State, e.LocationId, e.ArrivedAt })
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > filter.PageSize;
        page = page.Take(filter.PageSize).ToList();

        var ids = page.Select(p => p.Id).ToList();
        var open = await _context.Escalations.AsNoTracking()
            .Where(e => ids.Contains(e.PatientEpisodeId) &&
                        e.State != Escalation.EscalationState.Resolved && e.State != Escalation.EscalationState.Closed)
            .Select(e => new { e.PatientEpisodeId, e.TierLevel, e.State, e.AcknowledgementDueAt })
            .ToListAsync(cancellationToken);

        var byEpisode = open.ToLookup(e => e.PatientEpisodeId);

        var items = page.Select(p => new WaitingBoardRow(
            p.Id, p.Version, $"{p.FirstName} {p.LastName}", AgeOn(p.DateOfBirth, now), p.State.ToString(), p.LocationId,
            p.ArrivedAt, Minutes(p.ArrivedAt, now),
            byEpisode[p.Id].Count(),
            byEpisode[p.Id].Max(e => e.TierLevel),
            byEpisode[p.Id].Any(e => e.State == Escalation.EscalationState.Created && e.AcknowledgementDueAt < now))).ToList();

        var last = page.LastOrDefault();
        return new WaitingBoardPage(now, "tenant-database", items,
            hasMore && last is not null ? EncodeCursor(last.ArrivedAt, last.Id) : null);
    }

    private static string EncodeCursor(DateTime arrivedAt, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{arrivedAt.Ticks}:{id:N}"));

    private static (DateTime ArrivedAt, Guid Id) DecodeCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            return (new DateTime(long.Parse(parts[0]), DateTimeKind.Utc), Guid.ParseExact(parts[1], "N"));
        }
        catch (Exception exception) when (exception is FormatException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new InvalidQueryException("cursor is not valid. Use the nextCursor from a previous page.");
        }
    }

    private static int Minutes(DateTime from, DateTime to) => (int)Math.Max(0, Math.Floor((to - from).TotalMinutes));

    private static int AgeOn(DateTime dateOfBirth, DateTime today)
    {
        var age = today.Year - dateOfBirth.Year;
        return dateOfBirth.Date > today.Date.AddYears(-age) ? age - 1 : age;
    }
}
