namespace Betsi.Application.Escalations;

using Betsi.Infrastructure.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class EscalationModule
{
    public static IServiceCollection AddEscalationEngine(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
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

        AddIntegrations(services, configuration, environment);

        return services;
    }

    private static void AddIntegrations(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Bound when first resolved, so configuration added after registration (a test host, a
        // secret store) is what takes effect.
        services.AddSingleton(sp =>
        {
            var webhooks = new Betsi.Integrations.WebhookOptions();
            sp.GetRequiredService<IConfiguration>().GetSection(Betsi.Integrations.WebhookOptions.SectionName).Bind(webhooks);
            return webhooks;
        });

        // Secrets are encrypted with Data Protection. Every instance must share the key ring, or
        // an instance cannot read a secret another created; the default store is the control-plane
        // database, so a second instance needs no extra configuration to be correct.
        services.AddBetsiDataProtection(configuration, environment);

        services.AddSingleton<Betsi.Integrations.IntegrationSecrets>();
        services.AddScoped<Betsi.Integrations.IntegrationAdministration>();
        services.AddScoped<Betsi.Integrations.InboundMessageProcessor>();

        services.AddScoped<Betsi.Infrastructure.Outbox.LoggingOutboxPublisher>();
        services.AddScoped<Betsi.Integrations.WebhookFanOutPublisher>();
        services.Replace(ServiceDescriptor.Scoped<Betsi.Infrastructure.Outbox.IOutboxPublisher, Betsi.Integrations.CompositeOutboxPublisher>());

        services.AddHttpClient(Betsi.Integrations.WebhookDeliverer.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
                Betsi.Integrations.NetworkTargets.CreateHandler(sp.GetRequiredService<Betsi.Integrations.WebhookOptions>().AllowPrivateNetworkTargets));
        services.AddScoped<Betsi.Integrations.WebhookDeliverer>();
        services.AddHostedService<Betsi.Integrations.WebhookDeliveryService>();
    }
}
