using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Application.Operations.Telemetry;

namespace Husaynia.Application.Tests.Operations.Jobs;

public sealed class DurableJobProcessorTests
{
    [Fact]
    public async Task CorrelationFlowsThroughHandlerExecution()
    {
        var correlation = new CorrelationContext();
        CorrelationSnapshot? observed = null;
        var processor = CreateProcessor(
            new FakeStore(CreateJob()),
            [new DelegateHandler("handler", (_, _) =>
            {
                observed = correlation.Current;
                return Task.FromResult(JobHandlerResult.Succeeded);
            })],
            correlation);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.Equal("synthetic-job-correlation", observed!.CorrelationId);
        Assert.Equal(32, observed.TraceId.Length);
    }

    [Fact]
    public async Task SuccessfulHandlerCompletesAndRecordsOutcome()
    {
        var store = new FakeStore(CreateJob());
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.Equal(1, store.CompleteCalls);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "Completed");
    }

    [Fact]
    public async Task AcquireStoreFailureHasExplicitAcquisitionFailureDisposition()
    {
        var store = new FakeStore(CreateJob())
        {
            AcquireError = new JobStoreError(
                JobStoreErrorCode.PersistenceFailure,
                "hostile password=hunter2"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.False(outcome.Acquired);
        Assert.True(outcome.AcquisitionFailed);
        Assert.Equal("StoreFailure", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.PersistenceFailure.ToString(), outcome.ErrorCode);
        Assert.Empty(metrics.Outcomes);
    }

    [Fact]
    public async Task HandlerReportedFailureSchedulesRetryWithoutThrowing()
    {
        var store = new FakeStore(CreateJob());
        var processor = CreateProcessor(
            store,
            [new DelegateHandler(
                "handler",
                (_, _) => Task.FromResult(JobHandlerResult.Failed("DependencyUnavailable")))],
            new CorrelationContext());

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.Equal("DependencyUnavailable", outcome.ErrorCode);
        Assert.Equal("DependencyUnavailable", store.LastErrorCode);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task LongRunningHandlerRenewsLeaseBeforeCompleting()
    {
        var store = new FakeStore(CreateJob());
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(60), token);
                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                HandlerTimeout = TimeSpan.FromSeconds(1),
            });

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("Completed", outcome.FinalState);
        Assert.True(store.RenewCalls >= 1);
    }

    [Fact]
    public async Task RenewalLossCancelsHandlerWithoutMutatingLostLease()
    {
        var store = new FakeStore(CreateJob()) { LoseRenewal = true };
        var cancellationObserved =
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.SetResult();
                    throw;
                }

                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                HandlerTimeout = TimeSpan.FromSeconds(5),
            });

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(0, store.FailCalls);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HandlerTimeoutSchedulesFailure()
    {
        var store = new FakeStore(CreateJob());
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", (_, token) =>
                WaitForCancellationAsync(token))],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
                HandlerTimeout = TimeSpan.FromMilliseconds(20),
            });

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.Equal("HandlerTimeoutOrCancellation", store.LastErrorCode);
    }

    [Fact]
    public async Task CancellationIgnoringHandlerTimesOutWithoutLateCompletion()
    {
        var store = new FakeStore(CreateJob());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                HandlerTimeout = TimeSpan.FromMilliseconds(20),
            });

        var execution = processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);
        await started.Task;
        var outcome = await execution.WaitAsync(TimeSpan.FromSeconds(1));
        var renewalsAfterTimeout = store.RenewCalls;

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.Equal("HandlerTimeoutOrCancellation", outcome.ErrorCode);
        Assert.Equal(1, store.FailCalls);
        Assert.Equal(0, store.CompleteCalls);

        release.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(renewalsAfterTimeout, store.RenewCalls);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task RenewalLossDuringHandlerExceptionDoesNotMutateLostLease()
    {
        var store = new FakeStore(CreateJob()) { LoseRenewal = true };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("handler failed after lease loss");
                }

                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                HandlerTimeout = TimeSpan.FromSeconds(5),
            },
            metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.LeaseLost.ToString(), outcome.ErrorCode);
        Assert.Equal(0, store.FailCalls);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(
            metrics.Outcomes,
            item => item.Outcome is "retry_scheduled" or "dead_lettered");
    }

    [Fact]
    public async Task ThrownExceptionRetriesThenDeadLettersDeterministically()
    {
        var retryStore = new FakeStore(CreateJob(attemptNumber: 1, maximumAttempts: 2));
        var retryProcessor = CreateProcessor(
            retryStore,
            [new DelegateHandler(
                "handler",
                (_, _) => throw new SyntheticJobException())],
            new CorrelationContext());
        var deadLetterStore = new FakeStore(CreateJob(attemptNumber: 2, maximumAttempts: 2));
        var deadLetterProcessor = CreateProcessor(
            deadLetterStore,
            [new DelegateHandler(
                "handler",
                (_, _) => throw new SyntheticJobException())],
            new CorrelationContext());

        var retry = await retryProcessor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);
        var deadLetter = await deadLetterProcessor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("RetryScheduled", retry.FinalState);
        Assert.NotNull(retryStore.NextRunAtUtc);
        Assert.Equal("DeadLettered", deadLetter.FinalState);
        Assert.Null(deadLetterStore.NextRunAtUtc);
        Assert.Equal(nameof(SyntheticJobException), retry.ErrorCode);
        Assert.Equal(nameof(SyntheticJobException), deadLetter.ErrorCode);
    }

    [Fact]
    public async Task HostStopReleasesLeaseForRecoveryAndPropagatesCancellation()
    {
        var store = new FakeStore(CreateJob());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
                RecoveryDelay = TimeSpan.FromSeconds(7),
                HandlerTimeout = TimeSpan.FromSeconds(5),
            });
        using var stopping = new CancellationTokenSource();
        var execution = processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            stopping.Token);
        await started.Task;

        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(1, store.ReleaseCalls);
        Assert.NotNull(store.RecoveryAtUtc);
    }

    [Fact]
    public async Task HostStopDoesNotWaitForCancellationIgnoringHandlerOrAllowLateCompletion()
    {
        var store = new FakeStore(CreateJob());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return JobHandlerResult.Succeeded;
            })],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                RecoveryDelay = TimeSpan.FromSeconds(7),
                HandlerTimeout = TimeSpan.FromSeconds(5),
            });
        using var stopping = new CancellationTokenSource();
        var execution = processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            stopping.Token);
        await started.Task;

        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(1)));
        var renewalsAfterStop = store.RenewCalls;
        Assert.Equal(1, store.ReleaseCalls);
        Assert.Equal(0, store.FailCalls);
        Assert.Equal(0, store.CompleteCalls);

        release.SetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(renewalsAfterStop, store.RenewCalls);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task RenewalExceptionCancelsHandlerAndDoesNotMutateLease()
    {
        var store = new FakeStore(CreateJob()) { ThrowOnRenewal = true };
        var processor = CreateProcessor(
            store,
            [new DelegateHandler("handler", (_, token) => WaitForCancellationAsync(token))],
            new CorrelationContext(),
            new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(10),
                HandlerTimeout = TimeSpan.FromSeconds(5),
            });

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(0, store.FailCalls);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task FailStoreFailureDoesNotEmitRetryOutcome()
    {
        var store = new FakeStore(CreateJob())
        {
            FailError = new JobStoreError(
                JobStoreErrorCode.PersistenceFailure,
                "synthetic failure"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [new DelegateHandler(
                "handler",
                (_, _) => throw new InvalidOperationException("handler failed"))],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("StoreFailure", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.PersistenceFailure.ToString(), outcome.ErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "store_failure");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(
            metrics.Outcomes,
            item => item.Outcome is "retry_scheduled" or "dead_lettered");
    }

    [Fact]
    public async Task FailLeaseLostResultIsReportedAndMeteredAsLeaseLost()
    {
        var store = new FakeStore(CreateJob())
        {
            FailError = new JobStoreError(
                JobStoreErrorCode.LeaseLost,
                "synthetic lease loss"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [new DelegateHandler(
                "handler",
                (_, _) => throw new InvalidOperationException("handler failed"))],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.LeaseLost.ToString(), outcome.ErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "store_failure");
        Assert.DoesNotContain(
            metrics.Outcomes,
            item => item.Outcome is "retry_scheduled" or "dead_lettered");
    }

    [Fact]
    public async Task CancellationFailLeaseLostResultIsReportedAndMeteredAsLeaseLost()
    {
        var store = new FakeStore(CreateJob(cancellationRequested: true))
        {
            FailError = new JobStoreError(
                JobStoreErrorCode.LeaseLost,
                "synthetic lease loss"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.LeaseLost.ToString(), outcome.ErrorCode);
        Assert.Equal("Cancelled", store.LastErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "store_failure");
    }

    [Fact]
    public async Task CompletePersistenceFailureIsReportedAndMeteredAsStoreFailure()
    {
        var store = new FakeStore(CreateJob())
        {
            CompleteError = new JobStoreError(
                JobStoreErrorCode.PersistenceFailure,
                "synthetic completion failure"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("StoreFailure", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.PersistenceFailure.ToString(), outcome.ErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "store_failure");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "lease_lost");
    }

    [Fact]
    public async Task CompleteLeaseLossIsReportedAndMeteredAsLeaseLost()
    {
        var store = new FakeStore(CreateJob()) { CompleteResult = false };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.LeaseLost.ToString(), outcome.ErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "store_failure");
    }

    [Fact]
    public async Task CompleteLeaseLostResultIsReportedAndMeteredAsLeaseLost()
    {
        var store = new FakeStore(CreateJob())
        {
            CompleteError = new JobStoreError(
                JobStoreErrorCode.LeaseLost,
                "synthetic lease loss"),
        };
        var metrics = new CapturingMetrics();
        var processor = CreateProcessor(
            store,
            [SuccessfulHandler()],
            new CorrelationContext(),
            metrics: metrics);

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("LeaseLost", outcome.FinalState);
        Assert.Equal(JobStoreErrorCode.LeaseLost.ToString(), outcome.ErrorCode);
        Assert.Contains(metrics.Outcomes, item => item.Outcome == "lease_lost");
        Assert.DoesNotContain(metrics.Outcomes, item => item.Outcome == "store_failure");
    }

    [Fact]
    public async Task UnregisteredHandlerFailsDeliberately()
    {
        var store = new FakeStore(CreateJob(handlerName: "missing"));
        var processor = CreateProcessor(store, [], new CorrelationContext());

        var outcome = await processor.ExecuteNextAsync(
            new WorkerIdentity("worker"),
            CancellationToken.None);

        Assert.Equal("RetryScheduled", outcome.FinalState);
        Assert.Equal("HandlerNotRegistered", store.LastErrorCode);
    }

    [Fact]
    public async Task DuplicateHandlerNamesFailFastInExecutionScope()
    {
        var handlers = new[] { SuccessfulHandler(), SuccessfulHandler() };
        var processor = CreateProcessor(
            new FakeStore(CreateJob()),
            handlers,
            new CorrelationContext());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            processor.ExecuteNextAsync(
                new WorkerIdentity("worker"),
                CancellationToken.None));
    }

    private static DurableJobProcessor CreateProcessor(
        FakeStore store,
        IEnumerable<IJobHandler> handlers,
        ICorrelationContext correlation,
        DurableJobOptions? options = null,
        IOperationsMetrics? metrics = null) =>
        new(
            store,
            handlers,
            options ?? new DurableJobOptions
            {
                LeaseDuration = TimeSpan.FromSeconds(1),
                LeaseRenewalInterval = TimeSpan.FromMilliseconds(100),
                HandlerTimeout = TimeSpan.FromSeconds(1),
            },
            new ExponentialJitterBackoffPolicy(
                new DurableJobOptions { JitterRatio = 0 },
                new Random(1)),
            metrics ?? new NullMetrics(),
            correlation,
            TimeProvider.System);

    private static AcquiredJob CreateJob(
        string handlerName = "handler",
        int attemptNumber = 1,
        int maximumAttempts = 3,
        bool cancellationRequested = false) =>
        new(
            Guid.NewGuid(),
            "definition",
            handlerName,
            "{}",
            "idempotency",
            "synthetic-job-correlation",
            attemptNumber,
            maximumAttempts,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1),
            new WorkerIdentity("worker"),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationRequested);

    private sealed class DelegateHandler(
        string handlerName,
        Func<string, CancellationToken, Task<JobHandlerResult>> execute) : IJobHandler
    {
        public string HandlerName => handlerName;

        public Task<JobHandlerResult> ExecuteAsync(
            string payloadJson,
            JobExecutionContext context,
            CancellationToken cancellationToken) =>
            execute(payloadJson, cancellationToken);
    }

    private static DelegateHandler SuccessfulHandler() =>
        new(
            "handler",
            (_, _) => Task.FromResult(JobHandlerResult.Succeeded));

    private static async Task<JobHandlerResult> WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return JobHandlerResult.Succeeded;
    }

    private sealed class FakeStore(AcquiredJob job) : IDurableJobStore
    {
        private bool acquired;

        internal bool LoseRenewal { get; set; }

        internal bool ThrowOnRenewal { get; set; }

        internal JobStoreError? AcquireError { get; set; }

        internal JobStoreError? FailError { get; set; }

        internal JobStoreError? CompleteError { get; set; }

        internal bool CompleteResult { get; set; } = true;

        internal int FailCalls { get; private set; }

        internal int CompleteCalls { get; private set; }

        internal int RenewCalls { get; private set; }

        internal int ReleaseCalls { get; private set; }

        internal string? LastErrorCode { get; private set; }

        internal DateTimeOffset? NextRunAtUtc { get; private set; }

        internal DateTimeOffset? RecoveryAtUtc { get; private set; }

        public Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
            WorkerIdentity worker,
            TimeSpan leaseDuration,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            string? definitionKey = null)
        {
            if (AcquireError is not null)
            {
                return Task.FromResult(
                    Result.Fail<JobAcquireResult, JobStoreError>(AcquireError));
            }

            var result = acquired ? new JobAcquireResult(null) : new JobAcquireResult(job);
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
            if (ThrowOnRenewal)
            {
                throw new InvalidOperationException("synthetic renewal failure");
            }

            return Task.FromResult(Result.Succeed<bool, JobStoreError>(!LoseRenewal));
        }

        public Task<Result<bool, JobStoreError>> CompleteAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.FromResult(CompleteError is null
                ? Result.Succeed<bool, JobStoreError>(CompleteResult)
                : Result.Fail<bool, JobStoreError>(CompleteError));
        }

        public Task<Result<bool, JobStoreError>> FailAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            string errorCode,
            DateTimeOffset? nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            FailCalls++;
            LastErrorCode = errorCode;
            NextRunAtUtc = nextRunAtUtc;
            if (FailError is not null)
            {
                return Task.FromResult(Result.Fail<bool, JobStoreError>(FailError));
            }

            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
            Guid jobInstanceId,
            Guid leaseToken,
            DateTimeOffset nextRunAtUtc,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            ReleaseCalls++;
            RecoveryAtUtc = nextRunAtUtc;
            return Task.FromResult(Result.Succeed<bool, JobStoreError>(true));
        }

        public Task<Result<bool, JobStoreError>> RequestCancellationAsync(
            Guid jobInstanceId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<bool, JobStoreError>(true));

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

    private sealed class SyntheticJobException : Exception
    {
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

    private sealed class CapturingMetrics : IOperationsMetrics
    {
        internal List<(string Definition, string Outcome, int Attempt)> Outcomes { get; } = [];

        public void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0)
        {
        }

        public void RecordJob(string definition, string outcome, int attempt) =>
            Outcomes.Add((definition, outcome, attempt));

        public void RecordStaleness(string dataSet, TimeSpan age)
        {
        }
    }
}
