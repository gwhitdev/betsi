namespace Betsi.Domain;

/// <summary>
/// Thrown when a command would violate an invariant of an aggregate — typically an illegal
/// state transition, such as discharging an already-discharged patient.
/// </summary>
/// <remarks>
/// Distinct from <see cref="InvalidOperationException"/> so the API can answer 422 rather
/// than 500: the request was well-formed and the caller is not at fault for a race, but the
/// episode is not in a state where the operation makes clinical sense.
/// </remarks>
public class DomainRuleViolationException : Exception
{
    public DomainRuleViolationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown when a command names an aggregate that does not exist in the current tenant.
/// </summary>
public class AggregateNotFoundException : Exception
{
    public AggregateNotFoundException(string aggregateType, Guid id)
        : base($"{aggregateType} '{id}' was not found.")
    {
        AggregateType = aggregateType;
        AggregateId = id;
    }

    public string AggregateType { get; }
    public Guid AggregateId { get; }
}

/// <summary>
/// Thrown when the caller's expected version does not match the stored aggregate, meaning
/// another actor changed it in between the caller's read and write.
/// </summary>
public class AggregateConcurrencyException : Exception
{
    public AggregateConcurrencyException(string aggregateType, Guid id, int expectedVersion, int actualVersion)
        : base($"{aggregateType} '{id}' has changed since it was read " +
               $"(expected version {expectedVersion}, found {actualVersion}). Re-read and retry.")
    {
        AggregateType = aggregateType;
        AggregateId = id;
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public string AggregateType { get; }
    public Guid AggregateId { get; }
    public int ExpectedVersion { get; }
    public int ActualVersion { get; }
}

/// <summary>
/// Thrown when a command conflicts with a concurrent change that is not an aggregate version
/// mismatch — for example, two proposals claiming the same policy revision number.
/// </summary>
public class ConflictException : Exception
{
    public ConflictException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
