using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Tests.Social;

public sealed class SocialRefreshJobTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CatchUpRegistersStableDefinitionAndEnqueuesIdempotently()
    {
        var jobs = new RecordingJobStore();
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            snapshots,
            jobs,
            new FixedCorrelationContext("social-catch-up"),
            new FixedTimeProvider(Now));

        var first = await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        var second = await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        Assert.All(jobs.Definitions, registration =>
        {
            Assert.Equal(SocialRefreshJobDefinition.Key, registration.Key);
            Assert.Equal(SocialRefreshJobDefinition.Handler, registration.HandlerName);
            Assert.Equal(3, registration.MaximumAttempts);
            Assert.Equal(TimeSpan.FromMinutes(1), registration.InitialBackoff);
            Assert.Equal(TimeSpan.FromMinutes(15), registration.MaximumBackoff);
        });
        Assert.Equal(2, jobs.Enqueues.Count);
        Assert.False(first.Single().Receipt.IsDuplicate);
        Assert.True(second.Single().Receipt.IsDuplicate);
        Assert.Equal(
            jobs.Enqueues[0].IdempotencyKey,
            jobs.Enqueues[1].IdempotencyKey);
        Assert.Equal("social-catch-up", jobs.Enqueues[0].CorrelationId);
        Assert.Equal("""{"provider":"facebook"}""", jobs.Enqueues[0].PayloadJson);
    }

    [Fact]
    public async Task CatchUpSchedulesHealthySnapshotAtItsExpiry()
    {
        var jobs = new RecordingJobStore();
        var expiresAtUtc = Now.AddHours(1);
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            new StaticSnapshotStore(Feed(expiresAtUtc)),
            jobs,
            new FixedCorrelationContext("social-catch-up"),
            new FixedTimeProvider(Now));

        var result = await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        Assert.Single(result);
        Assert.Single(jobs.Definitions);
        var enqueue = Assert.Single(jobs.Enqueues);
        Assert.Equal(expiresAtUtc, enqueue.NotBeforeUtc);
        Assert.Contains(":v1:e", enqueue.IdempotencyKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchUpReactivatesMissingSnapshotWithRecoveryGeneration()
    {
        var retryAfter = Now.AddMinutes(10);
        var jobs = new RecordingJobStore();
        var snapshots = new StaticSnapshotStore(
            null,
            FailureState(
                SocialRefreshError.Unavailable,
                Now.AddMinutes(-5),
                retryAfter));
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            snapshots,
            jobs,
            new FixedCorrelationContext("social-catch-up"),
            new FixedTimeProvider(Now));

        await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        var enqueue = Assert.Single(jobs.Enqueues);
        Assert.Equal(retryAfter, enqueue.NotBeforeUtc);
        Assert.Equal(RecoveryKey(retryAfter), enqueue.IdempotencyKey);
    }

    [Fact]
    public async Task CatchUpClampsLegacyFarFutureRecoveryState()
    {
        var attemptedAtUtc = Now.AddDays(-2);
        var maximumRecoveryUtc = attemptedAtUtc.AddHours(24);
        var jobs = new RecordingJobStore();
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            new StaticSnapshotStore(
                null,
                FailureState(
                    SocialRefreshError.RateLimited,
                    attemptedAtUtc,
                    new DateTimeOffset(
                        2099,
                        1,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero))),
            jobs,
            new FixedCorrelationContext("social-catch-up"),
            new FixedTimeProvider(Now));

        await coordinator.RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        var enqueue = Assert.Single(jobs.Enqueues);
        Assert.Equal(Now, enqueue.NotBeforeUtc);
        Assert.Equal(RecoveryKey(maximumRecoveryUtc), enqueue.IdempotencyKey);
    }

    [Fact]
    public async Task CatchUpRequeuesDuplicateDeadLetteredCanonicalJob()
    {
        var expiresAtUtc = Now.AddMinutes(-1);
        var idempotencyKey = SnapshotKey(1, expiresAtUtc);
        var jobInstanceId = Guid.NewGuid();
        var jobs = new RecordingJobStore();
        jobs.Seed(jobInstanceId, idempotencyKey);
        jobs.States[jobInstanceId] = new JobStateSnapshot(
            jobInstanceId,
            "DeadLettered",
            3,
            expiresAtUtc,
            null,
            false,
            "WorkerStopping");
        var coordinator = Coordinator(
            new StaticSnapshotStore(Feed(expiresAtUtc)),
            jobs);

        var result = await coordinator.EnsureScheduledAsync(
            "facebook",
            CancellationToken.None);

        Assert.True(result.Receipt.IsDuplicate);
        var requeue = Assert.Single(jobs.Requeues);
        Assert.Equal(jobInstanceId, requeue.JobInstanceId);
        Assert.Equal("social-refresh-repair", requeue.Actor);
        Assert.Equal(
            "Restore the canonical recurring social refresh job.",
            requeue.Reason);
        Assert.Equal("social-handler", requeue.CorrelationId);
        Assert.Equal(Now, requeue.OccurredAtUtc);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Cancelled")]
    public async Task CatchUpDoesNotRequeueCompletedOrCancelledDuplicate(
        string state)
    {
        var expiresAtUtc = Now.AddMinutes(-1);
        var idempotencyKey = SnapshotKey(1, expiresAtUtc);
        var jobInstanceId = Guid.NewGuid();
        var jobs = new RecordingJobStore();
        jobs.Seed(jobInstanceId, idempotencyKey);
        jobs.States[jobInstanceId] = new JobStateSnapshot(
            jobInstanceId,
            state,
            3,
            expiresAtUtc,
            null,
            state == "Cancelled",
            null);

        var result = await Coordinator(
                new StaticSnapshotStore(Feed(expiresAtUtc)),
                jobs)
            .EnsureScheduledAsync("facebook", CancellationToken.None);

        Assert.True(result.Receipt.IsDuplicate);
        Assert.Empty(jobs.Requeues);
    }

    [Fact]
    public async Task CatchUpDoesNotRequeueDuplicateThatIsNoLongerCanonical()
    {
        var firstExpiry = Now.AddMinutes(-1);
        var secondExpiry = Now.AddHours(1);
        var firstKey = SnapshotKey(1, firstExpiry);
        var jobInstanceId = Guid.NewGuid();
        var jobs = new RecordingJobStore();
        jobs.Seed(jobInstanceId, firstKey);
        jobs.States[jobInstanceId] = new JobStateSnapshot(
            jobInstanceId,
            "DeadLettered",
            3,
            firstExpiry,
            null,
            false,
            "WorkerStopping");
        var coordinator = new SocialRefreshJobCoordinator(
            ["facebook"],
            new SequencedSnapshotStore(
                new SocialRefreshSchedulingState(1, firstExpiry, null),
                new SocialRefreshSchedulingState(2, secondExpiry, null)),
            jobs,
            new FixedCorrelationContext("social-handler"),
            new FixedTimeProvider(Now));

        var result = await coordinator.EnsureScheduledAsync(
            "facebook",
            CancellationToken.None);

        Assert.True(result.Receipt.IsDuplicate);
        Assert.Equal(firstKey, Assert.Single(jobs.Enqueues).IdempotencyKey);
        Assert.Empty(jobs.Requeues);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"provider":"facebook","unexpected":true}""")]
    [InlineData("""{"Provider":"facebook"}""")]
    [InlineData("""{"provider":"facebook","provider":"facebook"}""")]
    [InlineData("""{"provider":"not valid"}""")]
    public async Task HandlerRejectsInvalidPayloadBeforeCallingRefresh(string payload)
    {
        var refresh = new RecordingRefreshService(SocialRefreshOutcome.Refreshed);
        var handler = Handler(refresh);

        var result = await handler.ExecuteAsync(
            payload,
            Context(),
            CancellationToken.None);

        Assert.Equal(0, refresh.Calls);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidSocialRefreshPayload", result.ErrorCode);
    }

    [Fact]
    public async Task HandlerInvokesRefreshWithValidatedProvider()
    {
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddHours(2)));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Refreshed,
            () => snapshots.Active = Feed(
                expiresAtUtc: Now.AddHours(2),
                version: 2));
        var handler = Handler(refresh, snapshots, jobs);
        var context = Context(SnapshotKey(1, Now.AddHours(2)));
        jobs.Seed(context.JobInstanceId, context.IdempotencyKey);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("FACEBOOK"),
            context,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal("facebook", refresh.Provider);
        var successor = Assert.Single(jobs.Enqueues);
        Assert.Equal(Now.AddHours(2), successor.NotBeforeUtc);
        Assert.Equal(SnapshotKey(2, Now.AddHours(2)), successor.IdempotencyKey);
    }

    [Fact]
    public async Task SuccessorStartupCrashReplayAndNextGenerationRemainCanonical()
    {
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var jobs = new RecordingJobStore();
        var version = 1L;
        var expiry = Now.AddHours(2);
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Refreshed,
            () =>
            {
                snapshots.Active = Feed(expiry, ++version);
                snapshots.State = SuccessState(Now);
                expiry = expiry.AddHours(2);
            });
        var handler = Handler(refresh, snapshots, jobs);
        var current = Context(SnapshotKey(1, Now.AddMinutes(-1)));
        jobs.Seed(current.JobInstanceId, current.IdempotencyKey);

        var first = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);
        var startup = await Coordinator(snapshots, jobs)
            .RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        var repeated = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal(2, snapshots.Active!.Version);
        Assert.Equal(3, jobs.Enqueues.Count);
        Assert.All(
            jobs.Enqueues,
            enqueue => Assert.Equal(
                SnapshotKey(2, Now.AddHours(2)),
                enqueue.IdempotencyKey));
        Assert.False(jobs.Receipts[0].IsDuplicate);
        Assert.True(jobs.Receipts[1].IsDuplicate);
        Assert.True(jobs.Receipts[2].IsDuplicate);
        Assert.True(startup.Single().Receipt.IsDuplicate);
        Assert.Equal(
            jobs.Receipts[0].JobInstanceId,
            jobs.Receipts[1].JobInstanceId);
        Assert.Equal(
            jobs.Receipts[0].JobInstanceId,
            jobs.Receipts[2].JobInstanceId);
        Assert.Equal(2, jobs.UniqueJobCount);

        var successor = Context(
            jobs.Enqueues[0].IdempotencyKey,
            jobs.Receipts[0].JobInstanceId);
        var next = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            successor,
            CancellationToken.None);

        Assert.True(next.IsSuccess);
        Assert.Equal(2, refresh.Calls);
        Assert.Equal(3, snapshots.Active.Version);
        Assert.Equal(4, jobs.Enqueues.Count);
        Assert.False(jobs.Receipts[3].IsDuplicate);
        Assert.NotEqual(
            jobs.Receipts[0].JobInstanceId,
            jobs.Receipts[3].JobInstanceId);
        Assert.Equal(
            SnapshotKey(3, Now.AddHours(4)),
            jobs.Enqueues[3].IdempotencyKey);
    }

    [Fact]
    public async Task RateLimitSuccessorAndFreshStartupUseOneRecoveryJob()
    {
        var retryAfter = Now.AddMinutes(7);
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.RateLimited,
            () => snapshots.State = FailureState(
                SocialRefreshError.RateLimited,
                Now,
                retryAfter));
        var handler = Handler(refresh, snapshots, jobs);
        var current = Context(SnapshotKey(1, Now.AddMinutes(-1)));
        jobs.Seed(current.JobInstanceId, current.IdempotencyKey);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);
        var startup = await Coordinator(snapshots, jobs)
            .RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        var replay = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal(2, jobs.UniqueJobCount);
        Assert.Equal(3, jobs.Enqueues.Count);
        Assert.All(
            jobs.Enqueues,
            enqueue => Assert.Equal(
                RecoveryKey(retryAfter),
                enqueue.IdempotencyKey));
        Assert.False(jobs.Receipts[0].IsDuplicate);
        Assert.True(startup.Single().Receipt.IsDuplicate);
        Assert.True(jobs.Receipts[2].IsDuplicate);
    }

    [Fact]
    public async Task TerminalRecoverySuccessorAndFreshStartupUseOneRecoveryJob()
    {
        var recoveryAfter = Now.AddMinutes(15);
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Unavailable,
            onRefreshWithDelay: delay =>
            {
                Assert.Equal(TimeSpan.FromMinutes(15), delay);
                snapshots.State = FailureState(
                    SocialRefreshError.Unavailable,
                    Now,
                    recoveryAfter);
            });
        var handler = Handler(refresh, snapshots, jobs);
        var current = Context(
            SnapshotKey(1, Now.AddMinutes(-1)),
            attemptNumber: 3);
        jobs.Seed(current.JobInstanceId, current.IdempotencyKey);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);
        var startup = await Coordinator(snapshots, jobs)
            .RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        var replay = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            current,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("SocialRefreshUnavailable", result.ErrorCode);
        Assert.False(replay.IsSuccess);
        Assert.Equal("SocialRefreshUnavailable", replay.ErrorCode);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal(2, jobs.UniqueJobCount);
        Assert.Equal(3, jobs.Enqueues.Count);
        Assert.All(
            jobs.Enqueues,
            enqueue => Assert.Equal(
                RecoveryKey(recoveryAfter),
                enqueue.IdempotencyKey));
        Assert.False(jobs.Receipts[0].IsDuplicate);
        Assert.True(startup.Single().Receipt.IsDuplicate);
        Assert.True(jobs.Receipts[2].IsDuplicate);
    }

    [Fact]
    public async Task RecoveryRetryAndFreshStartupRetainTheSamePastGeneration()
    {
        var recoveryAnchor = Now.AddMinutes(-1);
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddHours(-1)),
            FailureState(
                SocialRefreshError.Unavailable,
                Now.AddMinutes(-16),
                recoveryAnchor));
        var jobs = new RecordingJobStore();
        var initial = await Coordinator(snapshots, jobs)
            .RegisterAndEnqueueCatchUpAsync(CancellationToken.None);
        var currentReceipt = initial.Single().Receipt;
        var currentKey = Assert.Single(jobs.Enqueues).IdempotencyKey;
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Unavailable,
            () => snapshots.State = FailureState(
                SocialRefreshError.Unavailable,
                Now,
                recoveryAnchor));
        var handler = Handler(refresh, snapshots, jobs);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            Context(currentKey, currentReceipt.JobInstanceId),
            CancellationToken.None);
        var startup = await Coordinator(snapshots, jobs)
            .RegisterAndEnqueueCatchUpAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal(recoveryAnchor, snapshots.State!.RetryAfterUtc);
        Assert.Equal(1, jobs.UniqueJobCount);
        Assert.Equal(3, jobs.Enqueues.Count);
        Assert.All(
            jobs.Enqueues,
            enqueue => Assert.Equal(currentKey, enqueue.IdempotencyKey));
        Assert.True(jobs.Receipts[1].IsDuplicate);
        Assert.True(startup.Single().Receipt.IsDuplicate);
    }

    [Fact]
    public async Task LegacyRecoveryAliasEnqueuesV2AndCompletesWithoutProviderCall()
    {
        var recoveryAfter = Now.AddMinutes(8);
        var snapshots = new StaticSnapshotStore(
            null,
            FailureState(
                SocialRefreshError.Unavailable,
                Now.AddMinutes(-7),
                recoveryAfter));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Unavailable);
        var handler = Handler(refresh, snapshots, jobs);
        var legacyKey =
            $"social-refresh:facebook:recovery:{Now.AddMinutes(-7).UtcTicks}:" +
            $"not-before:{recoveryAfter.UtcTicks}";
        var context = Context(legacyKey);
        jobs.Seed(context.JobInstanceId, context.IdempotencyKey);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            context,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, refresh.Calls);
        Assert.Equal(
            RecoveryKey(recoveryAfter),
            Assert.Single(jobs.Enqueues).IdempotencyKey);
    }

    [Fact]
    public async Task LegacyGenerationUsesScheduledMarkerAndEmitsCanonicalSuccessor()
    {
        var scheduledMarker = Now.AddMinutes(-1);
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: scheduledMarker),
            SuccessState(Now.AddHours(-1)));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Refreshed,
            () =>
            {
                snapshots.Active = Feed(Now.AddHours(2), version: 2);
                snapshots.State = SuccessState(Now);
            });
        var handler = Handler(refresh, snapshots, jobs);
        var context = Context(
            $"social-refresh:facebook:generation:{Guid.NewGuid():N}");
        jobs.Seed(context.JobInstanceId, context.IdempotencyKey);
        jobs.States[context.JobInstanceId] = JobState(
            context.JobInstanceId,
            scheduledMarker);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            context,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, refresh.Calls);
        Assert.Equal(
            SnapshotKey(2, Now.AddHours(2)),
            Assert.Single(jobs.Enqueues).IdempotencyKey);
    }

    [Fact]
    public async Task ProcessedLegacyGenerationRepairsCanonicalSuccessorWithoutRefresh()
    {
        var scheduledMarker = Now.AddMinutes(-1);
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddHours(2), version: 2),
            SuccessState(Now));
        var jobs = new RecordingJobStore();
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Refreshed);
        var handler = Handler(refresh, snapshots, jobs);
        var context = Context(
            $"social-refresh:facebook:generation:{Guid.NewGuid():N}");
        jobs.Seed(context.JobInstanceId, context.IdempotencyKey);
        jobs.States[context.JobInstanceId] = JobState(
            context.JobInstanceId,
            scheduledMarker);

        var result = await handler.ExecuteAsync(
            SocialRefreshJobPayload.Serialize("facebook"),
            context,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, refresh.Calls);
        Assert.Equal(
            SnapshotKey(2, Now.AddHours(2)),
            Assert.Single(jobs.Enqueues).IdempotencyKey);
    }

    [Fact]
    public async Task ProcessorCompletesSuccessfulRefreshAndFlowsEnqueuedCorrelation()
    {
        var correlation = new CorrelationContext();
        CorrelationSnapshot? observed = null;
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddHours(1)));
        var refresh = new RecordingRefreshService(
            SocialRefreshOutcome.Refreshed,
            () =>
            {
                observed = correlation.Current;
                snapshots.Active = Feed(Now.AddHours(2), version: 2);
                snapshots.State = SuccessState(Now);
            });
        var store = new RecordingJobStore
        {
            AcquiredJob = Job(
                attempt: 1,
                maximumAttempts: 3,
                idempotencyKey: SnapshotKey(1, Now.AddHours(1))),
        };
        var processor = Processor(
            store,
            [Handler(refresh, snapshots, store, correlation)],
            correlation);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("social-worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.True(store.Completed);
        Assert.Equal("facebook", refresh.Provider);
        Assert.Equal("social-job-correlation", observed!.CorrelationId);
        Assert.Equal(
            "social-job-correlation",
            Assert.Single(store.Enqueues).CorrelationId);
    }

    [Theory]
    [InlineData(SocialRefreshOutcome.Unavailable, "SocialRefreshUnavailable")]
    [InlineData(SocialRefreshOutcome.Cancelled, "SocialRefreshCancelled")]
    public async Task ProcessorSchedulesRetryForRefreshFailureOrCancellation(
        SocialRefreshOutcome refreshOutcome,
        string expectedErrorCode)
    {
        var store = new RecordingJobStore
        {
            AcquiredJob = Job(
                attempt: 1,
                maximumAttempts: 3,
                idempotencyKey: SnapshotKey(1, Now.AddMinutes(-1))),
        };
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var correlation = new CorrelationContext();
        var processor = Processor(
            store,
            [Handler(
                new RecordingRefreshService(
                    refreshOutcome,
                    () => snapshots.State = FailureState(
                        refreshOutcome == SocialRefreshOutcome.Unavailable
                            ? SocialRefreshError.Unavailable
                            : SocialRefreshError.Cancelled,
                        Now)),
                snapshots,
                store,
                correlation)],
            correlation);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("social-worker"),
            CancellationToken.None);

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.Equal(expectedErrorCode, store.LastFailureCode);
        Assert.NotNull(store.NextRunAtUtc);
        Assert.Single(store.Enqueues);
        Assert.True(Assert.Single(store.Receipts).IsDuplicate);
    }

    [Fact]
    public async Task ProcessorDeadLettersExhaustedFailureAndSchedulesRecoveryGeneration()
    {
        var store = new RecordingJobStore
        {
            AcquiredJob = Job(
                attempt: 3,
                maximumAttempts: 3,
                idempotencyKey: SnapshotKey(1, Now.AddMinutes(-1))),
        };
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)));
        var correlation = new CorrelationContext();
        var processor = Processor(
            store,
            [Handler(
                new RecordingRefreshService(
                    SocialRefreshOutcome.Unavailable,
                    onRefreshWithDelay: delay =>
                    {
                        Assert.Equal(TimeSpan.FromMinutes(15), delay);
                        snapshots.State = FailureState(
                            SocialRefreshError.Unavailable,
                            Now,
                            Now.AddMinutes(15));
                    }),
                snapshots,
                store,
                correlation)],
            correlation);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("social-worker"),
            CancellationToken.None);

        Assert.Equal("DeadLettered", outcome.FinalState);
        Assert.Equal("SocialRefreshUnavailable", store.LastFailureCode);
        Assert.Null(store.NextRunAtUtc);
        var recovery = Assert.Single(store.Enqueues);
        Assert.Equal(Now.AddMinutes(15), recovery.NotBeforeUtc);
        Assert.Equal(RecoveryKey(Now.AddMinutes(15)), recovery.IdempotencyKey);
        Assert.Equal("social-job-correlation", recovery.CorrelationId);
    }

    [Fact]
    public async Task ProcessorCompletesRateLimitedJobAndSchedulesRetryAfter()
    {
        var retryAfter = Now.AddMinutes(7);
        var store = new RecordingJobStore
        {
            AcquiredJob = Job(
                attempt: 1,
                maximumAttempts: 3,
                idempotencyKey: RecoveryKey(retryAfter)),
        };
        var snapshots = new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddMinutes(-1)),
            FailureState(SocialRefreshError.RateLimited, Now, retryAfter));
        var correlation = new CorrelationContext();
        var processor = Processor(
            store,
            [Handler(
                new RecordingRefreshService(SocialRefreshOutcome.RateLimited),
                snapshots,
                store,
                correlation)],
            correlation);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("social-worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.True(store.Completed);
        Assert.Null(store.LastFailureCode);
        var recovery = Assert.Single(store.Enqueues);
        Assert.Equal(retryAfter, recovery.NotBeforeUtc);
        Assert.Equal(RecoveryKey(retryAfter), recovery.IdempotencyKey);
        Assert.True(Assert.Single(store.Receipts).IsDuplicate);
        Assert.Equal("social-job-correlation", recovery.CorrelationId);
    }

    [Fact]
    public async Task ProcessorReleasesForRecoveryWhenCallerCancelsRefresh()
    {
        var store = new RecordingJobStore
        {
            AcquiredJob = Job(
                attempt: 1,
                maximumAttempts: 3,
                idempotencyKey: SnapshotKey(1, Now.AddMinutes(-1))),
        };
        var processor = Processor(
            store,
            [Handler(
                new BlockingRefreshService(),
                new StaticSnapshotStore(Feed(expiresAtUtc: Now.AddMinutes(-1))),
                store)],
            new CorrelationContext());
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ExecuteNextAsync(
                new WorkerIdentity("social-worker"),
                cancellation.Token));

        Assert.True(store.ReleasedForRecovery);
        Assert.Null(store.LastFailureCode);
    }

    private static DurableJobProcessor Processor(
        RecordingJobStore store,
        IEnumerable<IJobHandler> handlers,
        ICorrelationContext correlation) =>
        new(
            store,
            handlers,
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
                HandlerTimeout = TimeSpan.FromSeconds(1),
                JitterRatio = 0,
            },
            new ExponentialJitterBackoffPolicy(
                new DurableJobOptions { JitterRatio = 0 },
                new Random(1)),
            new NullMetrics(),
            correlation,
            new FixedTimeProvider(Now));

    private static SocialRefreshJobHandler Handler(
        ISocialFeedRefreshService refresh,
        StaticSnapshotStore? snapshots = null,
        RecordingJobStore? jobs = null,
        ICorrelationContext? correlation = null)
    {
        snapshots ??= new StaticSnapshotStore(
            Feed(expiresAtUtc: Now.AddHours(1)));
        jobs ??= new RecordingJobStore();
        correlation ??= new FixedCorrelationContext("social-handler");
        return new SocialRefreshJobHandler(
            refresh,
            Coordinator(snapshots, jobs, correlation));
    }

    private static SocialRefreshJobCoordinator Coordinator(
        StaticSnapshotStore snapshots,
        RecordingJobStore jobs,
        ICorrelationContext? correlation = null) =>
        new(
            ["facebook"],
            snapshots,
            jobs,
            correlation ?? new FixedCorrelationContext("social-handler"),
            new FixedTimeProvider(Now));

    private static JobExecutionContext Context(
        string idempotencyKey = "social-refresh:facebook:missing",
        Guid? jobInstanceId = null,
        int attemptNumber = 1) =>
        new(
            jobInstanceId ?? Guid.NewGuid(),
            idempotencyKey,
            "social-correlation",
            attemptNumber);

    private static AcquiredJob Job(
        int attempt,
        int maximumAttempts,
        string idempotencyKey) =>
        new(
            Guid.NewGuid(),
            SocialRefreshJobDefinition.Key,
            SocialRefreshJobDefinition.Handler,
            SocialRefreshJobPayload.Serialize("facebook"),
            idempotencyKey,
            "social-job-correlation",
            attempt,
            maximumAttempts,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(15),
            new WorkerIdentity("social-worker"),
            Guid.NewGuid(),
            Now.AddMinutes(2),
            false);

    private static JobStateSnapshot JobState(
        Guid jobInstanceId,
        DateTimeOffset nextRunAtUtc) =>
        new(
            jobInstanceId,
            "Running",
            1,
            nextRunAtUtc,
            Now.AddMinutes(2),
            false,
            null);

    private static string SnapshotKey(
        long version,
        DateTimeOffset expiresAtUtc) =>
        $"social-refresh:facebook:v{version}:e{expiresAtUtc.UtcTicks}";

    private static string RecoveryKey(DateTimeOffset recoveryAfterUtc) =>
        $"social-refresh:facebook:recovery:v2:" +
        $"not-before:{recoveryAfterUtc.UtcTicks}";

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

    private static SocialRefreshState FailureState(
        SocialRefreshError error,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset? retryAfterUtc = null) =>
        new(
            attemptedAtUtc,
            Now.AddHours(-1),
            attemptedAtUtc,
            error,
            retryAfterUtc,
            1);

    private static SocialRefreshState SuccessState(DateTimeOffset occurredAtUtc) =>
        new(
            occurredAtUtc,
            occurredAtUtc,
            null,
            SocialRefreshError.None,
            null,
            0);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedCorrelationContext(string correlationId) : ICorrelationContext
    {
        public CorrelationSnapshot Current => new(correlationId, correlationId, null);

        public IDisposable Begin(string? suppliedCorrelationId = null, string operationName = "operation") =>
            new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingRefreshService(
        SocialRefreshOutcome outcome,
        Action? onRefresh = null,
        Action<TimeSpan?>? onRefreshWithDelay = null) : ISocialFeedRefreshService
    {
        internal int Calls { get; private set; }

        internal string? Provider { get; private set; }

        internal TimeSpan? TerminalRecoveryDelay { get; private set; }

        public Task<SocialRefreshReceipt> RefreshAsync(
            string provider,
            CancellationToken cancellationToken,
            TimeSpan? terminalRecoveryDelay = null)
        {
            Calls++;
            Provider = provider;
            TerminalRecoveryDelay = terminalRecoveryDelay;
            onRefresh?.Invoke();
            onRefreshWithDelay?.Invoke(terminalRecoveryDelay);
            return Task.FromResult(new SocialRefreshReceipt(
                outcome,
                outcome is SocialRefreshOutcome.Refreshed or
                    SocialRefreshOutcome.RefreshedWithRejectedItems or
                    SocialRefreshOutcome.Empty ? 1 : null,
                0,
                0,
                Now));
        }
    }

    private sealed class BlockingRefreshService : ISocialFeedRefreshService
    {
        public async Task<SocialRefreshReceipt> RefreshAsync(
            string provider,
            CancellationToken cancellationToken,
            TimeSpan? terminalRecoveryDelay = null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StaticSnapshotStore(
        StoredSocialFeed? active,
        SocialRefreshState? state = null) : ISocialSnapshotStore
    {
        internal StoredSocialFeed? Active { get; set; } = active;

        internal SocialRefreshState? State { get; set; } = state;

        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(Active);

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(State);

        public Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialRefreshSchedulingState(
                Active?.Version,
                Active?.ExpiresAtUtc,
                State));

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

    private sealed class SequencedSnapshotStore(
        params SocialRefreshSchedulingState[] states) : ISocialSnapshotStore
    {
        private readonly Queue<SocialRefreshSchedulingState> states =
            new(states);

        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(states.Dequeue());
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
        Guid JobInstanceId,
        string Actor,
        string Reason,
        string CorrelationId,
        DateTimeOffset OccurredAtUtc);

    private sealed class RecordingJobStore : IDurableJobStore
    {
        private readonly Dictionary<string, Guid> jobsByIdempotency = new(StringComparer.Ordinal);
        private bool acquired;

        internal List<JobDefinitionRegistration> Definitions { get; } = [];

        internal List<JobEnqueueRequest> Enqueues { get; } = [];

        internal List<JobEnqueueReceipt> Receipts { get; } = [];

        internal Dictionary<Guid, JobStateSnapshot> States { get; } = [];

        internal List<RequeueInvocation> Requeues { get; } = [];

        internal AcquiredJob? AcquiredJob
        {
            get;
            set
            {
                field = value;
                if (value is not null)
                {
                    Seed(value.JobInstanceId, value.IdempotencyKey);
                }
            }
        }

        internal int UniqueJobCount => jobsByIdempotency.Count;

        internal bool Completed { get; private set; }

        internal bool ReleasedForRecovery { get; private set; }

        internal string? LastFailureCode { get; private set; }

        internal DateTimeOffset? NextRunAtUtc { get; private set; }

        internal void Seed(Guid jobInstanceId, string idempotencyKey) =>
            jobsByIdempotency.Add(idempotencyKey, jobInstanceId);

        public Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
            JobDefinitionRegistration registration,
            CancellationToken cancellationToken)
        {
            Definitions.Add(registration);
            return Task.FromResult(Result.Succeed<Guid, JobStoreError>(Guid.NewGuid()));
        }

        public Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
            JobEnqueueRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            Enqueues.Add(request);
            var duplicate = jobsByIdempotency.TryGetValue(request.IdempotencyKey, out var id);
            id = duplicate ? id : Guid.NewGuid();
            jobsByIdempotency.TryAdd(request.IdempotencyKey, id);
            var receipt = new JobEnqueueReceipt(id, duplicate);
            Receipts.Add(receipt);
            return Task.FromResult(
                Result.Succeed<JobEnqueueReceipt, JobStoreError>(receipt));
        }

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null)
        {
            var job = acquired ? null : AcquiredJob;
            acquired = true;
            return Task.FromResult(Result.Succeed<JobAcquireResult, JobStoreError>(
                new JobAcquireResult(job)));
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
            CancellationToken cancellationToken)
        {
            Completed = true;
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            LastFailureCode = errorCode;
            NextRunAtUtc = nextRunAtUtc;
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ReleasedForRecovery = true;
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

        public Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
            Guid jobInstanceId,
            string actor,
            string reason,
            string correlationId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requeues.Add(new RequeueInvocation(
                jobInstanceId,
                actor,
                reason,
                correlationId,
                now));
            if (!States.TryGetValue(jobInstanceId, out var state) ||
                !string.Equals(
                    state.State,
                    "DeadLettered",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    Result.Fail<bool, JobStoreError>(
                        new JobStoreError(
                            JobStoreErrorCode.InvalidState,
                            "Only dead-lettered jobs can be requeued.")));
            }

            States[jobInstanceId] = state with
            {
                State = "RetryScheduled",
                AttemptsStarted = 0,
                NextRunAtUtc = now,
                LeaseExpiresAtUtc = null,
                CancellationRequested = false,
            };
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
            Guid jobInstanceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<JobStateLookup, JobStoreError>(
                new JobStateLookup(
                    States.GetValueOrDefault(jobInstanceId))));
    }

    private sealed class NullMetrics : IOperationsMetrics
    {
        public void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0)
        {
        }

        public void RecordJob(string definition, string outcome, int attempt)
        {
        }

        public void RecordStaleness(string dataSet, TimeSpan age)
        {
        }
    }
}
