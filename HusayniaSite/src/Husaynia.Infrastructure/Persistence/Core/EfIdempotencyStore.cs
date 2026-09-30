using Husaynia.Application.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Cryptography;
using System.Text;

namespace Husaynia.Infrastructure.Persistence.Core;

public sealed class EfIdempotencyStore(HusayniaDbContext dbContext) : IIdempotencyStore
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<Result<IdempotencyReservation, IdempotencyError>> TryReserveAsync(
        string scope,
        string key,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var validationError = Validate(scope, key, requestHash);
        if (validationError is not null)
        {
            return Result.Fail<IdempotencyReservation, IdempotencyError>(validationError);
        }

        if (dbContext.Database.CurrentTransaction is not null)
        {
            var lockAcquired = await AcquireTransactionLockAsync(
                    scope,
                    key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!lockAcquired)
            {
                return PersistenceFailure();
            }
        }

        var existingBeforeInsert = await dbContext.Set<IdempotencyRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Scope == scope && candidate.Key == key,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingBeforeInsert is not null)
        {
            return FromExisting(existingBeforeInsert, requestHash);
        }

        var record = new IdempotencyRecord(scope, key, requestHash);
        dbContext.Set<IdempotencyRecord>().Add(record);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Succeed<IdempotencyReservation, IdempotencyError>(
                new IdempotencyReservation(record.Id, IdempotencyOutcome.Acquired, null));
        }
        catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
        {
            dbContext.Entry(record).State = EntityState.Detached;
            var existing = await dbContext.Set<IdempotencyRecord>()
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Scope == scope && candidate.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);

            return FromExisting(existing, requestHash);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(record).State = EntityState.Detached;
            return PersistenceFailure();
        }
    }

    public async Task<Result<IdempotencyReservation, IdempotencyError>> CompleteAsync(
        Guid reservationId,
        string receipt,
        CancellationToken cancellationToken)
    {
        if (reservationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(receipt) ||
            receipt.Length > 4000)
        {
            return Result.Fail<IdempotencyReservation, IdempotencyError>(
                new IdempotencyError(
                    IdempotencyErrorCode.InvalidKey,
                    "A reservation identifier and receipt are required."));
        }

        var record = await dbContext.Set<IdempotencyRecord>()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == reservationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Result.Fail<IdempotencyReservation, IdempotencyError>(
                new IdempotencyError(
                    IdempotencyErrorCode.ReservationNotFound,
                    "The idempotency reservation was not found."));
        }

        if (record.CompletedAtUtc.HasValue)
        {
            return Result.Succeed<IdempotencyReservation, IdempotencyError>(
                ToReservation(record));
        }

        record.Complete(receipt);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return PersistenceFailure();
        }

        return Result.Succeed<IdempotencyReservation, IdempotencyError>(
            ToReservation(record));
    }

    public async Task<Result<IdempotencyReservation, IdempotencyError>> FindAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(key))
        {
            return Result.Fail<IdempotencyReservation, IdempotencyError>(
                new IdempotencyError(
                    IdempotencyErrorCode.InvalidKey,
                    "An idempotency scope and key are required."));
        }

        IdempotencyRecord? record;
        try
        {
            record = await dbContext.Set<IdempotencyRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    candidate => candidate.Scope == scope && candidate.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException)
        {
            return PersistenceFailure();
        }

        return record is null
            ? Result.Fail<IdempotencyReservation, IdempotencyError>(
                new IdempotencyError(
                    IdempotencyErrorCode.ReservationNotFound,
                    "The idempotency reservation was not found."))
            : Result.Succeed<IdempotencyReservation, IdempotencyError>(
                ToReservation(record));
    }

    private static IdempotencyReservation ToReservation(IdempotencyRecord record) =>
        new(
            record.Id,
            record.CompletedAtUtc.HasValue
                ? IdempotencyOutcome.DuplicateCompleted
                : IdempotencyOutcome.DuplicateInProgress,
            record.Receipt);

    private static Result<IdempotencyReservation, IdempotencyError> FromExisting(
        IdempotencyRecord existing,
        string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return Result.Fail<IdempotencyReservation, IdempotencyError>(
                new IdempotencyError(
                    IdempotencyErrorCode.PayloadConflict,
                    "The idempotency key was already used for a different request."));
        }

        return Result.Succeed<IdempotencyReservation, IdempotencyError>(
            ToReservation(existing));
    }

    private static IdempotencyError? Validate(
        string scope,
        string key,
        string requestHash)
    {
        if (string.IsNullOrWhiteSpace(scope) ||
            string.IsNullOrWhiteSpace(key) ||
            string.IsNullOrWhiteSpace(requestHash) ||
            scope.Length > 100 ||
            key.Length > 256 ||
            requestHash.Length > 128)
        {
            return new IdempotencyError(
                IdempotencyErrorCode.InvalidKey,
                "Idempotency scope, key, and request hash must be present and within limits.");
        }

        return null;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private async Task<bool> AcquireTransactionLockAsync(
        string scope,
        string key,
        CancellationToken cancellationToken)
    {
        var resourceHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\u001f{key}")));
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@resource";
        parameter.Value = $"Husaynia:Idempotency:{resourceHash}";
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is int code && code >= 0;
    }

    private static Result<IdempotencyReservation, IdempotencyError> PersistenceFailure() =>
        Result.Fail<IdempotencyReservation, IdempotencyError>(
            new IdempotencyError(
                IdempotencyErrorCode.PersistenceFailure,
                "The idempotency operation could not be persisted."));
}
