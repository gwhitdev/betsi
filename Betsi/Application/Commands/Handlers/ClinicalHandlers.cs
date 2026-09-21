namespace Betsi.Application.Commands.Handlers;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Domain.Clinical;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;

/// <summary>Site-configurable clinical settings (MVP-030, MVP-034).</summary>
/// <remarks>
/// Per site, because "child" for safeguarding purposes and "who responds to a deterioration" are
/// local policy, not facts about medicine. The defaults are conservative: a higher unaccompanied
/// age alerts more often, and alerting too often is a far better failure than not alerting.
/// </remarks>
public sealed class ClinicalOptions
{
    public const string SectionName = "Clinical";

    /// <summary>A patient under this age with no carer present raises a safeguarding escalation.</summary>
    public int UnaccompaniedChildAgeYears { get; set; } = 16;

    /// <summary>Who a safeguarding concern escalates to.</summary>
    public string SafeguardingRole { get; set; } = "Nurse in Charge";

    /// <summary>Who a deterioration flag escalates to.</summary>
    public string DeteriorationRole { get; set; } = "Senior Clinician";

    /// <summary>Pain score at which an analgesic review is requested.</summary>
    public int PainReviewThreshold { get; set; } = 5;

    /// <summary>Role responsible for responding to a pain-review alert.</summary>
    public string PainReviewRole { get; set; } = "Nurse in Charge";

    /// <summary>Patients younger than this require the paediatric staffing pathway.</summary>
    public int PaediatricPathwayAgeYears { get; set; } = 18;

    /// <summary>Role responsible for resolving a missing paediatric competence alert.</summary>
    public string PaediatricSkillGapRole { get; set; } = "Nurse in Charge";
}

public sealed class RecordObservationCommandHandler : IRequestHandler<RecordObservationCommand, CommandResult>
{
    private readonly IAggregateRepository<ClinicalObservation> _observations;
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ClinicalOptions _options;

    public RecordObservationCommandHandler(
        IAggregateRepository<ClinicalObservation> observations,
        IAggregateRepository<PatientEpisode> episodes,
        ITenantContext tenantContext,
        TimeProvider time,
        IUnitOfWork unitOfWork,
        ClinicalOptions options)
    {
        _observations = observations;
        _episodes = episodes;
        _tenantContext = tenantContext;
        _time = time;
        _unitOfWork = unitOfWork;
        _options = options;
    }

    public async Task<CommandResult> Handle(RecordObservationCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        // The episode is loaded, not assumed: an observation filed against an episode that does
        // not exist is a measurement nobody will ever see again (hazard H-04). It also supplies
        // the age band, which decides whether the observation can be scored at all.
        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        ClinicalObservation? superseded = null;
        if (command.SupersedesObservationId is { } supersedesId)
        {
            superseded = await _observations.GetByIdAsync(supersedesId, cancellationToken)
                ?? throw new AggregateNotFoundException(nameof(ClinicalObservation), supersedesId);

            if (command.ExpectedVersion is { } expected && superseded.Version != expected)
                throw new AggregateConcurrencyException(nameof(ClinicalObservation), supersedesId,
                    expected, superseded.Version);

            if (superseded.PatientEpisodeId != command.PatientEpisodeId)
            {
                throw new DomainRuleViolationException(
                    "A correction must belong to the same episode as the observation it corrects.");
            }
        }

        var vitals = new VitalSigns(
            command.RespiratoryRate,
            command.OxygenSaturation,
            command.OnSupplementalOxygen,
            command.SystolicBloodPressure,
            command.Pulse,
            command.Consciousness,
            command.Temperature);

        var kind = Enum.TryParse<ClinicalObservation.ObservationKind>(command.Kind, ignoreCase: true, out var parsed)
            ? parsed
            : ClinicalObservation.ObservationKind.Routine;

        var observation = ClinicalObservation.Record(
            _tenantContext.TenantId,
            command.PatientEpisodeId,
            episode.AgeBandAt(now),
            kind,
            vitals,
            command.PainScore,
            command.PainScale,
            command.Notes,
            now,
            _tenantContext.ActorId,
            _tenantContext.ActorRole,
            command.SupersedesObservationId,
            command.PainLocation,
            command.PainCharacter,
            command.PainOnsetAt,
            command.Source,
            command.SbarSituation,
            command.SbarBackground,
            command.SbarAssessment,
            command.SbarRecommendation,
            command.BreathingFinding,
            command.BreathingDetails,
            command.CirculationFinding,
            command.CirculationDetails,
            command.MobilityFinding,
            command.MobilityDetails);

        if (superseded is not null)
        {
            superseded.SupersededBy(observation.Id, now, _tenantContext.ActorId, _tenantContext.ActorRole);
        }

        _unitOfWork.Add(observation);
        Escalation? painEscalation = null;
        if (command.PainScore >= _options.PainReviewThreshold &&
            (superseded?.PainScore is null || superseded.PainScore < _options.PainReviewThreshold))
        {
            painEscalation = Escalation.CreateSystemAlert(
                _tenantContext.TenantId,
                episode.Id,
                episode.LocationId,
                _options.PainReviewRole,
                PainReviewNotes(command),
                _tenantContext.ActorId,
                _tenantContext.ActorRole,
                now);
            _unitOfWork.Add(painEscalation);
        }

        var aggregates = new List<AggregateRoot> { observation };
        if (superseded is not null) aggregates.Add(superseded);
        if (painEscalation is not null) aggregates.Add(painEscalation);
        if (episode.AgeYearsAt(now) < _options.PaediatricPathwayAgeYears &&
            episode.AssignedStaffPaediatricTrained != true && !episode.PaediatricSkillGapAlertOpen)
        {
            episode.MarkPaediatricSkillGapAlerted(now, _tenantContext.ActorId, _tenantContext.ActorRole);
            var skillGap = Escalation.CreateSystemAlert(
                _tenantContext.TenantId, episode.Id, episode.LocationId,
                _options.PaediatricSkillGapRole,
                "Paediatric competence required: an observation was recorded without a paediatric-trained clinician assigned.",
                _tenantContext.ActorId, _tenantContext.ActorRole, now);
            _unitOfWork.Add(skillGap);
            aggregates.Add(episode);
            aggregates.Add(skillGap);
        }
        await _unitOfWork.CommitAsync(
            aggregates, cancellationToken);

        return new CommandResult(observation.Id, observation.Version);
    }

    private static string PainReviewNotes(RecordObservationCommand command)
    {
        var details = new[]
        {
            command.PainLocation is { Length: > 0 } ? $"location {command.PainLocation.Trim()}" : null,
            command.PainCharacter is { Length: > 0 } ? $"character {command.PainCharacter.Trim()}" : null,
            command.PainOnsetAt is { } onset ? $"onset {onset:u}" : null
        }.Where(value => value is not null);
        var suffix = string.Join(", ", details);
        return $"Pain management review: score {command.PainScore} on {command.PainScale}" +
            (suffix.Length == 0 ? "." : $"; {suffix}.");
    }
}

public sealed class AssignClinicalStaffCommandHandler : IRequestHandler<AssignClinicalStaffCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ClinicalOptions _options;
    private readonly TimeProvider _time;

    public AssignClinicalStaffCommandHandler(
        IAggregateRepository<PatientEpisode> episodes, IUnitOfWork unitOfWork,
        ITenantContext tenantContext, ClinicalOptions options, TimeProvider time)
    {
        _episodes = episodes;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _options = options;
        _time = time;
    }

    public async Task<CommandResult> Handle(AssignClinicalStaffCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);
        if (episode.Version != command.ExpectedVersion)
            throw new AggregateConcurrencyException(nameof(PatientEpisode), episode.Id,
                command.ExpectedVersion, episode.Version);

        episode.AssignClinicalStaff(command.AssignedStaffActorId, command.AssignedStaffName,
            command.AssignedStaffRole, command.PaediatricTrained, now,
            _tenantContext.ActorId, _tenantContext.ActorRole);

        Escalation? escalation = null;
        if (episode.AgeYearsAt(now) < _options.PaediatricPathwayAgeYears &&
            !command.PaediatricTrained && !episode.PaediatricSkillGapAlertOpen)
        {
            episode.MarkPaediatricSkillGapAlerted(now, _tenantContext.ActorId, _tenantContext.ActorRole);
            escalation = Escalation.CreateSystemAlert(
                _tenantContext.TenantId, episode.Id, episode.LocationId,
                _options.PaediatricSkillGapRole,
                $"Paediatric competence required: {command.AssignedStaffName.Trim()} is assigned without recorded paediatric training.",
                _tenantContext.ActorId, _tenantContext.ActorRole, now);
            _unitOfWork.Add(escalation);
        }

        await _unitOfWork.CommitAsync(escalation is null ? [episode] : [episode, escalation], cancellationToken);
        return new CommandResult(episode.Id, episode.Version);
    }
}

public sealed class RecordCarerPresenceCommandHandler : IRequestHandler<RecordCarerPresenceCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ClinicalOptions _options;
    private readonly TimeProvider _time;

    public RecordCarerPresenceCommandHandler(
        IAggregateRepository<PatientEpisode> episodes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ClinicalOptions options,
        TimeProvider time)
    {
        _episodes = episodes;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _options = options;
        _time = time;
    }

    public async Task<CommandResult> Handle(RecordCarerPresenceCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        episode.RecordCarerPresence(
            command.CarerPresent, command.CarerName, command.CarerRelationship,
            now, _tenantContext.ActorId, _tenantContext.ActorRole);

        // A child recorded as alone is a safeguarding fact the moment it is recorded, so the
        // escalation is raised in the same transaction as the record of it. A carer leaving and
        // nobody saying so is not detectable here, and the runbook says as much (hazard H-07).
        var unaccompaniedChild =
            !command.CarerPresent && episode.AgeYearsAt(now) < _options.UnaccompaniedChildAgeYears;

        Escalation? escalation = null;
        if (unaccompaniedChild && !episode.SafeguardingConcernRaised)
        {
            episode.RaiseSafeguardingConcern(now, _tenantContext.ActorId, _tenantContext.ActorRole);

            escalation = Escalation.CreateManual(
                _tenantContext.TenantId,
                episode.Id,
                episode.LocationId,
                _options.SafeguardingRole,
                $"Unaccompanied child: {episode.AgeYearsAt(now)} years old, no carer present.",
                _tenantContext.ActorId,
                _tenantContext.ActorRole,
                now);

            _unitOfWork.Add(escalation);
        }

        await _unitOfWork.CommitAsync(
            escalation is null ? [episode] : [episode, escalation], cancellationToken);
        return new CommandResult(episode.Id, episode.Version);
    }
}

public sealed class RaiseSafeguardingConcernCommandHandler
    : IRequestHandler<RaiseSafeguardingConcernCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ClinicalOptions _options;
    private readonly TimeProvider _time;

    public RaiseSafeguardingConcernCommandHandler(
        IAggregateRepository<PatientEpisode> episodes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ClinicalOptions options,
        TimeProvider time)
    {
        _episodes = episodes;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _options = options;
        _time = time;
    }

    public async Task<CommandResult> Handle(
        RaiseSafeguardingConcernCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        episode.RaiseSafeguardingConcern(now, _tenantContext.ActorId, _tenantContext.ActorRole);

        // Raised every time, even when the episode already carries a concern: two people
        // noticing separate things must not have the second one silently discarded.
        var escalation = Escalation.CreateManual(
            _tenantContext.TenantId,
            episode.Id,
            episode.LocationId,
            command.ResponsibleRole ?? _options.SafeguardingRole,
            $"Safeguarding concern: {command.Reason}",
            _tenantContext.ActorId,
            _tenantContext.ActorRole,
            now);

        _unitOfWork.Add(escalation);
        await _unitOfWork.CommitAsync([episode, escalation], cancellationToken);

        return new CommandResult(escalation.Id, escalation.Version);
    }
}

public sealed class FlagPatientDeteriorationCommandHandler
    : IRequestHandler<FlagPatientDeteriorationCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;
    private readonly ClinicalOptions _options;
    private readonly TimeProvider _time;

    public FlagPatientDeteriorationCommandHandler(
        IAggregateRepository<PatientEpisode> episodes,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext,
        ClinicalOptions options,
        TimeProvider time)
    {
        _episodes = episodes;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
        _options = options;
        _time = time;
    }

    public async Task<CommandResult> Handle(
        FlagPatientDeteriorationCommand command, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);

        episode.FlagDeterioration(command.Reason, now, _tenantContext.ActorId, _tenantContext.ActorRole);

        var escalation = Escalation.CreateManual(
            _tenantContext.TenantId,
            episode.Id,
            episode.LocationId,
            command.ResponsibleRole ?? _options.DeteriorationRole,
            $"Deterioration: {command.Reason}",
            _tenantContext.ActorId,
            _tenantContext.ActorRole,
            now);

        _unitOfWork.Add(escalation);
        await _unitOfWork.CommitAsync([episode, escalation], cancellationToken);

        return new CommandResult(escalation.Id, escalation.Version);
    }
}
