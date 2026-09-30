using System.Threading.Channels;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Social;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Social;

public sealed class SocialRefreshJobLivenessTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SocialRefreshOutcome.Refreshed)]
    [InlineData(SocialRefreshOutcome.RateLimited)]
    [InlineData(SocialRefreshOutcome.Unavailable)]
    public async Task FinalAttemptEnqueueFailureIsRepairedByLaterLoop(
        SocialRefreshOutcome refreshOutcome)
    {
        var expiresAtUtc = Now.AddMinutes(-1);
        var currentKey = SnapshotKey(1, expiresAtUtc);
        var currentJobId = Guid.NewGuid();
        await using var harness = CreateHarness(
            currentJobId,
            currentKey,
            expiresAtUtc,
            attemptsStarted: 2);

        await harness.Startup.StartAsync(CancellationToken.None);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await harness.Delay.WaitForDelayAsync());
        harness.Jobs.EnqueueFailuresRemaining = 1;

        using var workerScope = harness.Provider.CreateScope();
        var refresh = new PersistingOutcomeRefreshService(
            refreshOutcome,
            harness.Snapshots);
        var processor = CreateProcessor(
            workerScope.ServiceProvider.GetRequiredService<IDurableJobStore>(),
            new SocialRefreshJobHandler(
                refresh,
                workerScope.ServiceProvider
                    .GetRequiredService<ISocialRefreshJobCoordinator>()),
            workerScope.ServiceProvider.GetRequiredService<ICorrelationContext>());

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("social-worker"),
            CancellationToken.None);

        Assert.Equal("DeadLettered", outcome.FinalState);
        Assert.Equal(
            "DeadLettered",
            harness.Jobs.GetSnapshot(currentJobId).State);
        Assert.Equal(1, refresh.Calls);

        harness.Delay.ReleaseNext();
        var successorKey = await harness.Jobs.WaitForNewJobAsync();
        Assert.Equal(ExpectedSuccessorKey(refreshOutcome), successorKey);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await harness.Delay.WaitForDelayAsync());
        Assert.Equal(1, refresh.Calls);
        Assert.Contains(
            harness.Metrics.JobOutcomes,
            value => value == "repair_iteration_succeeded");
    }

    [Fact]
    public async Task FinalWorkerShutdownDeadLetterIsRequeuedAndCanContinue()
    {
        var expiresAtUtc = Now.AddMinutes(-1);
        var currentKey = SnapshotKey(1, expiresAtUtc);
        var currentJobId = Guid.NewGuid();
        await using var harness = CreateHarness(
            currentJobId,
            currentKey,
            expiresAtUtc,
            attemptsStarted: 2);

        await harness.Startup.StartAsync(CancellationToken.None);
        await harness.Delay.WaitForDelayAsync();
        var refresh = new BlockingThenSuccessfulRefreshService(
            harness.Snapshots);

        using (var firstWorkerScope = harness.Provider.CreateScope())
        {
            var processor = CreateProcessor(
                firstWorkerScope.ServiceProvider
                    .GetRequiredService<IDurableJobStore>(),
                new SocialRefreshJobHandler(
                    refresh,
                    firstWorkerScope.ServiceProvider
                        .GetRequiredService<ISocialRefreshJobCoordinator>()),
                firstWorkerScope.ServiceProvider
                    .GetRequiredService<ICorrelationContext>());
            using var cancellation = new CancellationTokenSource();
            var execution = processor.ExecuteNextAsync(
                new WorkerIdentity("social-worker"),
                cancellation.Token);
            await refresh.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution);
        }

        Assert.Equal(
            "DeadLettered",
            harness.Jobs.GetSnapshot(currentJobId).State);

        harness.Delay.ReleaseNext();
        await harness.Jobs.Requeued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Delay.WaitForDelayAsync();

        var repaired = harness.Jobs.GetSnapshot(currentJobId);
        Assert.Equal("RetryScheduled", repaired.State);
        Assert.Equal(0, repaired.AttemptsStarted);
        var requeue = Assert.Single(harness.Jobs.Requeues);
        Assert.Equal("social-refresh-repair", requeue.Actor);
        Assert.Equal(
            "Restore the canonical recurring social refresh job.",
            requeue.Reason);
        Assert.Matches(
            "^social-startup-[a-f0-9]{32}$",
            requeue.CorrelationId);

        using (var secondWorkerScope = harness.Provider.CreateScope())
        {
            var processor = CreateProcessor(
                secondWorkerScope.ServiceProvider
                    .GetRequiredService<IDurableJobStore>(),
                new SocialRefreshJobHandler(
                    refresh,
                    secondWorkerScope.ServiceProvider
                        .GetRequiredService<ISocialRefreshJobCoordinator>()),
                secondWorkerScope.ServiceProvider
                    .GetRequiredService<ICorrelationContext>());

            var outcome = await processor.ExecuteNextAsync(
                new WorkerIdentity("social-worker"),
                CancellationToken.None);

            Assert.Equal("Completed", outcome.FinalState);
        }

        Assert.Equal("Completed", harness.Jobs.GetSnapshot(currentJobId).State);
        Assert.Equal(2, refresh.Calls);
        Assert.Contains(
            SnapshotKey(2, Now.AddHours(2)),
            harness.Jobs.IdempotencyKeys);
    }

    [Fact]
    public async Task RepairLoopRetriesTransientFailureWithFreshScopesAndStops()
    {
        var expiresAtUtc = Now.AddMinutes(-1);
        await using var harness = CreateHarness(
            Guid.NewGuid(),
            SnapshotKey(1, expiresAtUtc),
            expiresAtUtc,
            attemptsStarted: 0);

        await harness.Startup.StartAsync(CancellationToken.None);
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await harness.Delay.WaitForDelayAsync());
        Assert.Equal(1, harness.Jobs.StoreInstances);
        Assert.Equal(1, harness.Snapshots.StoreInstances);

        harness.Jobs.RegistrationFailuresRemaining = 1;
        harness.Delay.ReleaseNext();
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await harness.Delay.WaitForDelayAsync());
        Assert.Equal(2, harness.Jobs.StoreInstances);
        Assert.Equal(2, harness.Snapshots.StoreInstances);
        Assert.Contains(
            harness.Metrics.JobOutcomes,
            value => value == "repair_iteration_failed");

        harness.Delay.ReleaseNext();
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            await harness.Delay.WaitForDelayAsync());
        Assert.Equal(3, harness.Jobs.StoreInstances);
        Assert.Equal(3, harness.Snapshots.StoreInstances);
        Assert.Contains(
            harness.Metrics.JobOutcomes,
            value => value == "repair_iteration_succeeded");

        await harness.Startup.StopAsync(CancellationToken.None);

        Assert.Equal(1, harness.Delay.CancellationCount);
        Assert.All(
            harness.Delay.RequestedDelays,
            value => Assert.Equal(TimeSpan.FromMinutes(5), value));
    }

    private static LivenessHarness CreateHarness(
        Guid currentJobId,
        string currentKey,
        DateTimeOffset expiresAtUtc,
        int attemptsStarted)
    {
        var jobs = new SharedJobState();
        jobs.Seed(
            currentJobId,
            currentKey,
            SocialRefreshJobPayload.Serialize("facebook"),
            attemptsStarted,
            expiresAtUtc);
        var snapshots = new SharedSnapshotState(
            Feed(expiresAtUtc),
            null);
        var delay = new ControlledSocialDelay();
        var metrics = new RecordingOperationsMetrics();
        var services = new ServiceCollection();
        services.AddSingleton(jobs);
        services.AddSingleton(snapshots);
        services.AddSingleton(delay);
        services.AddSingleton<ISocialDelay>(delay);
        services.AddSingleton(metrics);
        services.AddSingleton<IOperationsMetrics>(metrics);
        services.AddSingleton<ICorrelationContext, CorrelationContext>();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddSingleton(new SocialRefreshOptions());
        services.AddScoped<IDurableJobStore, ScopedLivenessJobStore>();
        services.AddScoped<ISocialSnapshotStore, ScopedSnapshotStore>();
        services.AddScoped<ISocialRefreshJobCoordinator>(provider =>
            new SocialRefreshJobCoordinator(
                ["facebook"],
                provider.GetRequiredService<ISocialSnapshotStore>(),
                provider.GetRequiredService<IDurableJobStore>(),
                provider.GetRequiredService<ICorrelationContext>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<SocialRefreshOptions>()));
        services.AddSingleton<SocialRefreshJobStartupService>();
        var provider = services.BuildServiceProvider();
        return new LivenessHarness(
            provider,
            provider.GetRequiredService<SocialRefreshJobStartupService>(),
            jobs,
            snapshots,
            delay,
            metrics);
    }

    private static DurableJobProcessor CreateProcessor(
        IDurableJobStore store,
        IJobHandler handler,
        ICorrelationContext correlation)
    {
        var options = new DurableJobOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(1),
            LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
            RecoveryDelay = TimeSpan.FromSeconds(5),
            HandlerTimeout = TimeSpan.FromSeconds(2),
            JitterRatio = 0,
        };
        return new DurableJobProcessor(
            store,
            [handler],
            options,
            new ExponentialJitterBackoffPolicy(
                options,
                new Random(1)),
            new RecordingOperationsMetrics(),
            correlation,
            new FixedTimeProvider(Now));
    }

    private static string ExpectedSuccessorKey(
        SocialRefreshOutcome outcome) =>
        outcome switch
        {
            SocialRefreshOutcome.Refreshed =>
                SnapshotKey(2, Now.AddHours(2)),
            SocialRefreshOutcome.RateLimited =>
                RecoveryKey(Now.AddMinutes(7)),
            SocialRefreshOutcome.Unavailable =>
                RecoveryKey(Now.AddMinutes(15)),
            _ => throw new InvalidOperationException(
                "The scripted refresh outcome is unsupported."),
        };

    private static string SnapshotKey(
        long version,
        DateTimeOffset expiresAtUtc) =>
        $"social-refresh:facebook:v{version}:e{expiresAtUtc.UtcTicks}";

    private static string RecoveryKey(DateTimeOffset retryAfterUtc) =>
        $"social-refresh:facebook:recovery:v2:" +
        $"not-before:{retryAfterUtc.UtcTicks}";

    private static StoredSocialFeed Feed(
        DateTimeOffset expiresAtUtc,
        long version = 1) =>
        new(
            "facebook",
            version,
            Now.AddHours(-1),
            expiresAtUtc,
            Now.AddHours(-1),
            null,
            []);

    private static SocialRefreshState SuccessState() =>
        new(
            Now,
            Now,
            null,
            SocialRefreshError.None,
            null,
            0);

    private static SocialRefreshState FailureState(
        SocialRefreshError error,
        DateTimeOffset retryAfterUtc) =>
        new(
            Now,
            Now.AddHours(-1),
            Now,
            error,
            retryAfterUtc,
            1);

    private sealed class LivenessHarness(
        ServiceProvider provider,
        SocialRefreshJobStartupService startup,
        SharedJobState jobs,
        SharedSnapshotState snapshots,
        ControlledSocialDelay delay,
        RecordingOperationsMetrics metrics) : IAsyncDisposable
    {
        internal ServiceProvider Provider { get; } = provider;

        internal SocialRefreshJobStartupService Startup { get; } = startup;

        internal SharedJobState Jobs { get; } = jobs;

        internal SharedSnapshotState Snapshots { get; } = snapshots;

        internal ControlledSocialDelay Delay { get; } = delay;

        internal RecordingOperationsMetrics Metrics { get; } = metrics;

        public async ValueTask DisposeAsync()
        {
            await Startup.StopAsync(CancellationToken.None);
            await Provider.DisposeAsync();
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ControlledSocialDelay : ISocialDelay
    {
        private readonly Channel<bool> releases =
            Channel.CreateUnbounded<bool>();
        private readonly Channel<TimeSpan> observations =
            Channel.CreateUnbounded<TimeSpan>();
        private int cancellationCount;

        internal List<TimeSpan> RequestedDelays { get; } = [];

        internal int CancellationCount => Volatile.Read(ref cancellationCount);

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            lock (RequestedDelays)
            {
                RequestedDelays.Add(delay);
            }

            await observations.Writer.WriteAsync(delay, cancellationToken);
            try
            {
                await releases.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref cancellationCount);
                throw;
            }
        }

        internal void ReleaseNext() =>
            Assert.True(releases.Writer.TryWrite(true));

        internal Task<TimeSpan> WaitForDelayAsync() =>
            observations.Reader.ReadAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class RecordingOperationsMetrics : IOperationsMetrics
    {
        private readonly object sync = new();

        internal IReadOnlyList<string> JobOutcomes
        {
            get
            {
                lock (sync)
                {
                    return [.. jobOutcomes];
                }
            }
        }

        private List<string> jobOutcomes { get; } = [];

        public void RecordDependency(
            string dependency,
            string outcome,
            double durationMilliseconds = 0)
        {
        }

        public void RecordJob(
            string definition,
            string outcome,
            int attempt)
        {
            lock (sync)
            {
                jobOutcomes.Add(outcome);
            }
        }

        public void RecordStaleness(string dataSet, TimeSpan age)
        {
        }
    }

    private sealed class PersistingOutcomeRefreshService(
        SocialRefreshOutcome outcome,
        SharedSnapshotState snapshots) : ISocialFeedRefreshService
    {
        internal int Calls { get; private set; }

        public Task<SocialRefreshReceipt> RefreshAsync(
            string provider,
            CancellationToken cancellationToken,
            TimeSpan? terminalRecoveryDelay = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            switch (outcome)
            {
                case SocialRefreshOutcome.Refreshed:
                    snapshots.Set(
                        Feed(Now.AddHours(2), version: 2),
                        SuccessState());
                    break;
                case SocialRefreshOutcome.RateLimited:
                    snapshots.Set(
                        snapshots.Active,
                        FailureState(
                            SocialRefreshError.RateLimited,
                            Now.AddMinutes(7)));
                    break;
                case SocialRefreshOutcome.Unavailable:
                    Assert.Equal(
                        TimeSpan.FromMinutes(15),
                        terminalRecoveryDelay);
                    snapshots.Set(
                        snapshots.Active,
                        FailureState(
                            SocialRefreshError.Unavailable,
                            Now.AddMinutes(15)));
                    break;
                default:
                    throw new InvalidOperationException(
                        "The scripted refresh outcome is unsupported.");
            }

            return Task.FromResult(new SocialRefreshReceipt(
                outcome,
                outcome == SocialRefreshOutcome.Refreshed ? 2 : null,
                0,
                0,
                Now));
        }
    }

    private sealed class BlockingThenSuccessfulRefreshService(
        SharedSnapshotState snapshots) : ISocialFeedRefreshService
    {
        internal TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Calls { get; private set; }

        public async Task<SocialRefreshReceipt> RefreshAsync(
            string provider,
            CancellationToken cancellationToken,
            TimeSpan? terminalRecoveryDelay = null)
        {
            Calls++;
            if (Calls == 1)
            {
                FirstCallStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }

            snapshots.Set(
                Feed(Now.AddHours(2), version: 2),
                SuccessState());
            return new SocialRefreshReceipt(
                SocialRefreshOutcome.Refreshed,
                2,
                0,
                0,
                Now);
        }
    }

    private sealed class SharedSnapshotState(
        StoredSocialFeed? active,
        SocialRefreshState? refreshState)
    {
        private readonly object sync = new();
        private StoredSocialFeed? active = active;
        private SocialRefreshState? refreshState = refreshState;
        private int storeInstances;

        internal StoredSocialFeed? Active
        {
            get
            {
                lock (sync)
                {
                    return active;
                }
            }
        }

        internal int StoreInstances => Volatile.Read(ref storeInstances);

        internal void AddStoreInstance() =>
            Interlocked.Increment(ref storeInstances);

        internal SocialRefreshSchedulingState ReadSchedulingState()
        {
            lock (sync)
            {
                return new SocialRefreshSchedulingState(
                    active?.Version,
                    active?.ExpiresAtUtc,
                    refreshState);
            }
        }

        internal void Set(
            StoredSocialFeed? value,
            SocialRefreshState? state)
        {
            lock (sync)
            {
                active = value;
                refreshState = state;
            }
        }
    }

    private sealed class ScopedSnapshotStore : ISocialSnapshotStore
    {
        private readonly SharedSnapshotState state;

        public ScopedSnapshotStore(SharedSnapshotState state)
        {
            this.state = state;
            state.AddStoreInstance();
        }

        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(state.Active);

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(state.ReadSchedulingState().RefreshState);

        public Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.ReadSchedulingState());
        }

        public Task<long> ReplaceAsync(
            NormalizedSocialFeed feed,
            int retainedVersions,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordFailureAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset? retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeferUntilAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record RequeueInvocation(
        string Actor,
        string Reason,
        string CorrelationId);

    private sealed class SharedJobState
    {
        private readonly object sync = new();
        private readonly Dictionary<Guid, JobRecord> jobs = [];
        private readonly Dictionary<string, Guid> jobsByIdempotency =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<string> newJobEnqueued =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private JobDefinitionRegistration? registration;
        private int enqueueFailuresRemaining;
        private int registrationFailuresRemaining;
        private int storeInstances;

        internal TaskCompletionSource Requeued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<RequeueInvocation> Requeues { get; } = [];

        internal int StoreInstances => Volatile.Read(ref storeInstances);

        internal int EnqueueFailuresRemaining
        {
            get
            {
                lock (sync)
                {
                    return enqueueFailuresRemaining;
                }
            }
            set
            {
                lock (sync)
                {
                    enqueueFailuresRemaining = value;
                }
            }
        }

        internal int RegistrationFailuresRemaining
        {
            get
            {
                lock (sync)
                {
                    return registrationFailuresRemaining;
                }
            }
            set
            {
                lock (sync)
                {
                    registrationFailuresRemaining = value;
                }
            }
        }

        internal IReadOnlyList<string> IdempotencyKeys
        {
            get
            {
                lock (sync)
                {
                    return [.. jobsByIdempotency.Keys];
                }
            }
        }

        internal void AddStoreInstance() =>
            Interlocked.Increment(ref storeInstances);

        internal void Seed(
            Guid id,
            string idempotencyKey,
            string payloadJson,
            int attemptsStarted,
            DateTimeOffset nextRunAtUtc)
        {
            lock (sync)
            {
                jobs.Add(
                    id,
                    new JobRecord(
                        id,
                        idempotencyKey,
                        payloadJson,
                        "social-job-correlation",
                        "Pending",
                        attemptsStarted,
                        nextRunAtUtc));
                jobsByIdempotency.Add(idempotencyKey, id);
            }
        }

        internal JobStateSnapshot GetSnapshot(Guid id)
        {
            lock (sync)
            {
                return jobs[id].Snapshot();
            }
        }

        internal Task<string> WaitForNewJobAsync() =>
            newJobEnqueued.Task.WaitAsync(TimeSpan.FromSeconds(2));

        internal Result<Guid, JobStoreError> Register(
            JobDefinitionRegistration value)
        {
            lock (sync)
            {
                if (registrationFailuresRemaining > 0)
                {
                    registrationFailuresRemaining--;
                    return Result.Fail<Guid, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.PersistenceFailure,
                            "Scripted registration failure."));
                }

                registration = value;
                return Result.Succeed<Guid, JobStoreError>(Guid.NewGuid());
            }
        }

        internal Result<JobEnqueueReceipt, JobStoreError> Enqueue(
            JobEnqueueRequest request,
            DateTimeOffset now)
        {
            lock (sync)
            {
                if (enqueueFailuresRemaining > 0)
                {
                    enqueueFailuresRemaining--;
                    return Result.Fail<JobEnqueueReceipt, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.PersistenceFailure,
                            "Scripted enqueue failure."));
                }

                if (jobsByIdempotency.TryGetValue(
                        request.IdempotencyKey,
                        out var existingId))
                {
                    return string.Equals(
                            jobs[existingId].PayloadJson,
                            request.PayloadJson,
                            StringComparison.Ordinal)
                        ? Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                            new JobEnqueueReceipt(existingId, true))
                        : Result.Fail<JobEnqueueReceipt, JobStoreError>(
                            new JobStoreError(
                                JobStoreErrorCode.DuplicateConflict,
                                "The payload conflicts with the existing job."));
                }

                var id = Guid.NewGuid();
                jobs.Add(
                    id,
                    new JobRecord(
                        id,
                        request.IdempotencyKey,
                        request.PayloadJson,
                        request.CorrelationId,
                        "Pending",
                        0,
                        request.NotBeforeUtc ?? now));
                jobsByIdempotency.Add(request.IdempotencyKey, id);
                newJobEnqueued.TrySetResult(request.IdempotencyKey);
                return Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                    new JobEnqueueReceipt(id, false));
            }
        }

        internal Result<JobAcquireResult, JobStoreError> TryAcquire(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            string? definitionKey)
        {
            lock (sync)
            {
                if (registration is null ||
                    definitionKey is not null &&
                    !string.Equals(
                        definitionKey,
                        registration.Key,
                        StringComparison.Ordinal))
                {
                    return Result.Succeed<JobAcquireResult, JobStoreError>(
                        new JobAcquireResult(null));
                }

                var job = jobs.Values
                    .Where(value =>
                        value.State is "Pending" or "RetryScheduled" &&
                        value.NextRunAtUtc <= now)
                    .OrderBy(value => value.NextRunAtUtc)
                    .ThenBy(value => value.Id)
                    .FirstOrDefault();
                if (job is null)
                {
                    return Result.Succeed<JobAcquireResult, JobStoreError>(
                        new JobAcquireResult(null));
                }

                job.State = "Running";
                job.AttemptsStarted++;
                job.LeaseToken = Guid.NewGuid();
                job.LeaseExpiresAtUtc = now.Add(leaseDuration);
                return Result.Succeed<JobAcquireResult, JobStoreError>(
                    new JobAcquireResult(
                        new AcquiredJob(
                            job.Id,
                            registration.Key,
                            registration.HandlerName,
                            job.PayloadJson,
                            job.IdempotencyKey,
                            job.CorrelationId,
                            job.AttemptsStarted,
                            registration.MaximumAttempts,
                            registration.InitialBackoff,
                            registration.MaximumBackoff,
                            worker,
                            job.LeaseToken.Value,
                            job.LeaseExpiresAtUtc.Value,
                            job.CancellationRequested)));
            }
        }

        internal Result<bool, JobStoreError> Renew(
            Guid id,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now)
        {
            lock (sync)
            {
                if (!HasLease(id, leaseToken, out var job))
                {
                    return Result.Fail<bool, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.LeaseLost,
                            "The lease was lost."));
                }

                job.LeaseExpiresAtUtc = now.Add(leaseDuration);
                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<bool, JobStoreError> Complete(
            Guid id,
            Guid leaseToken)
        {
            lock (sync)
            {
                if (!HasLease(id, leaseToken, out var job))
                {
                    return LeaseLost();
                }

                job.State = job.CancellationRequested
                    ? "Cancelled"
                    : "Completed";
                job.LeaseToken = null;
                job.LeaseExpiresAtUtc = null;
                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<bool, JobStoreError> Fail(
            Guid id,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc)
        {
            lock (sync)
            {
                if (!HasLease(id, leaseToken, out var job))
                {
                    return LeaseLost();
                }

                job.State = string.Equals(
                        errorCode,
                        "Cancelled",
                        StringComparison.Ordinal)
                    ? "Cancelled"
                    : nextRunAtUtc.HasValue
                        ? "RetryScheduled"
                        : "DeadLettered";
                if (nextRunAtUtc.HasValue)
                {
                    job.NextRunAtUtc = nextRunAtUtc.Value;
                }

                job.LastErrorCode = errorCode;
                job.LeaseToken = null;
                job.LeaseExpiresAtUtc = null;
                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<bool, JobStoreError> ReleaseForRecovery(
            Guid id,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc)
        {
            lock (sync)
            {
                if (!HasLease(id, leaseToken, out var job))
                {
                    return LeaseLost();
                }

                if (job.CancellationRequested)
                {
                    job.State = "Cancelled";
                }
                else if (registration is not null &&
                    job.AttemptsStarted >= registration.MaximumAttempts)
                {
                    job.State = "DeadLettered";
                    job.LastErrorCode = "WorkerStopping";
                }
                else
                {
                    job.State = "RetryScheduled";
                    job.NextRunAtUtc = nextRunAtUtc;
                }

                job.LeaseToken = null;
                job.LeaseExpiresAtUtc = null;
                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<bool, JobStoreError> RequestCancellation(Guid id)
        {
            lock (sync)
            {
                if (!jobs.TryGetValue(id, out var job))
                {
                    return Result.Fail<bool, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.InvalidState,
                            "The job was not found."));
                }

                job.CancellationRequested = true;
                if (job.State is "Pending" or "RetryScheduled")
                {
                    job.State = "Cancelled";
                }

                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<bool, JobStoreError> Requeue(
            Guid id,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now)
        {
            lock (sync)
            {
                if (!jobs.TryGetValue(id, out var job) ||
                    !string.Equals(
                        job.State,
                        "DeadLettered",
                        StringComparison.Ordinal))
                {
                    return Result.Fail<bool, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.InvalidState,
                            "Only dead-lettered jobs can be requeued."));
                }

                job.State = "RetryScheduled";
                job.AttemptsStarted = 0;
                job.NextRunAtUtc = now;
                job.LastErrorCode = null;
                job.CancellationRequested = false;
                Requeues.Add(new RequeueInvocation(
                    actor,
                    reason,
                    correlationId));
                Requeued.TrySetResult();
                return Result.Succeed<bool, JobStoreError>(true);
            }
        }

        internal Result<JobStateLookup, JobStoreError> GetState(Guid id)
        {
            lock (sync)
            {
                return Result.Succeed<JobStateLookup, JobStoreError>(
                    new JobStateLookup(
                        jobs.TryGetValue(id, out var job)
                            ? job.Snapshot()
                            : null));
            }
        }

        private bool HasLease(
            Guid id,
            Guid leaseToken,
            out JobRecord job)
        {
            if (jobs.TryGetValue(id, out job!) &&
                string.Equals(
                    job.State,
                    "Running",
                    StringComparison.Ordinal) &&
                job.LeaseToken == leaseToken)
            {
                return true;
            }

            job = null!;
            return false;
        }

        private static Result<bool, JobStoreError> LeaseLost() =>
            Result.Fail<bool, JobStoreError>(
                new JobStoreError(
                    JobStoreErrorCode.LeaseLost,
                    "The lease was lost."));
    }

    private sealed class JobRecord(
        Guid id,
        string idempotencyKey,
        string payloadJson,
        string correlationId,
        string state,
        int attemptsStarted,
        DateTimeOffset nextRunAtUtc)
    {
        internal Guid Id { get; } = id;

        internal string IdempotencyKey { get; } = idempotencyKey;

        internal string PayloadJson { get; } = payloadJson;

        internal string CorrelationId { get; } = correlationId;

        internal string State { get; set; } = state;

        internal int AttemptsStarted { get; set; } = attemptsStarted;

        internal DateTimeOffset NextRunAtUtc { get; set; } = nextRunAtUtc;

        internal Guid? LeaseToken { get; set; }

        internal DateTimeOffset? LeaseExpiresAtUtc { get; set; }

        internal bool CancellationRequested { get; set; }

        internal string? LastErrorCode { get; set; }

        internal JobStateSnapshot Snapshot() =>
            new(
                Id,
                State,
                AttemptsStarted,
                NextRunAtUtc,
                LeaseExpiresAtUtc,
                CancellationRequested,
                LastErrorCode);
    }

    private sealed class ScopedLivenessJobStore : IDurableJobStore
    {
        private readonly SharedJobState state;

        public ScopedLivenessJobStore(SharedJobState state)
        {
            this.state = state;
            state.AddStoreInstance();
        }

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Register(registration));
        }

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Enqueue(request, now));
        }

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.TryAcquire(
                worker,
                leaseDuration,
                now,
                definitionKey));
        }

        public Task<Result<bool, JobStoreError>> RenewAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Renew(
                jobInstanceId,
                leaseToken,
                leaseDuration,
                now));
        }

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Complete(
                jobInstanceId,
                leaseToken));
        }

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Fail(
                jobInstanceId,
                leaseToken,
                errorCode,
                nextRunAtUtc));
        }

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.ReleaseForRecovery(
                jobInstanceId,
                leaseToken,
                nextRunAtUtc));
        }

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.RequestCancellation(jobInstanceId));
        }

        public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
            Guid jobInstanceId,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.Requeue(
                jobInstanceId,
                actor,
                reason,
                correlationId,
                now));
        }

        public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
            Guid jobInstanceId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state.GetState(jobInstanceId));
        }
    }
}
