using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Infrastructure.Operations;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Operations.Jobs;

public sealed class DurableJobDependencyScopeTests
{
    [Fact]
    public async Task HandlerAndRenewalStoreUseIndependentScopedDependencies()
    {
        var services = new ServiceCollection();
        services.AddDbContext<HusayniaDbContext>(options =>
            options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=scope-isolation"));
        services.AddSingleton<ScopeCoordinator>();
        services.AddScoped<ScopedMarker>();
        new OperationsModule().AddServices(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Operations:Jobs:LeaseDuration"] = "00:00:01",
                    ["Operations:Jobs:LeaseRenewalInterval"] = "00:00:00.010",
                    ["Operations:Jobs:HandlerTimeout"] = "00:00:01",
                })
                .Build());
        services.AddScoped<ScopedMarkerStore>();
        services.AddScoped<IDurableJobStore>(
            provider => provider.GetRequiredService<ScopedMarkerStore>());
        services.AddScoped<IJobHandler, ScopedMarkerHandler>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var workerScope = provider.CreateAsyncScope();
        var store = workerScope.ServiceProvider.GetRequiredService<ScopedMarkerStore>();
        var processor = workerScope.ServiceProvider.GetRequiredService<DurableJobProcessor>();

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("scope-test-worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.NotNull(store.Coordinator.HandlerMarker);
        Assert.NotSame(store.Marker, store.Coordinator.HandlerMarker);
        Assert.True(store.RenewCalls > 0);
    }

    [Fact]
    public async Task DetachedNonCooperativeHandlerKeepsScopeUntilCompletion()
    {
        var services = new ServiceCollection();
        var coordinator = new DetachmentCoordinator();
        services.AddSingleton(coordinator);
        services.AddDbContext<HusayniaDbContext>(options =>
            options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=scope-detachment"));
        new OperationsModule().AddServices(
            services,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Operations:Jobs:LeaseDuration"] = "00:00:01",
                    ["Operations:Jobs:LeaseRenewalInterval"] = "00:00:00.010",
                    ["Operations:Jobs:HandlerTimeout"] = "00:00:00.020",
                })
                .Build());
        services.AddScoped<IDurableJobStore, TimeoutStore>();
        services.AddScoped<IJobHandler, NonCooperativeHandler>();
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var workerScope = provider.CreateAsyncScope();
        var processor = workerScope.ServiceProvider.GetRequiredService<DurableJobProcessor>();

        var execution = processor.ExecuteNextAsync(
            new WorkerIdentity("detachment-test-worker"),
            CancellationToken.None);
        await coordinator.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.False(coordinator.Disposed.Task.IsCompleted);

        coordinator.Release.TrySetResult();
        await coordinator.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class ScopedMarker;

    private sealed class ScopeCoordinator
    {
        internal ScopedMarker? HandlerMarker { get; set; }

        internal TaskCompletionSource Renewed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DetachmentCoordinator
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NonCooperativeHandler(
        DetachmentCoordinator coordinator) : IJobHandler, IAsyncDisposable
    {
        public string HandlerName => "non-cooperative-handler";

        public async Task<JobHandlerResult> ExecuteAsync(
            string payloadJson,
            JobExecutionContext context,
            CancellationToken cancellationToken)
        {
            coordinator.Started.TrySetResult();
            await coordinator.Release.Task;
            return JobHandlerResult.Succeeded;
        }

        public ValueTask DisposeAsync()
        {
            coordinator.Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TimeoutStore : IDurableJobStore
    {
        private bool acquired;

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null)
        {
            var result = acquired
                ? new JobAcquireResult(null)
                : new JobAcquireResult(new AcquiredJob(
                    Guid.NewGuid(),
                    "detachment-definition",
                    "non-cooperative-handler",
                    "{}",
                    "detachment-idempotency",
                    "detachment-correlation",
                    1,
                    3,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(1),
                    worker,
                    Guid.NewGuid(),
                    now.Add(leaseDuration),
                    false));
            acquired = true;
            return Task.FromResult(Result.Succeed<JobAcquireResult, JobStoreError>(result));
        }

        public Task<Result<bool, JobStoreError>> RenewAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
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

    private sealed class ScopedMarkerHandler(
        ScopedMarker marker,
        ScopeCoordinator coordinator) : IJobHandler
    {
        public string HandlerName => "scope-test-handler";

        public async Task<JobHandlerResult> ExecuteAsync(
            string payloadJson,
            JobExecutionContext context,
            CancellationToken cancellationToken)
        {
            coordinator.HandlerMarker = marker;
            await coordinator.Renewed.Task.WaitAsync(cancellationToken);
            return JobHandlerResult.Succeeded;
        }
    }

    private sealed class ScopedMarkerStore(
        ScopedMarker marker,
        ScopeCoordinator coordinator) : IDurableJobStore
    {
        private bool acquired;

        internal ScopedMarker Marker { get; } = marker;

        internal ScopeCoordinator Coordinator { get; } = coordinator;

        internal int RenewCalls { get; private set; }

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null)
        {
            var result = acquired
                ? new JobAcquireResult(null)
                : new JobAcquireResult(new AcquiredJob(
                    Guid.NewGuid(),
                    "scope-test-definition",
                    "scope-test-handler",
                    "{}",
                    "scope-test-idempotency",
                    "scope-test-correlation",
                    1,
                    3,
                    TimeSpan.FromMilliseconds(10),
                    TimeSpan.FromSeconds(1),
                    worker,
                    Guid.NewGuid(),
                    now.Add(leaseDuration),
                    false));
            acquired = true;
            return Task.FromResult(Result.Succeed<JobAcquireResult, JobStoreError>(result));
        }

        public Task<Result<bool, JobStoreError>> RenewAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            RenewCalls++;
            Coordinator.Renewed.TrySetResult();
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
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
}
