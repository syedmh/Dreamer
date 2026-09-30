using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Domain.Operations.Jobs;
using Husaynia.IntegrationTests.Persistence.Core;
using Husaynia.Infrastructure.Operations.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.IntegrationTests.Operations.Jobs;

public sealed class DurableJobStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentWorkersAcquireOnlyOneLease()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ConcurrentWorkersAcquireOnlyOneLease));
        await RegisterAndEnqueueAsync(database, "single");

        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var firstStore = new EfDurableJobStore(firstContext);
        var secondStore = new EfDurableJobStore(secondContext);

        var acquisitions = await Task.WhenAll(
            firstStore.TryAcquireNextAsync(
                new WorkerIdentity("worker-1"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None),
            secondStore.TryAcquireNextAsync(
                new WorkerIdentity("worker-2"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None));

        Assert.All(acquisitions, result => Assert.True(result.IsSuccess));
        Assert.Single(acquisitions, result => result.Success.Job is not null);
    }

    [Fact]
    public async Task ExpiredLeaseIsTakenOverAndAbandonedAttemptIsClosed()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredLeaseIsTakenOverAndAbandonedAttemptIsClosed));
        var jobId = await RegisterAndEnqueueAsync(database, "takeover");

        await using (var firstContext = database.CreateContext())
        {
            var first = await new EfDurableJobStore(firstContext).TryAcquireNextAsync(
                new WorkerIdentity("crashed-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None);
            Assert.NotNull(first.Success.Job);
        }

        await using var restartedContext = database.CreateContext();
        var takeover = await new EfDurableJobStore(restartedContext).TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.True(takeover.IsSuccess);
        Assert.NotNull(takeover.Success.Job);
        Assert.Equal(jobId, takeover.Success.Job.JobInstanceId);
        Assert.Equal(2, takeover.Success.Job.AttemptNumber);
        Assert.Equal(2, await database.CountRowsAsync("OperationsJobAttempts"));
    }

    [Fact]
    public async Task ExpiredFinalAttemptIsDeadLetteredWithoutExecutingAgain()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredFinalAttemptIsDeadLetteredWithoutExecutingAgain));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "final-attempt-crash");

        await using (var context = database.CreateContext())
        {
            var acquired = await new EfDurableJobStore(context).TryAcquireNextAsync(
                new WorkerIdentity("crashed-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None);
            Assert.Equal(1, acquired.Success.Job!.AttemptNumber);
        }

        await using var restartedContext = database.CreateContext();
        var restartedStore = new EfDurableJobStore(restartedContext);
        var reacquired = await restartedStore.TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);
        var state = await restartedStore.GetStateAsync(jobId, CancellationToken.None);

        Assert.Null(reacquired.Success.Job);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.Equal(1, state.Success.Snapshot.AttemptsStarted);
        Assert.Equal(
            "MaximumAttemptsExceededAfterLeaseExpiry",
            state.Success.Snapshot.LastErrorCode);
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobAttempts"));
    }

    [Fact]
    public async Task RetryDeadLetterAndAuditedRequeueAreDurable()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(RetryDeadLetterAndAuditedRequeueAreDurable));
        var jobId = await RegisterAndEnqueueAsync(database, "retry");

        await using (var firstContext = database.CreateContext())
        {
            var store = new EfDurableJobStore(firstContext);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            var failure = await store.FailAsync(
                lease.JobInstanceId,
                lease.LeaseToken,
                "Transient",
                Now.AddMinutes(1),
                Now,
                CancellationToken.None);
            Assert.True(failure.IsSuccess);
        }

        await using (var secondContext = database.CreateContext())
        {
            var store = new EfDurableJobStore(secondContext);
            var tooEarly = await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now.AddSeconds(30),
                CancellationToken.None);
            Assert.Null(tooEarly.Success.Job);

            var retry = (await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now.AddMinutes(1),
                CancellationToken.None)).Success.Job!;
            var deadLetter = await store.FailAsync(
                retry.JobInstanceId,
                retry.LeaseToken,
                "Permanent",
                null,
                Now.AddMinutes(1),
                CancellationToken.None);
            Assert.True(deadLetter.IsSuccess);
        }

        await using (var operatorContext = database.CreateContext())
        {
            var store = new EfDurableJobStore(operatorContext);
            var deadLettered = await store.GetStateAsync(jobId, CancellationToken.None);
            Assert.Equal("DeadLettered", deadLettered.Success.Snapshot!.State);

            var requeue = await store.RequeueDeadLetterAsync(
                jobId,
                "site-admin",
                "dependency repaired",
                "correlation-1",
                Now.AddMinutes(2),
                CancellationToken.None);
            Assert.True(requeue.IsSuccess);
        }

        Assert.Equal(1, await database.CountRowsAsync("OperationsJobAudit"));
        await using var verificationContext = database.CreateContext();
        var state = await new EfDurableJobStore(verificationContext)
            .GetStateAsync(jobId, CancellationToken.None);
        Assert.Equal("RetryScheduled", state.Success.Snapshot!.State);
    }

    [Fact]
    public async Task ExhaustedDeadLetterRequeueGetsFreshBudgetAndCompletes()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExhaustedDeadLetterRequeueGetsFreshBudgetAndCompletes));
        var jobId = await RegisterAndEnqueueAsync(database, "exhausted-requeue");

        for (var attemptNumber = 1; attemptNumber <= 2; attemptNumber++)
        {
            await using var context = database.CreateContext();
            var store = new EfDurableJobStore(context);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity($"worker-{attemptNumber}"),
                TimeSpan.FromMinutes(1),
                Now.AddMinutes(attemptNumber - 1),
                CancellationToken.None)).Success.Job!;
            Assert.Equal(attemptNumber, lease.AttemptNumber);
            Assert.True((await store.FailAsync(
                jobId,
                lease.LeaseToken,
                "Failure",
                attemptNumber == 1 ? Now.AddMinutes(1) : null,
                Now.AddMinutes(attemptNumber - 1),
                CancellationToken.None)).IsSuccess);
        }

        await using (var operatorContext = database.CreateContext())
        {
            var requeue = await new EfDurableJobStore(operatorContext)
                .RequeueDeadLetterAsync(
                    jobId,
                    "site-admin",
                    "dependency repaired",
                    "correlation-requeue",
                    Now.AddMinutes(2),
                    CancellationToken.None);
            Assert.True(requeue.IsSuccess);
        }

        await using (var workerContext = database.CreateContext())
        {
            var store = new EfDurableJobStore(workerContext);
            var reacquired = await store.TryAcquireNextAsync(
                new WorkerIdentity("requeue-worker"),
                TimeSpan.FromMinutes(1),
                Now.AddMinutes(2),
                CancellationToken.None);
            Assert.NotNull(reacquired.Success.Job);
            Assert.Equal(1, reacquired.Success.Job.AttemptNumber);
            Assert.True((await store.CompleteAsync(
                jobId,
                reacquired.Success.Job.LeaseToken,
                Now.AddMinutes(2),
                CancellationToken.None)).IsSuccess);
        }

        await using var verificationContext = database.CreateContext();
        var state = await new EfDurableJobStore(verificationContext)
            .GetStateAsync(jobId, CancellationToken.None);
        var attemptNumbers = await verificationContext.Set<JobAttempt>()
            .AsNoTracking()
            .Where(attempt => attempt.JobInstanceId == jobId)
            .OrderBy(attempt => attempt.Number)
            .Select(attempt => attempt.Number)
            .ToArrayAsync();

        Assert.Equal("Completed", state.Success.Snapshot!.State);
        Assert.Equal(1, state.Success.Snapshot.AttemptsStarted);
        Assert.Equal([1, 2, 3], attemptNumbers);
    }

    [Fact]
    public async Task DeadLetterCancellationIsRejectedWithoutPersistingRequest()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(DeadLetterCancellationIsRejectedWithoutPersistingRequest));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "dead-letter-cancel");

        await using (var workerContext = database.CreateContext())
        {
            var store = new EfDurableJobStore(workerContext);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            Assert.True((await store.FailAsync(
                jobId,
                lease.LeaseToken,
                "Permanent",
                null,
                Now,
                CancellationToken.None)).IsSuccess);
        }

        await using (var operatorContext = database.CreateContext())
        {
            var cancellation = await new EfDurableJobStore(operatorContext)
                .RequestCancellationAsync(
                    jobId,
                    Now.AddMinutes(1),
                    CancellationToken.None);

            Assert.True(cancellation.IsFailure);
            Assert.Equal(JobStoreErrorCode.InvalidState, cancellation.Error.Code);
        }

        await using var verificationContext = database.CreateContext();
        var state = await new EfDurableJobStore(verificationContext)
            .GetStateAsync(jobId, CancellationToken.None);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.False(state.Success.Snapshot.CancellationRequested);
    }

    [Fact]
    public async Task CancellationStopsPendingAndRunningJobs()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(CancellationStopsPendingAndRunningJobs));
        var pendingId = await RegisterAndEnqueueAsync(database, "pending-cancel");
        var runningId = await EnqueueAsync(database, "running-cancel");

        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            Assert.True((await store.RequestCancellationAsync(
                pendingId,
                Now,
                CancellationToken.None)).IsSuccess);
        }

        AcquiredJob runningLease;
        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            runningLease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            Assert.Equal(runningId, runningLease.JobInstanceId);
        }

        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            Assert.True((await store.RequestCancellationAsync(
                runningId,
                Now.AddSeconds(1),
                CancellationToken.None)).IsSuccess);
            var renewal = await store.RenewAsync(
                runningId,
                runningLease.LeaseToken,
                TimeSpan.FromMinutes(1),
                Now.AddSeconds(2),
                CancellationToken.None);
            Assert.True(renewal.IsSuccess);
            Assert.False(renewal.Success);
            Assert.True((await store.FailAsync(
                runningId,
                runningLease.LeaseToken,
                "Cancelled",
                null,
                Now.AddSeconds(2),
                CancellationToken.None)).IsSuccess);
        }

        await using var verification = database.CreateContext();
        var verificationStore = new EfDurableJobStore(verification);
        Assert.Equal(
            "Cancelled",
            (await verificationStore.GetStateAsync(pendingId, CancellationToken.None)).Success.Snapshot!.State);
        Assert.Equal(
            "Cancelled",
            (await verificationStore.GetStateAsync(runningId, CancellationToken.None)).Success.Snapshot!.State);
    }

    [Fact]
    public async Task WorkerShutdownReleasesJobForRestart()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(WorkerShutdownReleasesJobForRestart));
        var jobId = await RegisterAndEnqueueAsync(database, "restart");
        AcquiredJob lease;
        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("stopping-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            Assert.True((await store.ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddSeconds(5),
                Now,
                CancellationToken.None)).IsSuccess);
        }

        await using var restartContext = database.CreateContext();
        var restarted = await new EfDurableJobStore(restartContext).TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddSeconds(5),
            CancellationToken.None);
        Assert.NotNull(restarted.Success.Job);
        Assert.Equal(2, restarted.Success.Job.AttemptNumber);
    }

    [Fact]
    public async Task WorkerShutdownCanReacquireAndCompleteInSameContext()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(WorkerShutdownCanReacquireAndCompleteInSameContext));
        var jobId = await RegisterAndEnqueueAsync(database, "same-context-restart");

        await using var context = database.CreateContext();
        var unrelatedDefinition = await context.Set<JobDefinition>().SingleAsync();
        var store = new EfDurableJobStore(context);
        var firstLease = (await store.TryAcquireNextAsync(
            new WorkerIdentity("stopping-worker"),
            TimeSpan.FromMinutes(1),
            Now,
            CancellationToken.None)).Success.Job!;

        var released = await store.ReleaseForRecoveryAsync(
            jobId,
            firstLease.LeaseToken,
            Now.AddSeconds(5),
            Now,
            CancellationToken.None);

        Assert.True(released.IsSuccess);
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<JobInstance>(),
            entry => entry.Entity.Id == jobId);
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<JobAttempt>(),
            entry =>
                entry.Entity.JobInstanceId == jobId &&
                entry.Entity.LeaseToken == firstLease.LeaseToken);
        Assert.Equal(EntityState.Unchanged, context.Entry(unrelatedDefinition).State);

        var reacquired = await store.TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddSeconds(5),
            CancellationToken.None);

        Assert.True(reacquired.IsSuccess);
        Assert.NotNull(reacquired.Success.Job);
        Assert.Equal(2, reacquired.Success.Job.AttemptNumber);
        Assert.True((await store.CompleteAsync(
            jobId,
            reacquired.Success.Job.LeaseToken,
            Now.AddSeconds(6),
            CancellationToken.None)).IsSuccess);
        Assert.Equal(
            "Completed",
            (await store.GetStateAsync(jobId, CancellationToken.None)).Success.Snapshot!.State);
    }

    [Fact]
    public async Task ExpiredNonFinalReleaseLosesLeaseWithoutMutationThenIsTakenOver()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredNonFinalReleaseLosesLeaseWithoutMutationThenIsTakenOver));
        var jobId = await RegisterAndEnqueueAsync(database, "expired-release-non-final");
        AcquiredJob lease;
        await using (var context = database.CreateContext())
        {
            lease = (await new EfDurableJobStore(context).TryAcquireNextAsync(
                new WorkerIdentity("expired-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
        }

        var before = await LoadJobSnapshotAsync(database, jobId);
        await using (var context = database.CreateContext())
        {
            var release = await new EfDurableJobStore(context).ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddMinutes(3),
                Now.AddMinutes(2),
                CancellationToken.None);

            Assert.True(release.IsFailure);
            Assert.Equal(JobStoreErrorCode.LeaseLost, release.Error.Code);
        }

        AssertJobSnapshotEqual(before, await LoadJobSnapshotAsync(database, jobId));

        await using var takeoverContext = database.CreateContext();
        var takeover = await new EfDurableJobStore(takeoverContext).TryAcquireNextAsync(
            new WorkerIdentity("takeover-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);
        var afterTakeover = await LoadJobSnapshotAsync(database, jobId);

        Assert.NotNull(takeover.Success.Job);
        Assert.Equal(2, takeover.Success.Job.AttemptNumber);
        Assert.Equal(JobInstanceState.Running, afterTakeover.Instance.State);
        Assert.Equal(2, afterTakeover.Instance.AttemptsStarted);
        Assert.Equal(2, afterTakeover.Attempts.Count);
        Assert.Equal(JobAttemptOutcome.LeaseLost, afterTakeover.Attempts[0].Outcome);
        Assert.Equal("LeaseExpired", afterTakeover.Attempts[0].ErrorCode);
        Assert.Equal(JobAttemptOutcome.Running, afterTakeover.Attempts[1].Outcome);
    }

    [Fact]
    public async Task ExpiredFinalReleaseLosesLeaseWithoutMutationThenIsDeadLettered()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredFinalReleaseLosesLeaseWithoutMutationThenIsDeadLettered));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "expired-release-final");
        AcquiredJob lease;
        await using (var context = database.CreateContext())
        {
            lease = (await new EfDurableJobStore(context).TryAcquireNextAsync(
                new WorkerIdentity("expired-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
        }

        var before = await LoadJobSnapshotAsync(database, jobId);
        await using (var context = database.CreateContext())
        {
            var release = await new EfDurableJobStore(context).ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddMinutes(3),
                Now.AddMinutes(2),
                CancellationToken.None);

            Assert.True(release.IsFailure);
            Assert.Equal(JobStoreErrorCode.LeaseLost, release.Error.Code);
        }

        AssertJobSnapshotEqual(before, await LoadJobSnapshotAsync(database, jobId));

        await using var takeoverContext = database.CreateContext();
        var takeover = await new EfDurableJobStore(takeoverContext).TryAcquireNextAsync(
            new WorkerIdentity("takeover-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);
        var afterTakeover = await LoadJobSnapshotAsync(database, jobId);

        Assert.Null(takeover.Success.Job);
        Assert.Equal(JobInstanceState.DeadLettered, afterTakeover.Instance.State);
        Assert.Equal(1, afterTakeover.Instance.AttemptsStarted);
        Assert.Equal(
            "MaximumAttemptsExceededAfterLeaseExpiry",
            afterTakeover.Instance.LastErrorCode);
        Assert.Single(afterTakeover.Attempts);
        Assert.Equal(JobAttemptOutcome.LeaseLost, afterTakeover.Attempts[0].Outcome);
        Assert.Equal("LeaseExpired", afterTakeover.Attempts[0].ErrorCode);
    }

    [Fact]
    public async Task ExpiredCancellationReleaseLosesLeaseWithoutMutationThenCancellationWins()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredCancellationReleaseLosesLeaseWithoutMutationThenCancellationWins));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "expired-release-cancelled");
        AcquiredJob lease;
        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("expired-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            Assert.True((await store.RequestCancellationAsync(
                jobId,
                Now.AddSeconds(1),
                CancellationToken.None)).IsSuccess);
        }

        var before = await LoadJobSnapshotAsync(database, jobId);
        await using (var context = database.CreateContext())
        {
            var release = await new EfDurableJobStore(context).ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddMinutes(3),
                Now.AddMinutes(2),
                CancellationToken.None);

            Assert.True(release.IsFailure);
            Assert.Equal(JobStoreErrorCode.LeaseLost, release.Error.Code);
        }

        AssertJobSnapshotEqual(before, await LoadJobSnapshotAsync(database, jobId));

        await using var takeoverContext = database.CreateContext();
        var takeover = await new EfDurableJobStore(takeoverContext).TryAcquireNextAsync(
            new WorkerIdentity("takeover-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);
        var afterTakeover = await LoadJobSnapshotAsync(database, jobId);

        Assert.Null(takeover.Success.Job);
        Assert.Equal(JobInstanceState.Cancelled, afterTakeover.Instance.State);
        Assert.Equal(1, afterTakeover.Instance.AttemptsStarted);
        Assert.NotNull(afterTakeover.Instance.CancellationRequestedAtUtc);
        Assert.Single(afterTakeover.Attempts);
        Assert.Equal(JobAttemptOutcome.Cancelled, afterTakeover.Attempts[0].Outcome);
        Assert.Equal("CancellationRequested", afterTakeover.Attempts[0].ErrorCode);
    }

    [Fact]
    public async Task FinalAttemptShutdownTerminalizesWithoutRestart()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(FinalAttemptShutdownTerminalizesWithoutRestart));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "final-shutdown");

        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("stopping-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            var released = await store.ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddSeconds(5),
                Now,
                CancellationToken.None);
            Assert.True(released.IsSuccess);
        }

        await using var restartContext = database.CreateContext();
        var restartedStore = new EfDurableJobStore(restartContext);
        var restarted = await restartedStore.TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddSeconds(5),
            CancellationToken.None);
        var state = await restartedStore.GetStateAsync(jobId, CancellationToken.None);

        Assert.Null(restarted.Success.Job);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.Equal(1, state.Success.Snapshot.AttemptsStarted);
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobAttempts"));
    }

    [Fact]
    public async Task RepeatedShutdownRecoveryNeverAcquiresMaximumAttemptsPlusOne()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(RepeatedShutdownRecoveryNeverAcquiresMaximumAttemptsPlusOne));
        var jobId = await RegisterAndEnqueueAsync(database, "repeated-shutdown");

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var context = database.CreateContext();
            var store = new EfDurableJobStore(context);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity($"worker-{attempt}"),
                TimeSpan.FromMinutes(1),
                Now.AddSeconds(attempt - 1),
                CancellationToken.None)).Success.Job!;
            Assert.Equal(attempt, lease.AttemptNumber);
            var released = await store.ReleaseForRecoveryAsync(
                jobId,
                lease.LeaseToken,
                Now.AddSeconds(attempt),
                Now.AddSeconds(attempt - 1),
                CancellationToken.None);
            Assert.True(released.IsSuccess);
        }

        await using var restartContext = database.CreateContext();
        var restartedStore = new EfDurableJobStore(restartContext);
        for (var restart = 0; restart < 3; restart++)
        {
            var acquisition = await restartedStore.TryAcquireNextAsync(
                new WorkerIdentity($"restart-{restart}"),
                TimeSpan.FromMinutes(1),
                Now.AddMinutes(1 + restart),
                CancellationToken.None);
            Assert.Null(acquisition.Success.Job);
        }

        var state = await restartedStore.GetStateAsync(jobId, CancellationToken.None);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.Equal(2, state.Success.Snapshot.AttemptsStarted);
        Assert.Equal(2, await database.CountRowsAsync("OperationsJobAttempts"));
    }

    [Fact]
    public async Task ExpiredFinalAttemptWithCancellationIsCancelled()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ExpiredFinalAttemptWithCancellationIsCancelled));
        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        var jobId = await EnqueueAsync(database, "cancel-final-expired");

        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            var acquired = await store.TryAcquireNextAsync(
                new WorkerIdentity("crashed-worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None);
            Assert.NotNull(acquired.Success.Job);
            Assert.True((await store.RequestCancellationAsync(
                jobId,
                Now.AddSeconds(1),
                CancellationToken.None)).IsSuccess);
        }

        await using var restartContext = database.CreateContext();
        var restartedStore = new EfDurableJobStore(restartContext);
        var acquisition = await restartedStore.TryAcquireNextAsync(
            new WorkerIdentity("restart-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddMinutes(2),
            CancellationToken.None);
        var state = await restartedStore.GetStateAsync(jobId, CancellationToken.None);

        Assert.Null(acquisition.Success.Job);
        Assert.Equal("Cancelled", state.Success.Snapshot!.State);
        Assert.Equal(1, state.Success.Snapshot.AttemptsStarted);
    }

    [Fact]
    public async Task ReducedRetryBudgetTerminalizesBeforeAnotherAcquisition()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ReducedRetryBudgetTerminalizesBeforeAnotherAcquisition));
        var jobId = await RegisterAndEnqueueAsync(database, "reduced-retry-budget");

        await using (var context = database.CreateContext())
        {
            var store = new EfDurableJobStore(context);
            var lease = (await store.TryAcquireNextAsync(
                new WorkerIdentity("worker"),
                TimeSpan.FromMinutes(1),
                Now,
                CancellationToken.None)).Success.Job!;
            Assert.True((await store.FailAsync(
                jobId,
                lease.LeaseToken,
                "OriginalFailure",
                Now.AddSeconds(1),
                Now,
                CancellationToken.None)).IsSuccess);
        }

        await RegisterDefinitionAsync(database, maximumAttempts: 1);
        await using var retryContext = database.CreateContext();
        var retryStore = new EfDurableJobStore(retryContext);
        var acquisition = await retryStore.TryAcquireNextAsync(
            new WorkerIdentity("retry-worker"),
            TimeSpan.FromMinutes(1),
            Now.AddSeconds(1),
            CancellationToken.None);
        var state = await retryStore.GetStateAsync(jobId, CancellationToken.None);

        Assert.Null(acquisition.Success.Job);
        Assert.Equal("DeadLettered", state.Success.Snapshot!.State);
        Assert.Equal(1, state.Success.Snapshot.AttemptsStarted);
        Assert.Equal("OriginalFailure", state.Success.Snapshot.LastErrorCode);
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobAttempts"));
    }

    [Fact]
    public async Task ConcurrentDuplicateEnqueueProducesOneInstance()
    {
        await using var database = await CreateDatabaseAsync(
            nameof(ConcurrentDuplicateEnqueueProducesOneInstance));
        await RegisterDefinitionAsync(database);
        var request = new JobEnqueueRequest(
            "test-job",
            """{"value":1}""",
            "duplicate",
            "correlation");

        await using var firstContext = database.CreateContext();
        await using var secondContext = database.CreateContext();
        var results = await Task.WhenAll(
            new EfDurableJobStore(firstContext).EnqueueAsync(
                request,
                Now,
                CancellationToken.None),
            new EfDurableJobStore(secondContext).EnqueueAsync(
                request,
                Now,
                CancellationToken.None));

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Success.JobInstanceId, results[1].Success.JobInstanceId);
        Assert.Contains(results, result => result.Success.IsDuplicate);
        Assert.Equal(1, await database.CountRowsAsync("OperationsJobInstances"));
    }

    private static Task<SqlServerTestDatabase> CreateDatabaseAsync(string name) =>
        SqlServerTestDatabase.CreateAsync(name);

    private static async Task<Guid> RegisterAndEnqueueAsync(
        SqlServerTestDatabase database,
        string idempotencyKey)
    {
        await RegisterDefinitionAsync(database);
        return await EnqueueAsync(database, idempotencyKey);
    }

    private static async Task RegisterDefinitionAsync(
        SqlServerTestDatabase database,
        int maximumAttempts = 2)
    {
        await using var context = database.CreateContext();
        var result = await new EfDurableJobStore(context).RegisterDefinitionAsync(
            new JobDefinitionRegistration(
                "test-job",
                "test-handler",
                maximumAttempts,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        Assert.True(result.IsSuccess);
    }

    private static async Task<Guid> EnqueueAsync(
        SqlServerTestDatabase database,
        string idempotencyKey)
    {
        await using var context = database.CreateContext();
        var result = await new EfDurableJobStore(context).EnqueueAsync(
            new JobEnqueueRequest(
                "test-job",
                """{"value":1}""",
                idempotencyKey,
                "correlation"),
            Now,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        return result.Success.JobInstanceId;
    }

    private static async Task<JobSnapshot> LoadJobSnapshotAsync(
        SqlServerTestDatabase database,
        Guid jobId)
    {
        await using var context = database.CreateContext();
        var instance = await context.Set<JobInstance>()
            .AsNoTracking()
            .Where(entity => entity.Id == jobId)
            .Select(entity => new JobInstanceSnapshot(
                entity.Id,
                entity.DefinitionId,
                entity.PayloadJson,
                entity.IdempotencyKey,
                entity.CorrelationId,
                entity.State,
                entity.AttemptsStarted,
                entity.NextRunAtUtc,
                entity.LeaseOwner,
                entity.LeaseToken,
                entity.LeaseExpiresAtUtc,
                entity.CancellationRequestedAtUtc,
                entity.CompletedAtUtc,
                entity.DeadLetteredAtUtc,
                entity.LastErrorCode,
                EF.Property<DateTimeOffset>(entity, "CreatedAtUtc"),
                EF.Property<DateTimeOffset>(entity, "UpdatedAtUtc"),
                EF.Property<byte[]>(entity, "RowVersion")))
            .SingleAsync();
        var attempts = await context.Set<JobAttempt>()
            .AsNoTracking()
            .Where(attempt => attempt.JobInstanceId == jobId)
            .OrderBy(attempt => attempt.Number)
            .Select(attempt => new JobAttemptSnapshot(
                attempt.Id,
                attempt.JobInstanceId,
                attempt.Number,
                attempt.Worker,
                attempt.LeaseToken,
                attempt.StartedAtUtc,
                attempt.CompletedAtUtc,
                attempt.Outcome,
                attempt.ErrorCode,
                EF.Property<DateTimeOffset>(attempt, "CreatedAtUtc"),
                EF.Property<DateTimeOffset>(attempt, "UpdatedAtUtc"),
                EF.Property<byte[]>(attempt, "RowVersion")))
            .ToArrayAsync();
        return new JobSnapshot(instance, attempts);
    }

    private static void AssertJobSnapshotEqual(JobSnapshot expected, JobSnapshot actual)
    {
        Assert.Equal(expected.Instance with { RowVersion = actual.Instance.RowVersion }, actual.Instance);
        Assert.Equal(expected.Instance.RowVersion, actual.Instance.RowVersion);
        Assert.Equal(expected.Attempts.Count, actual.Attempts.Count);
        for (var index = 0; index < expected.Attempts.Count; index++)
        {
            Assert.Equal(
                expected.Attempts[index] with { RowVersion = actual.Attempts[index].RowVersion },
                actual.Attempts[index]);
            Assert.Equal(expected.Attempts[index].RowVersion, actual.Attempts[index].RowVersion);
        }
    }

    private sealed record JobSnapshot(
        JobInstanceSnapshot Instance,
        IReadOnlyList<JobAttemptSnapshot> Attempts);

    private sealed record JobInstanceSnapshot(
        Guid Id,
        Guid DefinitionId,
        string PayloadJson,
        string IdempotencyKey,
        string CorrelationId,
        JobInstanceState State,
        int AttemptsStarted,
        DateTimeOffset NextRunAtUtc,
        string? LeaseOwner,
        Guid? LeaseToken,
        DateTimeOffset? LeaseExpiresAtUtc,
        DateTimeOffset? CancellationRequestedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        DateTimeOffset? DeadLetteredAtUtc,
        string? LastErrorCode,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        byte[] RowVersion);

    private sealed record JobAttemptSnapshot(
        Guid Id,
        Guid JobInstanceId,
        int Number,
        string Worker,
        Guid LeaseToken,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        JobAttemptOutcome Outcome,
        string? ErrorCode,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        byte[] RowVersion);
}
