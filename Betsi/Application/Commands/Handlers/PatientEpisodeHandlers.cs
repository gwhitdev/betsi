namespace Betsi.Application.Commands.Handlers;

using Betsi.Domain;
using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;

public sealed class RegisterPatientCommandHandler
    : IRequestHandler<RegisterPatientCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _repository;
    private readonly ITenantContext _tenantContext;

    public RegisterPatientCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(
        RegisterPatientCommand command, CancellationToken cancellationToken)
    {
        var episode = PatientEpisode.CreateNew(
            _tenantContext.TenantId,
            command.FirstName,
            command.LastName,
            command.DateOfBirth,
            command.NhsNumber,
            _tenantContext.ActorId,
            _tenantContext.ActorRole);

        await _repository.AddAsync(episode, cancellationToken);

        return new CommandResult(episode.Id, episode.Version);
    }
}

public sealed class BeginPatientTriageCommandHandler
    : AggregateCommandHandler<BeginPatientTriageCommand, PatientEpisode>
{
    private readonly ITenantContext _tenantContext;

    public BeginPatientTriageCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(BeginPatientTriageCommand c) => c.PatientEpisodeId;
    protected override int? GetExpectedVersion(BeginPatientTriageCommand c) => c.ExpectedVersion;

    protected override void Apply(BeginPatientTriageCommand c, PatientEpisode episode) =>
        episode.BeginTriage(_tenantContext.ActorId, _tenantContext.ActorRole);
}

public sealed class CompletePatientTriageCommandHandler
    : AggregateCommandHandler<CompletePatientTriageCommand, PatientEpisode>
{
    private readonly ITenantContext _tenantContext;

    public CompletePatientTriageCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(CompletePatientTriageCommand c) => c.PatientEpisodeId;
    protected override int? GetExpectedVersion(CompletePatientTriageCommand c) => c.ExpectedVersion;

    protected override void Apply(CompletePatientTriageCommand c, PatientEpisode episode) =>
        episode.CompleteTriage(_tenantContext.ActorId, _tenantContext.ActorRole);
}

public sealed class BeginPatientTreatmentCommandHandler
    : IRequestHandler<BeginPatientTreatmentCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IAggregateRepository<Location> _locations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public BeginPatientTreatmentCommandHandler(
        IAggregateRepository<PatientEpisode> episodes,
        IAggregateRepository<Location> locations,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _episodes = episodes;
        _locations = locations;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(BeginPatientTreatmentCommand command, CancellationToken cancellationToken)
    {
        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);
        if (episode.Version != command.ExpectedVersion)
            throw new AggregateConcurrencyException(nameof(PatientEpisode), episode.Id, command.ExpectedVersion, episode.Version);

        // Apply the episode state rule first so an illegal workflow remains a 422 even when
        // the supplied location is also wrong. Nothing is persisted until both aggregates commit.
        episode.BeginTreatment(command.LocationId, _tenantContext.ActorId, _tenantContext.ActorRole);
        var location = await _locations.GetByIdAsync(command.LocationId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(Location), command.LocationId);
        location.OccupySpace(episode.Id, _tenantContext.ActorId, _tenantContext.ActorRole);
        await _unitOfWork.CommitAsync([episode, location], cancellationToken);
        return new CommandResult(episode.Id, episode.Version);
    }
}

public sealed class DischargePatientCommandHandler
    : IRequestHandler<DischargePatientCommand, CommandResult>
{
    private readonly IAggregateRepository<PatientEpisode> _episodes;
    private readonly IAggregateRepository<Location> _locations;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITenantContext _tenantContext;

    public DischargePatientCommandHandler(
        IAggregateRepository<PatientEpisode> episodes,
        IAggregateRepository<Location> locations,
        IUnitOfWork unitOfWork,
        ITenantContext tenantContext)
    {
        _episodes = episodes;
        _locations = locations;
        _unitOfWork = unitOfWork;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(DischargePatientCommand command, CancellationToken cancellationToken)
    {
        var episode = await _episodes.GetByIdAsync(command.PatientEpisodeId, cancellationToken)
            ?? throw new AggregateNotFoundException(nameof(PatientEpisode), command.PatientEpisodeId);
        if (episode.Version != command.ExpectedVersion)
            throw new AggregateConcurrencyException(nameof(PatientEpisode), episode.Id, command.ExpectedVersion, episode.Version);

        Location? location = episode.LocationId is { } locationId
            ? await _locations.GetByIdAsync(locationId, cancellationToken)
            : null;
        episode.Discharge(_tenantContext.ActorId, _tenantContext.ActorRole, command.DischargeNotes);
        location?.ReleaseSpace(episode.Id, _tenantContext.ActorId, _tenantContext.ActorRole);
        await _unitOfWork.CommitAsync(location is null ? [episode] : [episode, location], cancellationToken);
        return new CommandResult(episode.Id, episode.Version);
    }
}

public sealed class CancelPatientEpisodeCommandHandler
    : AggregateCommandHandler<CancelPatientEpisodeCommand, PatientEpisode>
{
    private readonly ITenantContext _tenantContext;

    public CancelPatientEpisodeCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(CancelPatientEpisodeCommand c) => c.PatientEpisodeId;
    protected override int? GetExpectedVersion(CancelPatientEpisodeCommand c) => c.ExpectedVersion;

    protected override void Apply(CancelPatientEpisodeCommand c, PatientEpisode episode) =>
        episode.Cancel(_tenantContext.ActorId, _tenantContext.ActorRole, c.Reason);
}
