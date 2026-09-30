using System.Transactions;
using Husaynia.Application.Contracts;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using IsolationLevel = System.Data.IsolationLevel;

namespace Husaynia.Infrastructure.Persistence.Transactions;

public sealed class EfHusayniaUnitOfWork(HusayniaDbContext dbContext) : IHusayniaUnitOfWork
{
    private readonly HusayniaDbContext dbContext =
        dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private int depth;
    private PersistenceError? nestedFailure;

    public async Task<Result<T, PersistenceError>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T, PersistenceError>>> operation,
        UnitOfWorkOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        options ??= UnitOfWorkOptions.Default;

        if (depth > 0)
        {
            return await ExecuteNestedAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        if (Transaction.Current is not null)
        {
            return Failure<T>(
                PersistenceErrorCode.AmbientTransactionNotSupported,
                "Ambient transactions are not supported; use the Husaynia unit of work.");
        }

        if (dbContext.Database.CurrentTransaction is not null)
        {
            return Failure<T>(
                PersistenceErrorCode.ExistingTransactionNotSupported,
                "An externally managed EF transaction cannot be adopted.");
        }

        if (options.RetryMode == ExecutionRetryMode.ExplicitlyIdempotent)
        {
            var strategy = new SqlServerRetryingExecutionStrategy(
                dbContext,
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(2),
                errorNumbersToAdd: null);
            try
            {
                return await strategy.ExecuteAsync(
                        () => ExecuteAttemptAsync(
                            operation,
                            options.IsolationLevel,
                            propagateUnexpectedFailure: true,
                            cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Failure<T>(
                    PersistenceErrorCode.UnexpectedFailure,
                    "The idempotent database operation failed after bounded retries.");
            }
        }

        return await ExecuteAttemptAsync(
                operation,
                options.IsolationLevel,
                propagateUnexpectedFailure: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Result<T, PersistenceError>> ExecuteNestedAsync<T>(
        Func<CancellationToken, Task<Result<T, PersistenceError>>> operation,
        CancellationToken cancellationToken)
    {
        depth++;
        try
        {
            var result = await operation(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                nestedFailure ??= result.Error;
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            nestedFailure ??= new PersistenceError(
                PersistenceErrorCode.NestedOperationFailed,
                "A nested operation was cancelled.");
            throw;
        }
        catch (Exception)
        {
            var error = new PersistenceError(
                PersistenceErrorCode.UnexpectedFailure,
                "The nested database operation failed.");
            nestedFailure ??= error;
            return Result.Fail<T, PersistenceError>(error);
        }
        finally
        {
            depth--;
        }
    }

    private async Task<Result<T, PersistenceError>> ExecuteAttemptAsync<T>(
        Func<CancellationToken, Task<Result<T, PersistenceError>>> operation,
        IsolationLevel isolationLevel,
        bool propagateUnexpectedFailure,
        CancellationToken cancellationToken)
    {
        nestedFailure = null;
        IDbContextTransaction? transaction = null;

        try
        {
            transaction = await dbContext.Database
                .BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);
            depth = 1;

            var result = await operation(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return result;
            }

            if (nestedFailure is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return NestedFailure<T>();
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackOwnedTransactionAsync(transaction).ConfigureAwait(false);
            return Failure<T>(
                PersistenceErrorCode.ConcurrencyConflict,
                "The record changed after it was loaded.");
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            nestedFailure is not null)
        {
            await RollbackOwnedTransactionAsync(transaction).ConfigureAwait(false);
            return NestedFailure<T>();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            transaction ??= dbContext.Database.CurrentTransaction;
            await RollbackOwnedTransactionAsync(transaction).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            transaction ??= dbContext.Database.CurrentTransaction;
            await RollbackOwnedTransactionAsync(transaction).ConfigureAwait(false);
            if (propagateUnexpectedFailure)
            {
                throw;
            }

            return Failure<T>(
                PersistenceErrorCode.UnexpectedFailure,
                "The database operation failed.");
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }

            depth = 0;
            nestedFailure = null;
        }
    }

    private async Task RollbackOwnedTransactionAsync(IDbContextTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private static Result<T, PersistenceError> Failure<T>(
        PersistenceErrorCode code,
        string message) =>
        Result.Fail<T, PersistenceError>(new PersistenceError(code, message));

    private static Result<T, PersistenceError> NestedFailure<T>() =>
        Failure<T>(
            PersistenceErrorCode.NestedOperationFailed,
            "A nested operation failed; the entire transaction was rolled back.");
}
