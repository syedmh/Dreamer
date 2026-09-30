namespace HusayniaTabruk.Domain.Common.Errors;

public enum ErrorType
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    PreconditionFailed,
    PayloadTooLarge,
    RateLimited,
    DependencyUnavailable,
}

public sealed record DomainError
{
    public DomainError(string code, string message, ErrorType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
        Type = type;
    }

    public string Code { get; }
    public string Message { get; }
    public ErrorType Type { get; }

    public static DomainError Validation(string code, string message) => new(code, message, ErrorType.Validation);
    public static DomainError Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);
    public static DomainError Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);
    public static DomainError NotFound(string code, string message) => new(code, message, ErrorType.NotFound);
    public static DomainError Conflict(string code, string message) => new(code, message, ErrorType.Conflict);
    public static DomainError PreconditionFailed(string code, string message) => new(code, message, ErrorType.PreconditionFailed);
    public static DomainError PayloadTooLarge(string message) => new(ErrorCodes.PayloadTooLarge, message, ErrorType.PayloadTooLarge);
    public static DomainError RateLimited(string message) => new(ErrorCodes.RateLimited, message, ErrorType.RateLimited);
    public static DomainError DependencyUnavailable(string code, string message) => new(code, message, ErrorType.DependencyUnavailable);
}
