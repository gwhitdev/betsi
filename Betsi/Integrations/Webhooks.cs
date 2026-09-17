namespace Betsi.Integrations;

using Betsi.ControlPlane;
using Betsi.Infrastructure.Outbox;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Which domain events are published to webhook subscribers, under what name, and with which
/// fields (MVP-064).
/// </summary>
/// <remarks>
/// An allowlist, not a blocklist. Webhook payloads carry opaque identifiers, roles, times and
/// states — never names, dates of birth, NHS numbers or free-text notes (spec §6: events contain
/// only the minimum necessary data). A subscriber that needs demographics calls the episode API
/// with its own authorised credentials, where the read is audited.
/// </remarks>
public static class WebhookEventCatalog
{
    public const int SchemaVersion = 1;

    private sealed record Mapping(string ExternalType, string[] Fields);

    private static readonly FrozenDictionary<string, Mapping> ByDomainEvent = new Dictionary<string, Mapping>
    {
        ["PatientEpisodeCreated"] = new("patient.arrived", []),
        ["PatientTriageStarted"] = new("patient.triage_started", []),
        ["PatientTriageCompleted"] = new("patient.triage_completed", []),
        ["PatientTreatmentStarted"] = new("patient.moved", ["LocationId"]),
        ["PatientDischarged"] = new("patient.discharged", []),
        ["PatientEpisodeCancelled"] = new("patient.cancelled", []),
        ["EscalationCreated"] = new("escalation.raised",
            ["PatientEpisodeId", "LocationId", "ResponsibleRole", "Trigger", "PolicyRevision", "TierLevel", "ThresholdMinutes", "WaitedMinutes", "AcknowledgementDueAt"]),
        ["EscalationAcknowledged"] = new("escalation.acknowledged", []),
        ["EscalationReassigned"] = new("escalation.reassigned", ["PreviousResponsibleRole", "NewResponsibleRole", "AcknowledgementDueAt"]),
        ["EscalationEscalatedFurther"] = new("escalation.escalated", ["NewResponsibleRole"]),
        ["EscalationResolved"] = new("escalation.resolved", ["ResolvedAt"]),
        ["EscalationClosed"] = new("escalation.closed", []),
        ["EscalationMarkedForManualFollowUp"] = new("escalation.follow_up_required", ["FollowUpExceptionId", "MissedDeadline"]),
        ["FollowUpExceptionClosed"] = new("escalation.follow_up_closed", ["EscalationId", "Outcome"])
    }.ToFrozenDictionary();

    public static IReadOnlyCollection<string> ExternalTypes { get; } =
        ByDomainEvent.Values.Select(m => m.ExternalType).Distinct().Order().ToArray();

    public static bool TryGetExternalType(string domainEventType, out string externalType)
    {
        externalType = ByDomainEvent.TryGetValue(domainEventType, out var mapping) ? mapping.ExternalType : string.Empty;
        return externalType.Length > 0;
    }

    /// <summary>The body sent to subscribers for one outbox message.</summary>
    public static string BuildPayload(OutboxMessage message, string externalType)
    {
        var mapping = ByDomainEvent[message.EventType];
        using var source = JsonDocument.Parse(message.EventData);
        var root = source.RootElement;

        var data = new JsonObject();
        foreach (var field in mapping.Fields)
        {
            if (root.TryGetProperty(field, out var value))
                data[JsonNamingPolicy.CamelCase.ConvertName(field)] = JsonNode.Parse(value.GetRawText());
        }

        var envelope = new JsonObject
        {
            ["id"] = message.EventId,
            ["type"] = externalType,
            ["schemaVersion"] = SchemaVersion,
            ["occurredAt"] = root.TryGetProperty("OccurredAt", out var at) ? JsonNode.Parse(at.GetRawText()) : null,
            ["tenantId"] = message.TenantId,
            ["aggregateType"] = message.AggregateType,
            ["aggregateId"] = message.AggregateId,
            ["aggregateVersion"] = root.TryGetProperty("Version", out var version) ? version.GetInt32() : null,
            ["actorRole"] = root.TryGetProperty("ActorRole", out var role) ? role.GetString() : null,
            ["data"] = data
        };

        return envelope.ToJsonString();
    }
}

/// <summary>
/// HMAC-SHA256 signatures for webhook bodies, outbound and inbound.
/// </summary>
/// <remarks>
/// Header format: <c>Betsi-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, signing
/// <c>"{t}.{raw body}"</c>. The timestamp is inside the signature, so a captured request cannot
/// be replayed later with a fresh timestamp; receivers reject timestamps outside a tolerance.
/// </remarks>
public static class WebhookSignature
{
    public const string SignatureHeader = "Betsi-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Create(string secret, DateTimeOffset timestamp, string body)
    {
        var t = timestamp.ToUnixTimeSeconds();
        return $"t={t},v1={Compute(secret, t, body)}";
    }

    public static bool Verify(string secret, string? header, string body, DateTimeOffset now, out string failure)
    {
        failure = string.Empty;

        if (string.IsNullOrWhiteSpace(header))
        {
            failure = $"The {SignatureHeader} header is missing.";
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("t=", StringComparison.Ordinal) && long.TryParse(part[2..], CultureInfo.InvariantCulture, out var t))
                timestamp = t;
            else if (part.StartsWith("v1=", StringComparison.Ordinal))
                signatures.Add(part[3..]);
        }

        if (timestamp is null || signatures.Count == 0)
        {
            failure = $"The {SignatureHeader} header is malformed.";
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).Duration() > Tolerance)
        {
            failure = "The signature timestamp is outside the permitted window.";
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp.Value, body));

        // Several v1 values are accepted so a sender can sign with old and new secrets during rotation.
        if (!signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s))))
        {
            failure = "The signature does not match.";
            return false;
        }

        return true;
    }

    private static string Compute(string secret, long timestamp, string body) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
}

/// <summary>Generates signing secrets and stores them encrypted with ASP.NET Data Protection.</summary>
public sealed class IntegrationSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Betsi.Integrations.Secrets.v1");

    public static string Generate(string prefix) =>
        prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public string Protect(string secret) => _protector.Protect(secret);

    public string Unprotect(string protectedSecret) => _protector.Unprotect(protectedSecret);
}

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Allow subscriptions to private, loopback and link-local addresses. Development only: in
    /// production it would let anyone able to register a webhook make the server call internal
    /// services (SSRF).
    /// </summary>
    public bool AllowPrivateNetworkTargets { get; set; }

    /// <summary>Allow plain-HTTP subscription URLs. Development only.</summary>
    public bool AllowInsecureHttp { get; set; }

    public TimeSpan DeliveryInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxAttempts { get; set; } = 8;
    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Creates one delivery row per matching active subscription as each outbox message is
/// processed, in the same transaction as marking the message processed.
/// </summary>
public sealed class WebhookFanOutPublisher : IOutboxPublisher
{
    private readonly BetsiDbContext _context;
    private readonly TimeProvider _time;

    public WebhookFanOutPublisher(BetsiDbContext context, TimeProvider time)
    {
        _context = context;
        _time = time;
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (!WebhookEventCatalog.TryGetExternalType(message.EventType, out var externalType))
            return;

        var subscriptions = (await _context.WebhookSubscriptions.AsNoTracking()
                .Where(s => s.Active)
                .ToListAsync(cancellationToken))
            .Where(s => s.EventTypes.Contains(externalType))
            .ToList();

        if (subscriptions.Count == 0)
            return;

        // Idempotent if the same message is processed again after a crash.
        var existing = await _context.WebhookDeliveries.AsNoTracking()
            .Where(d => d.EventId == message.EventId)
            .Select(d => d.SubscriptionId)
            .ToListAsync(cancellationToken);

        var payload = WebhookEventCatalog.BuildPayload(message, externalType);
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var subscription in subscriptions.Where(s => !existing.Contains(s.Id)))
        {
            _context.WebhookDeliveries.Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                TenantId = message.TenantId,
                SubscriptionId = subscription.Id,
                EventId = message.EventId,
                EventType = externalType,
                Payload = payload,
                Status = WebhookDeliveryStatus.Pending,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }
}

public static class WebhookDeliveryStatus
{
    public const string Pending = "Pending";
    public const string Delivered = "Delivered";
    public const string DeadLettered = "DeadLettered";
}

/// <summary>The outbox publisher: records the event in the log and fans it out to webhook subscribers.</summary>
public sealed class CompositeOutboxPublisher(LoggingOutboxPublisher logging, WebhookFanOutPublisher webhooks) : IOutboxPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await logging.PublishAsync(message, cancellationToken);
        await webhooks.PublishAsync(message, cancellationToken);
    }
}

/// <summary>Sends due webhook deliveries for the current tenant, with retries and dead-lettering.</summary>
public sealed class WebhookDeliverer
{
    public const string HttpClientName = "betsi-webhooks";

    private readonly BetsiDbContext _context;
    private readonly IntegrationSecrets _secrets;
    private readonly IHttpClientFactory _httpClients;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;
    private readonly Betsi.Infrastructure.Observability.BetsiMetrics _metrics;
    private readonly ILogger<WebhookDeliverer> _logger;

    public WebhookDeliverer(
        BetsiDbContext context,
        IntegrationSecrets secrets,
        IHttpClientFactory httpClients,
        WebhookOptions options,
        TimeProvider time,
        Betsi.Infrastructure.Observability.BetsiMetrics metrics,
        ILogger<WebhookDeliverer> logger)
    {
        _context = context;
        _secrets = secrets;
        _httpClients = httpClients;
        _options = options;
        _time = time;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task<(int Delivered, int Failed, int DeadLettered)> DeliverDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var due = await _context.WebhookDeliveries
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (due.Count == 0)
            return (0, 0, 0);

        var subscriptionIds = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var subscriptions = await _context.WebhookSubscriptions.AsNoTracking()
            .Where(s => subscriptionIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken);

        int delivered = 0, failed = 0, deadLettered = 0;
        var client = _httpClients.CreateClient(HttpClientName);

        foreach (var delivery in due)
        {
            if (!subscriptions.TryGetValue(delivery.SubscriptionId, out var subscription) || !subscription.Active)
            {
                delivery.Status = WebhookDeliveryStatus.DeadLettered;
                delivery.LastError = "The subscription has been deactivated.";
                deadLettered++;
                _metrics.WebhookDelivered(delivery.TenantId, "dead-lettered");
            }
            else
            {
                var outcome = await SendAsync(client, subscription, delivery, cancellationToken);
                switch (outcome)
                {
                    case WebhookDeliveryStatus.Delivered: delivered++; break;
                    case WebhookDeliveryStatus.DeadLettered: deadLettered++; break;
                    default: failed++; break;
                }

                _metrics.WebhookDelivered(delivery.TenantId, outcome.ToString().ToLowerInvariant());
            }

            // Saved per delivery so a crash mid-batch does not resend the ones already sent.
            await _context.SaveChangesAsync(cancellationToken);
        }

        return (delivered, failed, deadLettered);
    }

    private async Task<string> SendAsync(HttpClient client, WebhookSubscription subscription, WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        delivery.Attempts++;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
            {
                Content = new StringContent(delivery.Payload, Encoding.UTF8, "application/json")
            };

            request.Headers.Add(WebhookSignature.SignatureHeader, WebhookSignature.Create(_secrets.Unprotect(subscription.ProtectedSecret), now, delivery.Payload));
            request.Headers.Add("Betsi-Delivery-Id", delivery.Id.ToString());
            request.Headers.Add("Betsi-Event-Id", delivery.EventId.ToString());
            request.Headers.Add("Betsi-Event-Type", delivery.EventType);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RequestTimeout);

            using var response = await client.SendAsync(request, timeout.Token);
            delivery.LastStatusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                delivery.Status = WebhookDeliveryStatus.Delivered;
                delivery.DeliveredAt = now.UtcDateTime;
                delivery.LastError = null;
                return delivery.Status;
            }

            return Retry(delivery, $"HTTP {(int)response.StatusCode}", now);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or BlockedWebhookTargetException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;

            return Retry(delivery, exception is TaskCanceledException ? "Timed out" : exception.Message, now);
        }
    }

    private string Retry(WebhookDelivery delivery, string error, DateTimeOffset now)
    {
        delivery.LastError = error.Length > 500 ? error[..500] : error;

        if (delivery.Attempts >= _options.MaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.DeadLettered;
            _logger.LogError(
                "Webhook delivery {DeliveryId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}",
                delivery.Id, delivery.EventType, delivery.Attempts, delivery.LastError);
            return delivery.Status;
        }

        delivery.NextAttemptAt = (now + BackoffAfter(delivery.Attempts)).UtcDateTime;
        _logger.LogWarning(
            "Webhook delivery {DeliveryId} ({EventType}) failed on attempt {Attempts}: {Error}; retrying at {NextAttemptAt}",
            delivery.Id, delivery.EventType, delivery.Attempts, delivery.LastError, delivery.NextAttemptAt);
        return WebhookDeliveryStatus.Pending;
    }

    /// <summary>30s, 1m, 2m, 4m… capped at an hour, with ±10% jitter so failures do not retry in lockstep.</summary>
    public static TimeSpan BackoffAfter(int attempts)
    {
        var seconds = Math.Min(30 * Math.Pow(2, attempts - 1), 3600);
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2 - 0.1);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}

/// <summary>Thrown when a webhook URL resolves to an address webhooks may not be sent to.</summary>
public sealed class BlockedWebhookTargetException(string message) : HttpRequestException(message);

/// <summary>Stops webhook delivery reaching private networks (SSRF), checked on the resolved address at connect time.</summary>
public static class NetworkTargets
{
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;

        var b = address.GetAddressBytes();
        return b[0] switch
        {
            0 or 10 or 127 => true,
            100 => b[1] is >= 64 and <= 127,          // carrier-grade NAT
            169 => b[1] == 254,                         // link-local, including cloud metadata endpoints
            172 => b[1] is >= 16 and <= 31,
            192 => b[1] == 168 || (b[1] == 0 && b[2] == 0),
            198 => b[1] is 18 or 19,
            >= 224 => true,                             // multicast and reserved
            _ => false
        };
    }

    /// <summary>
    /// A handler that resolves the host itself and refuses private addresses. Checking at connect
    /// time, not only at registration, defeats DNS names that later re-point at internal hosts.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var permitted = addresses.Where(a => allowPrivate || !IsPrivate(a)).ToArray();

            if (permitted.Length == 0)
                throw new BlockedWebhookTargetException($"'{context.DnsEndPoint.Host}' resolves only to addresses webhooks may not be sent to.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}

/// <summary>Delivers webhooks for every available tenant on a timer.</summary>
public sealed class WebhookDeliveryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ITenantRegistry _registry;
    private readonly WebhookOptions _options;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(IServiceScopeFactory scopes, ITenantRegistry registry, WebhookOptions options, ILogger<WebhookDeliveryService> logger)
    {
        _scopes = scopes;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.DeliveryInterval);

        do
        {
            foreach (var tenant in _registry.Available)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    scope.ServiceProvider.GetRequiredService<TenantContext>().ResolveSystem(tenant.TenantId);
                    var (delivered, failed, dead) = await scope.ServiceProvider.GetRequiredService<WebhookDeliverer>().DeliverDueAsync(stoppingToken);

                    if (delivered + failed + dead > 0)
                    {
                        _logger.LogInformation("Tenant {TenantId} webhooks: {Delivered} delivered, {Failed} retrying, {DeadLettered} dead-lettered",
                            tenant.TenantId, delivered, failed, dead);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Webhook delivery failed for tenant {TenantId}", tenant.TenantId);
                }
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try { return await timer.WaitForNextTickAsync(cancellationToken); }
        catch (OperationCanceledException) { return false; }
    }
}
