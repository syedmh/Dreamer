using System.Globalization;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Decisions;

public static class SignupDecisionEndpointSupport
{
    private static readonly HashSet<string> AllowedProperties =
        new(StringComparer.Ordinal)
        {
            "reason",
        };

    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? value = request.Headers[ApiDefaults.IdempotencyHeaderName].FirstOrDefault();
        return IdempotencyKey.TryParse(value, out IdempotencyKey key)
            ? Result.Success(key)
            : Invalid<IdempotencyKey>("A valid Idempotency-Key header is required.");
    }

    public static Result<long> ParseIfMatch(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Microsoft.Extensions.Primitives.StringValues values =
            request.Headers[ApiDefaults.IfMatchHeaderName];
        if (values.Count != 1)
        {
            return Invalid<long>("A valid If-Match header is required.");
        }

        string? value = values[0];
        if (string.IsNullOrWhiteSpace(value))
        {
            return Invalid<long>("A valid If-Match header is required.");
        }

        string trimmed = value.Trim();
        if (trimmed.Contains(',', StringComparison.Ordinal)
            || trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid<long>("A valid If-Match header is required.");
        }

        if (trimmed.StartsWith('"') || trimmed.EndsWith('"'))
        {
            if (trimmed.Length < 2
                || !trimmed.StartsWith('"')
                || !trimmed.EndsWith('"'))
            {
                return Invalid<long>("A valid If-Match header is required.");
            }

            trimmed = trimmed[1..^1];
        }

        return long.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long version)
            && version >= 0
            ? Result.Success(version)
            : Invalid<long>("A valid If-Match header is required.");
    }

    public static async ValueTask<Result<string?>> ParseReasonAsync(
        HttpRequest request,
        bool required,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid<string?>(
                    "The signup decision request body must be a JSON object.");
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!AllowedProperties.Contains(property.Name)
                    || !seen.Add(property.Name))
                {
                    return Invalid<string?>(
                        "The signup decision request contains an unsupported or duplicate field.");
                }
            }

            string? reason = null;
            if (document.RootElement.TryGetProperty(
                    "reason",
                    out JsonElement reasonElement))
            {
                if (reasonElement.ValueKind == JsonValueKind.String)
                {
                    reason = reasonElement.GetString();
                }
                else if (reasonElement.ValueKind != JsonValueKind.Null)
                {
                    return Invalid<string?>(
                        "The signup decision reason must be a string or null.");
                }
            }

            return required && string.IsNullOrWhiteSpace(reason)
                ? Invalid<string?>("A nonblank decline reason is required.")
                : Result.Success(reason);
        }
        catch (JsonException)
        {
            return Invalid<string?>(
                "The signup decision request body is malformed JSON.");
        }
    }

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        SignupEndpointSupport.Problem(error, fieldErrors);

    public static SignupResponse ToResponse(SignupSummary signup) =>
        SignupEndpointSupport.ToResponse(signup);

    public static void WriteEtag(HttpResponse response, long signupVersion) =>
        SignupEndpointSupport.WriteEtag(response, signupVersion);

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));
}

public sealed record ApproveSignupRequest(string? Reason);

public sealed record DeclineSignupRequest(string Reason);

public sealed record WaitlistSignupRequest(string? Reason);
