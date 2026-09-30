using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Retention;

namespace Husaynia.Application.Tests.Operations.Retention;

public sealed class RetentionWorkflowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InvalidRequestIsRejectedBeforeStoreAccess()
    {
        var store = new FakeStore();
        var workflow = new RetentionWorkflow([], store, new FixedTimeProvider());

        var result = await workflow.ExecuteAsync(
            Request("", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_retention_request", result.Error);
        Assert.Equal(0, store.StartCalls);
    }

    [Fact]
    public async Task UnknownTargetIsRejectedBeforeReservation()
    {
        var store = new FakeStore();
        var workflow = new RetentionWorkflow([], store, new FixedTimeProvider());

        var result = await workflow.ExecuteAsync(
            Request("unknown", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("retention_target_not_registered", result.Error);
        Assert.Equal(0, store.StartCalls);
    }

    [Fact]
    public async Task InProgressDuplicateDoesNotExecuteTarget()
    {
        var target = new FakeTarget(
            new RetentionCandidate("submissions", "should-not-run", Now.AddDays(-40)));
        var store = new FakeStore();
        store.MarkInProgress("duplicate");
        var workflow = new RetentionWorkflow([target], store, new FixedTimeProvider());

        var result = await workflow.ExecuteAsync(
            Request("duplicate", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("retention_run_in_progress", result.Error);
        Assert.Empty(target.Applied);
    }

    [Fact]
    public async Task CandidateTargetMismatchFailsBeforeMutation()
    {
        var target = new FakeTarget(
            new RetentionCandidate("other-target", "subject", Now.AddDays(-40)));
        var workflow = new RetentionWorkflow(
            [target],
            new FakeStore(),
            new FixedTimeProvider());

        var result = await workflow.ExecuteAsync(
            Request("mismatch", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("retention_target_mismatch", result.Error);
        Assert.Empty(target.Applied);
    }

    [Fact]
    public async Task DryRunReportsCandidatesWithoutMutationAndHonorsHolds()
    {
        var target = new FakeTarget(
            new RetentionCandidate("submissions", "delete-me", Now.AddDays(-40)),
            new RetentionCandidate("submissions", "held", Now.AddDays(-40)));
        var store = new FakeStore("held");
        var workflow = new RetentionWorkflow([target], store, new FixedTimeProvider());

        var result = await workflow.ExecuteAsync(
            Request("dry-run", RetentionMode.DryRun),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Success.Examined);
        Assert.Equal(1, result.Success.Held);
        Assert.Equal(0, result.Success.Applied);
        Assert.Empty(target.Applied);
        Assert.Single(store.Completed);
    }

    [Fact]
    public async Task ApplyIsBoundedIdempotentAndSkipsHeldSubjects()
    {
        var target = new FakeTarget(
            new RetentionCandidate("submissions", "delete-me", Now.AddDays(-40)),
            new RetentionCandidate("submissions", "held", Now.AddDays(-40)));
        var store = new FakeStore("held");
        var workflow = new RetentionWorkflow([target], store, new FixedTimeProvider());

        var first = await workflow.ExecuteAsync(
            Request("apply-once", RetentionMode.Apply),
            CancellationToken.None);
        var duplicate = await workflow.ExecuteAsync(
            Request("apply-once", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(1, first.Success.Applied);
        Assert.Equal(["delete-me"], target.Applied);
        Assert.True(duplicate.IsSuccess);
        Assert.True(duplicate.Success.IsDuplicate);
        Assert.Equal(["delete-me"], target.Applied);
    }

    [Fact]
    public async Task TargetCannotReturnMoreThanConfiguredBatch()
    {
        var target = new FakeTarget(
            new RetentionCandidate("submissions", "one", Now.AddDays(-40)),
            new RetentionCandidate("submissions", "two", Now.AddDays(-40)));
        var store = new FakeStore();
        var workflow = new RetentionWorkflow([target], store, new FixedTimeProvider());
        var request = Request("bounded", RetentionMode.Apply) with
        {
            Policy = new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                1,
                TimeSpan.FromSeconds(5)),
        };

        var result = await workflow.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("retention_target_exceeded_batch", result.Error);
        Assert.Empty(target.Applied);
    }

    [Fact]
    public async Task RestartAfterPartialApplyResumesIdempotentlyAndCompletes()
    {
        var target = new FakeTarget(
            new RetentionCandidate("submissions", "one", Now.AddDays(-40)),
            new RetentionCandidate("submissions", "two", Now.AddDays(-40)))
        {
            FailAfterFirstApply = true,
        };
        var store = new FakeStore();
        var time = new MutableTimeProvider();
        var workflow = new RetentionWorkflow([target], store, time);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ExecuteAsync(Request("recover", RetentionMode.Apply), CancellationToken.None));

        target.FailAfterFirstApply = false;
        time.Advance(TimeSpan.FromSeconds(6));
        var recovered = await workflow.ExecuteAsync(
            Request("recover", RetentionMode.Apply),
            CancellationToken.None);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(2, recovered.Success.Applied);
        Assert.Equal(["one", "two"], target.Applied);
        Assert.Single(store.Completed);
    }

    [Fact]
    public async Task OperationTimeoutCancelsTargetAndLeavesRunRecoverable()
    {
        var store = new FakeStore();
        var workflow = new RetentionWorkflow(
            [new BlockingTarget()],
            store,
            TimeProvider.System);
        var request = Request("timeout", RetentionMode.Apply) with
        {
            Policy = new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromMilliseconds(20)),
        };

        var result = await workflow.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("retention_operation_timeout", result.Error);
        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task CancellationIgnoringDiscoveryReturnsAtDeadlineWithoutLateCompletion()
    {
        var store = new FakeStore();
        var target = new CancellationIgnoringDiscoveryTarget();
        var workflow = new RetentionWorkflow([target], store, TimeProvider.System);
        var request = Request("ignored-discovery-cancellation", RetentionMode.Apply) with
        {
            Policy = new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromMilliseconds(20)),
        };

        var result = await workflow.ExecuteAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.IsFailure);
        Assert.Equal("retention_operation_timeout", result.Error);
        Assert.Empty(store.Completed);

        target.Release();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Empty(store.Completed);
    }

    [Fact]
    public async Task CancellationIgnoringApplyReturnsAtDeadlineWithoutLateMarkOrCompletion()
    {
        var store = new FakeStore();
        var target = new CancellationIgnoringApplyTarget();
        var workflow = new RetentionWorkflow([target], store, TimeProvider.System);
        var request = Request("ignored-apply-cancellation", RetentionMode.Apply) with
        {
            Policy = new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromMilliseconds(20)),
        };

        var result = await workflow.ExecuteAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.IsFailure);
        Assert.Equal("retention_operation_timeout", result.Error);
        Assert.Equal(0, store.MarkAppliedCalls);
        Assert.Empty(store.Completed);

        target.Release();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(0, store.MarkAppliedCalls);
        Assert.Empty(store.Completed);
    }

    private static RetentionRunRequest Request(string key, RetentionMode mode) =>
        new(
            key,
            new RetentionPolicy(
                "submission-retention",
                "submissions",
                TimeSpan.FromDays(30),
                100,
                TimeSpan.FromSeconds(5)),
            mode,
            "operator",
            "correlation");

    private sealed class FakeTarget(params RetentionCandidate[] candidates) : IRetentionTarget
    {
        private readonly HashSet<string> appliedKeys = new(StringComparer.Ordinal);

        internal List<string> Applied { get; } = [];

        internal bool FailAfterFirstApply { get; set; }

        public string Target => "submissions";

        public Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
            DateTimeOffset olderThanUtc,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionCandidate>>(candidates);

        public Task<Result<bool, string>> ApplyAsync(
            RetentionApplyPermit permit,
            CancellationToken cancellationToken)
        {
            var candidate = permit.Candidate;
            var idempotencyKey = permit.IdempotencyKey;
            if (appliedKeys.Add(idempotencyKey))
            {
                Applied.Add(candidate.SubjectId);
            }

            if (FailAfterFirstApply && Applied.Count == 1)
            {
                throw new InvalidOperationException("simulated crash after destructive apply");
            }

            return Task.FromResult(Result.Succeed<bool, string>(true));
        }
    }

    private sealed class FakeStore(params string[] heldSubjects) : IRetentionStore
    {
        private readonly Dictionary<string, RunState> runs =
            new(StringComparer.Ordinal);

        internal List<RetentionRunResult> Completed { get; } = [];

        internal int StartCalls { get; private set; }

        internal int MarkAppliedCalls { get; private set; }

        private HashSet<string> InProgressKeys { get; } = new(StringComparer.Ordinal);

        internal void MarkInProgress(string idempotencyKey) =>
            InProgressKeys.Add(idempotencyKey);

        public Task<Result<RetentionRunReservation, string>> TryStartAsync(
            RetentionRunRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            if (InProgressKeys.Contains(request.IdempotencyKey))
            {
                return Task.FromResult(
                    Result.Succeed<RetentionRunReservation, string>(
                        new RetentionRunReservation(
                            Guid.NewGuid(),
                            true,
                            null,
                            Guid.NewGuid(),
                            false)));
            }

            if (runs.TryGetValue(request.IdempotencyKey, out var existing))
            {
                if (existing.Result is null && existing.LeaseExpiresAtUtc <= now)
                {
                    existing.LeaseToken = Guid.NewGuid();
                    existing.LeaseExpiresAtUtc = now.Add(request.Policy.OperationTimeout);
                    return Task.FromResult(
                        Result.Succeed<RetentionRunReservation, string>(
                            new RetentionRunReservation(
                                existing.RunId,
                                true,
                                null,
                                existing.LeaseToken,
                                true)));
                }

                return Task.FromResult(
                    Result.Succeed<RetentionRunReservation, string>(
                        new RetentionRunReservation(
                            existing.RunId,
                            true,
                            existing.Result,
                            existing.LeaseToken,
                            false)));
            }

            var state = new RunState(
                Guid.NewGuid(),
                Guid.NewGuid(),
                now.Add(request.Policy.OperationTimeout));
            runs.Add(request.IdempotencyKey, state);
            return Task.FromResult(
                Result.Succeed<RetentionRunReservation, string>(
                    new RetentionRunReservation(
                        state.RunId,
                        false,
                        null,
                        state.LeaseToken,
                        true)));
        }

        public Task<Result<IReadOnlyList<RetentionBatchItem>, string>> GetOrCreateBatchAsync(
            Guid runId,
            Guid leaseToken,
            IReadOnlyList<RetentionCandidate> candidates,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var run = FindRun(runId, leaseToken, now);
            if (run is null)
            {
                return Task.FromResult(
                    Result.Fail<IReadOnlyList<RetentionBatchItem>, string>("retention_lease_lost"));
            }

            if (run.Items.Count == 0)
            {
                run.Items.AddRange(candidates.Select(candidate =>
                    new RetentionBatchItem(candidate, RetentionBatchItemStatus.Pending)));
            }

            return Task.FromResult(
                Result.Succeed<IReadOnlyList<RetentionBatchItem>, string>(run.Items.ToArray()));
        }

        public Task<Result<RetentionApplyAuthorization, string>> TryBeginApplyAsync(
            Guid runId,
            Guid leaseToken,
            RetentionCandidate candidate,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var run = FindRun(runId, leaseToken, now);
            if (run is null)
            {
                return Task.FromResult(
                    Result.Fail<RetentionApplyAuthorization, string>("retention_lease_lost"));
            }

            var index = run.Items.FindIndex(item =>
                item.Candidate.SubjectId == candidate.SubjectId);
            var held = heldSubjects.Contains(candidate.SubjectId, StringComparer.Ordinal);
            run.Items[index] = run.Items[index] with
            {
                Status = held
                    ? RetentionBatchItemStatus.Held
                    : RetentionBatchItemStatus.Applying,
            };
            return Task.FromResult(
                Result.Succeed<RetentionApplyAuthorization, string>(
                    held
                        ? new RetentionApplyAuthorization(RetentionApplyDecision.Held, null)
                        : new RetentionApplyAuthorization(
                            RetentionApplyDecision.Apply,
                            new RetentionApplyPermit(
                                runId,
                                leaseToken,
                                candidate,
                                $"{runId:N}:{candidate.SubjectId}"))));
        }

        public Task<Result<bool, string>> MarkAppliedAsync(
            Guid runId,
            Guid leaseToken,
            RetentionCandidate candidate,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            MarkAppliedCalls++;
            var run = FindRun(runId, leaseToken, now);
            if (run is null)
            {
                return Task.FromResult(Result.Fail<bool, string>("retention_lease_lost"));
            }

            var index = run.Items.FindIndex(item =>
                item.Candidate.SubjectId == candidate.SubjectId);
            run.Items[index] = run.Items[index] with { Status = RetentionBatchItemStatus.Applied };
            return Task.FromResult(Result.Succeed<bool, string>(true));
        }

        public Task CompleteAsync(
            RetentionRunRequest request,
            RetentionRunResult result,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            runs[request.IdempotencyKey].Result = result;
            Completed.Add(result);
            return Task.CompletedTask;
        }

        private RunState? FindRun(Guid runId, Guid leaseToken, DateTimeOffset now) =>
            runs.Values.SingleOrDefault(run =>
                run.RunId == runId &&
                run.LeaseToken == leaseToken &&
                run.LeaseExpiresAtUtc > now);

        private sealed class RunState(
            Guid runId,
            Guid leaseToken,
            DateTimeOffset leaseExpiresAtUtc)
        {
            internal Guid RunId { get; } = runId;

            internal Guid LeaseToken { get; set; } = leaseToken;

            internal DateTimeOffset LeaseExpiresAtUtc { get; set; } = leaseExpiresAtUtc;

            internal List<RetentionBatchItem> Items { get; } = [];

            internal RetentionRunResult? Result { get; set; }
        }
    }

    private sealed class BlockingTarget : IRetentionTarget
    {
        public string Target => "submissions";

        public async Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
            DateTimeOffset olderThanUtc,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public Task<Result<bool, string>> ApplyAsync(
            RetentionApplyPermit permit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, string>(true));
    }

    private sealed class CancellationIgnoringDiscoveryTarget : IRetentionTarget
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Target => "submissions";

        public async Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
            DateTimeOffset olderThanUtc,
            int maximumCount,
            CancellationToken cancellationToken)
        {
            await release.Task;
            return [];
        }

        public Task<Result<bool, string>> ApplyAsync(
            RetentionApplyPermit permit,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, string>(true));

        internal void Release() => release.SetResult();
    }

    private sealed class CancellationIgnoringApplyTarget : IRetentionTarget
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Target => "submissions";

        public Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
            DateTimeOffset olderThanUtc,
            int maximumCount,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionCandidate>>(
                [new RetentionCandidate(Target, "subject-1", Now.AddDays(-40))]);

        public async Task<Result<bool, string>> ApplyAsync(
            RetentionApplyPermit permit,
            CancellationToken cancellationToken)
        {
            await release.Task;
            return Result.Succeed<bool, string>(true);
        }

        internal void Release() => release.SetResult();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = Now;

        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
