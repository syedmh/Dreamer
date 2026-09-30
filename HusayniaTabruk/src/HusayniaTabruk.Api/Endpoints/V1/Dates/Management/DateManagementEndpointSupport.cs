using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Dates;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Application.Dates.Management;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Dates.Management;

public static class DateManagementEndpointSupport
{
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

    public static Result<IdempotencyKey> ParseIdempotencyKey(HttpRequest request) =>
        DateEndpointSupport.ParseIdempotencyKey(request);

    public static async ValueTask<Result<ParsedDatePatch>> ParseDatePatchAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            "The service date request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<ParsedDatePatch>(body.Error);
        }

        JsonElement root = body.Value;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not ("title" or "instructions" or "startsAt" or "endsAt" or "cancellationDeadlineAt")
                || !seen.Add(property.Name))
            {
                return Invalid<ParsedDatePatch>(
                    "The service date request contains an unsupported or duplicate field.");
            }
        }

        if (!TryGetRequiredString(root, "title", out string? title)
            || !TryGetRequiredString(root, "instructions", out string? instructions)
            || !TryGetRequiredDateTimeOffset(root, "startsAt", out string? startsAtLexeme, out DateTimeOffset startsAt)
            || !TryGetRequiredDateTimeOffset(root, "endsAt", out string? endsAtLexeme, out DateTimeOffset endsAt)
            || !TryGetRequiredDateTimeOffset(root, "cancellationDeadlineAt", out string? cancellationDeadlineAtLexeme, out DateTimeOffset cancellationDeadlineAt))
        {
            return Invalid<ParsedDatePatch>(
                "The service date request body must include title, instructions, startsAt, endsAt, and cancellationDeadlineAt.");
        }

        if (!HasExplicitZoneDesignator(startsAtLexeme)
            || !HasExplicitZoneDesignator(endsAtLexeme)
            || !HasExplicitZoneDesignator(cancellationDeadlineAtLexeme)
            || !IsUtc(startsAt)
            || !IsUtc(endsAt)
            || !IsUtc(cancellationDeadlineAt))
        {
            return Invalid<ParsedDatePatch>(
                "The service date timestamps must use the UTC offset (Z).");
        }

        return Result.Success(
            new ParsedDatePatch(
                title!,
                instructions!,
                startsAt,
                endsAt,
                cancellationDeadlineAt));
    }

    public static async ValueTask<Result<ParsedNeedPatch>> ParseNeedPatchAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            "The help need request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<ParsedNeedPatch>(body.Error);
        }

        JsonElement root = body.Value;
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not ("instructions" or "capacity" or "status")
                || !seen.Add(property.Name))
            {
                return Invalid<ParsedNeedPatch>(
                    "The help need request contains an unsupported or duplicate field.");
            }
        }

        if (!TryGetRequiredString(root, "instructions", out string? instructions)
            || !TryGetCapacity(root, out int? capacity)
            || !TryGetRequiredStatus(root, out HelpNeedStatus status))
        {
            return Invalid<ParsedNeedPatch>(
                "The help need request body must include instructions, capacity, and status.");
        }

        return Result.Success(new ParsedNeedPatch(instructions!, capacity, status));
    }

    public static async ValueTask<Result<string?>> ParseReasonAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        Result<JsonElement> body = await ParseObjectAsync(
            request,
            "The date-management request body must be a JSON object.",
            cancellationToken);
        if (body.IsFailure)
        {
            return Result.Failure<string?>(body.Error);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in body.Value.EnumerateObject())
        {
            if (property.Name is not "reason" || !seen.Add(property.Name))
            {
                return Invalid<string?>(
                    "The date-management request contains an unsupported or duplicate field.");
            }
        }

        if (!body.Value.TryGetProperty("reason", out JsonElement reason))
        {
            return Result.Success<string?>(null);
        }

        return reason.ValueKind switch
        {
            JsonValueKind.Null => Result.Success<string?>(null),
            JsonValueKind.String => Result.Success(reason.GetString()),
            _ => Invalid<string?>("The date-management reason must be a string or null."),
        };
    }

    public static IResult Problem(
        DomainError error,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        DateEndpointSupport.Problem(error, fieldErrors);

    public static void WriteEtag(HttpResponse response, long version) =>
        DateEndpointSupport.WriteEtag(response, version);

    public static ServiceDateResponse ToResponse(ServiceDateSummary date) =>
        DateEndpointSupport.ToResponse(date);

    public static HelpNeedResponse ToResponse(HelpNeedSummary need) =>
        DateEndpointSupport.ToResponse(need);

    private static async ValueTask<Result<JsonElement>> ParseObjectAsync(
        HttpRequest request,
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
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Result.Success(document.RootElement.Clone())
                : Invalid<JsonElement>(invalidMessage);
        }
        catch (JsonException)
        {
            return Invalid<JsonElement>(invalidMessage.Replace("must be", "is malformed and must be", StringComparison.Ordinal));
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
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetRequiredDateTimeOffset(
        JsonElement root,
        string propertyName,
        out string? lexeme,
        out DateTimeOffset value)
    {
        if (root.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTimeOffset(out value))
        {
            lexeme = property.GetString();
            return !string.IsNullOrWhiteSpace(lexeme);
        }

        lexeme = null;
        value = default;
        return false;
    }

    private static bool TryGetCapacity(JsonElement root, out int? value)
    {
        if (!root.TryGetProperty("capacity", out JsonElement property))
        {
            value = null;
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out int capacity))
        {
            value = capacity;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryGetRequiredStatus(
        JsonElement root,
        out HelpNeedStatus status)
    {
        if (root.TryGetProperty("status", out JsonElement property)
            && property.ValueKind == JsonValueKind.String)
        {
            string? value = property.GetString();
            if (string.Equals(value, "open", StringComparison.OrdinalIgnoreCase))
            {
                status = HelpNeedStatus.Open;
                return true;
            }

            if (string.Equals(value, "closed", StringComparison.OrdinalIgnoreCase))
            {
                status = HelpNeedStatus.Closed;
                return true;
            }
        }

        status = default;
        return false;
    }

    private static bool HasExplicitZoneDesignator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int timeSeparatorIndex = value.IndexOf('T');
        if (timeSeparatorIndex < 0)
        {
            timeSeparatorIndex = value.IndexOf('t');
        }

        if (timeSeparatorIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> timePortion = value.AsSpan(timeSeparatorIndex + 1);
        return timePortion.IndexOf('Z') >= 0
               || timePortion.IndexOf('z') >= 0
               || timePortion.IndexOf('+') >= 0
               || timePortion.IndexOf('-') >= 0;
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;

    private static Result<T> Invalid<T>(string message) =>
        Result.Failure<T>(
            DomainError.Validation(
                DateApplicationErrorCodes.InvalidDateRequest,
                message));
}

public sealed record PatchServiceDateRequest(
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt);

public sealed record CloseServiceDateRequest(string? Reason);

public sealed record CancelServiceDateRequest(string? Reason);

public sealed record PatchHelpNeedRequest
{
    public required string Instructions { get; init; }

    [JsonRequired]
    public int? Capacity { get; init; }

    public required HelpNeedStatus Status { get; init; }
}

public sealed record ParsedDatePatch(
    string Title,
    string Instructions,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationDeadlineAt);

public sealed record ParsedNeedPatch(
    string Instructions,
    int? Capacity,
    HelpNeedStatus Status);
