using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Retention;
using Husaynia.Infrastructure.Operations.Persistence;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace Husaynia.Infrastructure.Operations.Retention;

public sealed class CompletedRetentionRunTarget(
    HusayniaDbContext dbContext,
    TimeProvider? timeProvider = null)
    : IRetentionTarget
{
    public const string TargetName = "operations_retention_runs";

    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public string Target => TargetName;

    public async Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
        DateTimeOffset olderThanUtc,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var records = await dbContext.Set<RetentionRunRecord>()
            .AsNoTracking()
            .Where(run =>
                run.CompletedAtUtc.HasValue &&
                run.CompletedAtUtc.Value < olderThanUtc)
            .OrderBy(run => run.CompletedAtUtc)
            .ThenBy(run => run.Id)
            .Take(maximumCount)
            .Select(run => new { run.Id, CompletedAtUtc = run.CompletedAtUtc!.Value })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return records
            .Select(run => new RetentionCandidate(
                TargetName,
                run.Id.ToString("D"),
                run.CompletedAtUtc))
            .ToArray();
    }

    public async Task<Result<bool, string>> ApplyAsync(
        RetentionApplyPermit permit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permit);
        var candidate = permit.Candidate;
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.Target, TargetName, StringComparison.Ordinal) ||
            !Guid.TryParse(candidate.SubjectId, out var runId) ||
            permit.RunId == Guid.Empty ||
            permit.LeaseToken == Guid.Empty ||
            string.IsNullOrWhiteSpace(permit.IdempotencyKey))
        {
            return Result.Fail<bool, string>("retention_apply_permit_invalid");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(cancellationToken).ConfigureAwait(false);

        // Lock order is target applock -> run -> exact item -> holds -> mutation.
        var authorized = await (
                from activeRun in dbContext.Set<RetentionRunRecord>()
                join item in dbContext.Set<RetentionRunItemRecord>()
                    on activeRun.Id equals item.RunId
                where activeRun.Id == permit.RunId &&
                    activeRun.LeaseToken == permit.LeaseToken &&
                    activeRun.LeaseExpiresAtUtc > now &&
                    !activeRun.CompletedAtUtc.HasValue &&
                    item.Target == candidate.Target &&
                    item.SubjectId == candidate.SubjectId &&
                    item.Status == RetentionBatchItemStatus.Applying.ToString()
                select item.Id)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!authorized)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("retention_apply_permit_refused");
        }

        var held = await dbContext.Set<RetentionHoldRecord>()
            .AnyAsync(
                hold => hold.Target == candidate.Target &&
                    (hold.SubjectId == candidate.SubjectId || hold.SubjectId == "*") &&
                    hold.StartsAtUtc <= now &&
                    !hold.ReleasedAtUtc.HasValue &&
                    (!hold.ExpiresAtUtc.HasValue || hold.ExpiresAtUtc > now),
                cancellationToken)
            .ConfigureAwait(false);
        if (held)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("retention_apply_permit_refused");
        }

        await dbContext.Set<RetentionRunRecord>()
            .Where(run => run.Id == runId && run.CompletedAtUtc.HasValue)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Succeed<bool, string>(true);
    }

    private async Task AcquireTargetLockAsync(CancellationToken cancellationToken)
    {
        var resource = $"retention-target:{Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(TargetName)))}";
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
