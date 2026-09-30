using Husaynia.Application.Contracts;

namespace Husaynia.Application.Operations.Retention;

public enum RetentionMode
{
    DryRun,
    Apply,
}

public enum RetentionHoldKind
{
    Legal,
    Operational,
}

public sealed record RetentionPolicy(
    string Name,
    string Target,
    TimeSpan RetainFor,
    int BatchSize,
    TimeSpan OperationTimeout);

public sealed record RetentionCandidate(
    string Target,
    string SubjectId,
    DateTimeOffset EligibleAtUtc);

public sealed record RetentionRunRequest(
    string IdempotencyKey,
    RetentionPolicy Policy,
    RetentionMode Mode,
    string Actor,
    string CorrelationId);

public sealed record RetentionRunResult(
    Guid RunId,
    RetentionMode Mode,
    int Examined,
    int Held,
    int Applied,
    bool IsDuplicate);

public sealed record RetentionRunReservation(
    Guid RunId,
    bool IsDuplicate,
    RetentionRunResult? ExistingResult,
    Guid LeaseToken,
    bool CanExecute);

public enum RetentionBatchItemStatus
{
    Pending,
    Applying,
    Applied,
    Held,
}

public sealed record RetentionBatchItem(
    RetentionCandidate Candidate,
    RetentionBatchItemStatus Status);

public enum RetentionApplyDecision
{
    Apply,
    Held,
}

public sealed record RetentionApplyPermit(
    Guid RunId,
    Guid LeaseToken,
    RetentionCandidate Candidate,
    string IdempotencyKey);

public sealed record RetentionApplyAuthorization(
    RetentionApplyDecision Decision,
    RetentionApplyPermit? Permit);

public sealed record RetentionHoldRequest(
    string IdempotencyKey,
    string Target,
    string SubjectId,
    RetentionHoldKind Kind,
    string Actor,
    string Reason,
    DateTimeOffset? ExpiresAtUtc);

public interface IRetentionHoldAdministration
{
    Task<Result<Guid, string>> PlaceAsync(
        RetentionHoldRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, string>> ReleaseAsync(
        Guid holdId,
        string actor,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface IRetentionTarget
{
    string Target { get; }

    Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
        DateTimeOffset olderThanUtc,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<Result<bool, string>> ApplyAsync(
        RetentionApplyPermit permit,
        CancellationToken cancellationToken);
}

public interface IRetentionStore
{
    Task<Result<RetentionRunReservation, string>> TryStartAsync(
        RetentionRunRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<RetentionBatchItem>, string>> GetOrCreateBatchAsync(
        Guid runId,
        Guid leaseToken,
        IReadOnlyList<RetentionCandidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<RetentionApplyAuthorization, string>> TryBeginApplyAsync(
        Guid runId,
        Guid leaseToken,
        RetentionCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<Result<bool, string>> MarkAppliedAsync(
        Guid runId,
        Guid leaseToken,
        RetentionCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task CompleteAsync(
        RetentionRunRequest request,
        RetentionRunResult result,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed class RetentionWorkflow(
    IEnumerable<IRetentionTarget> targets,
    IRetentionStore store,
    TimeProvider timeProvider)
{
    private readonly Dictionary<string, IRetentionTarget> targets =
        (targets ?? throw new ArgumentNullException(nameof(targets)))
        .ToDictionary(target => target.Target, StringComparer.Ordinal);
    private readonly IRetentionStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<RetentionRunResult, string>> ExecuteAsync(
        RetentionRunRequest request,
        CancellationToken cancellationToken)
    {
        var validation = Validate(request);
        if (validation is not null)
        {
            return Result.Fail<RetentionRunResult, string>(validation);
        }

        if (!targets.TryGetValue(request.Policy.Target, out var target))
        {
            return Result.Fail<RetentionRunResult, string>("retention_target_not_registered");
        }

        var now = timeProvider.GetUtcNow();
        var reservation = await store.TryStartAsync(request, now, cancellationToken)
            .ConfigureAwait(false);
        if (reservation.IsFailure)
        {
            return Result.Fail<RetentionRunResult, string>(reservation.Error);
        }

        if (!reservation.Success.CanExecute)
        {
            return reservation.Success.ExistingResult is null
                ? Result.Fail<RetentionRunResult, string>("retention_run_in_progress")
                : Result.Succeed<RetentionRunResult, string>(
                    reservation.Success.ExistingResult with { IsDuplicate = true });
        }

        try
        {
            return await ExecuteReservedAsync(
                    request,
                    target,
                    reservation.Success,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result.Fail<RetentionRunResult, string>("retention_operation_timeout");
        }
    }

    private async Task<Result<RetentionRunResult, string>> ExecuteReservedAsync(
        RetentionRunRequest request,
        IRetentionTarget target,
        RetentionRunReservation reservation,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            request.Policy.OperationTimeout,
            timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token);
        var hostStopTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var discoveredCandidates = await AwaitTargetAsync(
                () => target.FindEligibleAsync(
                    startedAtUtc.Subtract(request.Policy.RetainFor),
                    request.Policy.BatchSize,
                    linked.Token),
                timeoutTask,
                hostStopTask,
                cancellationToken,
                timeout.Token)
            .ConfigureAwait(false);
        if (discoveredCandidates.Count > request.Policy.BatchSize)
        {
            return Result.Fail<RetentionRunResult, string>("retention_target_exceeded_batch");
        }

        if (discoveredCandidates.Any(candidate =>
                !string.Equals(candidate.Target, request.Policy.Target, StringComparison.Ordinal)))
        {
            return Result.Fail<RetentionRunResult, string>("retention_target_mismatch");
        }

        var batch = await store.GetOrCreateBatchAsync(
                reservation.RunId,
                reservation.LeaseToken,
                discoveredCandidates,
                timeProvider.GetUtcNow(),
                linked.Token)
            .ConfigureAwait(false);
        if (batch.IsFailure)
        {
            return Result.Fail<RetentionRunResult, string>(batch.Error);
        }

        foreach (var item in batch.Success)
        {
            if (item.Status is RetentionBatchItemStatus.Applied or RetentionBatchItemStatus.Held)
            {
                continue;
            }

            var decision = await store.TryBeginApplyAsync(
                    reservation.RunId,
                    reservation.LeaseToken,
                    item.Candidate,
                    timeProvider.GetUtcNow(),
                    linked.Token)
                .ConfigureAwait(false);
            if (decision.IsFailure)
            {
                return Result.Fail<RetentionRunResult, string>(decision.Error);
            }

            if (decision.Success.Decision == RetentionApplyDecision.Held)
            {
                continue;
            }

            if (request.Mode == RetentionMode.Apply)
            {
                var permit = decision.Success.Permit ??
                    throw new InvalidOperationException(
                        "An apply authorization must include its fencing permit.");
                var applied = await AwaitTargetAsync(
                        () => target.ApplyAsync(
                            permit,
                            linked.Token),
                        timeoutTask,
                        hostStopTask,
                        cancellationToken,
                        timeout.Token)
                    .ConfigureAwait(false);
                if (applied.IsFailure)
                {
                    return Result.Fail<RetentionRunResult, string>(applied.Error);
                }
            }

            var marked = await store.MarkAppliedAsync(
                    reservation.RunId,
                    reservation.LeaseToken,
                    item.Candidate,
                    timeProvider.GetUtcNow(),
                    linked.Token)
                .ConfigureAwait(false);
            if (marked.IsFailure)
            {
                return Result.Fail<RetentionRunResult, string>(marked.Error);
            }
        }

        var finalBatch = await store.GetOrCreateBatchAsync(
                reservation.RunId,
                reservation.LeaseToken,
                [],
                timeProvider.GetUtcNow(),
                linked.Token)
            .ConfigureAwait(false);
        if (finalBatch.IsFailure)
        {
            return Result.Fail<RetentionRunResult, string>(finalBatch.Error);
        }

        var result = new RetentionRunResult(
            reservation.RunId,
            request.Mode,
            finalBatch.Success.Count,
            finalBatch.Success.Count(item => item.Status == RetentionBatchItemStatus.Held),
            request.Mode == RetentionMode.Apply
                ? finalBatch.Success.Count(item => item.Status == RetentionBatchItemStatus.Applied)
                : 0,
            false);
        await store.CompleteAsync(
                request,
                result,
                reservation.LeaseToken,
                timeProvider.GetUtcNow(),
                linked.Token)
            .ConfigureAwait(false);
        return Result.Succeed<RetentionRunResult, string>(result);
    }

    private static async Task<T> AwaitTargetAsync<T>(
        Func<Task<T>> operation,
        Task timeoutTask,
        Task hostStopTask,
        CancellationToken cancellationToken,
        CancellationToken timeoutToken)
    {
        var operationTask = operation();
        var completedTask = await Task.WhenAny(operationTask, hostStopTask, timeoutTask)
            .ConfigureAwait(false);
        if (completedTask == operationTask)
        {
            return await operationTask.ConfigureAwait(false);
        }

        ObserveDetached(operationTask);
        throw cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new OperationCanceledException(timeoutToken);
    }

    private static async Task AwaitTargetAsync(
        Func<Task> operation,
        Task timeoutTask,
        Task hostStopTask,
        CancellationToken cancellationToken,
        CancellationToken timeoutToken)
    {
        var operationTask = operation();
        var completedTask = await Task.WhenAny(operationTask, hostStopTask, timeoutTask)
            .ConfigureAwait(false);
        if (completedTask == operationTask)
        {
            await operationTask.ConfigureAwait(false);
            return;
        }

        ObserveDetached(operationTask);
        throw cancellationToken.IsCancellationRequested
            ? new OperationCanceledException(cancellationToken)
            : new OperationCanceledException(timeoutToken);
    }

    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static string? Validate(RetentionRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Policy);

        return string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Actor) ||
            request.Actor.Length > 200 ||
            string.IsNullOrWhiteSpace(request.CorrelationId) ||
            request.CorrelationId.Length > 128 ||
            string.IsNullOrWhiteSpace(request.Policy.Name) ||
            string.IsNullOrWhiteSpace(request.Policy.Target) ||
            request.Policy.RetainFor <= TimeSpan.Zero ||
            request.Policy.BatchSize is < 1 or > 10_000 ||
            request.Policy.OperationTimeout <= TimeSpan.Zero ||
            request.Policy.OperationTimeout > TimeSpan.FromHours(1)
                ? "invalid_retention_request"
                : null;
    }
}
