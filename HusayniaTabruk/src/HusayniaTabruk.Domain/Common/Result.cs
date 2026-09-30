using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Domain.Common;

public class Result
{
    protected Result(bool isSuccess, DomainError? error)
    {
        if (isSuccess == (error is not null))
        {
            throw new ArgumentException("A result must contain either success or one error.");
        }

        IsSuccess = isSuccess;
        ErrorOrNull = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public DomainError Error => ErrorOrNull ?? throw new InvalidOperationException("A successful result has no error.");
    protected DomainError? ErrorOrNull { get; }

    public static Result Success() => new(true, null);
    public static Result Failure(DomainError error) => new(false, error ?? throw new ArgumentNullException(nameof(error)));
    public static Result<T> Success<T>(T value) => new(value);
    public static Result<T> Failure<T>(DomainError error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class Result<T> : Result
{
    private readonly T? value;

    internal Result(T value)
        : base(true, null) => this.value = value;

    internal Result(DomainError error)
        : base(false, error)
    {
    }

    public T Value => IsSuccess ? value! : throw new InvalidOperationException("A failed result has no value.");
}
