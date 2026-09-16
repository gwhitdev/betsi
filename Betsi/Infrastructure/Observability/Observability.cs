namespace Betsi.Infrastructure.Observability;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>
    /// OTLP endpoint of a collector, for example <c>http://otel-collector:4317</c>. Without one
    /// nothing is exported: traces and metrics are still recorded in-process and are available
    /// to anything that reads the .NET diagnostic sources, but no data leaves the service.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    /// <summary>Name this service reports itself under. Set per environment, not per instance.</summary>
    public string ServiceName { get; set; } = "betsi";

    /// <summary>Deployment environment tag on every span and metric ("staging", "production").</summary>
    public string? DeploymentEnvironment { get; set; }

    /// <summary>
    /// Fraction of traces sampled, 0–1. Health checks are never sampled regardless; they are
    /// the bulk of the traffic on a quiet night and none of the interest.
    /// </summary>
    public double TraceSampleRatio { get; set; } = 1.0;
}

/// <summary>
/// The metrics this service publishes (MVP-109). Dimensioned by tenant, because a problem in
/// one site's database is invisible in an aggregate across sites.
/// </summary>
/// <remarks>
/// No metric carries a patient identifier. Tenant id is an opaque GUID and appears in the
/// control-plane audit trail already; a patient id in a metric would be clinical data leaving
/// the database, in a store with no audit and a long retention.
/// </remarks>
public sealed class BetsiMetrics : IDisposable
{
    public const string MeterName = "Betsi";

    private readonly Meter _meter;
    private readonly Counter<long> _commands;
    private readonly Histogram<double> _commandDuration;
    private readonly Counter<long> _escalations;
    private readonly Counter<long> _outbox;
    private readonly Counter<long> _webhooks;
    private readonly Histogram<double> _outboxLag;

    public BetsiMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName, Version);

        _commands = _meter.CreateCounter<long>(
            "betsi.commands", "{command}", "Commands handled, by name and outcome.");
        _commandDuration = _meter.CreateHistogram<double>(
            "betsi.command.duration", "ms", "How long a command took, from pipeline entry to response.");
        _escalations = _meter.CreateCounter<long>(
            "betsi.escalations.raised", "{escalation}", "Escalations raised, by trigger.");
        _outbox = _meter.CreateCounter<long>(
            "betsi.outbox.messages", "{message}", "Outbox messages processed, by outcome.");
        _webhooks = _meter.CreateCounter<long>(
            "betsi.webhook.deliveries", "{delivery}", "Webhook delivery attempts, by outcome.");
        _outboxLag = _meter.CreateHistogram<double>(
            "betsi.outbox.lag", "s", "How long a message waited in the outbox before it was published.");
    }

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public void CommandHandled(string command, Guid tenantId, bool succeeded, double milliseconds)
    {
        var tags = new TagList
        {
            { "command", command },
            { "tenant", tenantId },
            { "outcome", succeeded ? "success" : "failure" }
        };

        _commands.Add(1, tags);
        _commandDuration.Record(milliseconds, tags);
    }

    public void EscalationRaised(Guid tenantId, string trigger) =>
        _escalations.Add(1, new TagList { { "tenant", tenantId }, { "trigger", trigger } });

    public void OutboxProcessed(Guid tenantId, string outcome) =>
        _outbox.Add(1, new TagList { { "tenant", tenantId }, { "outcome", outcome } });

    /// <summary>
    /// Event lag: how long a message sat in the outbox. This is the measure that says whether
    /// a webhook subscriber is seeing escalations in time, which is the point of the outbox.
    /// </summary>
    public void OutboxLag(Guid tenantId, TimeSpan waited) =>
        _outboxLag.Record(waited.TotalSeconds, new TagList { { "tenant", tenantId } });

    public void WebhookDelivered(Guid tenantId, string outcome) =>
        _webhooks.Add(1, new TagList { { "tenant", tenantId }, { "outcome", outcome } });

    public void Dispose() => _meter.Dispose();
}

/// <summary>
/// Gives every request a correlation id, echoes it to the caller and puts it on the log scope.
/// </summary>
/// <remarks>
/// A caller-supplied id is honoured so a trace can be followed from the EPR or the integration
/// engine that started it — but it is length-capped and stripped of control characters before
/// it reaches a log, because it is attacker-controlled text that ends up in an operator's
/// console and in the log store's query language.
/// </remarks>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// Matches the <c>IdempotencyRecord.CorrelationId</c> column. A command envelope persists
    /// its correlation id, so an id this middleware accepts must be one that can be stored —
    /// otherwise a caller with a long id gets a 500 from the database rather than a command.
    /// </summary>
    internal const int MaxLength = 100;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Sanitise(context.Request.Headers[HeaderName].FirstOrDefault())
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("betsi.correlation_id", correlationId);

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }

    internal static string? Sanitise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
            trimmed = trimmed[..MaxLength];

        return trimmed.Any(character => char.IsControl(character) || character > '~') ? null : trimmed;
    }
}

public static class ObservabilityModule
{
    /// <summary>Traces, metrics and the correlation id (MVP-109).</summary>
    public static IServiceCollection AddBetsiObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = new ObservabilityOptions();
        configuration.GetSection(ObservabilityOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        var resource = ResourceBuilder.CreateDefault()
            .AddService(options.ServiceName, serviceVersion: BetsiMetrics.Version)
            .AddAttributes(options.DeploymentEnvironment is { Length: > 0 } environment
                ? [new KeyValuePair<string, object>("deployment.environment", environment)]
                : []);

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resource)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)))
                    .AddAspNetCoreInstrumentation(instrumentation =>
                    {
                        // Health and metrics probes run every few seconds for ever and say
                        // nothing about a request a clinician made.
                        instrumentation.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation()
                    .AddSource(MediatRTracing.ActivitySourceName);

                if (options.OtlpEndpoint is { Length: > 0 } endpoint)
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(endpoint));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resource)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(BetsiMetrics.MeterName);

                if (options.OtlpEndpoint is { Length: > 0 } endpoint)
                    metrics.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(endpoint));
            });

        return services;
    }

    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app) =>
        app.UseMiddleware<CorrelationIdMiddleware>();
}

/// <summary>The activity source commands are traced on.</summary>
public static class MediatRTracing
{
    public const string ActivitySourceName = "Betsi.Commands";

    public static readonly ActivitySource Source = new(ActivitySourceName, BetsiMetrics.Version);
}
