using TCFUploader.Configuration;
using TCFUploader.Hosting;
using TCFUploader.State;
using TCFUploader.Time;

namespace TCFUploader.Upload;

internal enum WorkerOutcome { Completed, Deferred, AuthenticationFatal, StorageFatal }

internal sealed class UploadWorker(
    StateRepository repository,
    LumaBoothClient client,
    RetryPolicy retryPolicy,
    IClock clock,
    RuntimeOptions options,
    string token)
{
    internal async Task<WorkerOutcome> ProcessAsync(
        string fingerprint,
        CancellationToken cancellationToken,
        CancellationToken stopStartingOperations = default,
        AttemptAdmission? sharedAdmission = null)
    {
        var item = repository.GetRequired(fingerprint);
        if (item.Status == UploadStatus.Completed) return WorkerOutcome.Completed;
        if (item.Status == UploadStatus.PendingPut)
        {
            if (StopRequested())
                return WorkerOutcome.Deferred;
            WatcherCoordinator.Log("INFO", "upload", item.RelativePath, "put", 1, fingerprint[..12], "starting");
            var put = await retryPolicy.ExecuteAsync(
                (_, cancellation) => client.PutAsync(item, repository.ResolveSpool(item), token, cancellation),
                result => result is PutResult.Failure failed ? failed.Error : null,
                cancellationToken,
                stopStartingOperations,
                sharedAdmission);
            if (put.StopRequested)
            {
                WatcherCoordinator.Log(
                    "INFO",
                    "upload",
                    item.RelativePath,
                    "put",
                    put.Attempts,
                    fingerprint[..12],
                    "shutdown_deferred");
                return WorkerOutcome.Deferred;
            }
            if (put.Failure is not null)
            {
                WatcherCoordinator.Log("ERROR", "upload", item.RelativePath, "put", put.Attempts,
                    fingerprint[..12], put.Failure.OutcomeCode);
                await repository.RecordCycleFailureAsync(fingerprint, put.Failure.OutcomeCode, put.Attempts,
                    clock.UtcNow + options.FailedCycleDelay, cancellationToken);
                return put.Failure.IsAuthenticationFatal ? WorkerOutcome.AuthenticationFatal : WorkerOutcome.Deferred;
            }
            var remote = ((PutResult.Success)put.LastResult).RemoteUrl;
            await repository.MarkPutCompleteAsync(fingerprint, remote, cancellationToken);
            item = repository.GetRequired(fingerprint);
        }

        if (StopRequested())
        {
            WatcherCoordinator.Log(
                "INFO",
                "upload",
                item.RelativePath,
                "post",
                0,
                fingerprint[..12],
                "shutdown_deferred");
            return WorkerOutcome.Deferred;
        }
        WatcherCoordinator.Log("INFO", "upload", item.RelativePath, "post", 1, fingerprint[..12], "starting");
        var post = await retryPolicy.ExecuteAsync(
            (_, cancellation) => client.PostAsync(item, token, cancellation),
            result => result is PostResult.Failure failed ? failed.Error : null,
            cancellationToken,
            stopStartingOperations,
            sharedAdmission);
        if (post.StopRequested)
        {
            WatcherCoordinator.Log(
                "INFO",
                "upload",
                item.RelativePath,
                "post",
                post.Attempts,
                fingerprint[..12],
                "shutdown_deferred");
            return WorkerOutcome.Deferred;
        }
        if (post.Failure is not null)
        {
            WatcherCoordinator.Log("ERROR", "upload", item.RelativePath, "post", post.Attempts,
                fingerprint[..12], post.Failure.OutcomeCode);
            await repository.RecordCycleFailureAsync(fingerprint, post.Failure.OutcomeCode, post.Attempts,
                clock.UtcNow + options.FailedCycleDelay, cancellationToken);
            return post.Failure.IsAuthenticationFatal ? WorkerOutcome.AuthenticationFatal : WorkerOutcome.Deferred;
        }

        await repository.MarkCompletedAsync(fingerprint, cancellationToken);
        try
        {
            repository.DeleteCompletedSpool(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WatcherCoordinator.Log("ERROR", "upload", item.RelativePath, "cleanup", post.Attempts,
                fingerprint[..12], "completed_spool_cleanup_failed");
            return WorkerOutcome.StorageFatal;
        }
        WatcherCoordinator.Log("INFO", "upload", item.RelativePath, "complete", post.Attempts,
            fingerprint[..12], "completed");
        return WorkerOutcome.Completed;

        bool StopRequested() =>
            stopStartingOperations.IsCancellationRequested ||
            sharedAdmission?.IsStopped == true;
    }
}
