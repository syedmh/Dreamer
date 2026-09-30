using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.Infrastructure.Social;

public sealed class SocialModule : IHusayniaModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = SocialProviderConfiguration.Read(configuration).Providers;
        var sourceHosts = configured.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.SourceHosts,
            StringComparer.Ordinal);
        var mediaHosts = configured.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.MediaHosts,
            StringComparer.Ordinal);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new SocialRefreshOptions());
        services.TryAddSingleton<ISocialLinkPolicy>(
            new SocialLinkPolicy(sourceHosts, mediaHosts));
        services.TryAddSingleton<IReadOnlyDictionary<string, SocialProviderSettings>>(configured);
        services.TryAddSingleton<ISocialProviderCredentialSource>(
            new ConfigurationSocialCredentialSource(configuration));
        services.TryAddSingleton<ISocialDnsResolver, SystemSocialDnsResolver>();
        services.TryAddSingleton<ISocialDelay, SystemSocialDelay>();
        services.TryAddSingleton<ISocialProviderHttpClientFactory, SocialProviderHttpClientFactory>();
        services.TryAddSingleton<ISocialFeedProvider, ConfiguredSocialFeedProvider>();
        services.TryAddSingleton<ISocialFeedSource>(
            provider => (ConfiguredSocialFeedProvider)
                provider.GetRequiredService<ISocialFeedProvider>());
        services.TryAddScoped<ISocialSnapshotStore, EfSocialSnapshotStore>();
        services.TryAddScoped<ISocialFeedReader, SocialFeedReader>();
        services.TryAddScoped<ISocialFeedRefreshService, SocialFeedRefreshService>();
        foreach (var providerName in configured.Keys.Order(StringComparer.Ordinal))
        {
            var registeredProvider = providerName;
            services.AddScoped<IOperationalHealthProbe>(provider =>
                new SocialDependencyHealthProbe(
                    registeredProvider,
                    provider.GetRequiredService<ISocialSnapshotStore>()));
            services.AddScoped<IOperationalHealthProbe>(provider =>
                new SocialSnapshotStalenessHealthProbe(
                    registeredProvider,
                    provider.GetRequiredService<ISocialSnapshotStore>(),
                    provider.GetRequiredService<TimeProvider>()));
        }

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, SocialRefreshJobHandler>());
        services.TryAddScoped<ISocialRefreshJobCoordinator>(provider =>
            new SocialRefreshJobCoordinator(
                configured.Keys,
                provider.GetRequiredService<ISocialSnapshotStore>(),
                provider.GetRequiredService<IDurableJobStore>(),
                provider.GetRequiredService<ICorrelationContext>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<SocialRefreshOptions>()));
        services.TryAddSingleton<SocialRefreshJobStartupService>();
        SocialRefreshJobHostedServiceRegistration.Add(services);
    }
}
