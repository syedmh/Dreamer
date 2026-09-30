using Husaynia.Application.Operations.Retention;
using Husaynia.IntegrationTests.Persistence.Core;
using Husaynia.Infrastructure.Operations.Retention;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Husaynia.IntegrationTests.Operations.Retention;

public sealed class RetentionStoreTests
{
    [Fact]
    public async Task CompletedRunTargetMutatesDatabase()
    {
        var oldNow = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var currentNow = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(CompletedRunTargetMutatesDatabase));
        Guid oldRunId;

        await using (var seedContext = database.CreateContext())
        {
            var seedStore = new EfRetentionStore(seedContext);
            var seedWorkflow = new RetentionWorkflow(
                [new CompletedRetentionRunTarget(seedContext, new FixedTimeProvider(oldNow))],
                seedStore,
                new FixedTimeProvider(oldNow));
            var seed = await seedWorkflow.ExecuteAsync(
                OperationsRunRequest("seed-old-run"),
                CancellationToken.None);
            Assert.True(seed.IsSuccess);
            oldRunId = seed.Success.RunId;
        }

        RetentionRunResult result;
        await using (var applyContext = database.CreateContext())
        {
            var applyStore = new EfRetentionStore(applyContext);
            var target = new CompletedRetentionRunTarget(
                applyContext,
                new FixedTimeProvider(currentNow));
            var workflow = new RetentionWorkflow(
                [target],
                applyStore,
                new FixedTimeProvider(currentNow));
            var applied = await workflow.ExecuteAsync(
                OperationsRunRequest("purge-old-runs"),
                CancellationToken.None);
            Assert.True(applied.IsSuccess);
            Assert.Equal(1, applied.Success.Applied);
            result = applied.Success;

        }

        Assert.NotEqual(oldRunId, result.RunId);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRuns"));
    }

    [Fact]
    public async Task ActiveApplyPermitPreservesIdempotentAtLeastOnceMutation()
    {
        var oldNow = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ActiveApplyPermitPreservesIdempotentAtLeastOnceMutation));
        Guid oldRunId;

        await using (var seedContext = database.CreateContext())
        {
            var seed = await new RetentionWorkflow(
                    [new CompletedRetentionRunTarget(
                        seedContext,
                        new FixedTimeProvider(oldNow))],
                    new EfRetentionStore(seedContext),
                    new FixedTimeProvider(oldNow))
                .ExecuteAsync(OperationsRunRequest("permit-idempotency-seed"), CancellationToken.None);
            Assert.True(seed.IsSuccess);
            oldRunId = seed.Success.RunId;
        }

        var request = OperationsRunRequest("permit-idempotency-apply");
        var candidate = new RetentionCandidate(
            CompletedRetentionRunTarget.TargetName,
            oldRunId.ToString("D"),
            oldNow);
        await using var context = database.CreateContext();
        var store = new EfRetentionStore(context);
        var reservation = (await store.TryStartAsync(
            request,
            now,
            CancellationToken.None)).Success;
        await store.GetOrCreateBatchAsync(
            reservation.RunId,
            reservation.LeaseToken,
            [candidate],
            now,
            CancellationToken.None);
        var authorization = await store.TryBeginApplyAsync(
            reservation.RunId,
            reservation.LeaseToken,
            candidate,
            now,
            CancellationToken.None);
        var permit = Assert.IsType<RetentionApplyPermit>(authorization.Success.Permit);
        var target = new CompletedRetentionRunTarget(context, new FixedTimeProvider(now));

        var first = await target.ApplyAsync(permit, CancellationToken.None);
        var duplicate = await target.ApplyAsync(permit, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(duplicate.IsSuccess);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRuns"));
    }

    [Fact]
    public async Task LegalHoldPlacementIsIdempotentAuditedAndReleasable()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(LegalHoldPlacementIsIdempotentAuditedAndReleasable));
        var request = new RetentionHoldRequest(
            "hold-operation-1",
            "submissions",
            "subject-1",
            RetentionHoldKind.Legal,
            "legal-operator",
            "litigation preservation",
            null);
        Guid holdId;

        await using (var context = database.CreateContext())
        {
            var store = new EfRetentionStore(context);
            var first = await store.PlaceAsync(request, now, CancellationToken.None);
            var duplicate = await store.PlaceAsync(request, now, CancellationToken.None);
            Assert.True(first.IsSuccess);
            Assert.True(duplicate.IsSuccess);
            Assert.Equal(first.Success, duplicate.Success);
            holdId = first.Success;
            Assert.True(await store.HasActiveHoldAsync(
                new RetentionCandidate("submissions", "subject-1", now),
                now,
                CancellationToken.None));
        }

        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionHolds"));

        await using var releaseContext = database.CreateContext();
        var releaseStore = new EfRetentionStore(releaseContext);
        var release = await releaseStore.ReleaseAsync(
            holdId,
            "legal-operator",
            "matter closed",
            now.AddDays(1),
            CancellationToken.None);
        Assert.True(release.IsSuccess);
        Assert.False(await releaseStore.HasActiveHoldAsync(
            new RetentionCandidate("submissions", "subject-1", now),
            now.AddDays(1),
            CancellationToken.None));
    }

    [Theory]
    [InlineData("target")]
    [InlineData("subject")]
    [InlineData("kind")]
    [InlineData("actor")]
    [InlineData("reason")]
    [InlineData("expiry")]
    public async Task ConflictingHoldIdempotencyKeyIsRejected(string mismatch)
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            $"{nameof(ConflictingHoldIdempotencyKeyIsRejected)}_{mismatch}");
        await using var context = database.CreateContext();
        var store = new EfRetentionStore(context);
        var original = HoldRequest("same-hold-key");
        var conflicting = mismatch switch
        {
            "target" => original with { Target = "other-target" },
            "subject" => original with { SubjectId = "other-subject" },
            "kind" => original with { Kind = RetentionHoldKind.Operational },
            "actor" => original with { Actor = "other-operator" },
            "reason" => original with { Reason = "other reason" },
            "expiry" => original with { ExpiresAtUtc = now.AddDays(10) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        var first = await store.PlaceAsync(original, now, CancellationToken.None);
        var conflict = await store.PlaceAsync(conflicting, now, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(conflict.IsFailure);
        Assert.Equal("retention_hold_idempotency_conflict", conflict.Error);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionHolds"));
    }

    [Fact]
    public async Task ConcurrentIdenticalHoldIdempotencyKeyReturnsSameHold()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentIdenticalHoldIdempotencyKeyReturnsSameHold));
        var request = HoldRequest("concurrent-identical-hold");
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new EfRetentionStore(firstContext);
        var secondStore = new EfRetentionStore(secondContext);

        var results = await Task.WhenAll(
            firstStore.PlaceAsync(request, now, CancellationToken.None),
            secondStore.PlaceAsync(request, now, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Success, results[1].Success);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionHolds"));
    }

    [Fact]
    public async Task ConcurrentConflictingHoldIdempotencyKeyHasOneWinner()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentConflictingHoldIdempotencyKeyHasOneWinner));
        var firstRequest = HoldRequest("concurrent-conflicting-hold");
        var secondRequest = firstRequest with { Target = "other-target" };
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new EfRetentionStore(firstContext);
        var secondStore = new EfRetentionStore(secondContext);

        var results = await Task.WhenAll(
            firstStore.PlaceAsync(firstRequest, now, CancellationToken.None),
            secondStore.PlaceAsync(secondRequest, now, CancellationToken.None));

        Assert.Single(results, result => result.IsSuccess);
        var conflict = Assert.Single(results, result => result.IsFailure);
        Assert.Equal("retention_hold_idempotency_conflict", conflict.Error);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionHolds"));
    }

    [Fact]
    public async Task ConflictingRetentionIdempotencyKeyIsRejected()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConflictingRetentionIdempotencyKeyIsRejected));
        await using var context = database.CreateContext();
        var store = new EfRetentionStore(context);
        var original = RunRequest("same-key");
        var conflicting = original with
        {
            Policy = original.Policy with { RetainFor = TimeSpan.FromDays(90) },
        };

        var first = await store.TryStartAsync(original, now, CancellationToken.None);
        var conflict = await store.TryStartAsync(conflicting, now, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(conflict.IsFailure);
        Assert.Equal("retention_idempotency_conflict", conflict.Error);
    }

    [Fact]
    public async Task ConcurrentConflictingRetentionIdempotencyKeyHasOneWinner()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentConflictingRetentionIdempotencyKeyHasOneWinner));
        var firstRequest = RunRequest("concurrent-key");
        var secondRequest = firstRequest with
        {
            Policy = firstRequest.Policy with { RetainFor = TimeSpan.FromDays(90) },
        };
        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new EfRetentionStore(firstContext);
        var secondStore = new EfRetentionStore(secondContext);

        var results = await Task.WhenAll(
            firstStore.TryStartAsync(firstRequest, now, CancellationToken.None),
            secondStore.TryStartAsync(secondRequest, now, CancellationToken.None));

        Assert.Single(results, result => result.IsSuccess);
        var conflict = Assert.Single(results, result => result.IsFailure);
        Assert.Equal("retention_idempotency_conflict", conflict.Error);
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRuns"));
    }

    [Fact]
    public async Task PartialApplyIsReclaimedWithDurableProgress()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(PartialApplyIsReclaimedWithDurableProgress));
        var request = RunRequest("partial-recovery");
        Guid runId;

        await using (var context = database.CreateContext())
        {
            var store = new EfRetentionStore(context);
            var reservation = (await store.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            runId = reservation.RunId;
            var candidate = new RetentionCandidate("submissions", "subject-1", now.AddDays(-40));
            await store.GetOrCreateBatchAsync(
                runId,
                reservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            var decision = await store.TryBeginApplyAsync(
                runId,
                reservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.Equal(RetentionApplyDecision.Apply, decision.Success.Decision);
        }

        await using var restartedContext = database.CreateContext();
        var restartedStore = new EfRetentionStore(restartedContext);
        var reclaimed = (await restartedStore.TryStartAsync(
            request,
            now.AddSeconds(6),
            CancellationToken.None)).Success;
        var batch = await restartedStore.GetOrCreateBatchAsync(
            runId,
            reclaimed.LeaseToken,
            [],
            now.AddSeconds(6),
            CancellationToken.None);

        Assert.True(reclaimed.CanExecute);
        Assert.Equal(RetentionBatchItemStatus.Pending, Assert.Single(batch.Success).Status);
    }

    [Fact]
    public async Task StaleMarkAppliedIsFencedAfterLeaseReclaim()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(StaleMarkAppliedIsFencedAfterLeaseReclaim));
        var request = RunRequest("stale-mark-applied");
        var candidate = new RetentionCandidate("submissions", "subject-1", now.AddDays(-40));
        RetentionRunReservation staleReservation;

        await using (var setupContext = database.CreateContext())
        {
            var setupStore = new EfRetentionStore(setupContext);
            staleReservation = (await setupStore.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await setupStore.GetOrCreateBatchAsync(
                staleReservation.RunId,
                staleReservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            var authorization = await setupStore.TryBeginApplyAsync(
                staleReservation.RunId,
                staleReservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.Equal(RetentionApplyDecision.Apply, authorization.Success.Decision);
        }

        var pause = new PauseAfterReaderInterceptor(
            command => command.CommandText.Contains(
                "OperationsRetentionRunItems",
                StringComparison.Ordinal));
        await using var staleContext = database.CreateContext(pause);
        var staleStore = new EfRetentionStore(staleContext);
        var staleWrite = staleStore.MarkAppliedAsync(
            staleReservation.RunId,
            staleReservation.LeaseToken,
            candidate,
            now.AddSeconds(1),
            CancellationToken.None);
        await pause.Paused.WaitAsync(TimeSpan.FromSeconds(10));

        RetentionRunReservation reclaimed;
        try
        {
            await using var reclaimContext = database.CreateContext();
            reclaimed = (await new EfRetentionStore(reclaimContext).TryStartAsync(
                request,
                now.AddSeconds(6),
                CancellationToken.None)).Success;
            Assert.True(reclaimed.CanExecute);
            Assert.NotEqual(staleReservation.LeaseToken, reclaimed.LeaseToken);
        }
        finally
        {
            pause.Release();
        }

        var staleResult = await staleWrite;
        Assert.True(staleResult.IsFailure);
        Assert.Equal("retention_lease_lost", staleResult.Error);

        await using var verificationContext = database.CreateContext();
        var batch = await new EfRetentionStore(verificationContext).GetOrCreateBatchAsync(
            reclaimed.RunId,
            reclaimed.LeaseToken,
            [],
            now.AddSeconds(6),
            CancellationToken.None);
        Assert.Equal(RetentionBatchItemStatus.Pending, Assert.Single(batch.Success).Status);
        Assert.Equal(
            (null, null, null, null),
            await ReadCompletionAsync(database.ConnectionString, reclaimed.RunId));
    }

    [Fact]
    public async Task StaleCompleteIsFencedAfterLeaseReclaim()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(StaleCompleteIsFencedAfterLeaseReclaim));
        var request = RunRequest("stale-complete");
        var candidate = new RetentionCandidate("submissions", "subject-1", now.AddDays(-40));
        RetentionRunReservation staleReservation;

        await using (var setupContext = database.CreateContext())
        {
            var setupStore = new EfRetentionStore(setupContext);
            staleReservation = (await setupStore.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await setupStore.GetOrCreateBatchAsync(
                staleReservation.RunId,
                staleReservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            await setupStore.TryBeginApplyAsync(
                staleReservation.RunId,
                staleReservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            var marked = await setupStore.MarkAppliedAsync(
                staleReservation.RunId,
                staleReservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.True(marked.IsSuccess);
        }

        var staleResult = new RetentionRunResult(
            staleReservation.RunId,
            RetentionMode.Apply,
            91,
            37,
            53,
            false);
        var pause = new PauseAfterReaderInterceptor(
            command => command.CommandText.Contains(
                "OperationsRetentionRuns",
                StringComparison.Ordinal));
        await using var staleContext = database.CreateContext(pause);
        var staleStore = new EfRetentionStore(staleContext);
        var staleCompletion = staleStore.CompleteAsync(
            request,
            staleResult,
            staleReservation.LeaseToken,
            now.AddSeconds(1),
            CancellationToken.None);
        await pause.Paused.WaitAsync(TimeSpan.FromSeconds(10));

        RetentionRunReservation reclaimed;
        try
        {
            await using var reclaimContext = database.CreateContext();
            reclaimed = (await new EfRetentionStore(reclaimContext).TryStartAsync(
                request,
                now.AddSeconds(6),
                CancellationToken.None)).Success;
            Assert.True(reclaimed.CanExecute);
            Assert.NotEqual(staleReservation.LeaseToken, reclaimed.LeaseToken);
        }
        finally
        {
            pause.Release();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleCompletion);
        Assert.Equal("The retention run lease is no longer valid.", exception.Message);
        Assert.Equal(
            (null, null, null, null),
            await ReadCompletionAsync(database.ConnectionString, reclaimed.RunId));

        await using var completionContext = database.CreateContext();
        await new EfRetentionStore(completionContext).CompleteAsync(
            request,
            new RetentionRunResult(
                reclaimed.RunId,
                RetentionMode.Apply,
                1,
                0,
                1,
                false),
            reclaimed.LeaseToken,
            now.AddSeconds(6),
            CancellationToken.None);
        Assert.Equal(
            (1, 0, 1, now.AddSeconds(6)),
            await ReadCompletionAsync(database.ConnectionString, reclaimed.RunId));
    }

    [Fact]
    public async Task CompletedRunRejectsStaleLeaseAndAcceptsCompletingLeaseIdempotently()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(CompletedRunRejectsStaleLeaseAndAcceptsCompletingLeaseIdempotently));
        var request = RunRequest("completed-run-stale-lease");
        var candidate = new RetentionCandidate("submissions", "subject-1", now.AddDays(-40));
        RetentionRunReservation ownerA;

        await using (var ownerAContext = database.CreateContext())
        {
            var ownerAStore = new EfRetentionStore(ownerAContext);
            ownerA = (await ownerAStore.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await ownerAStore.GetOrCreateBatchAsync(
                ownerA.RunId,
                ownerA.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            await ownerAStore.TryBeginApplyAsync(
                ownerA.RunId,
                ownerA.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            var marked = await ownerAStore.MarkAppliedAsync(
                ownerA.RunId,
                ownerA.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.True(marked.IsSuccess);
        }

        RetentionRunReservation ownerB;
        var completion = new RetentionRunResult(
            ownerA.RunId,
            RetentionMode.Apply,
            1,
            0,
            1,
            false);
        await using (var ownerBContext = database.CreateContext())
        {
            var ownerBStore = new EfRetentionStore(ownerBContext);
            ownerB = (await ownerBStore.TryStartAsync(
                request,
                now.AddSeconds(6),
                CancellationToken.None)).Success;
            Assert.True(ownerB.CanExecute);
            Assert.NotEqual(ownerA.LeaseToken, ownerB.LeaseToken);
            await ownerBStore.CompleteAsync(
                request,
                completion,
                ownerB.LeaseToken,
                now.AddSeconds(6),
                CancellationToken.None);
        }

        var completedState = await ReadRunStateAsync(database.ConnectionString, ownerA.RunId);
        Assert.Equal(ownerB.LeaseToken, completedState.LeaseToken);

        await using (var staleContext = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new EfRetentionStore(staleContext).CompleteAsync(
                    request,
                    completion with { Examined = 99, Held = 98, Applied = 97 },
                    ownerA.LeaseToken,
                    now.AddSeconds(7),
                    CancellationToken.None));
            Assert.Equal("The retention run lease is no longer valid.", exception.Message);
        }

        Assert.Equal(
            completedState,
            await ReadRunStateAsync(database.ConnectionString, ownerA.RunId));
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRuns"));
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRunItems"));
        Assert.Equal(0, await database.CountRowsAsync("OperationsRetentionHolds"));
        Assert.Equal(0, await database.CountRowsAsync("OperationsJobAudit"));

        await using (var duplicateContext = database.CreateContext())
        {
            await new EfRetentionStore(duplicateContext).CompleteAsync(
                request,
                completion,
                ownerB.LeaseToken,
                now.AddSeconds(12),
                CancellationToken.None);
        }

        Assert.Equal(
            completedState,
            await ReadRunStateAsync(database.ConnectionString, ownerA.RunId));
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRuns"));
        Assert.Equal(1, await database.CountRowsAsync("OperationsRetentionRunItems"));
        Assert.Equal(0, await database.CountRowsAsync("OperationsRetentionHolds"));
        Assert.Equal(0, await database.CountRowsAsync("OperationsJobAudit"));
    }

    [Fact]
    public async Task HoldPlacementIsBlockedByExpiredApplyingItem()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(HoldPlacementIsBlockedByExpiredApplyingItem));
        var request = RunRequest("expired-applying-hold");
        var candidate = new RetentionCandidate("submissions", "subject-1", now.AddDays(-40));

        await using (var context = database.CreateContext())
        {
            var store = new EfRetentionStore(context);
            var reservation = (await store.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await store.GetOrCreateBatchAsync(
                reservation.RunId,
                reservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            var decision = await store.TryBeginApplyAsync(
                reservation.RunId,
                reservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.Equal(RetentionApplyDecision.Apply, decision.Success.Decision);
        }

        await using var holdContext = database.CreateContext();
        var hold = await new EfRetentionStore(holdContext).PlaceAsync(
            HoldRequest("expired-applying-hold-request"),
            now.AddSeconds(6),
            CancellationToken.None);

        Assert.True(hold.IsFailure);
        Assert.Equal("retention_apply_in_progress", hold.Error);
    }

    [Fact]
    public async Task DetachedOldPermitCannotDeleteAfterReclaimAndHoldAcceptance()
    {
        var oldNow = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DetachedOldPermitCannotDeleteAfterReclaimAndHoldAcceptance));
        Guid oldRunId;

        await using (var seedContext = database.CreateContext())
        {
            var seed = await new RetentionWorkflow(
                    [new CompletedRetentionRunTarget(
                        seedContext,
                        new FixedTimeProvider(oldNow))],
                    new EfRetentionStore(seedContext),
                    new FixedTimeProvider(oldNow))
                .ExecuteAsync(OperationsRunRequest("stale-target-seed"), CancellationToken.None);
            Assert.True(seed.IsSuccess);
            oldRunId = seed.Success.RunId;
        }

        var request = OperationsRunRequest("stale-target-purge") with
        {
            Policy = OperationsRunRequest("unused").Policy with
            {
                OperationTimeout = TimeSpan.FromSeconds(5),
            },
        };
        var candidate = new RetentionCandidate(
            CompletedRetentionRunTarget.TargetName,
            oldRunId.ToString("D"),
            oldNow);
        RetentionRunReservation oldReservation;
        RetentionApplyPermit oldPermit;
        await using (var applyContext = database.CreateContext())
        {
            var store = new EfRetentionStore(applyContext);
            oldReservation = (await store.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await store.GetOrCreateBatchAsync(
                oldReservation.RunId,
                oldReservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
            var decision = await store.TryBeginApplyAsync(
                oldReservation.RunId,
                oldReservation.LeaseToken,
                candidate,
                now,
                CancellationToken.None);
            Assert.Equal(RetentionApplyDecision.Apply, decision.Success.Decision);
            oldPermit = Assert.IsType<RetentionApplyPermit>(decision.Success.Permit);
        }

        await using (var reclaimContext = database.CreateContext())
        {
            var reclaimed = await new EfRetentionStore(reclaimContext).TryStartAsync(
                request,
                now.AddSeconds(6),
                CancellationToken.None);
            Assert.True(reclaimed.Success.CanExecute);
            Assert.NotEqual(oldReservation.LeaseToken, reclaimed.Success.LeaseToken);
        }

        await using (var holdContext = database.CreateContext())
        {
            var hold = await new EfRetentionStore(holdContext).PlaceAsync(
                new RetentionHoldRequest(
                    "stale-target-hold",
                    CompletedRetentionRunTarget.TargetName,
                    oldRunId.ToString("D"),
                    RetentionHoldKind.Legal,
                    "legal-operator",
                    "preserve detached target",
                    null),
                now.AddSeconds(6),
                CancellationToken.None);
            Assert.True(hold.IsSuccess);
        }

        await using (var staleContext = database.CreateContext())
        {
            var staleApply = await new CompletedRetentionRunTarget(
                    staleContext,
                    new FixedTimeProvider(now.AddSeconds(6)))
                .ApplyAsync(
                oldPermit,
                CancellationToken.None);
            Assert.True(staleApply.IsFailure);
            Assert.Equal("retention_apply_permit_refused", staleApply.Error);
        }

        await using var verificationContext = database.CreateContext();
        var remaining = await new CompletedRetentionRunTarget(verificationContext)
            .FindEligibleAsync(now, 100, CancellationToken.None);
        Assert.Contains(remaining, item => item.SubjectId == oldRunId.ToString("D"));
    }

    [Fact]
    public async Task ConcurrentHoldAndApplyAreSerialized()
    {
        var now = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentHoldAndApplyAreSerialized));
        var request = RunRequest("hold-race");
        var candidate = new RetentionCandidate("submissions", "race-subject", now.AddDays(-40));
        RetentionRunReservation reservation;

        await using (var setupContext = database.CreateContext())
        {
            var setupStore = new EfRetentionStore(setupContext);
            reservation = (await setupStore.TryStartAsync(
                request,
                now,
                CancellationToken.None)).Success;
            await setupStore.GetOrCreateBatchAsync(
                reservation.RunId,
                reservation.LeaseToken,
                [candidate],
                now,
                CancellationToken.None);
        }

        await using var applyContext = database.CreateContext();
        await using var holdContext = database.CreateContext();
        var applyStore = new EfRetentionStore(applyContext);
        var holdStore = new EfRetentionStore(holdContext);
        var applyTask = applyStore.TryBeginApplyAsync(
            reservation.RunId,
            reservation.LeaseToken,
            candidate,
            now,
            CancellationToken.None);
        var holdTask = holdStore.PlaceAsync(
            new RetentionHoldRequest(
                "race-hold",
                "submissions",
                "race-subject",
                RetentionHoldKind.Legal,
                "legal-operator",
                "preserve",
                null),
            now,
            CancellationToken.None);
        await Task.WhenAll(applyTask, holdTask);

        var apply = await applyTask;
        var hold = await holdTask;
        Assert.False(
            apply.IsSuccess &&
            apply.Success.Decision == RetentionApplyDecision.Apply &&
            hold.IsSuccess);
        Assert.True(
            (apply.IsSuccess && apply.Success.Decision == RetentionApplyDecision.Held &&
                hold.IsSuccess) ||
            (apply.IsSuccess && apply.Success.Decision == RetentionApplyDecision.Apply &&
                hold.IsFailure && hold.Error == "retention_apply_in_progress"));
    }

    private static RetentionRunRequest RunRequest(string idempotencyKey) =>
        new(
            idempotencyKey,
            new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromSeconds(5)),
            RetentionMode.Apply,
            "operator",
            "correlation");

    private static RetentionHoldRequest HoldRequest(string idempotencyKey) =>
        new(
            idempotencyKey,
            "submissions",
            "subject-1",
            RetentionHoldKind.Legal,
            "legal-operator",
            "litigation preservation",
            null);

    private static RetentionRunRequest OperationsRunRequest(string idempotencyKey) =>
        new(
            idempotencyKey,
            new RetentionPolicy(
                "operations-history-retention",
                CompletedRetentionRunTarget.TargetName,
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromSeconds(5)),
            RetentionMode.Apply,
            "operator",
            "correlation");

    private static async Task<(int? Examined, int? Held, int? Applied, DateTimeOffset? CompletedAtUtc)>
        ReadCompletionAsync(string connectionString, Guid runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT [Examined], [Held], [Applied], [CompletedAtUtc]
            FROM [OperationsRetentionRuns]
            WHERE [Id] = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3));
    }

    private static async Task<(
        Guid LeaseToken,
        DateTimeOffset LeaseExpiresAtUtc,
        int? Examined,
        int? Held,
        int? Applied,
        DateTimeOffset? CompletedAtUtc)> ReadRunStateAsync(
            string connectionString,
            Guid runId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT [LeaseToken], [LeaseExpiresAtUtc], [Examined], [Held], [Applied], [CompletedAtUtc]
            FROM [OperationsRetentionRuns]
            WHERE [Id] = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5));
    }

    private sealed class PauseAfterReaderInterceptor(Func<DbCommand, bool> shouldPause)
        : DbCommandInterceptor
    {
        private readonly TaskCompletionSource paused =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int pauseClaimed;

        internal Task Paused => paused.Task;

        internal void Release() => release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (shouldPause(command) &&
                Interlocked.CompareExchange(ref pauseClaimed, 1, 0) == 0)
            {
                paused.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
