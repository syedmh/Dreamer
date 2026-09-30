using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Retention;
using Husaynia.Infrastructure.Operations.Persistence;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace Husaynia.Infrastructure.Operations.Retention;

public sealed class EfRetentionStore(HusayniaDbContext dbContext)
    : IRetentionStore, IRetentionHoldAdministration
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<Result<RetentionRunReservation, string>> TryStartAsync(
        RetentionRunRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var operationFingerprint = CreateOperationFingerprint(request);
        var existing = await dbContext.Set<RetentionRunRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                record => record.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!MatchesOperation(existing, request, operationFingerprint))
            {
                return Result.Fail<RetentionRunReservation, string>(
                    "retention_idempotency_conflict");
            }

            if (!existing.CompletedAtUtc.HasValue && existing.LeaseExpiresAtUtc <= now)
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken)
                    .ConfigureAwait(false);
                await AcquireTargetLockAsync(existing.Target, cancellationToken)
                    .ConfigureAwait(false);
                existing = await dbContext.Set<RetentionRunRecord>()
                    .AsNoTracking()
                    .SingleAsync(record => record.Id == existing.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.CompletedAtUtc.HasValue || existing.LeaseExpiresAtUtc > now)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Result.Succeed<RetentionRunReservation, string>(
                        new RetentionRunReservation(
                            existing.Id,
                            true,
                            existing.CompletedAtUtc.HasValue
                                ? new RetentionRunResult(
                                    existing.Id,
                                    Enum.Parse<RetentionMode>(existing.Mode),
                                    existing.Examined ?? 0,
                                    existing.Held ?? 0,
                                    existing.Applied ?? 0,
                                    true)
                                : null,
                            existing.LeaseToken,
                            false));
                }

                var leaseToken = Guid.NewGuid();
                var leaseExpiresAtUtc = now.Add(request.Policy.OperationTimeout);
                var reclaimed = await dbContext.Set<RetentionRunRecord>()
                    .Where(record => record.Id == existing.Id &&
                        !record.CompletedAtUtc.HasValue &&
                        record.LeaseExpiresAtUtc <= now)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(record => record.LeaseToken, leaseToken)
                            .SetProperty(record => record.LeaseExpiresAtUtc, leaseExpiresAtUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (reclaimed == 1)
                {
                    // Lease rotation and Applying reconciliation are one fenced claim.
                    await dbContext.Set<RetentionRunItemRecord>()
                        .Where(item => item.RunId == existing.Id &&
                            item.Status == RetentionBatchItemStatus.Applying.ToString())
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(
                                    item => item.Status,
                                    RetentionBatchItemStatus.Pending.ToString())
                                .SetProperty(item => item.UpdatedAtUtc, now),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return Result.Succeed<RetentionRunReservation, string>(
                        new RetentionRunReservation(
                            existing.Id,
                            true,
                            null,
                            leaseToken,
                            true));
                }

                existing = await dbContext.Set<RetentionRunRecord>()
                    .AsNoTracking()
                    .SingleAsync(record => record.Id == existing.Id, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return Result.Succeed<RetentionRunReservation, string>(
                new RetentionRunReservation(
                    existing.Id,
                    true,
                    existing.CompletedAtUtc.HasValue
                        ? new RetentionRunResult(
                            existing.Id,
                            Enum.Parse<RetentionMode>(existing.Mode),
                            existing.Examined ?? 0,
                            existing.Held ?? 0,
                            existing.Applied ?? 0,
                            true)
                        : null,
                    existing.LeaseToken,
                    false));
        }

        var record = new RetentionRunRecord(
            request.IdempotencyKey,
            operationFingerprint,
            request.Policy.Target,
            request.Mode.ToString(),
            request.Actor,
            request.CorrelationId,
            now);
        record.StartLease(now, request.Policy.OperationTimeout);
        dbContext.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<RetentionRunReservation, string>(
                new RetentionRunReservation(record.Id, false, null, record.LeaseToken, true));
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            dbContext.Entry(record).State = EntityState.Detached;
            existing = await dbContext.Set<RetentionRunRecord>()
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesOperation(existing, request, operationFingerprint))
            {
                return Result.Fail<RetentionRunReservation, string>(
                    "retention_idempotency_conflict");
            }

            return Result.Succeed<RetentionRunReservation, string>(
                new RetentionRunReservation(
                    existing.Id,
                    true,
                    null,
                    existing.LeaseToken,
                    false));
        }
        catch (DbUpdateException)
        {
            return Result.Fail<RetentionRunReservation, string>("retention_persistence_failure");
        }
    }

    public async Task<Result<IReadOnlyList<RetentionBatchItem>, string>> GetOrCreateBatchAsync(
        Guid runId,
        Guid leaseToken,
        IReadOnlyList<RetentionCandidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var run = await FindLeasedRunAsync(runId, leaseToken, now, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result.Fail<IReadOnlyList<RetentionBatchItem>, string>("retention_lease_lost");
        }

        var items = await dbContext.Set<RetentionRunItemRecord>()
            .Where(item => item.RunId == runId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (items.Count == 0 && candidates.Count > 0)
        {
            foreach (var candidate in candidates)
            {
                dbContext.Add(new RetentionRunItemRecord(
                    runId,
                    candidate.Target,
                    candidate.SubjectId,
                    candidate.EligibleAtUtc));
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                return Result.Fail<IReadOnlyList<RetentionBatchItem>, string>(
                    "retention_persistence_failure");
            }

            items = await dbContext.Set<RetentionRunItemRecord>()
                .Where(item => item.RunId == runId)
                .OrderBy(item => item.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        return Result.Succeed<IReadOnlyList<RetentionBatchItem>, string>(
            items.Select(ToBatchItem).ToArray());
    }

    public async Task<Result<RetentionApplyAuthorization, string>> TryBeginApplyAsync(
        Guid runId,
        Guid leaseToken,
        RetentionCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(candidate.Target, cancellationToken).ConfigureAwait(false);
        var run = await FindLeasedRunAsync(runId, leaseToken, now, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
        {
            return Result.Fail<RetentionApplyAuthorization, string>("retention_lease_lost");
        }

        var item = await FindItemAsync(runId, candidate, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Result.Fail<RetentionApplyAuthorization, string>("retention_item_not_found");
        }

        if (string.Equals(item.Status, RetentionBatchItemStatus.Applied.ToString(), StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<RetentionApplyAuthorization, string>(
                CreateApplyAuthorization(runId, leaseToken, candidate));
        }

        var held = await HasActiveHoldAsync(candidate, now, cancellationToken).ConfigureAwait(false);
        if (held)
        {
            item.MarkHeld(now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<RetentionApplyAuthorization, string>(
                new RetentionApplyAuthorization(RetentionApplyDecision.Held, null));
        }

        item.MarkApplying(now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Succeed<RetentionApplyAuthorization, string>(
            CreateApplyAuthorization(runId, leaseToken, candidate));
    }

    public async Task<Result<bool, string>> MarkAppliedAsync(
        Guid runId,
        Guid leaseToken,
        RetentionCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (await FindLeasedRunAsync(runId, leaseToken, now, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Fail<bool, string>("retention_lease_lost");
        }

        var item = await FindItemAsync(runId, candidate, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return Result.Fail<bool, string>("retention_item_not_found");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(candidate.Target, cancellationToken).ConfigureAwait(false);

        // Lock order is target applock -> run -> exact item -> mutation.
        var leaseIsCurrent = await dbContext.Set<RetentionRunRecord>()
            .AsNoTracking()
            .AnyAsync(
                run => run.Id == runId &&
                    run.LeaseToken == leaseToken &&
                    run.LeaseExpiresAtUtc > now &&
                    !run.CompletedAtUtc.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leaseIsCurrent)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("retention_lease_lost");
        }

        var marked = await dbContext.Set<RetentionRunItemRecord>()
            .Where(candidateItem =>
                candidateItem.RunId == runId &&
                candidateItem.Target == candidate.Target &&
                candidateItem.SubjectId == candidate.SubjectId &&
                candidateItem.Status == RetentionBatchItemStatus.Applying.ToString())
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        candidateItem => candidateItem.Status,
                        RetentionBatchItemStatus.Applied.ToString())
                    .SetProperty(candidateItem => candidateItem.UpdatedAtUtc, now),
                cancellationToken)
            .ConfigureAwait(false);
        if (marked != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("retention_item_state_conflict");
        }

        dbContext.Entry(item).State = EntityState.Detached;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Succeed<bool, string>(true);
    }

    public async Task CompleteAsync(
        RetentionRunRequest request,
        RetentionRunResult result,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.Set<RetentionRunRecord>()
            .SingleAsync(entity => entity.Id == result.RunId, cancellationToken)
            .ConfigureAwait(false);
        if (record.CompletedAtUtc.HasValue)
        {
            if (record.LeaseToken != leaseToken)
            {
                throw new InvalidOperationException(
                    "The retention run lease is no longer valid.");
            }

            return;
        }

        if (!record.HasLease(leaseToken, now))
        {
            throw new InvalidOperationException("The retention run lease is no longer valid.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(record.Target, cancellationToken).ConfigureAwait(false);

        // Lock order is target applock -> run -> items -> mutation.
        var leaseIsCurrent = await dbContext.Set<RetentionRunRecord>()
            .AsNoTracking()
            .AnyAsync(
                run => run.Id == result.RunId &&
                    run.LeaseToken == leaseToken &&
                    run.LeaseExpiresAtUtc > now &&
                    !run.CompletedAtUtc.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        if (!leaseIsCurrent)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The retention run lease is no longer valid.");
        }

        var itemStatuses = await dbContext.Set<RetentionRunItemRecord>()
            .AsNoTracking()
            .Where(item => item.RunId == result.RunId)
            .Select(item => item.Status)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var held = itemStatuses.Count(status =>
            string.Equals(
                status,
                RetentionBatchItemStatus.Held.ToString(),
                StringComparison.Ordinal));
        var applied = itemStatuses.Count(status =>
            string.Equals(
                status,
                RetentionBatchItemStatus.Applied.ToString(),
                StringComparison.Ordinal));
        var expectedApplied = request.Mode == RetentionMode.Apply ? applied : 0;
        if (itemStatuses.Length != result.Examined ||
            held != result.Held ||
            expectedApplied != result.Applied ||
            held + applied != itemStatuses.Length)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                "The retention run state no longer matches the completion result.");
        }

        var completed = await dbContext.Set<RetentionRunRecord>()
            .Where(run => run.Id == result.RunId &&
                run.LeaseToken == leaseToken &&
                run.LeaseExpiresAtUtc > now &&
                !run.CompletedAtUtc.HasValue)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Examined, result.Examined)
                    .SetProperty(run => run.Held, result.Held)
                    .SetProperty(run => run.Applied, result.Applied)
                    .SetProperty(run => run.CompletedAtUtc, now),
                cancellationToken)
            .ConfigureAwait(false);
        if (completed != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("The retention run lease is no longer valid.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<Guid, string>> PlaceAsync(
        RetentionHoldRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Target) ||
            request.Target.Length > 100 ||
            string.IsNullOrWhiteSpace(request.SubjectId) ||
            request.SubjectId.Length > 256 ||
            string.IsNullOrWhiteSpace(request.Actor) ||
            request.Actor.Length > 200 ||
            string.IsNullOrWhiteSpace(request.Reason) ||
            request.Reason.Length > 1_000 ||
            request.ExpiresAtUtc <= now)
        {
            return Result.Fail<Guid, string>("invalid_retention_hold");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireHoldIdempotencyLockAsync(
                request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(request.Target, cancellationToken).ConfigureAwait(false);
        var existing = await dbContext.Set<RetentionHoldRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                hold => hold.IdempotencyKey == request.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return HoldMatches(existing, request)
                ? Result.Succeed<Guid, string>(existing.Id)
                : Result.Fail<Guid, string>("retention_hold_idempotency_conflict");
        }

        var applyInProgress = await dbContext.Set<RetentionRunItemRecord>()
            .Where(item => item.Target == request.Target &&
                (item.SubjectId == request.SubjectId || request.SubjectId == "*") &&
                item.Status == RetentionBatchItemStatus.Applying.ToString())
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (applyInProgress)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<Guid, string>("retention_apply_in_progress");
        }

        var hold = new RetentionHoldRecord(
            request.IdempotencyKey,
            request.Target,
            request.SubjectId,
            request.Kind.ToString(),
            request.Actor,
            request.Reason,
            now,
            request.ExpiresAtUtc);
        dbContext.Add(hold);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<Guid, string>(hold.Id);
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException { Number: 2601 or 2627 })
        {
            dbContext.Entry(hold).State = EntityState.Detached;
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            existing = await dbContext.Set<RetentionHoldRecord>()
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return HoldMatches(existing, request)
                ? Result.Succeed<Guid, string>(existing.Id)
                : Result.Fail<Guid, string>("retention_hold_idempotency_conflict");
        }

        catch (DbUpdateException)
        {
            return Result.Fail<Guid, string>("retention_hold_persistence_failure");
        }
    }

    private static bool HoldMatches(
        RetentionHoldRecord existing,
        RetentionHoldRequest request) =>
        string.Equals(existing.Target, request.Target, StringComparison.Ordinal) &&
        string.Equals(existing.SubjectId, request.SubjectId, StringComparison.Ordinal) &&
        string.Equals(existing.Kind, request.Kind.ToString(), StringComparison.Ordinal) &&
        string.Equals(existing.Actor, request.Actor, StringComparison.Ordinal) &&
        string.Equals(existing.Reason, request.Reason, StringComparison.Ordinal) &&
        existing.ExpiresAtUtc == request.ExpiresAtUtc;

    public async Task<Result<bool, string>> ReleaseAsync(
        Guid holdId,
        string actor,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (holdId == Guid.Empty ||
            string.IsNullOrWhiteSpace(actor) ||
            actor.Length > 200 ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Length > 1_000)
        {
            return Result.Fail<bool, string>("invalid_retention_hold_release");
        }

        var hold = await dbContext.Set<RetentionHoldRecord>()
            .SingleOrDefaultAsync(entity => entity.Id == holdId, cancellationToken)
            .ConfigureAwait(false);
        if (hold is null)
        {
            return Result.Fail<bool, string>("retention_hold_not_found");
        }

        hold.Release(actor, reason, now);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Succeed<bool, string>(true);
    }

    public Task<bool> HasActiveHoldAsync(
        RetentionCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.Set<RetentionHoldRecord>()
            .AnyAsync(
                hold => hold.Target == candidate.Target &&
                    (hold.SubjectId == candidate.SubjectId || hold.SubjectId == "*") &&
                    hold.StartsAtUtc <= now &&
                    !hold.ReleasedAtUtc.HasValue &&
                    (!hold.ExpiresAtUtc.HasValue || hold.ExpiresAtUtc > now),
                cancellationToken);

    private Task<RetentionRunRecord?> FindLeasedRunAsync(
        Guid runId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        dbContext.Set<RetentionRunRecord>()
            .SingleOrDefaultAsync(
                run => run.Id == runId &&
                    run.LeaseToken == leaseToken &&
                    run.LeaseExpiresAtUtc > now &&
                    !run.CompletedAtUtc.HasValue,
                cancellationToken);

    private Task<RetentionRunItemRecord?> FindItemAsync(
        Guid runId,
        RetentionCandidate candidate,
        CancellationToken cancellationToken) =>
        dbContext.Set<RetentionRunItemRecord>()
            .SingleOrDefaultAsync(
                item => item.RunId == runId &&
                    item.Target == candidate.Target &&
                    item.SubjectId == candidate.SubjectId,
                cancellationToken);

    private static RetentionBatchItem ToBatchItem(RetentionRunItemRecord item) =>
        new(
            new RetentionCandidate(item.Target, item.SubjectId, item.EligibleAtUtc),
            Enum.Parse<RetentionBatchItemStatus>(item.Status));

    private static RetentionApplyAuthorization CreateApplyAuthorization(
        Guid runId,
        Guid leaseToken,
        RetentionCandidate candidate) =>
        new(
            RetentionApplyDecision.Apply,
            new RetentionApplyPermit(
                runId,
                leaseToken,
                candidate,
                $"{runId:N}:{candidate.SubjectId}"));

    private static string CreateOperationFingerprint(RetentionRunRequest request)
    {
        var canonical = string.Join(
            "\n",
            request.Policy.Name,
            request.Policy.Target,
            request.Mode.ToString(),
            request.Policy.RetainFor.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            request.Policy.BatchSize.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            request.Policy.OperationTimeout.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return $"v1:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
    }

    private static bool MatchesOperation(
        RetentionRunRecord record,
        RetentionRunRequest request,
        string operationFingerprint) =>
        string.Equals(record.Policy, operationFingerprint, StringComparison.Ordinal) &&
        string.Equals(record.Target, request.Policy.Target, StringComparison.Ordinal) &&
        string.Equals(record.Mode, request.Mode.ToString(), StringComparison.Ordinal);

    private async Task AcquireTargetLockAsync(
        string target,
        CancellationToken cancellationToken)
    {
        await AcquireTransactionLockAsync(
                "retention-target",
                target,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AcquireHoldIdempotencyLockAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await AcquireTransactionLockAsync(
                "retention-hold-idempotency",
                idempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task AcquireTransactionLockAsync(
        string scope,
        string value,
        CancellationToken cancellationToken)
    {
        var resource = $"{scope}:{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value)))}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DECLARE @lockResult int;
                 EXEC @lockResult = sys.sp_getapplock
                     @Resource = {resource},
                     @LockMode = 'Exclusive',
                     @LockOwner = 'Transaction',
                     @LockTimeout = 10000;
                 IF @lockResult < 0
                     THROW 51000, 'Retention target lock could not be acquired.', 1;
                 """,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
