using System.Reflection;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Infrastructure.Social;

public sealed class SocialRefreshJobStartupService(
    IServiceScopeFactory scopeFactory,
    ISocialDelay delay,
    IOperationsMetrics metrics) : IDisposable
{
    private const int MaximumRegistrationAttempts = 3;
    private static readonly TimeSpan RepairInterval = TimeSpan.FromMinutes(5);
    private readonly object startLock = new();
    private readonly IServiceScopeFactory scopeFactory =
        scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly ISocialDelay delay =
        delay ?? throw new ArgumentNullException(nameof(delay));
    private readonly IOperationsMetrics metrics =
        metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly string startupCorrelationId = $"social-startup-{Guid.NewGuid():N}";
    private Task? startTask;
    private Task? repairTask;
    private CancellationTokenSource? repairCancellation;
    private bool stopRequested;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (startLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return startTask ??= StartCoreAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? startup;
        lock (startLock)
        {
            stopRequested = true;
            repairCancellation?.Cancel();
            startup = startTask;
        }

        if (startup is null)
        {
            return;
        }

        await startup.WaitAsync(cancellationToken).ConfigureAwait(false);

        Task? repair;
        lock (startLock)
        {
            repairCancellation?.Cancel();
            repair = repairTask;
        }

        if (repair is not null)
        {
            await repair.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaximumRegistrationAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await StartAttemptAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (SocialRefreshJobRegistrationException exception) when (
                exception.ErrorCode == JobStoreErrorCode.PersistenceFailure &&
                attempt < MaximumRegistrationAttempts)
            {
            }
        }

        lock (startLock)
        {
            if (stopRequested || disposed)
            {
                return;
            }

            repairCancellation = new CancellationTokenSource();
            repairTask = RunRepairLoopAsync(repairCancellation.Token);
        }
    }

    private async Task StartAttemptAsync(CancellationToken cancellationToken)
    {
        await RunCatchUpAsync(
                "social-refresh-job-startup",
                recordRepairMetric: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunRepairLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await delay.DelayAsync(RepairInterval, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await RunCatchUpAsync(
                            "social-refresh-job-repair",
                            recordRepairMetric: true,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    RecordRepairMetric("repair_iteration_failed");
                }
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            RecordRepairMetric("repair_loop_stopped");
        }
    }

    private async Task RunCatchUpAsync(
        string operationName,
        bool recordRepairMetric,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var correlation = scope.ServiceProvider.GetRequiredService<ICorrelationContext>();
        using var correlationScope = correlation.Begin(
            startupCorrelationId,
            operationName);
        var coordinator = scope.ServiceProvider
            .GetRequiredService<ISocialRefreshJobCoordinator>();
        await coordinator.RegisterAndEnqueueCatchUpAsync(cancellationToken)
            .ConfigureAwait(false);
        if (recordRepairMetric)
        {
            RecordRepairMetric("repair_iteration_succeeded");
        }
    }

    private void RecordRepairMetric(string outcome)
    {
        try
        {
            metrics.RecordJob(
                SocialRefreshJobDefinition.Key,
                outcome,
                attempt: 0);
        }
        catch
        {
            // Repair liveness must not depend on telemetry availability.
        }
    }

    public void Dispose()
    {
        lock (startLock)
        {
            disposed = true;
            stopRequested = true;
            repairCancellation?.Cancel();
        }
    }
}

internal static class SocialRefreshJobHostedServiceRegistration
{
    private const string HostedServiceTypeName =
        "Microsoft.Extensions.Hosting.IHostedService, Microsoft.Extensions.Hosting.Abstractions";

    public static void Add(IServiceCollection services)
    {
        var hostedServiceType = Type.GetType(
            HostedServiceTypeName,
            throwOnError: true)!;

        if (services.Any(descriptor =>
                descriptor.ServiceType == hostedServiceType &&
                descriptor.ImplementationFactory?.Method.DeclaringType ==
                    typeof(SocialRefreshJobHostedServiceRegistration)))
        {
            return;
        }

        services.Add(ServiceDescriptor.Singleton(
            hostedServiceType,
            provider => CreateHostedServiceProxy(
                hostedServiceType,
                provider.GetRequiredService<SocialRefreshJobStartupService>())));
    }

    private static SocialRefreshJobHostedServiceProxy CreateHostedServiceProxy(
        Type hostedServiceType,
        SocialRefreshJobStartupService startupService)
    {
        var createMethod = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method =>
                method.Name == nameof(DispatchProxy.Create) &&
                method.IsGenericMethodDefinition &&
                method.GetGenericArguments().Length == 2 &&
                method.GetParameters().Length == 0);
        var proxy = (SocialRefreshJobHostedServiceProxy)createMethod
            .MakeGenericMethod(
                hostedServiceType,
                typeof(SocialRefreshJobHostedServiceProxy))
            .Invoke(null, null)!;
        proxy.Initialize(startupService);
        return proxy;
    }
}

public class SocialRefreshJobHostedServiceProxy : DispatchProxy
{
    private SocialRefreshJobStartupService? startupService;

    public void Initialize(SocialRefreshJobStartupService value) =>
        startupService = value ?? throw new ArgumentNullException(nameof(value));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (args is not [CancellationToken cancellationToken])
        {
            throw new InvalidOperationException(
                "The social refresh hosted service received an invalid invocation.");
        }

        return targetMethod.Name switch
        {
            nameof(SocialRefreshJobStartupService.StartAsync) =>
                StartupService.StartAsync(cancellationToken),
            nameof(SocialRefreshJobStartupService.StopAsync) =>
                StartupService.StopAsync(cancellationToken),
            _ => throw new InvalidOperationException(
                "The social refresh hosted service received an unsupported invocation."),
        };
    }

    private SocialRefreshJobStartupService StartupService =>
        startupService ?? throw new InvalidOperationException(
            "The social refresh hosted service has not been initialized.");
}
