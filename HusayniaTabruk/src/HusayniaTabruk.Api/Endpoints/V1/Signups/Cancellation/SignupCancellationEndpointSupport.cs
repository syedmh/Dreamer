using System.Text.Json;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Decisions;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Cancellation;

public static class SignupCancellationEndpointSupport
{
    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request) =>
        SignupDecisionEndpointSupport.ParseIdempotencyKey(request);

    public static Result<long> ParseIfMatch(HttpRequest request) =>
        SignupDecisionEndpointSupport.ParseIfMatch(request);

    public static async ValueTask<Result> ParseEmptyObjectAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> parsed = await ParseObjectAsync(request, cancellationToken);
        return parsed.IsFailure
            ? Result.Failure(parsed.Error)
            : parsed.Value.EnumerateObject().Any()
                ? Invalid("The withdrawal request body must be an empty JSON object.")
                : Result.Success();
    }

    public static async ValueTask<Result<ParsedCancellationOverride>> ParseOverrideAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> parsed = await ParseObjectAsync(request, cancellationToken);
        if (parsed.IsFailure)
        {
            return Result.Failure<ParsedCancellationOverride>(parsed.Error);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in parsed.Value.EnumerateObject())
        {
            if (property.Name is not ("targetState" or "reason")
                || !seen.Add(property.Name))
            {
                return Invalid<ParsedCancellationOverride>(
                    "The cancellation override request contains an unsupported or duplicate field.");
            }
        }

        if (!parsed.Value.TryGetProperty("targetState", out JsonElement targetElement)
            || targetElement.ValueKind != JsonValueKind.String
            || !string.Equals(
                targetElement.GetString(),
                "cancelled",
                StringComparison.Ordinal))
        {
            return Invalid<ParsedCancellationOverride>(
                "The cancellation override target state must be cancelled.");
        }

        if (!parsed.Value.TryGetProperty("reason", out JsonElement reasonElement)
            || reasonElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(reasonElement.GetString()))
        {
            return Invalid<ParsedCancellationOverride>(
                "A nonblank cancellation override reason is required.");
        }

        return Result.Success(
            new ParsedCancellationOverride(
                SignupStatus.Cancelled,
                reasonElement.GetString()!));
    }

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        SignupDecisionEndpointSupport.Problem(error, fieldErrors);

    public static SignupResponse ToResponse(SignupSummary signup) =>
        SignupDecisionEndpointSupport.ToResponse(signup);

    public static void WriteEtag(HttpResponse response, long signupVersion) =>
        SignupDecisionEndpointSupport.WriteEtag(response, signupVersion);

    private static async ValueTask<Result<JsonElement>> ParseObjectAsync(
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
                    MaxDepth = 8,
                },
                cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Result.Success(document.RootElement.Clone())
                : Invalid<JsonElement>(
                    "The signup cancellation request body must be a JSON object.");
        }
        catch (JsonException)
        {
            return Invalid<JsonElement>(
                "The signup cancellation request body is malformed JSON.");
        }
    }

    private static Result Invalid(string message) =>
        Result.Failure(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));
}

public sealed record ParsedCancellationOverride(
    SignupStatus TargetStatus,
    string Reason);

public sealed record WithdrawSignupRequest;

public enum CancellationOverrideTargetState
{
    Cancelled = 1,
}

public sealed record OverrideSignupRequest(
    CancellationOverrideTargetState TargetState,
    string Reason);
