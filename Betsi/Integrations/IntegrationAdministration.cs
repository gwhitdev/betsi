namespace Betsi.Integrations;

using Betsi.ControlPlane;
using Betsi.Domain;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Licensing;
using Microsoft.EntityFrameworkCore;

public sealed record WebhookSubscriptionView(
    Guid Id, string Url, IReadOnlyList<string> EventTypes, string? Description, bool Active, DateTime CreatedAt, DateTime? SecretRotatedAt);

/// <summary>Returned once, at creation or rotation. The secret is not retrievable afterwards.</summary>
public sealed record WebhookSecretIssued(Guid Id, string Url, IReadOnlyList<string> EventTypes, string Secret);

public sealed record WebhookDeliveryView(
    Guid Id, Guid EventId, string EventType, string Status, int Attempts, DateTime CreatedAt, DateTime NextAttemptAt,
    DateTime? DeliveredAt, int? LastStatusCode, string? LastError);

public sealed record InboundSourceView(Guid Id, string Name, string Format, bool Active, DateTime CreatedAt, string InboundPath);

public sealed record InboundSourceSecretIssued(Guid Id, string Name, string Format, string InboundPath, string Secret);

public sealed record InboundMessageSummary(long Id, string MessageId, string Status, string? MessageType, string? Error, Guid? EpisodeId, DateTime ReceivedAt);

public sealed record InboundMessageDetail(long Id, Guid SourceId, string MessageId, string Status, string? MessageType, string? Error, string? Body, DateTime ReceivedAt);

public sealed class IntegrationRequestException(string message) : Exception(message);

/// <summary>
/// Managing webhook subscriptions and inbound sources (MVP-063, MVP-064).
/// </summary>
/// <remarks>
/// Not MediatR commands, because creating one must hand a generated secret back to the caller
/// exactly once, and a secret must never travel through the generic command envelope. Permission
/// is enforced on the endpoints; every change is audited here, and gated on the licence like other
/// site administration.
/// </remarks>
public sealed class IntegrationAdministration
{
    private readonly BetsiDbContext _context;
    private readonly ITenantContext _tenantContext;
    private readonly ITenantRegistry _registry;
    private readonly IntegrationSecrets _secrets;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _time;

    public IntegrationAdministration(
        BetsiDbContext context, ITenantContext tenantContext, ITenantRegistry registry,
        IntegrationSecrets secrets, WebhookOptions options, TimeProvider time)
    {
        _context = context;
        _tenantContext = tenantContext;
        _registry = registry;
        _secrets = secrets;
        _options = options;
        _time = time;
    }

    // ---------------- Webhooks ----------------

    public async Task<WebhookSecretIssued> RegisterWebhookAsync(string url, IReadOnlyList<string> eventTypes, string? description, CancellationToken cancellationToken)
    {
        RequireLicence();
        var uri = ValidateUrl(url);

        var events = eventTypes.Select(e => e.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (events.Count == 0)
            throw new IntegrationRequestException("Subscribe to at least one event type.");

        var unknown = events.Except(WebhookEventCatalog.ExternalTypes).ToList();
        if (unknown.Count > 0)
        {
            throw new IntegrationRequestException(
                $"Unknown event types: {string.Join(", ", unknown)}. Known: {string.Join(", ", WebhookEventCatalog.ExternalTypes)}.");
        }

        if (description is { Length: > 200 })
            throw new IntegrationRequestException("Description must be at most 200 characters.");

        var secret = IntegrationSecrets.Generate("whsec_");
        var subscription = new WebhookSubscription
        {
            Id = Guid.NewGuid(),
            Url = uri.ToString(),
            EventTypes = events,
            Description = description,
            ProtectedSecret = _secrets.Protect(secret),
            Active = true,
            CreatedByActorId = _tenantContext.ActorId,
            CreatedAt = Now
        };

        _context.WebhookSubscriptions.Add(subscription);
        Audit("RegisterWebhook", subscription.Id, "WebhookSubscription", $"{uri.Host}; {string.Join(",", events)}");
        await _context.SaveChangesAsync(cancellationToken);

        return new WebhookSecretIssued(subscription.Id, subscription.Url, events, secret);
    }

    public async Task<IReadOnlyList<WebhookSubscriptionView>> ListWebhooksAsync(CancellationToken cancellationToken) =>
        (await _context.WebhookSubscriptions.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(cancellationToken))
        .Select(s => new WebhookSubscriptionView(s.Id, s.Url, s.EventTypes, s.Description, s.Active, s.CreatedAt, s.SecretRotatedAt))
        .ToList();

    public async Task<WebhookSecretIssued> RotateWebhookSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireLicence();
        var subscription = await FindWebhookAsync(id, cancellationToken);

        var secret = IntegrationSecrets.Generate("whsec_");
        subscription.ProtectedSecret = _secrets.Protect(secret);
        subscription.SecretRotatedAt = Now;

        Audit("RotateWebhookSecret", id, "WebhookSubscription", null);
        await _context.SaveChangesAsync(cancellationToken);

        return new WebhookSecretIssued(id, subscription.Url, subscription.EventTypes, secret);
    }

    /// <remarks>Deactivation is not licence-gated: switching off a data feed must always be possible.</remarks>
    public async Task DeactivateWebhookAsync(Guid id, CancellationToken cancellationToken)
    {
        var subscription = await FindWebhookAsync(id, cancellationToken);
        if (!subscription.Active)
            return;

        subscription.Active = false;
        subscription.DeactivatedAt = Now;
        Audit("DeactivateWebhook", id, "WebhookSubscription", null);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WebhookDeliveryView>> ListDeliveriesAsync(Guid subscriptionId, string? status, CancellationToken cancellationToken)
    {
        await FindWebhookAsync(subscriptionId, cancellationToken);

        return await _context.WebhookDeliveries.AsNoTracking()
            .Where(d => d.SubscriptionId == subscriptionId && (status == null || d.Status == status))
            .OrderByDescending(d => d.CreatedAt)
            .Take(200)
            .Select(d => new WebhookDeliveryView(d.Id, d.EventId, d.EventType, d.Status, d.Attempts, d.CreatedAt, d.NextAttemptAt, d.DeliveredAt, d.LastStatusCode, d.LastError))
            .ToListAsync(cancellationToken);
    }

    public async Task RetryDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var delivery = await _context.WebhookDeliveries.SingleOrDefaultAsync(d => d.Id == deliveryId, cancellationToken)
            ?? throw new AggregateNotFoundException("WebhookDelivery", deliveryId);

        if (delivery.Status != WebhookDeliveryStatus.DeadLettered)
            throw new DomainRuleViolationException($"Only dead-lettered deliveries can be retried; this one is {delivery.Status}.");

        delivery.Status = WebhookDeliveryStatus.Pending;
        delivery.Attempts = 0;
        delivery.NextAttemptAt = Now;
        Audit("RetryWebhookDelivery", deliveryId, "WebhookDelivery", null);
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ---------------- Inbound sources ----------------

    public async Task<InboundSourceSecretIssued> RegisterSourceAsync(string name, string format, CancellationToken cancellationToken)
    {
        RequireLicence();

        name = name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
            throw new IntegrationRequestException("A source needs a name of at most 200 characters.");

        var canonicalFormat = InboundFormats.All.FirstOrDefault(f => string.Equals(f, format, StringComparison.OrdinalIgnoreCase))
            ?? throw new IntegrationRequestException($"format must be one of: {string.Join(", ", InboundFormats.All)}.");

        if (await _context.InboundSources.AnyAsync(s => s.Name == name, cancellationToken))
            throw new ConflictException($"An inbound source named '{name}' already exists.");

        var secret = IntegrationSecrets.Generate("insec_");
        var source = new InboundSource
        {
            Id = Guid.NewGuid(),
            Name = name,
            Format = canonicalFormat,
            ProtectedSecret = _secrets.Protect(secret),
            Active = true,
            CreatedByActorId = _tenantContext.ActorId,
            CreatedAt = Now
        };

        _context.InboundSources.Add(source);
        Audit("RegisterInboundSource", source.Id, "InboundSource", $"{name}; {canonicalFormat}");
        await _context.SaveChangesAsync(cancellationToken);

        return new InboundSourceSecretIssued(source.Id, name, canonicalFormat, InboundPath(source.Id), secret);
    }

    public async Task<IReadOnlyList<InboundSourceView>> ListSourcesAsync(CancellationToken cancellationToken) =>
        (await _context.InboundSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(cancellationToken))
        .Select(s => new InboundSourceView(s.Id, s.Name, s.Format, s.Active, s.CreatedAt, InboundPath(s.Id)))
        .ToList();

    public async Task<InboundSourceSecretIssued> RotateSourceSecretAsync(Guid id, CancellationToken cancellationToken)
    {
        RequireLicence();
        var source = await FindSourceAsync(id, cancellationToken);

        var secret = IntegrationSecrets.Generate("insec_");
        source.ProtectedSecret = _secrets.Protect(secret);
        Audit("RotateInboundSourceSecret", id, "InboundSource", null);
        await _context.SaveChangesAsync(cancellationToken);

        return new InboundSourceSecretIssued(id, source.Name, source.Format, InboundPath(id), secret);
    }

    public async Task DeactivateSourceAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await FindSourceAsync(id, cancellationToken);
        if (!source.Active)
            return;

        source.Active = false;
        Audit("DeactivateInboundSource", id, "InboundSource", null);
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Message metadata for monitoring a feed. No message bodies.</summary>
    public async Task<IReadOnlyList<InboundMessageSummary>> ListMessagesAsync(Guid sourceId, string? status, CancellationToken cancellationToken)
    {
        await FindSourceAsync(sourceId, cancellationToken);

        return await _context.InboundMessages.AsNoTracking()
            .Where(m => m.SourceId == sourceId && (status == null || m.Status == status))
            .OrderByDescending(m => m.ReceivedAt)
            .Take(200)
            .Select(m => new InboundMessageSummary(m.Id, m.MessageId, m.Status, m.MessageType, m.Error, m.EpisodeId, m.ReceivedAt))
            .ToListAsync(cancellationToken);
    }

    /// <summary>One message including a quarantined body. Contains patient data.</summary>
    public Task<InboundMessageDetail?> GetMessageAsync(long id, CancellationToken cancellationToken) =>
        _context.InboundMessages.AsNoTracking()
            .Where(m => m.Id == id)
            .Select(m => new InboundMessageDetail(m.Id, m.SourceId, m.MessageId, m.Status, m.MessageType, m.Error, m.Body, m.ReceivedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public string InboundPath(Guid sourceId) =>
        $"{Betsi.Security.InboundSignatureAuthenticationHandler.PathPrefix}/{_tenantContext.TenantId}/{sourceId}";

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new IntegrationRequestException("url must be an absolute URL without credentials or a fragment.");

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && _options.AllowInsecureHttp))
            throw new IntegrationRequestException("url must use https.");

        // A literal private address is refused now. Hostnames are checked again at every
        // connection, since what a name resolves to can change after registration.
        if (!_options.AllowPrivateNetworkTargets &&
            (uri.IsLoopback || (System.Net.IPAddress.TryParse(uri.DnsSafeHost, out var address) && NetworkTargets.IsPrivate(address))))
        {
            throw new IntegrationRequestException("url must not point at a private, loopback or link-local address.");
        }

        return uri;
    }

    private void RequireLicence()
    {
        if (_registry.TryGet(_tenantContext.TenantId, out var tenant) && !tenant.License.Grants(LicenseFeatures.Core))
            throw new LicenseRestrictedException(LicenseFeatures.Core, tenant.License);
    }

    private async Task<WebhookSubscription> FindWebhookAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.WebhookSubscriptions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw new AggregateNotFoundException("WebhookSubscription", id);

    private async Task<InboundSource> FindSourceAsync(Guid id, CancellationToken cancellationToken) =>
        await _context.InboundSources.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw new AggregateNotFoundException("InboundSource", id);

    private void Audit(string action, Guid affectedId, string affectedType, string? context) =>
        _context.AuditLogs.Add(new AuditLogRecord
        {
            TenantId = _tenantContext.TenantId,
            Action = action,
            ActorId = _tenantContext.ActorId,
            ActorRole = _tenantContext.ActorRole,
            AffectedAggregateId = affectedId,
            AffectedAggregateType = affectedType,
            Outcome = "Success",
            Context = context,
            CreatedAt = Now
        });
}
