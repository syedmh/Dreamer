using System.Data;
using Husaynia.Application.Contracts;

namespace Husaynia.Infrastructure.Persistence.Transactions;

public enum PersistenceErrorCode
{
    ConcurrencyConflict,
    AmbientTransactionNotSupported,
    ExistingTransactionNotSupported,
    NestedOperationFailed,
    UnexpectedFailure,
}

public sealed record PersistenceError(PersistenceErrorCode Code, string Message);

public enum ExecutionRetryMode
{
    None,
    ExplicitlyIdempotent,
}

public sealed record UnitOfWorkOptions(
    IsolationLevel IsolationLevel,
    ExecutionRetryMode RetryMode)
{
    public static UnitOfWorkOptions Default { get; } =
        new(IsolationLevel.ReadCommitted, ExecutionRetryMode.None);

    public static UnitOfWorkOptions Serializable { get; } =
        new(IsolationLevel.Serializable, ExecutionRetryMode.None);

    public static UnitOfWorkOptions IdempotentWithRetry(IsolationLevel isolationLevel) =>
        new(isolationLevel, ExecutionRetryMode.ExplicitlyIdempotent);
}

public interface IHusayniaUnitOfWork
{
    Task<Result<T, PersistenceError>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T, PersistenceError>>> operation,
        UnitOfWorkOptions? options,
        CancellationToken cancellationToken);
}
