using Betsi.API.Problems;
using Betsi.Application.Behaviours;
using Betsi.Application.Escalations;
using Betsi.Application.Validation;
using Betsi.Infrastructure;
using Betsi.Infrastructure.Configuration;
using Betsi.Infrastructure.DataProtection;
using Betsi.Infrastructure.Observability;
using Betsi.ControlPlane;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using Betsi.UI;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

// Answered before the host is built: a health probe must not pay for dependency injection,
// configuration binding or a database connection to ask one question over loopback.
if (HealthProbe.IsProbe(args))
{
    Environment.ExitCode = await HealthProbe.RunAsync(
        new ConfigurationBuilder().AddEnvironmentVariables().Build());
    return;
}

var isOperatorCommand = OperatorCli.IsOperatorCommand(args);

try
{
    if (!isOperatorCommand)
        Log.Information("Starting Betsi Patient Flow");

    var builder = WebApplication.CreateBuilder(args);

    // Before anything binds options, so no component ever sees an unresolved reference and
    // the process refuses to start if a secret the deployment named is not there.
    builder.Configuration.ResolveSecretReferences();

    // Operator commands share the service's configuration and services, but their output is
    // for a person at a terminal, so framework logging is kept to warnings and above.
    if (isOperatorCommand)
        builder.Configuration["Serilog:MinimumLevel:Default"] = "Warning";

    // Rendered text for a person at a terminal in Development; newline-delimited JSON
    // everywhere else, because a log aggregator has to parse it (MVP-109). Serilog carries the
    // current activity's trace and span ids, which the compact formatter writes as @tr and @sp,
    // so a log line can be joined to the span that produced it.
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service.name", context.Configuration["Observability:ServiceName"] ?? "betsi");

        if (context.HostingEnvironment.IsDevelopment() || isOperatorCommand)
            configuration.WriteTo.Console();
        else
            configuration.WriteTo.Console(new CompactJsonFormatter());
    });

    // Enums accepted and returned by name ("IncidentReported"), not by number, so a request body
    // is readable in an audit and does not silently change meaning if an enum is reordered.
    builder.Services.AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Betsi Patient Flow API",
            Version = "v1",
            Description = "Patient flow and escalation for emergency departments. Errors are RFC 9457 problem details " +
                          "with a stable 'code'. See docs/API.md and docs/API-VERSIONING.md."
        });

        var xml = Path.Combine(AppContext.BaseDirectory, "Betsi.Core.xml");
        if (File.Exists(xml))
            options.IncludeXmlComments(xml);

        // Without these, every "Try it out" call is rejected by the tenant middleware. They are
        // described as API keys so Swagger UI's Authorize dialog sends them on every request.
        AddHeaderScheme(TenantResolutionMiddleware.TenantHeaderName,
            "Tenant ID. Development tenants: 11111111-1111-1111-1111-111111111111 or 22222222-2222-2222-2222-222222222222.");
        AddHeaderScheme(TenantResolutionMiddleware.ActorRoleHeaderName, "Actor role, e.g. Nurse.");
        AddHeaderScheme(TenantResolutionMiddleware.ActorHeaderName, "Optional actor ID (GUID).");

        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "An access token from the configured OIDC provider. In Development the X-Betsi headers may be used instead."
        });

        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            [new OpenApiSecuritySchemeReference(TenantResolutionMiddleware.TenantHeaderName, document)] = [],
            [new OpenApiSecuritySchemeReference(TenantResolutionMiddleware.ActorRoleHeaderName, document)] = [],
            [new OpenApiSecuritySchemeReference(TenantResolutionMiddleware.ActorHeaderName, document)] = []
        });

        void AddHeaderScheme(string header, string description) =>
            options.AddSecurityDefinition(header, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = header,
                Description = description
            });
    });

    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
    builder.Services.AddBetsiObservability(builder.Configuration);
    builder.Services.AddBetsiUi(builder.Configuration);
    builder.Services.AddEscalationEngine(builder.Configuration, builder.Environment);

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);

        // Order matters. Logging wraps everything so a rejected command is still traced;
        // auditing sits outside validation so failed validation is recorded too; validation
        // runs last, immediately before the handler.
        // Metrics outermost: the duration recorded is the one the caller experienced.
        cfg.AddOpenBehavior(typeof(MetricsBehaviour<,>));
        cfg.AddOpenBehavior(typeof(LoggingBehaviour<,>));
        cfg.AddOpenBehavior(typeof(AuditBehaviour<,>));
        // Authorisation inside auditing, so a refused command leaves an audit row.
        cfg.AddOpenBehavior(typeof(AuthorizationBehaviour<,>));
        // Licence before validation: a refused command is audited, but its body is not
        // inspected for a tenant that may not use it.
        cfg.AddOpenBehavior(typeof(LicenseBehaviour<,>));
        cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
        // Innermost: the boards are told only about commands that actually happened.
        cfg.AddOpenBehavior(typeof(BoardNotificationBehaviour<,>));
    });

    builder.Services.AddValidatorsFromAssemblyContaining<RegisterPatientCommandValidator>();

    builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
    builder.Services.AddProblemDetails();

    builder.Services.AddHealthChecks()
        .AddCheck<TenantDatabaseHealthCheck>("tenant-databases", tags: ["ready"]);

    var tenantResolution = new TenantResolutionOptions();
    builder.Configuration.GetSection("TenantResolution").Bind(tenantResolution);

    builder.Services.AddBetsiAuthentication(builder.Configuration, builder.Environment, tenantResolution);

    // Header-supplied tenants let anyone who can reach the API name any tenant. That is a
    // development convenience only, so refuse to start with it enabled outside development
    // rather than relying on configuration review to catch it.
    // Webhook SSRF and plain-HTTP allowances are for pointing webhooks at a local receiver while
    // developing. Anywhere else they would let a webhook registration reach internal services.
    if (!builder.Environment.IsDevelopment() &&
        (builder.Configuration.GetValue<bool>("Webhooks:AllowPrivateNetworkTargets") ||
         builder.Configuration.GetValue<bool>("Webhooks:AllowInsecureHttp")))
    {
        throw new InvalidOperationException(
            "Webhooks:AllowPrivateNetworkTargets and Webhooks:AllowInsecureHttp are Development-only settings.");
    }

    if (tenantResolution.AllowHeaderFallback && !builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "TenantResolution:AllowHeaderFallback is enabled outside Development. Header-based " +
            "tenant selection is unauthenticated and must never be used where real patient " +
            "data is held.");
    }

    var app = builder.Build();

    if (isOperatorCommand)
    {
        Environment.ExitCode = await OperatorCli.RunAsync(app.Services, args, Console.Out, CancellationToken.None);
        return;
    }

    // Outermost, so every response carries the id — including the OpenAPI document, the health
    // probes, and a problem detail written by the exception handler, which replaces the response
    // and would otherwise discard a header set further in.
    app.UseCorrelationId();

    app.UseExceptionHandler();

    // The OpenAPI document is published in every environment (MVP-060): it describes the contract,
    // not any data. The interactive UI is Development only.
    app.UseSwagger(options => options.RouteTemplate = "openapi/{documentName}.json");

    if (app.Environment.IsDevelopment())
    {
        app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Betsi v1"));
    }
    else
    {
        app.UseHttpsRedirection();
    }

    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseTenantResolution(tenantResolution);
    app.UseAuthorization();

    // Razor Components require it, and the sign-out and language forms depend on it. After
    // authorisation, so the token is validated for a request that has an identity.
    app.UseAntiforgery();

    app.MapControllers();
    app.MapBetsiUi();
    // Three probes, because an orchestrator asks three different questions. /health/live says
    // the process is up and must not depend on a database, or a database outage would have
    // every instance killed and restarted into the same outage. /health/ready says this
    // instance can serve tenants, and is what a load balancer and a deployment smoke test read.
    // /health stays as it was: the aggregate, for a person.
    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    }).AllowAnonymous();

    await app.Services.GetRequiredService<IPlatformStartup>().RunAsync(CancellationToken.None);

    Log.Information("Betsi Patient Flow started");
    await app.RunAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Betsi Patient Flow terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Exposed so the integration tests can drive the real pipeline through WebApplicationFactory.
/// </summary>
public partial class Program;
