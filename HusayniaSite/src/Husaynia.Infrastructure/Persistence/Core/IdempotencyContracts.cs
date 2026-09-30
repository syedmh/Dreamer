using Husaynia.Application.Contracts;

namespace Husaynia.Infrastructure.Persistence.Core;

public enum IdempotencyOutcome
{
    Acquired,
    DuplicateCompleted,
    DuplicateInProgress,
}

public sealed record IdempotencyReservation(
    Guid ReservationId,
    IdempotencyOutcome Outcome,
    string? Receipt);

public enum IdempotencyErrorCode
{
    InvalidKey,
    PayloadConflict,
    ReservationNotFound,
    PersistenceFailure,
}

public sealed record IdempotencyError(IdempotencyErrorCode Code, string Message);

public interface IIdempotencyStore
{
    Task<Result<IdempotencyReservation, IdempotencyError>> TryReserveAsync(
        string scope,
        string key,
        string requestHash,
        CancellationToken cancellationToken);

    Task<Result<IdempotencyReservation, IdempotencyError>> CompleteAsync(
        Guid reservationId,
        string receipt,
        CancellationToken cancellationToken);

    Task<Result<IdempotencyReservation, IdempotencyError>> FindAsync(
        string scope,
        string key,
        CancellationToken cancellationToken);
}
