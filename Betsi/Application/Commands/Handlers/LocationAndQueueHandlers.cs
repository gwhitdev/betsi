namespace Betsi.Application.Commands.Handlers;

using Betsi.Domain.Aggregates;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;

public sealed class CreateLocationCommandHandler
    : IRequestHandler<CreateLocationCommand, CommandResult>
{
    private readonly IAggregateRepository<Location> _repository;
    private readonly ITenantContext _tenantContext;

    public CreateLocationCommandHandler(
        IAggregateRepository<Location> repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(
        CreateLocationCommand command, CancellationToken cancellationToken)
    {
        var location = Location.CreateNew(
            _tenantContext.TenantId,
            command.Name,
            command.Capacity,
            command.Description,
            command.EscalationEnabled,
            _tenantContext.ActorId,
            _tenantContext.ActorRole);

        await _repository.AddAsync(location, cancellationToken);

        return new CommandResult(location.Id, location.Version);
    }
}

public sealed class CreateQueueCommandHandler
    : IRequestHandler<CreateQueueCommand, CommandResult>
{
    private readonly IAggregateRepository<Queue> _repository;
    private readonly ITenantContext _tenantContext;

    public CreateQueueCommandHandler(
        IAggregateRepository<Queue> repository, ITenantContext tenantContext)
    {
        _repository = repository;
        _tenantContext = tenantContext;
    }

    public async Task<CommandResult> Handle(
        CreateQueueCommand command, CancellationToken cancellationToken)
    {
        var queue = Queue.CreateNew(
            _tenantContext.TenantId,
            command.LocationId,
            command.Name,
            _tenantContext.ActorId,
            _tenantContext.ActorRole);

        await _repository.AddAsync(queue, cancellationToken);

        return new CommandResult(queue.Id, queue.Version);
    }
}

public sealed class EnqueuePatientCommandHandler
    : AggregateCommandHandler<EnqueuePatientCommand, Queue>
{
    private readonly ITenantContext _tenantContext;

    public EnqueuePatientCommandHandler(
        IAggregateRepository<Queue> repository, ITenantContext tenantContext)
        : base(repository) => _tenantContext = tenantContext;

    protected override Guid GetAggregateId(EnqueuePatientCommand c) => c.QueueId;
    protected override int? GetExpectedVersion(EnqueuePatientCommand c) => c.ExpectedVersion;

    protected override void Apply(EnqueuePatientCommand c, Queue queue) =>
        queue.EnqueuePatient(c.PatientEpisodeId, _tenantContext.ActorId, _tenantContext.ActorRole);
}
