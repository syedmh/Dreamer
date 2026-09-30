using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HusayniaTabruk.Infrastructure.Persistence.Repositories;

public sealed class PostgresUnitOfWork(TabrukDbContext context) : IUnitOfWork
{
    public ValueTask<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<Result<T>>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return PostgresDependencyFailure.ExecuteAsync(
            () => ExecuteCoreAsync(operation, cancellationToken));
    }

    private async ValueTask<Result<T>> ExecuteCoreAsync<T>(
        Func<CancellationToken, ValueTask<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken);
        }

        IDbContextTransaction? transaction =
            await context.Database.BeginTransactionAsync(cancellationToken);
        Exception? primaryException = null;
        bool rollbackOwnedTransaction = false;
        bool clearChangeTracker = false;

        try
        {
            Result<T> result = await operation(cancellationToken);
            if (result.IsFailure)
            {
                rollbackOwnedTransaction = true;
                clearChangeTracker = true;
                return result;
            }

            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception exception)
        {
            primaryException = exception;
            rollbackOwnedTransaction = true;
            clearChangeTracker = true;
            throw;
        }
        finally
        {
            await PostgresOwnedTransactionCleanup.CleanupAsync(
                context,
                transaction,
                rollbackOwnedTransaction,
                clearChangeTracker,
                primaryException);
        }
    }
}

public sealed class PostgresIdempotencyStore(TabrukDbContext context) : IIdempotencyStore
{
    public ValueTask<IdempotencyReceipt?> FindAsync(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        PostgresDependencyFailure.ExecuteAsync(
            () => FindCoreAsync(organizationId, membershipId, key, cancellationToken));

    private async ValueTask<IdempotencyReceipt?> FindCoreAsync(
        OrganizationId organizationId,
        MembershipId membershipId,
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        IdempotencyRecordEntity? record = await context.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == organizationId.Value
                    && candidate.MembershipId == membershipId.Value
                    && candidate.Key == key.Value,
                cancellationToken);

        return record is null ? null : ToReceipt(record);
    }

    public ValueTask<IdempotencyCreateResult> TryCreateProcessingAsync(
        IdempotencyCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostgresDependencyFailure.ExecuteAsync(
            () => TryCreateProcessingCoreAsync(request, cancellationToken));
    }

    private async ValueTask<IdempotencyCreateResult> TryCreateProcessingCoreAsync(
        IdempotencyCreateRequest request,
        CancellationToken cancellationToken)
    {
        int inserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO idempotency_records
                (organization_id, membership_id, key, operation, request_fingerprint, status, result_reference, created_at, expires_at)
            VALUES
                ({request.OrganizationId.Value}, {request.MembershipId.Value}, {request.Key.Value}, {request.Operation},
                 {request.RequestFingerprint.Value}, {0}, {null}, {request.CreatedAt}, {request.ExpiresAt})
            ON CONFLICT (organization_id, membership_id, key) DO NOTHING
            """,
            cancellationToken);

        IdempotencyRecordEntity record = await LoadRequiredAsync(request, cancellationToken);
        IdempotencyReceipt receipt = ToReceipt(record);
        if (inserted == 1)
        {
            return new IdempotencyCreateResult(IdempotencyCreateOutcome.Created, receipt);
        }

        IdempotencyReceiptDisposition disposition =
            receipt.Evaluate(request.Operation, request.RequestFingerprint, request.CreatedAt);
        IdempotencyCreateOutcome outcome = disposition switch
        {
            IdempotencyReceiptDisposition.Processing => IdempotencyCreateOutcome.ExistingProcessing,
            IdempotencyReceiptDisposition.Completed => IdempotencyCreateOutcome.ExistingCompleted,
            IdempotencyReceiptDisposition.Failed => IdempotencyCreateOutcome.ExistingFailed,
            IdempotencyReceiptDisposition.RequestMismatch => IdempotencyCreateOutcome.RequestMismatch,
            IdempotencyReceiptDisposition.Expired => IdempotencyCreateOutcome.Expired,
            _ => throw new InvalidOperationException($"Unsupported idempotency disposition: {disposition}."),
        };

        return new IdempotencyCreateResult(outcome, receipt);
    }

    public ValueTask<IdempotencyTransitionResult> TryCompleteAsync(
        IdempotencyRequest request,
        string resultReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultReference);
        return PostgresDependencyFailure.ExecuteAsync(
            () => TransitionAsync(request, IdempotencyStatus.Completed, resultReference, cancellationToken));
    }

    public ValueTask<IdempotencyTransitionResult> TryFailAsync(
        IdempotencyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PostgresDependencyFailure.ExecuteAsync(
            () => TransitionAsync(request, IdempotencyStatus.Failed, resultReference: null, cancellationToken));
    }

    private async ValueTask<IdempotencyTransitionResult> TransitionAsync(
        IdempotencyRequest request,
        IdempotencyStatus targetStatus,
        string? resultReference,
        CancellationToken cancellationToken)
    {
        int changed = await context.IdempotencyRecords
            .Where(
                candidate => candidate.OrganizationId == request.OrganizationId.Value
                    && candidate.MembershipId == request.MembershipId.Value
                    && candidate.Key == request.Key.Value
                    && candidate.Operation == request.Operation
                    && candidate.RequestFingerprint == request.RequestFingerprint.Value
                    && candidate.Status == (short)IdempotencyStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.Status, checked((short)targetStatus))
                    .SetProperty(candidate => candidate.ResultReference, resultReference),
                cancellationToken);

        IdempotencyRecordEntity? current = await context.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == request.OrganizationId.Value
                    && candidate.MembershipId == request.MembershipId.Value
                    && candidate.Key == request.Key.Value,
                cancellationToken);
        if (current is null)
        {
            return new IdempotencyTransitionResult(IdempotencyTransitionOutcome.Missing, null);
        }

        IdempotencyReceipt receipt = ToReceipt(current);
        if (current.Operation != request.Operation
            || current.RequestFingerprint != request.RequestFingerprint.Value)
        {
            return new IdempotencyTransitionResult(IdempotencyTransitionOutcome.RequestMismatch, receipt);
        }

        if (changed == 1)
        {
            IdempotencyTransitionOutcome completed = targetStatus == IdempotencyStatus.Completed
                ? IdempotencyTransitionOutcome.Completed
                : IdempotencyTransitionOutcome.Failed;
            return new IdempotencyTransitionResult(completed, receipt);
        }

        return new IdempotencyTransitionResult(IdempotencyTransitionOutcome.ExpectedStatusMismatch, receipt);
    }

    private async ValueTask<IdempotencyRecordEntity> LoadRequiredAsync(
        IdempotencyCreateRequest request,
        CancellationToken cancellationToken) =>
        await context.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.OrganizationId == request.OrganizationId.Value
                    && candidate.MembershipId == request.MembershipId.Value
                    && candidate.Key == request.Key.Value,
                cancellationToken)
        ?? throw new InvalidOperationException("The idempotency record was not available after its insert attempt.");

    private static IdempotencyReceipt ToReceipt(IdempotencyRecordEntity record) =>
        new(
            OrganizationId.From(record.OrganizationId),
            MembershipId.From(record.MembershipId),
            IdempotencyKey.From(record.Key),
            record.Operation,
            RequestFingerprint.FromSha256(record.RequestFingerprint),
            (IdempotencyStatus)record.Status,
            record.ResultReference,
            record.CreatedAt,
            record.ExpiresAt);
}

public sealed class PostgresAuditWriter(TabrukDbContext context) : IAuditWriter
{
    public ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.AuditEvents.Add(PersistenceWriteSupport.ToEntity(entry));
        return ValueTask.CompletedTask;
    }
}

public sealed class PostgresPrivilegedAccessWriter(TabrukDbContext context) : IPrivilegedAccessWriter
{
    public ValueTask WriteAsync(PrivilegedAccessEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.PrivilegedAccessEvents.Add(PersistenceWriteSupport.ToEntity(entry));
        return ValueTask.CompletedTask;
    }
}

public sealed class PostgresOutboxWriter(TabrukDbContext context) : IOutboxWriter
{
    public ValueTask AddAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.OutboxMessages.Add(PersistenceWriteSupport.ToEntity(message));
        return ValueTask.CompletedTask;
    }
}
