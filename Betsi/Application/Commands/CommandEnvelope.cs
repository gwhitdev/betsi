namespace Betsi.Application.Commands;

using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A command submitted through <c>POST /api/v1/commands</c> (spec §4.1, MVP-061).
/// </summary>
/// <param name="CommandType">The command name without the "Command" suffix, e.g. <c>RegisterPatient</c>.</param>
/// <param name="CommandId">Client-generated id for this command, echoed in the response and logs.</param>
/// <param name="IdempotencyKey">Retrying with the same key returns the original result instead of acting again.</param>
/// <param name="CorrelationId">Carried through logs so one user action can be traced across systems.</param>
/// <param name="ExpectedVersion">For commands against an existing record: the version the client last read.</param>
/// <param name="Payload">The command's fields.</param>
public sealed record CommandEnvelope(
    string CommandType,
    Guid CommandId,
    string? IdempotencyKey,
    string? CorrelationId,
    int? ExpectedVersion,
    JsonElement Payload);

public sealed record CommandEnvelopeResult(
    Guid CommandId,
    string CommandType,
    string Status,
    CommandResult Result,
    string? CorrelationId,
    bool Replayed);

/// <summary>Thrown when an envelope cannot be accepted before its command runs.</summary>
public sealed class EnvelopeRejectedException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Dispatches envelope commands with idempotency.</summary>
/// <remarks>
/// The command still passes through the full pipeline — audit, permission, licence and
/// validation — so the envelope is a second door into the same room, not a way around it.
/// Monitor-only commands are not in the registry at all.
///
/// Idempotency is reserve-then-run: a pending record is committed under the key before the
/// command executes, so two concurrent retries cannot both act. A success is stored and
/// replayed; a failure removes the reservation so the client can correct and retry.
/// </remarks>
public sealed class CommandEnvelopeDispatcher
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Every command a person or integration may submit, by name.</summary>
    public static readonly FrozenDictionary<string, Type> Registry = typeof(ICommand).Assembly.GetTypes()
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ICommand).IsAssignableFrom(t))
        .Where(t => t.GetCustomAttribute<RequiresPermissionAttribute>() is not null)
        .ToFrozenDictionary(t => t.Name.EndsWith("Command", StringComparison.Ordinal) ? t.Name[..^"Command".Length] : t.Name,
            StringComparer.OrdinalIgnoreCase);

    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public CommandEnvelopeDispatcher(IMediator mediator, ITenantContext tenantContext, IServiceScopeFactory scopes, TimeProvider time)
    {
        _mediator = mediator;
        _tenantContext = tenantContext;
        _scopes = scopes;
        _time = time;
    }

    public async Task<CommandEnvelopeResult> DispatchAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!Registry.TryGetValue(envelope.CommandType ?? string.Empty, out var commandType))
        {
            throw new EnvelopeRejectedException(StatusCodes.Status400BadRequest, "UNKNOWN_COMMAND_TYPE",
                $"'{envelope.CommandType}' is not a command. Known commands: {string.Join(", ", Registry.Keys.Order())}.");
        }

        if (envelope.CommandId == Guid.Empty)
            throw new EnvelopeRejectedException(StatusCodes.Status400BadRequest, "BAD_REQUEST", "commandId is required.");

        if (envelope.IdempotencyKey is { Length: > 100 })
            throw new EnvelopeRejectedException(StatusCodes.Status400BadRequest, "BAD_REQUEST", "idempotencyKey must be at most 100 characters.");

        var command = Materialise(envelope, commandType);
        var name = commandType.Name[..^"Command".Length];

        if (string.IsNullOrWhiteSpace(envelope.IdempotencyKey))
        {
            var result = await _mediator.Send((ICommand)command, cancellationToken);
            return new CommandEnvelopeResult(envelope.CommandId, name, "Succeeded", result, envelope.CorrelationId, false);
        }

        var hash = Hash(name, envelope);

        var replay = await ReserveAsync(envelope, name, hash, cancellationToken);
        if (replay is not null)
            return replay;

        try
        {
            var result = await _mediator.Send((ICommand)command, cancellationToken);
            var outcome = new CommandEnvelopeResult(envelope.CommandId, name, "Succeeded", result, envelope.CorrelationId, false);
            await CompleteAsync(envelope.IdempotencyKey, outcome, CancellationToken.None);
            return outcome;
        }
        catch
        {
            await ReleaseAsync(envelope.IdempotencyKey, CancellationToken.None);
            throw;
        }
    }

    private static object Materialise(CommandEnvelope envelope, Type commandType)
    {
        object command;
        try
        {
            command = envelope.Payload.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize(envelope.Payload, commandType, PayloadOptions)!
                : throw new JsonException("payload must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new EnvelopeRejectedException(StatusCodes.Status400BadRequest, "BAD_REQUEST", $"payload is not a valid {commandType.Name}: {exception.Message}");
        }

        if (envelope.ExpectedVersion is { } version)
        {
            var property = commandType.GetProperty("ExpectedVersion");
            if (property is null)
            {
                throw new EnvelopeRejectedException(StatusCodes.Status400BadRequest, "BAD_REQUEST",
                    $"{commandType.Name} does not take an expected version.");
            }

            property.SetValue(command, version);
        }

        return command;
    }

    private async Task<CommandEnvelopeResult?> ReserveAsync(CommandEnvelope envelope, string name, string hash, CancellationToken cancellationToken)
    {
        await using var scope = NewScope(out var db);

        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            IdempotencyKey = envelope.IdempotencyKey!,
            ActorId = _tenantContext.ActorId,
            CommandType = name,
            RequestHash = hash,
            Status = "Pending",
            CommandId = envelope.CommandId,
            CorrelationId = envelope.CorrelationId,
            CreatedAt = _time.GetUtcNow().UtcDateTime
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }

        var existing = await db.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(r => r.IdempotencyKey == envelope.IdempotencyKey, cancellationToken)
            ?? throw new EnvelopeRejectedException(StatusCodes.Status409Conflict, "IDEMPOTENCY_IN_PROGRESS",
                "A request with this idempotency key is being processed. Retry shortly.");

        // A key belongs to the actor and request that first used it. Anything else is refused
        // without revealing what the original request was.
        if (existing.ActorId != _tenantContext.ActorId || existing.RequestHash != hash)
        {
            throw new EnvelopeRejectedException(StatusCodes.Status422UnprocessableEntity, "IDEMPOTENCY_KEY_REUSED",
                "This idempotency key was already used for a different request.");
        }

        if (existing.Status != "Completed" || existing.ResponseBody is null)
        {
            throw new EnvelopeRejectedException(StatusCodes.Status409Conflict, "IDEMPOTENCY_IN_PROGRESS",
                "A request with this idempotency key is being processed. Retry shortly.");
        }

        return JsonSerializer.Deserialize<CommandEnvelopeResult>(existing.ResponseBody, PayloadOptions)! with { Replayed = true };
    }

    private async Task CompleteAsync(string key, CommandEnvelopeResult outcome, CancellationToken cancellationToken)
    {
        await using var scope = NewScope(out var db);
        var record = await db.IdempotencyRecords.SingleAsync(r => r.IdempotencyKey == key, cancellationToken);
        record.Status = "Completed";
        record.ResponseBody = JsonSerializer.Serialize(outcome, PayloadOptions);
        record.CompletedAt = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        await using var scope = NewScope(out var db);
        await db.IdempotencyRecords.Where(r => r.IdempotencyKey == key && r.Status == "Pending").ExecuteDeleteAsync(cancellationToken);
    }

    /// <remarks>
    /// Idempotency records are written in their own scope: a command that fails on SaveChanges
    /// leaves the request's context holding its failed changes, which must not be flushed along
    /// with the record.
    /// </remarks>
    private AsyncServiceScope NewScope(out BetsiDbContext db)
    {
        var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(_tenantContext.TenantId, _tenantContext.ActorId, _tenantContext.ActorRole);
        db = scope.ServiceProvider.GetRequiredService<BetsiDbContext>();
        return scope;
    }

    private static string Hash(string commandType, CommandEnvelope envelope)
    {
        var canonical = $"{commandType}\n{envelope.ExpectedVersion}\n{JsonSerializer.Serialize(envelope.Payload)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
