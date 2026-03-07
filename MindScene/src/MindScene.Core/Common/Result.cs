namespace MindScene.Core.Common;

/// <summary>
/// Discriminated union result type for expected success/failure paths.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;
    private readonly string? _error;
    private readonly bool _isSuccess;

    private Result(T value) { _value = value; _isSuccess = true; _error = null; }
    private Result(string error) { _value = default; _isSuccess = false; _error = error; }

    public bool IsSuccess => _isSuccess;
    public bool IsFailure => !_isSuccess;
    public T Value => _isSuccess ? _value! : throw new InvalidOperationException($"Cannot access Value on failed Result: {_error}");
    public string Error => !_isSuccess ? _error! : throw new InvalidOperationException("Cannot access Error on successful Result");

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(string error) => new(error);

    public Result<TOut> Map<TOut>(Func<T, TOut> map) =>
        _isSuccess ? Result<TOut>.Ok(map(_value!)) : Result<TOut>.Fail(_error!);

    public async Task<Result<TOut>> MapAsync<TOut>(Func<T, Task<TOut>> map) =>
        _isSuccess ? Result<TOut>.Ok(await map(_value!)) : Result<TOut>.Fail(_error!);

    public T? GetValueOrDefault(T? defaultValue = default) => _isSuccess ? _value : defaultValue;

    public override string ToString() => _isSuccess ? $"Ok({_value})" : $"Fail({_error})";
}

public static class Result
{
    public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);
    public static Result<T> Fail<T>(string error) => Result<T>.Fail(error);
}
