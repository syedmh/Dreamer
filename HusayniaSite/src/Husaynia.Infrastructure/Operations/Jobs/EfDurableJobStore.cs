using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Jobs;
using Husaynia.Domain.Operations.Jobs;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Operations.Jobs;

public sealed class EfDurableJobStore(
    HusayniaDbContext dbContext,
    TimeProvider? timeProvider = null)
    : IDurableJobStore, IJobLeaseStore
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<Result<Guid, JobStoreError>> RegisterDefinitionAsync(
        JobDefinitionRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await dbContext.Set<JobDefinition>()
                .SingleOrDefaultAsync(entity => entity.Key == registration.Key, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                existing = new JobDefinition(
                    registration.Key,
                    registration.HandlerName,
                    registration.MaximumAttempts,
                    registration.InitialBackoff,
                    registration.MaximumBackoff);
                if (!registration.IsEnabled)
                {
                    existing.Update(
                        registration.HandlerName,
                        registration.MaximumAttempts,
                        registration.InitialBackoff,
                        registration.MaximumBackoff,
                        false);
                }

                dbContext.Add(existing);
            }
            else
            {
                existing.Update(
                    registration.HandlerName,
                    registration.MaximumAttempts,
                    registration.InitialBackoff,
                    registration.MaximumBackoff,
                    registration.IsEnabled);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<Guid, JobStoreError>(existing.Id);
        }
        catch (ArgumentException)
        {
            return Failure<Guid>(JobStoreErrorCode.InvalidInput, "The job definition is invalid.");
        }
        catch (DbUpdateException)
        {
            return Failure<Guid>(JobStoreErrorCode.PersistenceFailure, "The job definition could not be persisted.");
        }
    }

    public async Task<Result<JobEnqueueReceipt, JobStoreError>> EnqueueAsync(
        JobEnqueueRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var definition = await dbContext.Set<JobDefinition>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.Key == request.DefinitionKey && entity.IsEnabled,
                cancellationToken)
            .ConfigureAwait(false);
        if (definition is null)
        {
            return Failure<JobEnqueueReceipt>(
                JobStoreErrorCode.DefinitionNotFound,
                "The enabled job definition was not found.");
        }

        var existing = await dbContext.Set<JobInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entity => entity.DefinitionId == definition.Id &&
                    entity.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return string.Equals(existing.PayloadJson, request.PayloadJson, StringComparison.Ordinal)
                ? Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                    new JobEnqueueReceipt(existing.Id, true))
                : Failure<JobEnqueueReceipt>(
                    JobStoreErrorCode.DuplicateConflict,
                    "The idempotency key is already associated with another payload.");
        }

        try
        {
            var instance = new JobInstance(
                definition.Id,
                request.PayloadJson,
                request.IdempotencyKey,
                request.CorrelationId,
                request.NotBeforeUtc ?? now);
            dbContext.Add(instance);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                new JobEnqueueReceipt(instance.Id, false));
        }
        catch (ArgumentException)
        {
            return Failure<JobEnqueueReceipt>(
                JobStoreErrorCode.InvalidInput,
                "The job request is invalid.");
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            existing = await dbContext.Set<JobInstance>()
                .AsNoTracking()
                .SingleAsync(
                    entity => entity.DefinitionId == definition.Id &&
                        entity.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return string.Equals(existing.PayloadJson, request.PayloadJson, StringComparison.Ordinal)
                ? Result.Succeed<JobEnqueueReceipt, JobStoreError>(
                    new JobEnqueueReceipt(existing.Id, true))
                : Failure<JobEnqueueReceipt>(
                    JobStoreErrorCode.DuplicateConflict,
                    "The idempotency key is already associated with another payload.");
        }
        catch (DbUpdateException)
        {
            return Failure<JobEnqueueReceipt>(
                JobStoreErrorCode.PersistenceFailure,
                "The job could not be persisted.");
        }
    }

    public async Task<Result<JobAcquireResult, JobStoreError>> TryAcquireNextAsync(
        WorkerIdentity worker,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? definitionKey = null)
    {
        if (string.IsNullOrWhiteSpace(worker.Value) ||
            worker.Value.Length > 200 ||
            leaseDuration <= TimeSpan.Zero)
        {
            return Failure<JobAcquireResult>(
                JobStoreErrorCode.InvalidInput,
                "A worker and positive lease duration are required.");
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var pending = (int)JobInstanceState.Pending;
            var retry = (int)JobInstanceState.RetryScheduled;
            var running = (int)JobInstanceState.Running;
            var instance = await dbContext.Set<JobInstance>()
                .FromSqlInterpolated(
                    $"""
                     SELECT TOP (1) instance.*
                     FROM [OperationsJobInstances] AS instance WITH (UPDLOCK, READPAST, ROWLOCK)
                     INNER JOIN [OperationsJobDefinitions] AS definition
                         ON definition.[Id] = instance.[DefinitionId]
                     WHERE definition.[IsEnabled] = CAST(1 AS bit)
                       AND ({definitionKey} IS NULL OR definition.[Key] = {definitionKey})
                       AND (
                            (instance.[State] IN ({pending}, {retry}) AND instance.[NextRunAtUtc] <= {now})
                            OR
                            (instance.[State] = {running} AND instance.[LeaseExpiresAtUtc] <= {now})
                       )
                     ORDER BY instance.[NextRunAtUtc], instance.[Id]
                     """)
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (instance is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Succeed<JobAcquireResult, JobStoreError>(
                    new JobAcquireResult(null));
            }

            var definition = await dbContext.Set<JobDefinition>()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == instance.DefinitionId, cancellationToken)
                .ConfigureAwait(false);
            JobAttempt? abandoned = null;
            if (instance.LeaseToken.HasValue)
            {
                abandoned = await dbContext.Set<JobAttempt>()
                    .SingleOrDefaultAsync(
                        attempt => attempt.LeaseToken == instance.LeaseToken.Value &&
                            attempt.Outcome == JobAttemptOutcome.Running,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Cancellation is authoritative even when an expired attempt consumed the budget.
            if (instance.CancellationRequestedAtUtc.HasValue)
            {
                instance.Cancel(now);
                abandoned?.Finish(
                    JobAttemptOutcome.Cancelled,
                    now,
                    "CancellationRequested");
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Succeed<JobAcquireResult, JobStoreError>(
                    new JobAcquireResult(null));
            }

            // Every candidate state is budget-fenced before a new attempt is persisted.
            if (instance.AttemptsStarted >= definition.MaximumAttempts)
            {
                instance.DeadLetterPreservingError(
                    now,
                    instance.State == JobInstanceState.Running
                        ? "MaximumAttemptsExceededAfterLeaseExpiry"
                        : "MaximumAttemptsExceededBeforeAcquisition");
                abandoned?.Finish(JobAttemptOutcome.LeaseLost, now, "LeaseExpired");
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Result.Succeed<JobAcquireResult, JobStoreError>(
                    new JobAcquireResult(null));
            }

            var lastAttemptNumber = await dbContext.Set<JobAttempt>()
                .Where(attempt => attempt.JobInstanceId == instance.Id)
                .MaxAsync(attempt => (int?)attempt.Number, cancellationToken)
                .ConfigureAwait(false) ?? 0;
            if (lastAttemptNumber == int.MaxValue)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<JobAcquireResult>(
                    JobStoreErrorCode.PersistenceFailure,
                    "The job attempt history cannot accept another attempt.");
            }

            abandoned?.Finish(JobAttemptOutcome.LeaseLost, now, "LeaseExpired");
            var token = instance.Acquire(worker.Value, now, leaseDuration);
            dbContext.Add(new JobAttempt(
                instance.Id,
                lastAttemptNumber + 1,
                worker.Value,
                token,
                now));
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<JobAcquireResult, JobStoreError>(
                new JobAcquireResult(new AcquiredJob(
                    instance.Id,
                    definition.Key,
                    definition.HandlerName,
                    instance.PayloadJson,
                    instance.IdempotencyKey,
                    instance.CorrelationId,
                    instance.AttemptsStarted,
                    definition.MaximumAttempts,
                    definition.InitialBackoff,
                    definition.MaximumBackoff,
                    worker,
                    token,
                    instance.LeaseExpiresAtUtc!.Value,
                    instance.CancellationRequestedAtUtc.HasValue)));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure<JobAcquireResult>(
                JobStoreErrorCode.PersistenceFailure,
                "The job lease could not be acquired.");
        }
    }

    public async Task<Result<bool, JobStoreError>> RenewAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var instance = await FindLeasedAsync(jobInstanceId, leaseToken, now, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            return Failure<bool>(JobStoreErrorCode.LeaseLost, "The job lease is no longer valid.");
        }

        if (instance.CancellationRequestedAtUtc.HasValue)
        {
            return Result.Succeed<bool, JobStoreError>(false);
        }

        instance.Renew(now, leaseDuration);
        return await SaveBooleanAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool, JobStoreError>> CompleteAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var instance = await FindLeasedAsync(jobInstanceId, leaseToken, now, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            return Failure<bool>(JobStoreErrorCode.LeaseLost, "The job lease is no longer valid.");
        }

        var attempt = await FindAttemptAsync(leaseToken, cancellationToken).ConfigureAwait(false);
        if (instance.CancellationRequestedAtUtc.HasValue)
        {
            instance.Cancel(now);
            attempt.Finish(JobAttemptOutcome.Cancelled, now, "CancellationRequested");
        }
        else
        {
            instance.Complete(now);
            attempt.Finish(JobAttemptOutcome.Succeeded, now);
        }

        var result = await SaveBooleanAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<Result<bool, JobStoreError>> FailAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        string errorCode,
        DateTimeOffset? nextRunAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var instance = await FindLeasedAsync(jobInstanceId, leaseToken, now, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            return Failure<bool>(JobStoreErrorCode.LeaseLost, "The job lease is no longer valid.");
        }

        var attempt = await FindAttemptAsync(leaseToken, cancellationToken).ConfigureAwait(false);
        if (instance.CancellationRequestedAtUtc.HasValue ||
            string.Equals(errorCode, "Cancelled", StringComparison.Ordinal))
        {
            instance.Cancel(now);
            attempt.Finish(JobAttemptOutcome.Cancelled, now, "CancellationRequested");
        }
        else if (nextRunAtUtc.HasValue)
        {
            instance.ScheduleRetry(nextRunAtUtc.Value, BoundError(errorCode));
            attempt.Finish(JobAttemptOutcome.Failed, now, BoundError(errorCode));
        }
        else
        {
            instance.DeadLetter(now, BoundError(errorCode));
            attempt.Finish(JobAttemptOutcome.Failed, now, BoundError(errorCode));
        }

        var result = await SaveBooleanAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<Result<bool, JobStoreError>> ReleaseForRecoveryAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        DateTimeOffset nextRunAtUtc,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var running = (int)JobInstanceState.Running;
            var retryScheduled = (int)JobInstanceState.RetryScheduled;
            var deadLettered = (int)JobInstanceState.DeadLettered;
            var cancelled = (int)JobInstanceState.Cancelled;
            var updatedInstances = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE instance
                 SET [State] = CASE
                         WHEN instance.[CancellationRequestedAtUtc] IS NOT NULL THEN {cancelled}
                         WHEN instance.[AttemptsStarted] >= definition.[MaximumAttempts] THEN {deadLettered}
                         ELSE {retryScheduled}
                     END,
                     [NextRunAtUtc] = CASE
                         WHEN instance.[CancellationRequestedAtUtc] IS NULL
                              AND instance.[AttemptsStarted] < definition.[MaximumAttempts]
                             THEN {nextRunAtUtc}
                         ELSE instance.[NextRunAtUtc]
                     END,
                     [CompletedAtUtc] = CASE
                         WHEN instance.[CancellationRequestedAtUtc] IS NOT NULL THEN {now}
                         ELSE instance.[CompletedAtUtc]
                     END,
                     [DeadLetteredAtUtc] = CASE
                         WHEN instance.[CancellationRequestedAtUtc] IS NULL
                              AND instance.[AttemptsStarted] >= definition.[MaximumAttempts]
                             THEN {now}
                         ELSE instance.[DeadLetteredAtUtc]
                     END,
                     [LastErrorCode] = CASE
                         WHEN instance.[CancellationRequestedAtUtc] IS NULL
                              AND instance.[AttemptsStarted] >= definition.[MaximumAttempts]
                             THEN COALESCE(instance.[LastErrorCode], N'WorkerStopping')
                         ELSE instance.[LastErrorCode]
                     END,
                     [LeaseOwner] = NULL,
                     [LeaseToken] = NULL,
                     [LeaseExpiresAtUtc] = NULL,
                     [UpdatedAtUtc] = {now}
                 FROM [OperationsJobInstances] AS instance
                 INNER JOIN [OperationsJobDefinitions] AS definition
                     ON definition.[Id] = instance.[DefinitionId]
                 WHERE instance.[Id] = {jobInstanceId}
                   AND instance.[State] = {running}
                   AND instance.[LeaseToken] = {leaseToken}
                   AND instance.[LeaseExpiresAtUtc] > {now}
                 """,
                cancellationToken)
                .ConfigureAwait(false);
            if (updatedInstances == 0)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<bool>(
                    JobStoreErrorCode.LeaseLost,
                    "The job lease is no longer valid.");
            }

            if (updatedInstances != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<bool>(
                    JobStoreErrorCode.PersistenceFailure,
                    "The durable job state could not be persisted.");
            }

            var runningAttempt = (int)JobAttemptOutcome.Running;
            var cancelledAttempt = (int)JobAttemptOutcome.Cancelled;
            var updatedAttempts = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE [OperationsJobAttempts]
                 SET [Outcome] = {cancelledAttempt},
                     [CompletedAtUtc] = {now},
                     [ErrorCode] = CASE
                         WHEN EXISTS (
                             SELECT 1
                             FROM [OperationsJobInstances]
                             WHERE [Id] = {jobInstanceId}
                               AND [CancellationRequestedAtUtc] IS NOT NULL
                         ) THEN N'CancellationRequested'
                         ELSE N'WorkerStopping'
                     END,
                     [UpdatedAtUtc] = {now}
                 WHERE [JobInstanceId] = {jobInstanceId}
                   AND [LeaseToken] = {leaseToken}
                   AND [Outcome] = {runningAttempt}
                 """,
                cancellationToken)
                .ConfigureAwait(false);
            if (updatedAttempts != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return Failure<bool>(
                    JobStoreErrorCode.PersistenceFailure,
                    "The durable job attempt could not be persisted.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            DetachReleasedEntities(jobInstanceId, leaseToken);
            return Result.Succeed<bool, JobStoreError>(true);
        }
        catch (SqlException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Failure<bool>(
                JobStoreErrorCode.PersistenceFailure,
                "The durable job state could not be persisted.");
        }
    }

    private void DetachReleasedEntities(Guid jobInstanceId, Guid leaseToken)
    {
        foreach (var entry in dbContext.ChangeTracker.Entries<JobInstance>()
                     .Where(entry => entry.Entity.Id == jobInstanceId)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }

        foreach (var entry in dbContext.ChangeTracker.Entries<JobAttempt>()
                     .Where(entry =>
                         entry.Entity.JobInstanceId == jobInstanceId &&
                         entry.Entity.LeaseToken == leaseToken)
                     .ToArray())
        {
            entry.State = EntityState.Detached;
        }
    }

    public async Task<Result<bool, JobStoreError>> RequestCancellationAsync(
        Guid jobInstanceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<JobInstance>()
            .SingleOrDefaultAsync(entity => entity.Id == jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null)
        {
            return Failure<bool>(JobStoreErrorCode.InvalidState, "The job was not found.");
        }

        if (instance.State is JobInstanceState.Completed or JobInstanceState.Cancelled)
        {
            return Result.Succeed<bool, JobStoreError>(true);
        }

        if (instance.State == JobInstanceState.DeadLettered)
        {
            return Failure<bool>(
                JobStoreErrorCode.InvalidState,
                "A dead-lettered job cannot be cancelled; requeue it before requesting cancellation.");
        }

        instance.RequestCancellation(now);
        if (instance.State is JobInstanceState.Pending or JobInstanceState.RetryScheduled)
        {
            instance.Cancel(now);
        }

        return await SaveBooleanAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<bool, JobStoreError>> RequeueDeadLetterAsync(
        Guid jobInstanceId,
        string actor,
        string reason,
        string correlationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 ||
            string.IsNullOrWhiteSpace(reason) || reason.Length > 1_000 ||
            string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            return Failure<bool>(JobStoreErrorCode.InvalidInput, "Audited requeue details are invalid.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var instance = await dbContext.Set<JobInstance>()
            .SingleOrDefaultAsync(entity => entity.Id == jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        if (instance is null || instance.State != JobInstanceState.DeadLettered)
        {
            return Failure<bool>(
                JobStoreErrorCode.InvalidState,
                "Only a dead-lettered job can be requeued.");
        }

        instance.Requeue(now);
        dbContext.Add(new JobOperationalAudit(
            instance.Id,
            "RequeueDeadLetter",
            actor,
            reason,
            correlationId,
            now));
        var result = await SaveBooleanAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<Result<JobStateLookup, JobStoreError>> GetStateAsync(
        Guid jobInstanceId,
        CancellationToken cancellationToken)
    {
        var instance = await dbContext.Set<JobInstance>()
            .AsNoTracking()
            .SingleOrDefaultAsync(entity => entity.Id == jobInstanceId, cancellationToken)
            .ConfigureAwait(false);
        return Result.Succeed<JobStateLookup, JobStoreError>(
            new JobStateLookup(
                instance is null
                    ? null
                    : new JobStateSnapshot(
                        instance.Id,
                        instance.State.ToString(),
                        instance.AttemptsStarted,
                        instance.NextRunAtUtc,
                        instance.LeaseExpiresAtUtc,
                        instance.CancellationRequestedAtUtc.HasValue,
                        instance.LastErrorCode)));
    }

    async Task<Result<JobLease?, IntegrationError>> IJobLeaseStore.TryAcquireAsync(
        JobKey key,
        WorkerIdentity worker,
        TimeSpan lease,
        CancellationToken ct)
    {
        var result = await TryAcquireNextAsync(
                worker,
                lease,
                timeProvider.GetUtcNow(),
                ct,
                key.Value)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Fail<JobLease?, IntegrationError>(
                new IntegrationError(result.Error.Code.ToString(), result.Error.Message));
        }

        if (result.Success.Job is null)
        {
            return Result.Fail<JobLease?, IntegrationError>(
                new IntegrationError(
                    "NoJobAvailable",
                    "No eligible job is currently available."));
        }

        return Result.Succeed<JobLease?, IntegrationError>(
            new JobLease(key, worker, result.Success.Job.LeaseExpiresAtUtc));
    }

    private async Task<JobInstance?> FindLeasedAsync(
        Guid jobInstanceId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Set<JobInstance>()
            .SingleOrDefaultAsync(
                entity => entity.Id == jobInstanceId &&
                    entity.State == JobInstanceState.Running &&
                    entity.LeaseToken == leaseToken &&
                    entity.LeaseExpiresAtUtc > now,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<JobAttempt> FindAttemptAsync(
        Guid leaseToken,
        CancellationToken cancellationToken) =>
        await dbContext.Set<JobAttempt>()
            .SingleAsync(
                attempt => attempt.LeaseToken == leaseToken &&
                    attempt.Outcome == JobAttemptOutcome.Running,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<Result<bool, JobStoreError>> SaveBooleanAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<bool, JobStoreError>(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure<bool>(JobStoreErrorCode.LeaseLost, "The job lease changed concurrently.");
        }
        catch (DbUpdateException)
        {
            return Failure<bool>(
                JobStoreErrorCode.PersistenceFailure,
                "The durable job state could not be persisted.");
        }
    }

    private static string BoundError(string errorCode) =>
        string.IsNullOrWhiteSpace(errorCode)
            ? "JobFailure"
            : errorCode.Length <= 200 ? errorCode : errorCode[..200];

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private static Result<T, JobStoreError> Failure<T>(
        JobStoreErrorCode code,
        string message) =>
        Result.Fail<T, JobStoreError>(new JobStoreError(code, message));
}
