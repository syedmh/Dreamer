using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;

namespace HusayniaTabruk.Api.Auth;

internal static class AuthEndpointResults
{
    public static IResult FromResult<T>(
        Result<T> result,
        Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error);
    }

    public static IResult FromResult(Result result, Func<IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess() : Problem(result.Error);
    }

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        (int status, string title) = error.Type switch
        {
            ErrorType.Validation => (StatusCodes.Status400BadRequest, "Bad request"),
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "Authentication failed"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Not found"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
            ErrorType.PreconditionFailed => (StatusCodes.Status412PreconditionFailed, "Precondition failed"),
            ErrorType.PayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, "Payload too large"),
            ErrorType.RateLimited => (StatusCodes.Status429TooManyRequests, "Rate limit exceeded"),
            ErrorType.DependencyUnavailable => (StatusCodes.Status503ServiceUnavailable, "Service unavailable"),
            _ => (StatusCodes.Status400BadRequest, "Request failed"),
        };

        return new ProblemHttpResult(status, error.Code, title, error.Message, fieldErrors);
    }

    private sealed class ProblemHttpResult(
        int status,
        string code,
        string title,
        string detail,
        IReadOnlyDictionary<string, string[]>? fieldErrors)
        : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            ApiProblemWriter.WriteAsync(
                httpContext,
                status,
                code,
                title,
                detail,
                fieldErrors);
    }
}
