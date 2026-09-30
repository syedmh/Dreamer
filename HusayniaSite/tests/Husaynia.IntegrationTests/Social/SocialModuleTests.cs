using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Health;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Operations;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Infrastructure.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Husaynia.IntegrationTests.Social;

public sealed class SocialModuleTests
{
    [Fact]
    public void ConfiguredModuleResolvesProviderRefreshReaderAndAllowlist()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HusayniaDatabase"] =
                    "Server=(localdb)\\MSSQLLocalDB;Database=unused;Integrated Security=true",
                ["Social:Providers:facebook:Endpoint"] =
                    "https://api.facebook.example/feed",
                ["Social:Providers:facebook:SourceHosts:0"] =
                    "api.facebook.example",
                ["Social:Providers:facebook:SourceHosts:1"] =
                    "facebook.example",
                ["Social:Providers:facebook:MediaHosts:0"] =
                    "media.example",
                ["Social:Providers:facebook:RequestTimeoutSeconds"] = "8",
            })
            .Build();
        var services = new ServiceCollection();
        new PersistenceModule().AddServices(services, configuration);
        new SocialModule().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var linkPolicy = scope.ServiceProvider.GetRequiredService<ISocialLinkPolicy>();

        Assert.IsType<ConfiguredSocialFeedProvider>(
            scope.ServiceProvider.GetRequiredService<ISocialFeedProvider>());
        Assert.IsType<ConfiguredSocialFeedProvider>(
            scope.ServiceProvider.GetRequiredService<ISocialFeedSource>());
        Assert.IsType<SocialProviderHttpClientFactory>(
            scope.ServiceProvider.GetRequiredService<ISocialProviderHttpClientFactory>());
        Assert.IsType<SocialFeedRefreshService>(
            scope.ServiceProvider.GetRequiredService<ISocialFeedRefreshService>());
        Assert.IsType<SocialFeedReader>(
            scope.ServiceProvider.GetRequiredService<ISocialFeedReader>());
        var socialProbes = scope.ServiceProvider
            .GetServices<IOperationalHealthProbe>()
            .ToArray();
        Assert.Contains(
            socialProbes,
            probe => probe is SocialDependencyHealthProbe);
        Assert.Contains(
            socialProbes,
            probe => probe is SocialSnapshotStalenessHealthProbe);
        Assert.Equal(
            "https://facebook.example/posts/1",
            linkPolicy.NormalizeSourceLink(
                "facebook",
                new Uri("https://facebook.example/posts/1?view=full")).AbsoluteUri);
        Assert.Throws<ArgumentException>(() =>
            linkPolicy.NormalizeSourceLink(
                "facebook",
                new Uri("https://tracker.example/pixel")));
    }

    [Fact]
    public void ValidatorRejectsUnsafeEndpointAndMissingAllowlist()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Social:Providers:facebook:Endpoint"] = "http://localhost/feed",
            })
            .Build();

        var errors = new SocialConfigurationValidator().Validate(configuration);

        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
            Assert.DoesNotContain("BearerToken", error, StringComparison.Ordinal));
    }

    [Fact]
    public void SocialModuleRegistersDiscoverableDurableRefreshHandlerAndCoordinator()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HusayniaDatabase"] =
                    "Server=(localdb)\\MSSQLLocalDB;Database=unused;Integrated Security=true",
            })
            .Build();
        var services = new ServiceCollection();
        new PersistenceModule().AddServices(services, configuration);
        new OperationsModule().AddServices(services, configuration);
        new SocialModule().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>();

        Assert.Contains(handlers, handler => handler is SocialRefreshJobHandler);
        Assert.IsType<SocialRefreshJobCoordinator>(
            scope.ServiceProvider.GetRequiredService<ISocialRefreshJobCoordinator>());
    }

    [Fact]
    public async Task SocialModuleRegistersHostedCatchUpThatUsesOneScopeAndStableStartupCorrelation()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        new PersistenceModule().AddServices(services, configuration);
        new OperationsModule().AddServices(services, configuration);
        new SocialModule().AddServices(services, configuration);
        var probe = new StartupProbe();
        services.AddSingleton(probe);
        services.AddScoped<ScopeMarker>();
        services.Replace(ServiceDescriptor.Scoped<ISocialRefreshJobCoordinator>(
            provider => new RecordingStartupCoordinator(
                probe,
                provider.GetRequiredService<ScopeMarker>(),
                provider.GetRequiredService<ICorrelationContext>())));

        using var provider = services.BuildServiceProvider();
        var rootScopeMarker = provider.GetRequiredService<ScopeMarker>();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StartAsync(CancellationToken.None);

        var invocation = Assert.Single(probe.Invocations);
        Assert.NotEqual(rootScopeMarker.Id, invocation.ScopeId);
        Assert.Matches("^social-startup-[a-f0-9]{32}$", invocation.CorrelationId);
    }

    [Fact]
    public async Task HostedCatchUpPropagatesCoordinatorFailure()
    {
        var services = ServicesWithHostedCatchUp();
        services.Replace(ServiceDescriptor.Scoped<ISocialRefreshJobCoordinator>(
            _ => new FailingStartupCoordinator()));

        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => hostedService.StartAsync(CancellationToken.None));

        Assert.Equal("Registration failed.", exception.Message);
    }

    [Fact]
    public async Task HostedCatchUpPropagatesCallerCancellation()
    {
        var probe = new BlockingStartupProbe();
        var services = ServicesWithHostedCatchUp();
        services.AddSingleton(probe);
        services.Replace(ServiceDescriptor.Scoped<ISocialRefreshJobCoordinator>(
            _ => new BlockingStartupCoordinator(probe)));
        using var cancellation = new CancellationTokenSource();
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        var start = hostedService.StartAsync(cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(cancellation.Token, probe.CancellationToken);
    }

    [Fact]
    public async Task HostedCatchUpRetriesOnePersistenceFailureThenSucceeds()
    {
        var probe = new RegistrationStoreProbe(
            JobStoreErrorCode.PersistenceFailure,
            null);
        var services = ServicesWithHostedCatchUp();
        services.AddSingleton(probe);
        services.Replace(ServiceDescriptor.Scoped<IDurableJobStore>(provider =>
            new ScriptedRegistrationJobStore(
                provider.GetRequiredService<RegistrationStoreProbe>())));
        services.Replace(ServiceDescriptor.Scoped<ISocialSnapshotStore>(
            _ => new StaticStore()));
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal(2, probe.RegistrationCalls);
        Assert.Equal(2, probe.StoreInstances);
    }

    [Fact]
    public async Task HostedCatchUpStopsAfterThreePersistenceFailures()
    {
        var probe = new RegistrationStoreProbe(
            JobStoreErrorCode.PersistenceFailure,
            JobStoreErrorCode.PersistenceFailure,
            JobStoreErrorCode.PersistenceFailure,
            null);
        var services = ServicesWithHostedCatchUp();
        services.AddSingleton(probe);
        services.Replace(ServiceDescriptor.Scoped<IDurableJobStore>(provider =>
            new ScriptedRegistrationJobStore(
                provider.GetRequiredService<RegistrationStoreProbe>())));
        services.Replace(ServiceDescriptor.Scoped<ISocialSnapshotStore>(
            _ => new StaticStore()));
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        var exception = await Assert.ThrowsAsync<SocialRefreshJobRegistrationException>(
            () => hostedService.StartAsync(CancellationToken.None));

        Assert.Equal(JobStoreErrorCode.PersistenceFailure, exception.ErrorCode);
        Assert.Equal(3, probe.RegistrationCalls);
        Assert.Equal(3, probe.StoreInstances);
    }

    [Fact]
    public async Task HostedCatchUpDoesNotRetryNontransientRegistrationFailure()
    {
        var probe = new RegistrationStoreProbe(
            JobStoreErrorCode.InvalidInput,
            null);
        var services = ServicesWithHostedCatchUp();
        services.AddSingleton(probe);
        services.Replace(ServiceDescriptor.Scoped<IDurableJobStore>(provider =>
            new ScriptedRegistrationJobStore(
                provider.GetRequiredService<RegistrationStoreProbe>())));
        services.Replace(ServiceDescriptor.Scoped<ISocialSnapshotStore>(
            _ => new StaticStore()));
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        var exception = await Assert.ThrowsAsync<SocialRefreshJobRegistrationException>(
            () => hostedService.StartAsync(CancellationToken.None));

        Assert.Equal(JobStoreErrorCode.InvalidInput, exception.ErrorCode);
        Assert.Equal(1, probe.RegistrationCalls);
        Assert.Equal(1, probe.StoreInstances);
    }

    [Fact]
    public async Task HostedStopAwaitsInFlightCatchUp()
    {
        var probe = new ControllableStartupProbe();
        var services = ServicesWithHostedCatchUp();
        services.AddSingleton(probe);
        services.Replace(ServiceDescriptor.Scoped<ISocialRefreshJobCoordinator>(
            _ => new ControllableStartupCoordinator(probe)));
        using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());

        var start = hostedService.StartAsync(CancellationToken.None);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = hostedService.StopAsync(CancellationToken.None);

        Assert.False(stop.IsCompleted);
        probe.Release.SetResult();
        await start;
        await stop;
    }

    [Fact]
    public async Task ProductionReaderResolutionPerformsZeroProviderResolutionAndNetworkCalls()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        new SocialModule().AddServices(services, configuration);
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(
            new FixedTimeProvider(Now)));
        var probe = new ResolutionProbe();
        services.Replace(ServiceDescriptor.Singleton<ISocialFeedSource>(_ =>
        {
            probe.SourceResolutions++;
            return new CountingSource(probe);
        }));
        services.Replace(ServiceDescriptor.Singleton<ISocialProviderHttpClientFactory>(_ =>
        {
            probe.HttpResolutions++;
            return new CountingHttpFactory(probe);
        }));
        services.Replace(ServiceDescriptor.Singleton<ISocialSnapshotStore>(
            new StaticStore()));

        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<ISocialFeedReader>();

        var view = await reader.ReadAsync(
            "facebook",
            socialConsentGranted: false,
            CancellationToken.None);

        Assert.Equal(SocialFeedAvailability.Healthy, view.Availability);
        Assert.Equal(0, probe.SourceResolutions);
        Assert.Equal(0, probe.HttpResolutions);
        Assert.Equal(0, probe.ProviderCalls);
        Assert.Equal(0, probe.NetworkCalls);
    }

    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static ServiceCollection ServicesWithHostedCatchUp()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        new PersistenceModule().AddServices(services, configuration);
        new OperationsModule().AddServices(services, configuration);
        new SocialModule().AddServices(services, configuration);
        return services;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ScopeMarker
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    private sealed record StartupInvocation(Guid ScopeId, string CorrelationId);

    private sealed class StartupProbe
    {
        internal List<StartupInvocation> Invocations { get; } = [];
    }

    private sealed class RecordingStartupCoordinator(
        StartupProbe probe,
        ScopeMarker scopeMarker,
        ICorrelationContext correlation) : ISocialRefreshJobCoordinator
    {
        public Task<IReadOnlyList<SocialRefreshJobEnqueueResult>>
            RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken)
        {
            probe.Invocations.Add(new StartupInvocation(
                scopeMarker.Id,
                correlation.Current.CorrelationId));
            return Task.FromResult<IReadOnlyList<SocialRefreshJobEnqueueResult>>([]);
        }

        public Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
            string provider,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingStartupCoordinator : ISocialRefreshJobCoordinator
    {
        public Task<IReadOnlyList<SocialRefreshJobEnqueueResult>>
            RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<SocialRefreshJobEnqueueResult>>(
                new InvalidOperationException("Registration failed."));

        public Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
            string provider,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingStartupProbe
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationToken CancellationToken { get; set; }
    }

    private sealed class BlockingStartupCoordinator(
        BlockingStartupProbe probe) : ISocialRefreshJobCoordinator
    {
        public async Task<IReadOnlyList<SocialRefreshJobEnqueueResult>>
            RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken)
        {
            probe.CancellationToken = cancellationToken;
            probe.Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
            string provider,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ControllableStartupProbe
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControllableStartupCoordinator(
        ControllableStartupProbe probe) : ISocialRefreshJobCoordinator
    {
        public async Task<IReadOnlyList<SocialRefreshJobEnqueueResult>>
            RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken)
        {
            probe.Started.SetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
            return [];
        }

        public Task<SocialRefreshJobEnqueueResult> EnsureScheduledAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
            string provider,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RegistrationStoreProbe(
        params JobStoreErrorCode?[] outcomes)
    {
        private readonly Queue<JobStoreErrorCode?> outcomes = new(outcomes);

        internal int RegistrationCalls { get; set; }

        internal int StoreInstances { get; set; }

        internal JobStoreErrorCode? NextOutcome()
        {
            RegistrationCalls++;
            return outcomes.Count == 0 ? null : outcomes.Dequeue();
        }
    }

    private sealed class ScriptedRegistrationJobStore : IDurableJobStore
    {
        private readonly RegistrationStoreProbe probe;

        internal ScriptedRegistrationJobStore(RegistrationStoreProbe probe)
        {
            this.probe = probe;
            probe.StoreInstances++;
        }

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = probe.NextOutcome();
            return Task.FromResult(
                outcome is { } errorCode
                    ? Result.Fail<Guid, JobStoreError>(
                        new JobStoreError(errorCode, "Scripted registration failure."))
                    : Result.Succeed<Guid, JobStoreError>(Guid.NewGuid()));
        }

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RenewAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
            Guid jobInstanceId,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
            Guid jobInstanceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ResolutionProbe
    {
        internal int SourceResolutions { get; set; }

        internal int HttpResolutions { get; set; }

        internal int ProviderCalls { get; set; }

        internal int NetworkCalls { get; set; }
    }

    private sealed class CountingSource(ResolutionProbe probe) : ISocialFeedSource
    {
        public Task<Result<SocialProviderFeed, SocialSourceError>> FetchAsync(
            SocialFeedRequest request,
            CancellationToken cancellationToken)
        {
            probe.ProviderCalls++;
            throw new InvalidOperationException("Public reads must not call providers.");
        }
    }

    private sealed class CountingHttpFactory(ResolutionProbe probe)
        : ISocialProviderHttpClientFactory
    {
        public Task<Result<HttpResponseMessage, SocialSourceError>> SendGetAsync(
            SocialProviderSettings providerSettings,
            string? bearerToken,
            CancellationToken cancellationToken)
        {
            probe.NetworkCalls++;
            throw new InvalidOperationException("Public reads must not call the network.");
        }
    }

    private sealed class StaticStore : ISocialSnapshotStore
    {
        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult<StoredSocialFeed?>(new StoredSocialFeed(
                "facebook",
                1,
                new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 20, 13, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero),
                null,
                [
                    new StoredSocialFeedItem(
                        "one",
                        "Update",
                        null,
                        SocialMediaType.None,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        new DateTimeOffset(2026, 8, 20, 11, 0, 0, TimeSpan.Zero)),
                ]));

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult<SocialRefreshState?>(null);

        public async Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken)
        {
            var snapshot = await ReadAsync(provider, cancellationToken);
            return new SocialRefreshSchedulingState(
                snapshot?.Version,
                snapshot?.ExpiresAtUtc,
                null);
        }

        public Task<long> ReplaceAsync(
            NormalizedSocialFeed feed,
            int retainedVersions,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public Task RecordFailureAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset? retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();

        public Task DeferUntilAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException();
    }
}
