using Betsi.API.Problems;
using Betsi.Application.Behaviours;
using Betsi.Application.Escalations;
using Betsi.Application.Validation;
using Betsi.Infrastructure;
using Betsi.ControlPlane;
using Betsi.Infrastructure.Persistence;
using Betsi.Infrastructure.Tenancy;
using Betsi.Security;
using FluentValidation;
using MediatR;
using Microsoft.OpenApi;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var isOperatorCommand = OperatorCli.IsOperatorCommand(args);

try
{
    if (!isOperatorCommand)
        Log.Information("Starting Betsi Patient Flow");

    var builder = WebApplication.CreateBuilder(args);

    // Operator commands share the service's configuration and services, but their output is
    // for a person at a terminal, so framework logging is kept to warnings and above.
    if (isOperatorCommand)
        builder.Configuration["Serilog:MinimumLevel:Default"] = "Warning";

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // Enums accepted and returned by name ("IncidentReported"), not by number, so a request body
    // is readable in an audit and does not silently change meaning if an enum is reordered.
    builder.Services.AddControllers()
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
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
    builder.Services.AddEscalationEngine(builder.Configuration);

    builder.Services.AddMediatR(cfg =>
    {
        cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);

        // Order matters. Logging wraps everything so a rejected command is still traced;
        // auditing sits outside validation so failed validation is recorded too; validation
        // runs last, immediately before the handler.
        cfg.AddOpenBehavior(typeof(LoggingBehaviour<,>));
        cfg.AddOpenBehavior(typeof(AuditBehaviour<,>));
        // Authorisation inside auditing, so a refused command leaves an audit row.
        cfg.AddOpenBehavior(typeof(AuthorizationBehaviour<,>));
        // Licence before validation: a refused command is audited, but its body is not
        // inspected for a tenant that may not use it.
        cfg.AddOpenBehavior(typeof(LicenseBehaviour<,>));
        cfg.AddOpenBehavior(typeof(ValidationBehaviour<,>));
    });

    builder.Services.AddValidatorsFromAssemblyContaining<RegisterPatientCommandValidator>();

    builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
    builder.Services.AddProblemDetails();

    builder.Services.AddHealthChecks()
        .AddCheck<TenantDatabaseHealthCheck>("tenant-databases");

    var tenantResolution = new TenantResolutionOptions();
    builder.Configuration.GetSection("TenantResolution").Bind(tenantResolution);

    builder.Services.AddBetsiAuthentication(builder.Configuration, builder.Environment, tenantResolution);

    // Header-supplied tenants let anyone who can reach the API name any tenant. That is a
    // development convenience only, so refuse to start with it enabled outside development
    // rather than relying on configuration review to catch it.
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

    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }
    else
    {
        app.UseHttpsRedirection();
    }

    app.UseSerilogRequestLogging();
    app.UseAuthentication();
    app.UseTenantResolution(tenantResolution);
    app.UseAuthorization();
    app.MapControllers();
    app.MapHealthChecks("/health").AllowAnonymous();

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
