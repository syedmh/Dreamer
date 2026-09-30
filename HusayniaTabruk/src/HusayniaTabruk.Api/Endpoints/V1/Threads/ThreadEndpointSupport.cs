using System.Globalization;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Dates;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Api.Endpoints.V1.Threads;

public static class ThreadEndpointSupport
{
    private const string InvalidThreadRequest = "invalid_thread_request";

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
        if (trimmed.Equals("*", StringComparison.Ordinal)
            || trimmed.Contains(',', StringComparison.Ordinal)
            || trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Length < 2
            || !trimmed.StartsWith('"')
            || !trimmed.EndsWith('"'))
        {
            return Invalid<long>("A valid If-Match header is required.");
        }

        string inner = trimmed[1..^1];
        return inner.Length > 0
               && long.TryParse(
                   inner,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out long parsed)
               && parsed >= 0
            ? Result.Success(parsed)
            : Invalid<long>("A valid If-Match header is required.");
    }

    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? value = request.Headers[ApiDefaults.IdempotencyHeaderName].FirstOrDefault();
        return IdempotencyKey.TryParse(value, out IdempotencyKey key)
            ? Result.Success(key)
            : Invalid<IdempotencyKey>("A valid Idempotency-Key header is required.");
    }

    public static Result<StepUpToken> ParseStepUpToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Microsoft.Extensions.Primitives.StringValues values =
            request.Headers[AuthHeaders.StepUpToken];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            return Result.Failure<StepUpToken>(
                DomainError.Unauthorized(
                    AuthenticationErrorCodes.StepUpInvalid,
                    "The step-up token is invalid or has expired."));
        }

        return Result.Success(new StepUpToken(values[0]!.Trim()));
    }

    public static async ValueTask<Result<ParsedPostThreadMessageRequest>> ParsePostAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            ["body"],
            "The thread message request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<ParsedPostThreadMessageRequest>(body.Error);
        }

        return TryGetRequiredString(body.Value, "body", out string? messageBody)
            ? Result.Success(new ParsedPostThreadMessageRequest(messageBody!))
            : Invalid<ParsedPostThreadMessageRequest>("A thread message body is required.");
    }

    public static async ValueTask<Result<ParsedReportThreadMessageRequest>> ParseReportAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            ["reason", "comment"],
            "The thread report request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<ParsedReportThreadMessageRequest>(body.Error);
        }

        if (!TryGetEnum(body.Value, "reason", out MessageReportReason reason))
        {
            return Invalid<ParsedReportThreadMessageRequest>(
                "A valid thread report reason is required.");
        }

        Result<string?> comment = GetOptionalString(body.Value, "comment");
        return comment.IsSuccess
            ? Result.Success(new ParsedReportThreadMessageRequest(reason, comment.Value))
            : Result.Failure<ParsedReportThreadMessageRequest>(comment.Error);
    }

    public static async ValueTask<Result<string>> ParseReasonAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            ["reason"],
            "The thread moderation request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<string>(body.Error);
        }

        return TryGetRequiredString(body.Value, "reason", out string? reason)
            ? Result.Success(reason!)
            : Invalid<string>("A moderation reason is required.");
    }

    public static async ValueTask<Result<ParsedPrivilegedThreadReadRequest>> ParsePrivilegedReadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            ["serviceDateId", "reason", "purpose", "caseId", "cursor", "pageSize"],
            "The privileged thread read request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<ParsedPrivilegedThreadReadRequest>(body.Error);
        }

        JsonElement root = body.Value;
        if (!TryGetRequiredString(root, "serviceDateId", out string? serviceDateIdValue)
            || !ServiceDateId.TryParse(serviceDateIdValue, out ServiceDateId serviceDateId))
        {
            return Invalid<ParsedPrivilegedThreadReadRequest>(
                "A valid service date ID is required.");
        }

        if (!TryGetRequiredString(root, "reason", out string? reason))
        {
            return Invalid<ParsedPrivilegedThreadReadRequest>(
                "A privileged access reason is required.");
        }

        if (!TryGetEnum(root, "purpose", out PrivilegedAccessPurpose purpose))
        {
            return Invalid<ParsedPrivilegedThreadReadRequest>(
                "A valid privileged access purpose is required.");
        }

        if (!TryGetRequiredString(root, "caseId", out string? caseId))
        {
            return Invalid<ParsedPrivilegedThreadReadRequest>(
                "A privileged access case ID is required.");
        }

        Result<string?> cursor = GetOptionalString(root, "cursor");
        if (cursor.IsFailure)
        {
            return Result.Failure<ParsedPrivilegedThreadReadRequest>(cursor.Error);
        }

        int? pageSize = null;
        if (root.TryGetProperty("pageSize", out JsonElement pageSizeElement))
        {
            if (pageSizeElement.ValueKind != JsonValueKind.Number
                || !pageSizeElement.TryGetInt32(out int parsedPageSize))
            {
                return Invalid<ParsedPrivilegedThreadReadRequest>(
                    "The page size must be an integer.");
            }

            pageSize = parsedPageSize;
        }

        return Result.Success(
            new ParsedPrivilegedThreadReadRequest(
                serviceDateId,
                reason!,
                purpose,
                caseId!,
                cursor.Value,
                pageSize));
    }

    public static IResult FromResult<T>(Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : Problem(result.Error);

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        DateEndpointSupport.Problem(error, fieldErrors);

    public static void WriteEtag(HttpResponse response, long version) =>
        DateEndpointSupport.WriteEtag(response, version);

    private static async ValueTask<Result<JsonElement>> ParseObjectAsync(
        HttpRequest request,
        string[] allowedProperties,
        string invalidMessage,
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
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid<JsonElement>(invalidMessage);
            }

            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!allowedProperties.Contains(property.Name) || !seen.Add(property.Name))
                {
                    return Invalid<JsonElement>(
                        "The thread request contains an unsupported or duplicate field.");
                }
            }

            return Result.Success(document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return Invalid<JsonElement>("The thread request body is malformed JSON.");
        }
    }

    private static bool TryGetRequiredString(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        if (root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static Result<string?> GetOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return Result.Success<string?>(null);
        }

        return property.ValueKind == JsonValueKind.String
            ? Result.Success(property.GetString())
            : Invalid<string?>($"The {propertyName} value must be a string or null.");
    }

    private static bool TryGetEnum<TEnum>(
        JsonElement root,
        string propertyName,
        out TEnum value)
        where TEnum : struct, Enum
    {
        if (root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && Enum.TryParse(property.GetString(), ignoreCase: true, out value)
            && Enum.IsDefined(value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(DomainError.Validation(InvalidThreadRequest, message));
}

public sealed record PostThreadMessageRequest(string Body);

public sealed record ReportThreadMessageRequest(
    MessageReportReason Reason,
    string? Comment);

public sealed record ReasonRequest(string Reason);

public sealed record PrivilegedThreadReadRequest(
    Guid ServiceDateId,
    string Reason,
    PrivilegedAccessPurpose Purpose,
    string CaseId,
    string? Cursor,
    int? PageSize);

public sealed record ParsedPostThreadMessageRequest(string Body);

public sealed record ParsedReportThreadMessageRequest(
    MessageReportReason Reason,
    string? Comment);

public sealed record ParsedPrivilegedThreadReadRequest(
    ServiceDateId ServiceDateId,
    string Reason,
    PrivilegedAccessPurpose Purpose,
    string CaseId,
    string? Cursor,
    int? PageSize);
