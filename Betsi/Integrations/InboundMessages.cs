namespace Betsi.Integrations;

using Betsi.Application.Commands;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class InboundFormats
{
    public const string Hl7v2 = "Hl7v2";
    public const string FhirR4 = "FhirR4";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Hl7v2, FhirR4 };
}

/// <summary>What an inbound message asks for, independent of its wire format.</summary>
public abstract record InboundIntent(string MessageType, string VisitId);

public sealed record ArrivalIntent(string MessageType, string VisitId, string FirstName, string LastName, DateTime DateOfBirth, string? NhsNumber)
    : InboundIntent(MessageType, VisitId);

public sealed record DischargeIntent(string MessageType, string VisitId) : InboundIntent(MessageType, VisitId);

public sealed record CancellationIntent(string MessageType, string VisitId) : InboundIntent(MessageType, VisitId);

/// <summary>Thrown when a message cannot be understood. The message is quarantined, not discarded.</summary>
public sealed class UnprocessableMessageException(string message) : Exception(message);

/// <summary>
/// Minimal HL7 v2.x ADT reader (MVP-066): A01 and A04 (arrival), A03 (discharge), A11 (cancel
/// admit). Reads MSH, PID and PV1 only.
/// </summary>
/// <remarks>
/// Supported: HL7 v2.3–v2.5.1 ER7 encoding, the delimiters declared in MSH-1/MSH-2, repetitions
/// in PID-3. Not supported, and quarantined: batches, Z-segments that carry required data, and
/// any other trigger event. This is an adapter for the arrival and discharge feed, not an HL7
/// engine; a site with an integration engine should have it forward these events.
/// </remarks>
public static class Hl7v2Reader
{
    public static (string ControlId, InboundIntent Intent) Read(string message)
    {
        var segments = message.Replace("\r\n", "\r").Replace('\n', '\r')
            .Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var msh = segments.FirstOrDefault(s => s.StartsWith("MSH", StringComparison.Ordinal))
            ?? throw new UnprocessableMessageException("No MSH segment.");

        if (msh.Length < 8)
            throw new UnprocessableMessageException("MSH segment is too short.");

        var fieldSeparator = msh[3];
        var componentSeparator = msh[4];
        var repetitionSeparator = msh[5];

        // MSH-1 is the separator itself, so MSH fields are offset by one when split.
        string[] Fields(string segment) => segment.Split(fieldSeparator);
        string Msh(int n) => Field(Fields(msh), n - 1);

        var controlId = Msh(10);
        var messageType = Msh(9).Split(componentSeparator);
        var trigger = messageType.Length > 1 ? messageType[1] : string.Empty;
        var type = $"{messageType[0]}^{trigger}";

        if (messageType[0] != "ADT")
            throw new UnprocessableMessageException($"Unsupported message type {type}.");

        var pid = Fields(Segment(segments, "PID"));
        var pv1 = Fields(Segment(segments, "PV1"));

        var visitId = Field(pv1, 19).Split(componentSeparator)[0];
        if (string.IsNullOrWhiteSpace(visitId))
            throw new UnprocessableMessageException("PV1-19 (visit number) is required to link arrival and discharge.");

        switch (trigger)
        {
            case "A01" or "A04":
            {
                var name = Field(pid, 5).Split(componentSeparator);
                var family = name.ElementAtOrDefault(0);
                var given = name.ElementAtOrDefault(1);

                if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(given))
                    throw new UnprocessableMessageException("PID-5 must contain family and given name.");

                var dobText = Field(pid, 7);
                if (dobText.Length < 8 || !DateTime.TryParseExact(dobText[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
                    throw new UnprocessableMessageException("PID-7 must be a date of birth (YYYYMMDD).");

                string? nhsNumber = null;
                foreach (var identifier in Field(pid, 3).Split(repetitionSeparator))
                {
                    var parts = identifier.Split(componentSeparator);
                    var authority = parts.ElementAtOrDefault(3) ?? string.Empty;
                    var identifierType = parts.ElementAtOrDefault(4) ?? string.Empty;

                    if (identifierType == "NH" || authority.Contains("NHS", StringComparison.OrdinalIgnoreCase))
                        nhsNumber = parts[0].Replace(" ", string.Empty);
                }

                return (controlId, new ArrivalIntent(type, visitId, given, family, dob, nhsNumber));
            }

            case "A03":
                return (controlId, new DischargeIntent(type, visitId));

            case "A11":
                return (controlId, new CancellationIntent(type, visitId));

            default:
                throw new UnprocessableMessageException($"Unsupported message type {type}.");
        }
    }

    /// <summary>MSH-10, read leniently so even a message that cannot be processed is acknowledged against its own id.</summary>
    public static string? TryReadControlId(string message)
    {
        var msh = message.Replace("\r\n", "\r").Replace('\n', '\r').Split('\r').FirstOrDefault(s => s.StartsWith("MSH", StringComparison.Ordinal));
        if (msh is null || msh.Length < 4)
            return null;

        var fields = msh.Split(msh[3]);
        return fields.Length > 9 && !string.IsNullOrWhiteSpace(fields[9]) ? fields[9] : null;
    }

    /// <summary>An HL7 acknowledgement: AA accepted, AE held for review.</summary>
    public static string Acknowledge(string controlId, bool accepted, string? error) =>
        $"MSH|^~\\&|BETSI|BETSI|||{DateTime.UtcNow:yyyyMMddHHmmss}||ACK|{Guid.NewGuid():N}|P|2.5\r" +
        $"MSA|{(accepted ? "AA" : "AE")}|{controlId}" + (error is null ? string.Empty : $"|{error.Replace('|', ' ')}") + "\r";

    private static string Segment(string[] segments, string name) =>
        segments.FirstOrDefault(s => s.StartsWith(name, StringComparison.Ordinal))
        ?? throw new UnprocessableMessageException($"No {name} segment.");

    private static string Field(string[] fields, int index) => index < fields.Length ? fields[index] : string.Empty;
}

/// <summary>
/// Minimal FHIR R4 reader (MVP-066): an Encounter with its Patient, as a Bundle or with the
/// Patient contained. Encounter status arrived/triaged/in-progress is an arrival, finished a
/// discharge, cancelled or entered-in-error a cancellation.
/// </summary>
public static class FhirR4Reader
{
    public const string NhsNumberSystem = "https://fhir.nhs.uk/Id/nhs-number";

    public static InboundIntent Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new UnprocessableMessageException($"Not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            var resources = ResourceType(root) == "Bundle" && root.TryGetProperty("entry", out var entries)
                ? entries.EnumerateArray().Select(e => e.TryGetProperty("resource", out var r) ? r : default).Where(r => r.ValueKind == JsonValueKind.Object).ToList()
                : [root];

            var encounter = resources.FirstOrDefault(r => ResourceType(r) == "Encounter");
            if (encounter.ValueKind != JsonValueKind.Object)
                throw new UnprocessableMessageException("No Encounter resource.");

            var visitId = encounter.TryGetProperty("identifier", out var ids) && ids.ValueKind == JsonValueKind.Array
                ? ids.EnumerateArray().Select(i => i.TryGetProperty("value", out var v) ? v.GetString() : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                : null;

            if (string.IsNullOrWhiteSpace(visitId))
                throw new UnprocessableMessageException("Encounter.identifier is required to link arrival and discharge.");

            var status = encounter.TryGetProperty("status", out var s) ? s.GetString() : null;
            var type = $"Encounter.{status}";

            switch (status)
            {
                case "arrived" or "triaged" or "in-progress":
                {
                    var patient = resources.FirstOrDefault(r => ResourceType(r) == "Patient");
                    if (patient.ValueKind != JsonValueKind.Object && encounter.TryGetProperty("contained", out var contained))
                        patient = contained.EnumerateArray().FirstOrDefault(r => ResourceType(r) == "Patient");

                    if (patient.ValueKind != JsonValueKind.Object)
                        throw new UnprocessableMessageException("An arriving Encounter needs its Patient in the Bundle or contained.");

                    var name = patient.TryGetProperty("name", out var names) && names.GetArrayLength() > 0 ? names[0] : default;
                    var family = name.ValueKind == JsonValueKind.Object && name.TryGetProperty("family", out var f) ? f.GetString() : null;
                    var given = name.ValueKind == JsonValueKind.Object && name.TryGetProperty("given", out var g) && g.GetArrayLength() > 0 ? g[0].GetString() : null;

                    if (string.IsNullOrWhiteSpace(family) || string.IsNullOrWhiteSpace(given))
                        throw new UnprocessableMessageException("Patient.name must have family and given.");

                    if (!patient.TryGetProperty("birthDate", out var birth) ||
                        !DateTime.TryParseExact(birth.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
                    {
                        throw new UnprocessableMessageException("Patient.birthDate must be a full date (YYYY-MM-DD).");
                    }

                    var nhsNumber = patient.TryGetProperty("identifier", out var identifiers)
                        ? identifiers.EnumerateArray()
                            .Where(i => i.TryGetProperty("system", out var sys) && sys.GetString() == NhsNumberSystem)
                            .Select(i => i.TryGetProperty("value", out var v) ? v.GetString() : null)
                            .FirstOrDefault()
                        : null;

                    return new ArrivalIntent(type, visitId, given, family, dob, nhsNumber);
                }

                case "finished":
                    return new DischargeIntent(type, visitId);

                case "cancelled" or "entered-in-error":
                    return new CancellationIntent(type, visitId);

                default:
                    throw new UnprocessableMessageException($"Unsupported Encounter status '{status}'.");
            }
        }
    }

    private static string? ResourceType(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("resourceType", out var t) ? t.GetString() : null;
}

public sealed record InboundOutcome(string MessageId, string Status, string? MessageType, Guid? EpisodeId, string? Error, bool Duplicate, string? ControlId);

/// <summary>
/// Turns a verified inbound message into commands, idempotently, quarantining what cannot be
/// processed (MVP-063, MVP-066; spec §6 integration contract).
/// </summary>
/// <remarks>
/// Commands run in the request's scope, where the actor is the integration source in the
/// reserved Integration role. They go through the normal pipeline, so an integration is
/// audited, permission-checked (it may only register, discharge and cancel) and validated
/// exactly like a person.
/// </remarks>
public sealed class InboundMessageProcessor
{
    public const string Accepted = "Accepted";
    public const string Quarantined = "Quarantined";
    public const string Processing = "Processing";

    private readonly BetsiDbContext _context;
    private readonly IMediator _mediator;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<InboundMessageProcessor> _logger;

    public InboundMessageProcessor(
        BetsiDbContext context, IMediator mediator, ITenantContext tenantContext, IServiceScopeFactory scopes,
        TimeProvider time, ILogger<InboundMessageProcessor> logger)
    {
        _context = context;
        _mediator = mediator;
        _tenantContext = tenantContext;
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    public async Task<InboundOutcome> ProcessAsync(Guid sourceId, string format, string messageId, string body, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

        // Reserve the message id before acting, so two copies arriving together cannot both
        // register the patient: the unique index lets exactly one through.
        var reservation = new InboundMessage
        {
            SourceId = sourceId,
            MessageId = messageId,
            RequestHash = hash,
            Status = Processing,
            ReceivedAt = _time.GetUtcNow().UtcDateTime
        };

        if (!await TryReserveAsync(reservation, cancellationToken))
        {
            var previous = await _context.InboundMessages.AsNoTracking()
                .SingleAsync(m => m.SourceId == sourceId && m.MessageId == messageId, cancellationToken);

            if (previous.RequestHash != hash)
                throw new Betsi.Domain.ConflictException($"Message id '{messageId}' was already used for a different message.");

            if (previous.Status == Processing)
                throw new Betsi.Domain.ConflictException($"Message '{messageId}' is still being processed. Retry shortly.");

            return new InboundOutcome(messageId, previous.Status, previous.MessageType, previous.EpisodeId, previous.Error, true, null);
        }

        string? controlId = null;
        InboundIntent? intent = null;
        Guid? episodeId = null;
        string? error = null;

        try
        {
            if (format == InboundFormats.Hl7v2)
            {
                controlId = Hl7v2Reader.TryReadControlId(body);
                (controlId, intent) = Hl7v2Reader.Read(body);
            }
            else
                intent = FhirR4Reader.Read(body);

            episodeId = await ApplyAsync(sourceId, intent, cancellationToken);
        }
        catch (Exception exception) when (exception is UnprocessableMessageException or FluentValidation.ValidationException
                                              or Betsi.Domain.DomainRuleViolationException or Betsi.Domain.AggregateNotFoundException
                                              or Betsi.Domain.AggregateConcurrencyException or DbUpdateException)
        {
            error = exception switch
            {
                FluentValidation.ValidationException v => string.Join("; ", v.Errors.Select(e => e.ErrorMessage)),
                DbUpdateException => "The message conflicts with an existing record (for example a duplicate NHS number).",
                _ => exception.Message
            };

            _logger.LogWarning("Inbound message {MessageId} from source {SourceId} quarantined: {Error}", messageId, sourceId, error);
        }
        catch
        {
            // Anything unexpected (the database unreachable, a bug): release the reservation so the
            // sender's retry is processed rather than reported as a duplicate.
            await WithScopeAsync(db => db.InboundMessages.Where(m => m.SourceId == sourceId && m.MessageId == messageId && m.Status == Processing)
                .ExecuteDeleteAsync(CancellationToken.None));
            throw;
        }

        var status = error is null ? Accepted : Quarantined;
        var truncated = error is { Length: > 1000 } ? error[..1000] : error;

        await WithScopeAsync(db => db.InboundMessages
            .Where(m => m.SourceId == sourceId && m.MessageId == messageId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.Status, status)
                .SetProperty(m => m.MessageType, intent == null ? null : intent.MessageType)
                .SetProperty(m => m.Error, truncated)
                .SetProperty(m => m.EpisodeId, episodeId)
                // Accepted messages have done their work; only a quarantined one needs a person to read it.
                .SetProperty(m => m.Body, error == null ? null : body), CancellationToken.None));

        return new InboundOutcome(messageId, status, intent?.MessageType, episodeId, truncated, false, controlId);
    }

    private async Task<Guid?> ApplyAsync(Guid sourceId, InboundIntent intent, CancellationToken cancellationToken)
    {
        var link = await _context.ExternalEpisodeLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.SourceId == sourceId && l.ExternalVisitId == intent.VisitId, cancellationToken);

        switch (intent)
        {
            case ArrivalIntent arrival:
            {
                // The same visit announced again (a resend under a new message id, or A01 after A04)
                // is the same patient episode, not a new one.
                if (link is not null)
                    return link.EpisodeId;

                var result = await _mediator.Send(new RegisterPatientCommand
                {
                    FirstName = arrival.FirstName,
                    LastName = arrival.LastName,
                    DateOfBirth = arrival.DateOfBirth,
                    NhsNumber = string.IsNullOrWhiteSpace(arrival.NhsNumber) ? null : arrival.NhsNumber
                }, cancellationToken);

                await RecordAsync(new ExternalEpisodeLink
                {
                    SourceId = sourceId,
                    ExternalVisitId = intent.VisitId,
                    EpisodeId = result.AggregateId,
                    CreatedAt = _time.GetUtcNow().UtcDateTime
                }, cancellationToken);

                return result.AggregateId;
            }

            case DischargeIntent or CancellationIntent:
            {
                if (link is null)
                    throw new UnprocessableMessageException($"No arrival has been received for visit '{intent.VisitId}'.");

                var episode = await _context.PatientEpisodes.AsNoTracking()
                    .SingleOrDefaultAsync(e => e.Id == link.EpisodeId, cancellationToken)
                    ?? throw new UnprocessableMessageException($"The episode for visit '{intent.VisitId}' no longer exists.");

                // A repeated discharge for an already-ended episode is accepted as done.
                if (episode.State is Betsi.Domain.Aggregates.PatientEpisode.PatientState.Discharged
                    or Betsi.Domain.Aggregates.PatientEpisode.PatientState.Cancelled)
                {
                    return episode.Id;
                }

                ICommand command = intent is DischargeIntent
                    ? new DischargePatientCommand { PatientEpisodeId = episode.Id, ExpectedVersion = episode.Version, DischargeNotes = $"Discharged in source system ({intent.MessageType})." }
                    : new CancelPatientEpisodeCommand { PatientEpisodeId = episode.Id, ExpectedVersion = episode.Version, Reason = $"Cancelled in source system ({intent.MessageType})." };

                await _mediator.Send(command, cancellationToken);
                return episode.Id;
            }

            default:
                throw new UnprocessableMessageException("Unsupported message.");
        }
    }

    private async Task<bool> TryReserveAsync(InboundMessage reservation, CancellationToken cancellationToken)
    {
        try
        {
            await RecordAsync(reservation, cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <remarks>Written in its own scope: a command that failed on save leaves the request's context dirty.</remarks>
    private Task RecordAsync(object entity, CancellationToken cancellationToken) =>
        WithScopeAsync(async db =>
        {
            db.Add(entity);
            return await db.SaveChangesAsync(cancellationToken);
        });

    private async Task WithScopeAsync(Func<BetsiDbContext, Task<int>> work)
    {
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>()
            .Resolve(_tenantContext.TenantId, _tenantContext.ActorId, _tenantContext.ActorRole);

        await work(scope.ServiceProvider.GetRequiredService<BetsiDbContext>());
    }
}
