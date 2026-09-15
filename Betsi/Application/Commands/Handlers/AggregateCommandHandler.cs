namespace Betsi.Application.Commands.Handlers;

using Betsi.Domain;
using Betsi.Infrastructure.Persistence;
using MediatR;

/// <summary>
/// Base for handlers that load an existing aggregate, apply a change and save it.
/// </summary>
/// <remarks>
/// Every such handler needs the same three steps — load or 404, check the caller's expected
/// version or 409, then mutate and save — so they live here rather than being copied into
/// each handler, where one omission would silently drop optimistic concurrency.
/// </remarks>
public abstract class AggregateCommandHandler<TCommand, TAggregate> : IRequestHandler<TCommand, CommandResult>
    where TCommand : ICommand
    where TAggregate : AggregateRoot
{
    private readonly IAggregateRepository<TAggregate> _repository;

    protected AggregateCommandHandler(IAggregateRepository<TAggregate> repository)
    {
        _repository = repository;
    }

    /// <summary>The aggregate the command targets.</summary>
    protected abstract Guid GetAggregateId(TCommand command);

    /// <summary>
    /// The version the caller believes the aggregate is at, or null to skip the check.
    /// </summary>
    protected abstract int? GetExpectedVersion(TCommand command);

    /// <summary>Applies the command to the loaded aggregate.</summary>
    protected abstract void Apply(TCommand command, TAggregate aggregate);

    public async Task<CommandResult> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var aggregateId = GetAggregateId(command);

        var aggregate = await _repository.GetByIdAsync(aggregateId, cancellationToken)
            ?? throw new AggregateNotFoundException(typeof(TAggregate).Name, aggregateId);

        var expectedVersion = GetExpectedVersion(command);

        // Fails fast on a stale caller. The EF concurrency token on Version is what catches
        // the narrower race where another writer commits between this read and this save.
        if (expectedVersion is { } expected && aggregate.Version != expected)
        {
            throw new AggregateConcurrencyException(
                typeof(TAggregate).Name, aggregateId, expected, aggregate.Version);
        }

        Apply(command, aggregate);

        await _repository.SaveAsync(aggregate, cancellationToken);

        return new CommandResult(aggregate.Id, aggregate.Version);
    }
}
