using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Dates;

public static class DateEndpointSupport
{
    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request)
    {
        string? value = request.Headers[ApiDefaults.IdempotencyHeaderName].FirstOrDefault();
        return IdempotencyKey.TryParse(value, out IdempotencyKey key)
            ? Result.Success(key)
            : Result.Failure<IdempotencyKey>(
                DateApplicationErrorCodes.Validation("A valid Idempotency-Key header is required."));
    }

    public static void WriteEtag(HttpResponse response, long version) =>
        response.Headers.ETag = $"\"{version}\"";

    public static IResult FromResult<T>(Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error);

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
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
        return new ProblemResult(status, error.Code, title, error.Message, fieldErrors);
    }

    public static ServiceDateResponse ToResponse(ServiceDateSummary date) =>
        new(
            date.Id.ToString(),
            date.Title,
            date.Instructions,
            date.StartsAt,
            date.EndsAt,
            date.CancellationDeadlineAt,
            date.ManagerMembershipId.ToString(),
            date.Status,
            date.Version,
            date.HelpNeeds.Select(
                ToResponse).ToArray());

    public static HelpNeedResponse ToResponse(HelpNeedSummary need) =>
        new(
            need.Id.ToString(),
            need.Category,
            need.Instructions,
            need.Availability,
            need.Status,
            need.Version);

    private sealed class ProblemResult(
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

public sealed record CreateServiceDateRequest(
    string? Title,
    string? Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt,
    string? ManagerMembershipId);

public sealed record CreateHelpNeedRequest(
    HelpCategory Category,
    string? Instructions,
    int? Capacity);

public sealed record ServiceDateResponse(
    string Id,
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt,
    string ManagerMembershipId,
    ServiceDateStatus Status,
    long Version,
    IReadOnlyCollection<HelpNeedResponse> HelpNeeds);

public sealed record HelpNeedResponse(
    string Id,
    HelpCategory Category,
    string Instructions,
    int? Availability,
    HelpNeedStatus Status,
    long Version);
