using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Prayer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.Infrastructure.Prayer;

public sealed class PrayerModule : IHusayniaModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = PrayerConfiguration.Read(configuration);
        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DeterministicPrayerCalculator>();
        services.TryAddSingleton<IPrayerExternalClient>(provider =>
            new HttpPrayerExternalClient(
                options,
                new HttpClient(
                    new SocketsHttpHandler
                    {
                        ConnectTimeout = options.ExternalProviderTimeout <
                            TimeSpan.FromSeconds(5)
                                ? options.ExternalProviderTimeout
                                : TimeSpan.FromSeconds(5),
                    })
                {
                    Timeout = options.ExternalProviderTimeout,
                },
                provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<EfPrayerStore>();
        services.TryAddScoped<PrayerTransactionalAuditAppender>();
        services.TryAddScoped<IPrayerScheduleStore>(
            provider => provider.GetRequiredService<EfPrayerStore>());
        services.TryAddScoped<IPrayerAdministrationStore>(
            provider => provider.GetRequiredService<EfPrayerStore>());
        services.TryAddScoped<IPrayerProfileDefinitionReader>(
            provider => provider.GetRequiredService<EfPrayerStore>());
        services.TryAddScoped<IPrayerSchedulingStore>(
            provider => provider.GetRequiredService<EfPrayerStore>());
        services.TryAddScoped<IPrayerRefreshStore>(
            provider => provider.GetRequiredService<EfPrayerStore>());
        services.TryAddScoped<IPrayerScheduleService, PrayerScheduleService>();
        services.TryAddScoped<IPrayerRefreshService, PrayerRefreshService>();
        services.TryAddScoped<IPrayerAdministration, PrayerAdministrationService>();
        services.TryAddScoped<IPrayerSource, ConfiguredPrayerSource>();
        services.TryAddScoped<IPrayerRefreshJobCoordinator, PrayerRefreshJobCoordinator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IJobHandler, PrayerRefreshJobHandler>());
    }
}
