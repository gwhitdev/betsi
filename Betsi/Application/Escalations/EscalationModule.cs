namespace Betsi.Application.Escalations;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class EscalationModule
{
    public static IServiceCollection AddEscalationEngine(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new EscalationMonitorOptions();
        configuration.GetSection(EscalationMonitorOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        services.AddScoped<IEscalationPolicyReader, EscalationPolicyReader>();
        services.AddScoped<IWaitingTimeMonitor, WaitingTimeMonitor>();
        services.AddScoped<EscalationQueries>();
        services.AddScoped<Betsi.Application.Queries.EpisodeQueries>();
        services.AddScoped<Betsi.Application.Commands.CommandEnvelopeDispatcher>();
        services.AddHostedService<WaitingTimeMonitorService>();

        return services;
    }
}
