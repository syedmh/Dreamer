using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Retention;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Infrastructure.Operations.Jobs;
using Husaynia.Infrastructure.Operations.Health;
using Husaynia.Infrastructure.Operations.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.Infrastructure.Operations;

public sealed class OperationsModule : IHusayniaModule
{
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = OperationsConfiguration.ReadJobOptions(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICorrelationContext, CorrelationContext>();
        services.AddSingleton<ISensitiveDataRedactor, HostileSensitiveDataRedactor>();
        services.AddSingleton<IOperationsMetrics, OperationsMetrics>();
        services.AddScoped<IOperationalHealthService, OperationalHealthService>();
        services.AddScoped<IOperationalHealthProbe, SqlDatabaseHealthProbe>();
        services.AddScoped<IOperationalHealthProbe, DurableJobHealthProbe>();
        services.AddScoped<IOperationalHealthProbe, RegisteredDependencyHealthProbe>();
        services.AddScoped<IOperationalHealthProbe, RegisteredStalenessHealthProbe>();
        services.AddScoped<EfDurableJobStore>();
        services.AddScoped<IDurableJobStore>(
            provider => provider.GetRequiredService<EfDurableJobStore>());
        services.AddScoped<IJobLeaseStore>(
            provider => provider.GetRequiredService<EfDurableJobStore>());
        services.AddScoped<IJobBackoffPolicy, ExponentialJitterBackoffPolicy>();
        services.AddScoped<DurableJobProcessor>(
            provider => new DurableJobProcessor(
                provider.GetRequiredService<IDurableJobStore>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<DurableJobOptions>(),
                provider.GetRequiredService<IJobBackoffPolicy>(),
                provider.GetRequiredService<IOperationsMetrics>(),
                provider.GetRequiredService<ICorrelationContext>(),
                provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<EfRetentionStore>();
        services.AddScoped<IRetentionStore>(
            provider => provider.GetRequiredService<EfRetentionStore>());
        services.AddScoped<IRetentionHoldAdministration>(
            provider => provider.GetRequiredService<EfRetentionStore>());
        services.AddScoped<IRetentionTarget, CompletedRetentionRunTarget>();
        services.AddScoped<RetentionWorkflow>();
    }
}

public sealed class OperationsConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return OperationsConfiguration.Validate(configuration);
    }
}

internal static class OperationsConfiguration
{
    private static readonly (string Name, TimeSpan Default)[] Durations =
    [
        ("LeaseDuration", TimeSpan.FromMinutes(2)),
        ("LeaseRenewalInterval", TimeSpan.FromSeconds(30)),
        ("RecoveryDelay", TimeSpan.FromSeconds(5)),
        ("HandlerTimeout", TimeSpan.FromMinutes(5)),
        ("PollingInterval", TimeSpan.FromSeconds(5)),
    ];

    internal static DurableJobOptions ReadJobOptions(IConfiguration configuration)
    {
        var section = DurableJobOptions.SectionName;
        return new DurableJobOptions
        {
            LeaseDuration = ReadDuration(configuration, section, Durations[0]),
            LeaseRenewalInterval = ReadDuration(configuration, section, Durations[1]),
            RecoveryDelay = ReadDuration(configuration, section, Durations[2]),
            HandlerTimeout = ReadDuration(configuration, section, Durations[3]),
            PollingInterval = ReadDuration(configuration, section, Durations[4]),
            JitterRatio = double.TryParse(
                configuration[$"{section}:JitterRatio"],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var jitter)
                    ? jitter
                    : 0.2,
        };
    }

    internal static IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        var options = ReadJobOptions(configuration);
        var failures = new List<string>();
        foreach (var duration in Durations)
        {
            var value = configuration[$"{DurableJobOptions.SectionName}:{duration.Name}"];
            if (value is not null &&
                !TimeSpan.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _))
            {
                failures.Add(
                    $"{DurableJobOptions.SectionName}:{duration.Name} must be a TimeSpan.");
            }
        }

        var jitterValue = configuration[$"{DurableJobOptions.SectionName}:JitterRatio"];
        if (jitterValue is not null &&
            !double.TryParse(
                jitterValue,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            failures.Add("Operations:Jobs:JitterRatio must be a number.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero ||
            options.LeaseDuration > TimeSpan.FromHours(1))
        {
            failures.Add("Operations:Jobs:LeaseDuration must be greater than zero and at most one hour.");
        }

        if (options.LeaseRenewalInterval <= TimeSpan.Zero ||
            options.LeaseRenewalInterval >= options.LeaseDuration)
        {
            failures.Add("Operations:Jobs:LeaseRenewalInterval must be positive and shorter than the lease.");
        }

        if (options.HandlerTimeout <= TimeSpan.Zero ||
            options.HandlerTimeout > TimeSpan.FromHours(1))
        {
            failures.Add("Operations:Jobs:HandlerTimeout must be greater than zero and at most one hour.");
        }

        if (options.RecoveryDelay < TimeSpan.Zero ||
            options.RecoveryDelay > TimeSpan.FromMinutes(10))
        {
            failures.Add("Operations:Jobs:RecoveryDelay must be between zero and ten minutes.");
        }

        if (options.PollingInterval <= TimeSpan.Zero ||
            options.PollingInterval > TimeSpan.FromMinutes(5))
        {
            failures.Add("Operations:Jobs:PollingInterval must be greater than zero and at most five minutes.");
        }

        if (!double.IsFinite(options.JitterRatio) ||
            options.JitterRatio is < 0 or > 1)
        {
            failures.Add("Operations:Jobs:JitterRatio must be between zero and one.");
        }

        return failures;
    }

    private static TimeSpan ReadDuration(
        IConfiguration configuration,
        string section,
        (string Name, TimeSpan Default) duration) =>
        TimeSpan.TryParse(
            configuration[$"{section}:{duration.Name}"],
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : duration.Default;
}
