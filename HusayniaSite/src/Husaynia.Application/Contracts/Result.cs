namespace Husaynia.Application.Contracts;

/// <summary>
/// Represents either an expected success value or an expected error value.
/// </summary>
public readonly struct Result<TSuccess, TError>
{
    private readonly TSuccess? success;
    private readonly TError? error;

    internal Result(TSuccess success)
    {
        this.success = success;
        error = default;
        IsSuccess = true;
    }

    internal Result(TError error)
    {
        success = default;
        this.error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public TSuccess Success =>
        IsSuccess ? success! : throw new InvalidOperationException("A failed result has no success value.");

    public TError Error =>
        IsFailure ? error! : throw new InvalidOperationException("A successful result has no error value.");

}

public static class Result
{
    public static Result<TSuccess, TError> Succeed<TSuccess, TError>(TSuccess value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<TSuccess, TError>(value);
    }

    public static Result<TSuccess, TError> Fail<TSuccess, TError>(TError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TSuccess, TError>(error);
    }
}
