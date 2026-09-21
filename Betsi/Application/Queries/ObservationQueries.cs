namespace Betsi.Application.Queries;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>The complete immutable history of observations recorded against an episode.</summary>
public sealed class ObservationQueries(BetsiDbContext db)
{
    public async Task<IReadOnlyList<ObservationView>> GetHistoryAsync(
        Guid episodeId, CancellationToken cancellationToken)
    {
        if (!await db.PatientEpisodes.AnyAsync(e => e.Id == episodeId, cancellationToken))
            throw new AggregateNotFoundException(nameof(PatientEpisode), episodeId);

        return await db.ClinicalObservations.AsNoTracking()
            .Where(o => o.PatientEpisodeId == episodeId)
            .OrderBy(o => o.RecordedAt).ThenBy(o => o.Id)
            .Select(o => new ObservationView(
                o.Id, o.Version, o.PatientEpisodeId, o.Kind.ToString(), o.AgeBand.ToString(),
                o.RespiratoryRate, o.OxygenSaturation, o.OnSupplementalOxygen,
                o.SystolicBloodPressure, o.Pulse,
                o.Consciousness == null ? null : o.Consciousness.ToString(), o.Temperature,
                o.PainScore, o.PainScale == null ? null : o.PainScale.ToString(),
                o.PainLocation, o.PainCharacter, o.PainOnsetAt,
                o.Source == null ? null : o.Source.ToString(),
                o.SbarSituation, o.SbarBackground, o.SbarAssessment, o.SbarRecommendation,
                o.BreathingFinding == null ? null : o.BreathingFinding.ToString(), o.BreathingDetails,
                o.CirculationFinding == null ? null : o.CirculationFinding.ToString(), o.CirculationDetails,
                o.MobilityFinding == null ? null : o.MobilityFinding.ToString(), o.MobilityDetails,
                o.Notes,
                o.EarlyWarningScore, o.HighestSingleParameter, o.ScoreUnavailable.ToString(),
                o.RecordedAt, o.RecordedBy, o.RecordedByRole, o.SupersedesObservationId,
                o.SupersededByObservationId))
            .ToListAsync(cancellationToken);
    }
}

public sealed record ObservationView(
    Guid Id,
    int Version,
    Guid PatientEpisodeId,
    string Kind,
    string AgeBand,
    int? RespiratoryRate,
    int? OxygenSaturation,
    bool OnSupplementalOxygen,
    int? SystolicBloodPressure,
    int? Pulse,
    string? Consciousness,
    decimal? Temperature,
    int? PainScore,
    string? PainScale,
    string? PainLocation,
    string? PainCharacter,
    DateTime? PainOnsetAt,
    string? Source,
    string? SbarSituation,
    string? SbarBackground,
    string? SbarAssessment,
    string? SbarRecommendation,
    string? BreathingFinding,
    string? BreathingDetails,
    string? CirculationFinding,
    string? CirculationDetails,
    string? MobilityFinding,
    string? MobilityDetails,
    string? Notes,
    int? EarlyWarningScore,
    int? HighestSingleParameter,
    string ScoreUnavailable,
    DateTime RecordedAt,
    Guid RecordedBy,
    string RecordedByRole,
    Guid? SupersedesObservationId,
    Guid? SupersededByObservationId);
