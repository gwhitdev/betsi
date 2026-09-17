namespace Betsi.API.Controllers;

using Betsi.Integrations;
using Betsi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

public sealed record RegisterWebhookRequest(string Url, List<string> EventTypes, string? Description);

public sealed record RegisterInboundSourceRequest(string Name, string Format);

/// <summary>Outbound webhook subscriptions (MVP-064).</summary>
[ApiController]
[Route("api/v1/webhooks")]
[Produces("application/json")]
[Authorize(Policy = Permissions.WebhooksManage)]
public sealed class WebhooksController(IntegrationAdministration administration) : ControllerBase
{
    /// <summary>Subscribes a URL to event types. The signing secret is in the response and is never shown again.</summary>
    [HttpPost("register")]
    [ProducesResponseType<WebhookSecretIssued>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterWebhookRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created,
            await administration.RegisterWebhookAsync(request.Url, request.EventTypes ?? [], request.Description, cancellationToken));

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<WebhookSubscriptionView>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<WebhookSubscriptionView>> List(CancellationToken cancellationToken) =>
        administration.ListWebhooksAsync(cancellationToken);

    /// <summary>The event types that can be subscribed to.</summary>
    [HttpGet("event-types")]
    public IReadOnlyCollection<string> EventTypes() => WebhookEventCatalog.ExternalTypes;

    [HttpPost("{subscriptionId:guid}/rotate-secret")]
    [ProducesResponseType<WebhookSecretIssued>(StatusCodes.Status200OK)]
    public Task<WebhookSecretIssued> RotateSecret(Guid subscriptionId, CancellationToken cancellationToken) =>
        administration.RotateWebhookSecretAsync(subscriptionId, cancellationToken);

    [HttpPost("{subscriptionId:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid subscriptionId, CancellationToken cancellationToken)
    {
        await administration.DeactivateWebhookAsync(subscriptionId, cancellationToken);
        return NoContent();
    }

    /// <param name="subscriptionId">The subscription.</param>
    /// <param name="status"><c>Pending</c>, <c>Delivered</c> or <c>DeadLettered</c>.</param>
    [HttpGet("{subscriptionId:guid}/deliveries")]
    [ProducesResponseType<IReadOnlyList<WebhookDeliveryView>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<WebhookDeliveryView>> Deliveries(Guid subscriptionId, [FromQuery] string? status, CancellationToken cancellationToken) =>
        administration.ListDeliveriesAsync(subscriptionId, status, cancellationToken);

    [HttpPost("deliveries/{deliveryId:guid}/retry")]
    public async Task<IActionResult> Retry(Guid deliveryId, CancellationToken cancellationToken)
    {
        await administration.RetryDeliveryAsync(deliveryId, cancellationToken);
        return NoContent();
    }
}

/// <summary>Inbound integration sources and the signed inbound endpoint (MVP-063, MVP-066).</summary>
[ApiController]
[Route("api/v1/integrations")]
[Produces("application/json")]
public sealed class IntegrationsController(IntegrationAdministration administration, InboundMessageProcessor processor) : ControllerBase
{
    [HttpPost("sources")]
    [Authorize(Policy = Permissions.IntegrationsManage)]
    [ProducesResponseType<InboundSourceSecretIssued>(StatusCodes.Status201Created)]
    public async Task<IActionResult> RegisterSource([FromBody] RegisterInboundSourceRequest request, CancellationToken cancellationToken) =>
        StatusCode(StatusCodes.Status201Created, await administration.RegisterSourceAsync(request.Name, request.Format, cancellationToken));

    [HttpGet("sources")]
    [Authorize(Policy = Permissions.IntegrationsManage)]
    public Task<IReadOnlyList<InboundSourceView>> ListSources(CancellationToken cancellationToken) =>
        administration.ListSourcesAsync(cancellationToken);

    [HttpPost("sources/{sourceId:guid}/rotate-secret")]
    [Authorize(Policy = Permissions.IntegrationsManage)]
    public Task<InboundSourceSecretIssued> RotateSourceSecret(Guid sourceId, CancellationToken cancellationToken) =>
        administration.RotateSourceSecretAsync(sourceId, cancellationToken);

    [HttpPost("sources/{sourceId:guid}/deactivate")]
    [Authorize(Policy = Permissions.IntegrationsManage)]
    public async Task<IActionResult> DeactivateSource(Guid sourceId, CancellationToken cancellationToken)
    {
        await administration.DeactivateSourceAsync(sourceId, cancellationToken);
        return NoContent();
    }

    /// <summary>Recent messages from a source, without bodies. Filter by <c>Quarantined</c> to find messages needing review.</summary>
    [HttpGet("sources/{sourceId:guid}/messages")]
    [Authorize(Policy = Permissions.IntegrationsManage)]
    public Task<IReadOnlyList<InboundMessageSummary>> ListMessages(Guid sourceId, [FromQuery] string? status, CancellationToken cancellationToken) =>
        administration.ListMessagesAsync(sourceId, status, cancellationToken);

    /// <summary>One message, including the body of a quarantined message. Contains patient data; the read is audited.</summary>
    [HttpGet("messages/{messageId:long}")]
    [Authorize(Policy = Permissions.EpisodesRead)]
    [AuditRead("InboundMessage")]
    public async Task<IActionResult> GetMessage(long messageId, CancellationToken cancellationToken) =>
        await administration.GetMessageAsync(messageId, cancellationToken) is { } message ? Ok(message) : NotFound();

    /// <summary>
    /// Receives one signed HL7 v2 or FHIR R4 message. Authenticated by the <c>Betsi-Signature</c>
    /// header, not a bearer token. Requires <c>Betsi-Message-Id</c>; resending the same id is safe.
    /// </summary>
    /// <remarks>
    /// A message that cannot be processed is quarantined and still acknowledged as received (HL7
    /// <c>AE</c>, or <c>status: Quarantined</c>), so the sender does not retry a message that will
    /// never succeed. Quarantined messages are listed for review.
    /// </remarks>
    [HttpPost("inbound/{tenantId:guid}/{sourceId:guid}")]
    [Authorize(AuthenticationSchemes = BetsiAuthenticationSchemes.InboundSignature, Policy = Permissions.IntegrationIngest)]
    [Consumes("application/json", "application/fhir+json", "text/plain", "application/hl7-v2", "x-application/hl7-v2+er7")]
    [ProducesResponseType<InboundOutcome>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Receive(Guid tenantId, Guid sourceId, CancellationToken cancellationToken)
    {
        var source = (await administration.ListSourcesAsync(cancellationToken)).Single(s => s.Id == sourceId);

        Request.Body.Position = 0;
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(cancellationToken);

        var messageId = Request.Headers[InboundSignatureAuthenticationHandler.MessageIdHeader].ToString();
        var outcome = await processor.ProcessAsync(sourceId, source.Format, messageId, body, cancellationToken);

        if (source.Format == InboundFormats.Hl7v2)
        {
            var controlId = outcome.ControlId ?? Hl7v2Reader.TryReadControlId(body) ?? messageId;
            return Content(Hl7v2Reader.Acknowledge(controlId, outcome.Status == InboundMessageProcessor.Accepted, outcome.Error),
                "application/hl7-v2; charset=utf-8");
        }

        return StatusCode(StatusCodes.Status202Accepted, outcome);
    }
}
