using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;

public static class SignupEndpointSupport
{
    private static readonly HashSet<string> AllowedSubmissionProperties =
        new(StringComparer.Ordinal)
        {
            "kind",
            "label",
            "memberParticipantIds",
            "unnamedParticipantCount",
        };

    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request)
    {
        string? value = request.Headers[ApiDefaults.IdempotencyHeaderName].FirstOrDefault();
        return IdempotencyKey.TryParse(value, out IdempotencyKey key)
            ? Result.Success(key)
            : Result.Failure<IdempotencyKey>(
                DomainError.Validation(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    "A valid Idempotency-Key header is required."));
    }

    public static async ValueTask<Result<ParsedSubmitSignupRequest>> ParseSubmissionAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                request.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    MaxDepth = 16,
                },
                cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidSubmission("The signup request body must be a JSON object.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!AllowedSubmissionProperties.Contains(property.Name)
                    || !seen.Add(property.Name))
                {
                    return InvalidSubmission(
                        "The signup request contains an unsupported or duplicate field.");
                }
            }

            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("kind", out JsonElement kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse(
                    kindElement.GetString(),
                    ignoreCase: true,
                    out SignupKind kind)
                || !Enum.IsDefined(kind))
            {
                return InvalidSubmission("A valid signup kind is required.");
            }

            string? label = null;
            if (root.TryGetProperty("label", out JsonElement labelElement))
            {
                if (labelElement.ValueKind == JsonValueKind.String)
                {
                    label = labelElement.GetString();
                }
                else if (labelElement.ValueKind != JsonValueKind.Null)
                {
                    return InvalidSubmission("The signup label must be a string or null.");
                }
            }

            if (!root.TryGetProperty(
                    "memberParticipantIds",
                    out JsonElement participantIdsElement)
                || participantIdsElement.ValueKind != JsonValueKind.Array)
            {
                return InvalidSubmission("The member participant ID array is required.");
            }

            List<MembershipId> participantIds = [];
            foreach (JsonElement participantIdElement in participantIdsElement.EnumerateArray())
            {
                if (participantIdElement.ValueKind != JsonValueKind.String
                    || !MembershipId.TryParse(
                        participantIdElement.GetString(),
                        out MembershipId participantId))
                {
                    return InvalidSubmission(
                        "Every member participant ID must be a valid membership ID.");
                }

                participantIds.Add(participantId);
            }

            if (!root.TryGetProperty(
                    "unnamedParticipantCount",
                    out JsonElement unnamedCountElement)
                || unnamedCountElement.ValueKind != JsonValueKind.Number
                || !unnamedCountElement.TryGetInt32(out int unnamedParticipantCount))
            {
                return InvalidSubmission(
                    "An integer unnamed participant count is required.");
            }

            return Result.Success(
                new ParsedSubmitSignupRequest(
                    kind,
                    label,
                    participantIds,
                    unnamedParticipantCount));
        }
        catch (JsonException)
        {
            return InvalidSubmission("The signup request body is malformed JSON.");
        }
    }

    public static void WriteEtag(HttpResponse response, long signupVersion) =>
        response.Headers.ETag = $"\"{signupVersion}\"";

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

    public static SignupResponse ToResponse(SignupSummary signup) =>
        new(
            signup.Id.ToString(),
            signup.ServiceDateId.ToString(),
            signup.HelpNeedId.ToString(),
            signup.Category,
            new SignupParticipantResponse(
                signup.PrimaryContact.MembershipId.ToString(),
                signup.PrimaryContact.DisplayName),
            signup.Kind,
            signup.Label,
            signup.MemberParticipants
                .Select(participant => new SignupParticipantResponse(
                    participant.MembershipId.ToString(),
                    participant.DisplayName))
                .ToArray(),
            signup.UnnamedParticipantCount,
            signup.TotalParticipantCount,
            signup.Status,
            signup.SubmittedAt,
            signup.LastTransitionAt,
            signup.WaitlistOrder,
            signup.Version,
            signup.SignupVersion);

    private static Result<ParsedSubmitSignupRequest> InvalidSubmission(string message) =>
        Result.Failure<ParsedSubmitSignupRequest>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

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

public sealed record SubmitSignupRequest(
    SignupKind Kind,
    string? Label,
    IReadOnlyCollection<string> MemberParticipantIds,
    int UnnamedParticipantCount);

public sealed record ParsedSubmitSignupRequest(
    SignupKind Kind,
    string? Label,
    IReadOnlyCollection<MembershipId> MemberParticipantIds,
    int UnnamedParticipantCount);

public sealed record SignupParticipantResponse(
    string MembershipId,
    string DisplayName);

public sealed record SignupResponse(
    string Id,
    string ServiceDateId,
    string HelpNeedId,
    HelpCategory Category,
    SignupParticipantResponse PrimaryContact,
    SignupKind Kind,
    string? Label,
    IReadOnlyCollection<SignupParticipantResponse> MemberParticipants,
    int UnnamedParticipantCount,
    int TotalParticipantCount,
    SignupStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? LastTransitionAt,
    long? WaitlistOrder,
    long Version,
    long SignupVersion);
