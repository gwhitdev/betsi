namespace Betsi.Application.Commands.Handlers;

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
    : AggregateCommandHandler<BeginPatientTreatmentCommand, PatientEpisode>
{
    private readonly ITenantContext _tenantContext;

    public BeginPatientTreatmentCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(BeginPatientTreatmentCommand c) => c.PatientEpisodeId;
    protected override int? GetExpectedVersion(BeginPatientTreatmentCommand c) => c.ExpectedVersion;

    protected override void Apply(BeginPatientTreatmentCommand c, PatientEpisode episode) =>
        episode.BeginTreatment(c.LocationId, _tenantContext.ActorId, _tenantContext.ActorRole);
}

public sealed class DischargePatientCommandHandler
    : AggregateCommandHandler<DischargePatientCommand, PatientEpisode>
{
    private readonly ITenantContext _tenantContext;

    public DischargePatientCommandHandler(
        IAggregateRepository<PatientEpisode> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(DischargePatientCommand c) => c.PatientEpisodeId;
    protected override int? GetExpectedVersion(DischargePatientCommand c) => c.ExpectedVersion;

    protected override void Apply(DischargePatientCommand c, PatientEpisode episode) =>
        episode.Discharge(_tenantContext.ActorId, _tenantContext.ActorRole, c.DischargeNotes);
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
