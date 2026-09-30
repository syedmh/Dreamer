using System.Data;
using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Retention;
using Husaynia.Domain.Forms;
using Husaynia.Infrastructure.Operations.Persistence;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Forms;

public sealed class FormSubmissionRetentionTarget(
    HusayniaDbContext dbContext,
    TimeProvider timeProvider)
    : IRetentionTarget
{
    public const string TargetName = "forms.submissions";
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public string Target => TargetName;

    public async Task<IReadOnlyList<RetentionCandidate>> FindEligibleAsync(
        DateTimeOffset olderThanUtc,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return await dbContext.Set<FormSubmission>()
            .AsNoTracking()
            .Where(submission =>
                submission.AcceptedAtUtc <= olderThanUtc &&
                submission.RetentionEligibleAtUtc <= now &&
                submission.RetentionStatus == FormRetentionStatus.Eligible &&
                !submission.HasLegalHold)
            .OrderBy(submission => submission.AcceptedAtUtc)
            .ThenBy(submission => submission.Id)
            .Take(maximumCount)
            .Select(submission => new RetentionCandidate(
                TargetName,
                submission.Id.ToString("N"),
                submission.RetentionEligibleAtUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<bool, string>> ApplyAsync(
        RetentionApplyPermit permit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permit);
        var candidate = permit.Candidate;
        ArgumentNullException.ThrowIfNull(candidate);
        if (!string.Equals(candidate.Target, TargetName, StringComparison.Ordinal) ||
            !Guid.TryParseExact(candidate.SubjectId, "N", out var submissionId) ||
            permit.RunId == Guid.Empty ||
            permit.LeaseToken == Guid.Empty ||
            !string.Equals(
                permit.IdempotencyKey,
                $"{permit.RunId:N}:{candidate.SubjectId}",
                StringComparison.Ordinal))
        {
            return Result.Fail<bool, string>("invalid_forms_retention_permit");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);
        await AcquireTargetLockAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow().ToUniversalTime();

        var authorizedItem = await dbContext.Set<RetentionRunItemRecord>()
            .FromSqlInterpolated(
                $"""
                 SELECT item.*
                 FROM [dbo].[OperationsRetentionRunItems] AS item WITH (UPDLOCK, HOLDLOCK)
                 INNER JOIN [dbo].[OperationsRetentionRuns] AS activeRun WITH (UPDLOCK, HOLDLOCK)
                     ON activeRun.[Id] = item.[RunId]
                 WHERE activeRun.[Id] = {permit.RunId}
                   AND activeRun.[LeaseToken] = {permit.LeaseToken}
                   AND activeRun.[LeaseExpiresAtUtc] > {now}
                   AND activeRun.[CompletedAtUtc] IS NULL
                   AND item.[Target] = {candidate.Target}
                   AND item.[SubjectId] = {candidate.SubjectId}
                   AND item.[Status] = {RetentionBatchItemStatus.Applying.ToString()}
                 """)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (authorizedItem is null ||
            authorizedItem.EligibleAtUtc != candidate.EligibleAtUtc)
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
            return Result.Fail<bool, string>("forms_retention_held");
        }

        var submission = await dbContext.Set<FormSubmission>()
            .FromSqlInterpolated(
                $"""
                 SELECT *
                 FROM [dbo].[FormSubmissions] WITH (UPDLOCK, HOLDLOCK)
                 WHERE [Id] = {submissionId}
                 """)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (submission is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_submission_not_found");
        }

        if (submission.RetentionStatus == FormRetentionStatus.Anonymized)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<bool, string>(true);
        }

        if (submission.HasLegalHold)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_legal_hold");
        }

        if (submission.RetentionStatus != FormRetentionStatus.Eligible)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_status_conflict");
        }

        if (submission.RetentionEligibleAtUtc > now ||
            submission.RetentionEligibleAtUtc != candidate.EligibleAtUtc)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_not_due");
        }

        var rowVersion = dbContext.Entry(submission)
            .Property<byte[]>(PersistencePropertyNames.RowVersion)
            .CurrentValue;
        if (rowVersion is not { Length: 8 })
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_rowversion_invalid");
        }

        var values = await dbContext.Set<FormSubmissionValue>()
            .Where(value => value.SubmissionId == submission.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var value in values)
        {
            value.Anonymize();
        }

        submission.Anonymize(now);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<bool, string>(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return Result.Fail<bool, string>("forms_retention_rowversion_conflict");
        }
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
