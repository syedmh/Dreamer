using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.Application.Operations.Jobs;

public sealed class DurableJobProcessor
{
    private static readonly TimeSpan HandlerShutdownGrace =
        TimeSpan.FromMilliseconds(250);
    private readonly IDurableJobStore store;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly DurableJobOptions options;
    private readonly IJobBackoffPolicy backoffPolicy;
    private readonly IOperationsMetrics metrics;
    private readonly ICorrelationContext correlation;
    private readonly TimeProvider timeProvider;

    public DurableJobProcessor(
        IDurableJobStore store,
        IServiceScopeFactory scopeFactory,
        DurableJobOptions options,
        IJobBackoffPolicy backoffPolicy,
        IOperationsMetrics metrics,
        ICorrelationContext correlation,
        TimeProvider timeProvider)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.backoffPolicy = backoffPolicy ?? throw new ArgumentNullException(nameof(backoffPolicy));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public DurableJobProcessor(
        IDurableJobStore store,
        IEnumerable<IJobHandler> handlers,
        DurableJobOptions options,
        IJobBackoffPolicy backoffPolicy,
        IOperationsMetrics metrics,
        ICorrelationContext correlation,
        TimeProvider timeProvider)
        : this(
            store,
            new FixedHandlerScopeFactory(handlers),
            options,
            backoffPolicy,
            metrics,
            correlation,
            timeProvider)
    {
    }

    public async Task<JobExecutionOutcome> ExecuteNextAsync(
        WorkerIdentity worker,
        CancellationToken cancellationToken)
    {
        var acquiredResult = await store.TryAcquireNextAsync(
                worker,
                options.LeaseDuration,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (acquiredResult.IsFailure)
        {
            return new JobExecutionOutcome(
                false,
                null,
                "StoreFailure",
                acquiredResult.Error.Code.ToString())
            {
                AcquisitionFailed = true,
            };
        }

        var job = acquiredResult.Success.Job;
        if (job is null)
        {
            return new JobExecutionOutcome(false, null, null, null);
        }

        using var correlationScope = correlation.Begin(
            job.CorrelationId,
            $"job.{job.DefinitionKey}");

        if (job.CancellationRequested)
        {
            var cancelled = await store.FailAsync(
                    job.JobInstanceId,
                    job.LeaseToken,
                    "Cancelled",
                    null,
                    timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (cancelled.IsFailure)
            {
                return StoreTransitionFailureOutcome(job, cancelled.Error);
            }

            if (!cancelled.Success)
            {
                metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                return new JobExecutionOutcome(
                    true,
                    job.JobInstanceId,
                    "LeaseLost",
                    JobStoreErrorCode.LeaseLost.ToString());
            }

            metrics.RecordJob(job.DefinitionKey, "cancelled", job.AttemptNumber);
            return new JobExecutionOutcome(true, job.JobInstanceId, "Cancelled", null);
        }

        await using var handlerScope =
            new HandlerExecutionScope(scopeFactory.CreateAsyncScope(), metrics);
        var handlers = handlerScope.ServiceProvider
            .GetServices<IJobHandler>()
            .ToDictionary(handler => handler.HandlerName, StringComparer.Ordinal);
        if (!handlers.TryGetValue(job.HandlerName, out var handler))
        {
            var transitionFailure = await TryFailAsync(
                    job,
                    "HandlerNotRegistered",
                    cancellationToken)
                .ConfigureAwait(false);
            if (transitionFailure is not null)
            {
                return transitionFailure;
            }

            return FailureOutcome(job, "HandlerNotRegistered");
        }

        using var handlerTimeout = new CancellationTokenSource(
            options.HandlerTimeout,
            timeProvider);
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        using var timeoutRegistration = handlerTimeout.Token.Register(
            static state => ((CancellationTokenSource)state!).Cancel(),
            handlerCancellation);
        using var renewalCancellation = new CancellationTokenSource();
        var renewalTask = RenewLeaseAsync(
            job,
            renewalCancellation,
            handlerCancellation);
        Task<JobHandlerResult>? handlerTask = null;

        try
        {
            handlerTask = handler.ExecuteAsync(
                    job.PayloadJson,
                    new JobExecutionContext(
                        job.JobInstanceId,
                        job.IdempotencyKey,
                        job.CorrelationId,
                        job.AttemptNumber),
                    handlerCancellation.Token);
            var completedTask = await Task.WhenAny(
                    handlerTask,
                    renewalTask,
                    Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                    Task.Delay(Timeout.InfiniteTimeSpan, handlerTimeout.Token))
                .ConfigureAwait(false);

            if (completedTask != handlerTask)
            {
                handlerCancellation.Cancel();
                renewalCancellation.Cancel();
                var orchestrationLeaseRetained =
                    await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
                await StopOrDetachHandlerAsync(handlerTask, handlerScope)
                    .ConfigureAwait(false);

                if (!orchestrationLeaseRetained)
                {
                    metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                    cancellationToken.ThrowIfCancellationRequested();

                    return new JobExecutionOutcome(
                        true,
                        job.JobInstanceId,
                        "LeaseLost",
                        JobStoreErrorCode.LeaseLost.ToString());
                }

                cancellationToken.ThrowIfCancellationRequested();

                var timeoutFailure = await TryFailAsync(
                        job,
                        "HandlerTimeoutOrCancellation",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return timeoutFailure ?? FailureOutcome(job, "HandlerTimeoutOrCancellation");
            }

            var handlerResult = await handlerTask.ConfigureAwait(false);
            renewalCancellation.Cancel();
            var leaseRetained = await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
            if (!leaseRetained)
            {
                metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                return new JobExecutionOutcome(
                    true,
                    job.JobInstanceId,
                    "LeaseLost",
                    JobStoreErrorCode.LeaseLost.ToString());
            }

            if (!handlerResult.IsSuccess)
            {
                var errorCode = handlerResult.ErrorCode ?? "HandlerReportedFailure";
                var transitionFailure = await TryFailAsync(
                        job,
                        errorCode,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return transitionFailure ?? FailureOutcome(job, errorCode);
            }

            var completion = await store.CompleteAsync(
                    job.JobInstanceId,
                    job.LeaseToken,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (completion.IsFailure)
            {
                return StoreTransitionFailureOutcome(job, completion.Error);
            }

            if (!completion.Success)
            {
                metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                return new JobExecutionOutcome(
                    true,
                    job.JobInstanceId,
                    "LeaseLost",
                    JobStoreErrorCode.LeaseLost.ToString());
            }

            metrics.RecordJob(job.DefinitionKey, "Completed", job.AttemptNumber);
            return new JobExecutionOutcome(
                true,
                job.JobInstanceId,
                "Completed",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            handlerCancellation.Cancel();
            renewalCancellation.Cancel();
            var leaseRetained = await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
            if (handlerTask is not null && !handlerTask.IsCompleted)
            {
                await StopOrDetachHandlerAsync(handlerTask, handlerScope)
                    .ConfigureAwait(false);
            }

            if (leaseRetained)
            {
                var release = await store.ReleaseForRecoveryAsync(
                        job.JobInstanceId,
                        job.LeaseToken,
                        timeProvider.GetUtcNow().Add(options.RecoveryDelay),
                        timeProvider.GetUtcNow(),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                metrics.RecordJob(
                    job.DefinitionKey,
                    release.IsSuccess && release.Success ? "released" : "release_failed",
                    job.AttemptNumber);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            renewalCancellation.Cancel();
            var leaseRetained = await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
            if (!leaseRetained)
            {
                metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                return new JobExecutionOutcome(
                    true,
                    job.JobInstanceId,
                    "LeaseLost",
                    JobStoreErrorCode.LeaseLost.ToString());
            }

            var transitionFailure = await TryFailAsync(
                    job,
                    "HandlerTimeoutOrCancellation",
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (transitionFailure is not null)
            {
                return transitionFailure;
            }

            return FailureOutcome(job, "HandlerTimeoutOrCancellation");
        }
        catch (Exception exception)
        {
            renewalCancellation.Cancel();
            var leaseRetained = await ObserveRenewalAsync(renewalTask).ConfigureAwait(false);
            if (!leaseRetained)
            {
                metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
                return new JobExecutionOutcome(
                    true,
                    job.JobInstanceId,
                    "LeaseLost",
                    JobStoreErrorCode.LeaseLost.ToString());
            }

            var errorCode = exception.GetType().Name;
            var transitionFailure = await TryFailAsync(
                    job,
                    errorCode,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (transitionFailure is not null)
            {
                return transitionFailure;
            }

            return FailureOutcome(job, errorCode);
        }
    }

    private async Task<bool> RenewLeaseAsync(
        AcquiredJob job,
        CancellationTokenSource renewalCancellation,
        CancellationTokenSource handlerCancellation)
    {
        using var timer = new PeriodicTimer(options.LeaseRenewalInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(renewalCancellation.Token).ConfigureAwait(false))
            {
                var renewal = await store.RenewAsync(
                        job.JobInstanceId,
                        job.LeaseToken,
                        options.LeaseDuration,
                        timeProvider.GetUtcNow(),
                        renewalCancellation.Token)
                    .ConfigureAwait(false);
                if (renewal.IsFailure || !renewal.Success)
                {
                    handlerCancellation.Cancel();
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            handlerCancellation.Cancel();
            return false;
        }

        return true;
    }

    private static JobExecutionOutcome FailureOutcome(
        AcquiredJob job,
        string errorCode) =>
        new(
            true,
            job.JobInstanceId,
            job.AttemptNumber >= job.MaximumAttempts ? "DeadLettered" : "RetryScheduled",
            errorCode);

    private async Task<JobExecutionOutcome?> TryFailAsync(
        AcquiredJob job,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var deadLetter = job.AttemptNumber >= job.MaximumAttempts;
        DateTimeOffset? nextRun = deadLetter
            ? null
            : timeProvider.GetUtcNow().Add(backoffPolicy.GetDelay(job));
        var failure = await store.FailAsync(
                job.JobInstanceId,
                job.LeaseToken,
                errorCode,
                nextRun,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (failure.IsFailure)
        {
            return StoreTransitionFailureOutcome(job, failure.Error);
        }

        if (!failure.Success)
        {
            metrics.RecordJob(job.DefinitionKey, "lease_lost", job.AttemptNumber);
            return new JobExecutionOutcome(
                true,
                job.JobInstanceId,
                "LeaseLost",
                JobStoreErrorCode.LeaseLost.ToString());
        }

        metrics.RecordJob(
            job.DefinitionKey,
            deadLetter ? "dead_lettered" : "retry_scheduled",
            job.AttemptNumber);
        return null;
    }

    private JobExecutionOutcome StoreTransitionFailureOutcome(
        AcquiredJob job,
        JobStoreError error)
    {
        var leaseLost = error.Code == JobStoreErrorCode.LeaseLost;
        metrics.RecordJob(
            job.DefinitionKey,
            leaseLost ? "lease_lost" : "store_failure",
            job.AttemptNumber);
        return new JobExecutionOutcome(
            true,
            job.JobInstanceId,
            leaseLost ? "LeaseLost" : "StoreFailure",
            error.Code.ToString());
    }

    private static async Task<bool> ObserveRenewalAsync(Task<bool> renewalTask)
    {
        try
        {
            return await renewalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task StopOrDetachHandlerAsync(
        Task handlerTask,
        HandlerExecutionScope handlerScope)
    {
        if (handlerTask.IsCompleted)
        {
            await ObserveHandlerAsync(handlerTask).ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(
                handlerTask,
                Task.Delay(HandlerShutdownGrace, timeProvider, CancellationToken.None))
            .ConfigureAwait(false);
        if (completed == handlerTask)
        {
            await ObserveHandlerAsync(handlerTask).ConfigureAwait(false);
            return;
        }

        handlerScope.DetachUntilCompletion(handlerTask);
    }

    private static async Task ObserveHandlerAsync(Task handlerTask)
    {
        try
        {
            await handlerTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private sealed class HandlerExecutionScope(
        AsyncServiceScope scope,
        IOperationsMetrics metrics) : IAsyncDisposable
    {
        private bool detached;

        internal IServiceProvider ServiceProvider => scope.ServiceProvider;

        internal void DetachUntilCompletion(Task handlerTask)
        {
            if (detached)
            {
                return;
            }

            detached = true;
            _ = ObserveAndDisposeAsync(handlerTask, scope);
        }

        public ValueTask DisposeAsync() =>
            detached ? ValueTask.CompletedTask : scope.DisposeAsync();

        private async Task ObserveAndDisposeAsync(
            Task handlerTask,
            AsyncServiceScope detachedScope)
        {
            await ObserveHandlerAsync(handlerTask).ConfigureAwait(false);
            try
            {
                await detachedScope.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                metrics.RecordDependency(
                    "durable_job_handler_scope",
                    "dispose_failed");
            }
        }
    }

    private sealed class FixedHandlerScopeFactory : IServiceScopeFactory
    {
        private readonly IReadOnlyList<IJobHandler> handlers;

        internal FixedHandlerScopeFactory(IEnumerable<IJobHandler> handlers)
        {
            this.handlers = (handlers ?? throw new ArgumentNullException(nameof(handlers)))
                .ToArray();
        }

        public IServiceScope CreateScope() =>
            new FixedHandlerScope(handlers);
    }

    private sealed class FixedHandlerScope(
        IReadOnlyList<IJobHandler> handlers) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } =
            new FixedHandlerServiceProvider(handlers);

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedHandlerServiceProvider(
        IReadOnlyList<IJobHandler> handlers) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEnumerable<IJobHandler>)
                ? handlers
                : null;
    }
}
