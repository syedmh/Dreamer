using TCFUploader.Configuration;
using TCFUploader.Time;

namespace TCFUploader.Upload;

internal sealed record RetryResult<T>(
    T LastResult,
    int Attempts,
    UploadFailure? Failure,
    bool StopRequested = false);

internal sealed class RetryPolicy(
    IClock clock,
    RuntimeOptions options,
    Func<int, Task>? beforeAttemptAdmission = null)
{
    internal async Task<RetryResult<T>> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> attempt,
        Func<T, UploadFailure?> classify,
        CancellationToken cancellationToken,
        CancellationToken stopStartingAttempts = default,
        AttemptAdmission? sharedAdmission = null)
    {
        T result = default!;
        UploadFailure? failure = null;
        using var ownedAdmission = sharedAdmission is null
            ? new AttemptAdmission(stopStartingAttempts)
            : null;
        var admission = sharedAdmission ?? ownedAdmission!;
        for (var number = 1; number <= options.MaxAttempts; number++)
        {
            if (beforeAttemptAdmission is not null)
                await beforeAttemptAdmission(number);
            if (!admission.TryStart(
                    () => attempt(number, cancellationToken),
                    out var started))
                return new RetryResult<T>(result, number - 1, failure, StopRequested: true);

            result = await started!;
            failure = classify(result);
            if (failure is null || !failure.IsTransient || number == options.MaxAttempts)
            {
                return new RetryResult<T>(result, number, failure);
            }
            if (admission.IsStopped)
                return new RetryResult<T>(result, number, failure, StopRequested: true);

            var exponential = TimeSpan.FromSeconds(Math.Pow(2, number - 1));
            var delay = failure.RetryAfter ?? exponential + TimeSpan.FromMilliseconds(clock.NextJitterMilliseconds(251));
            if (delay > TimeSpan.FromSeconds(60)) delay = TimeSpan.FromSeconds(60);
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                admission.StoppedToken);
            try
            {
                await clock.DelayAsync(delay, delayCts.Token);
            }
            catch (OperationCanceledException) when (
                admission.IsStopped &&
                !cancellationToken.IsCancellationRequested)
            {
                return new RetryResult<T>(result, number, failure, StopRequested: true);
            }
        }
        return new RetryResult<T>(result, options.MaxAttempts, failure);
    }
}
