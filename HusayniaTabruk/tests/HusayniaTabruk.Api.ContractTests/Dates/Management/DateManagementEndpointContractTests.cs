using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Endpoints.V1.Dates.Management;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using Microsoft.AspNetCore.Http;

namespace HusayniaTabruk.Api.ContractTests.Dates.Management;

public sealed class DateManagementEndpointContractTests
{
    private static readonly (string Path, string Method, string OperationId, string Schema, bool RequiresIdempotency)[] FrozenOperations =
    [
        ("/dates/{dateId}", "patch", "EditServiceDate", "PatchServiceDateRequest", false),
        ("/dates/{dateId}/close", "post", "CloseServiceDate", "CloseServiceDateRequest", true),
        ("/dates/{dateId}/cancel", "post", "CancelServiceDate", "CancelServiceDateRequest", true),
        ("/needs/{needId}", "patch", "EditHelpNeed", "PatchHelpNeedRequest", false),
    ];

    [Fact]
    public async Task ManagementRoutesExposeFrozenOperationsSchemasAndHeaders()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;

        foreach ((string path, string method, string operationId, string schema, bool requiresIdempotency)
                 in FrozenOperations)
        {
            JsonElement operation = root.GetProperty("paths")
                .GetProperty(path)
                .GetProperty(method);

            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal(
                $"#/components/schemas/{schema}",
                operation.GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString());

            string[] parameterReferences = operation.GetProperty("parameters")
                .EnumerateArray()
                .Where(parameter => parameter.TryGetProperty("$ref", out _))
                .Select(parameter => parameter.GetProperty("$ref").GetString()!)
                .ToArray();
            Assert.Contains("#/components/parameters/ifMatch", parameterReferences);
            if (requiresIdempotency)
            {
                Assert.Contains("#/components/parameters/idempotencyKey", parameterReferences);
            }

            JsonElement success = operation.GetProperty("responses").GetProperty("200");
            Assert.True(success.GetProperty("headers").GetProperty("ETag").GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task HelpNeedPatchSchemaRequiresNullableCapacity()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PatchHelpNeedRequest");

        string[] required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        Assert.Contains("instructions", required);
        Assert.Contains("capacity", required);
        Assert.Contains("status", required);

        JsonElement capacity = schema.GetProperty("properties").GetProperty("capacity");
        Assert.Contains(
            capacity.GetProperty("type").EnumerateArray().Select(item => item.GetString()),
            type => type == "null");
    }

    [Fact]
    public async Task HelpNeedPatchRejectsMissingCapacityAndNumericStatusToken()
    {
        Result<ParsedNeedPatch> missingCapacity = await ParseNeedPatchAsync(
            """
            {
              "instructions": "Need instructions",
              "status": "open"
            }
            """);
        Result<ParsedNeedPatch> numericStatus = await ParseNeedPatchAsync(
            """
            {
              "instructions": "Need instructions",
              "capacity": 4,
              "status": "0"
            }
            """);

        Assert.True(missingCapacity.IsFailure);
        Assert.Equal(DateApplicationErrorCodes.InvalidDateRequest, missingCapacity.Error.Code);
        Assert.True(numericStatus.IsFailure);
        Assert.Equal(DateApplicationErrorCodes.InvalidDateRequest, numericStatus.Error.Code);
    }

    [Fact]
    public void StrongIfMatchAcceptsOnlyOneQuotedNonnegativeInt64()
    {
        foreach ((string Header, long Expected) item in new[]
                 {
                     ("\"0\"", 0L),
                     ($"\"{long.MaxValue}\"", long.MaxValue),
                 })
        {
            Result<long> parsed = ParseIfMatch(item.Header);
            Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : null);
            Assert.Equal(item.Expected, parsed.Value);
        }
    }

    [Fact]
    public void StrongIfMatchRejectsWeakWildcardListMalformedAndUnquotedValues()
    {
        foreach (string? header in new string?[]
                 {
                     null,
                     "",
                     " ",
                     "*",
                     "\"\"",
                     "0",
                     "1",
                     "W/\"0\"",
                     "\"0\",\"1\"",
                     "\"0\", \"1\"",
                     "0,1",
                     "\"1",
                     "1\"",
                     "\"-1\"",
                     "\"abc\"",
                     "\"9223372036854775808\"",
                 })
        {
            Result<long> parsed = ParseIfMatch(header);
            Assert.True(parsed.IsFailure);
            Assert.Equal(ErrorType.Validation, parsed.Error.Type);
            Assert.Equal("invalid_date_request", parsed.Error.Code);
        }
    }

    [Fact]
    public async Task EditDateAndNeedUnknownRoutesAreNotYetSatisfiedByExistingApi()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage date = await api.Client.PatchAsync(
            $"{ApiDefaults.BasePath}/dates/0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52a",
            JsonContent.Create(
                new
                {
                    title = "Date",
                    instructions = "Instructions",
                    startsAt = DateTimeOffset.UnixEpoch,
                    endsAt = DateTimeOffset.UnixEpoch.AddHours(1),
                    cancellationDeadlineAt = DateTimeOffset.UnixEpoch,
                }));
        using HttpResponseMessage need = await api.Client.PatchAsync(
            $"{ApiDefaults.BasePath}/needs/0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52b",
            JsonContent.Create(
                new
                {
                    instructions = "Need instructions",
                    capacity = 4,
                    status = "open",
                }));

        Assert.NotEqual(HttpStatusCode.NotFound, date.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, need.StatusCode);
    }

    [Fact]
    public async Task EditDateRejectsNonUtcTimestampsWithValidationProblemDetails()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using HttpRequestMessage request = new(
            HttpMethod.Patch,
            $"{ApiDefaults.BasePath}/dates/0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52a")
        {
            Content = JsonContent.Create(
                new
                {
                    title = "Date",
                    instructions = "Instructions",
                    startsAt = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.FromHours(-7)),
                    endsAt = new DateTimeOffset(2026, 8, 19, 14, 0, 0, TimeSpan.FromHours(-7)),
                    cancellationDeadlineAt = new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.FromHours(-7)),
                }),
        };
        request.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, "\"0\"");

        using HttpResponseMessage response = await api.Client.SendAsync(request);
        using JsonDocument problem =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(DateApplicationErrorCodes.InvalidDateRequest, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Bad request", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The service date timestamps must use the UTC offset (Z).",
            problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EditDateRejectsZoneLessTimestampsWithValidationProblemDetails()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using HttpRequestMessage request = new(
            HttpMethod.Patch,
            $"{ApiDefaults.BasePath}/dates/0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52a")
        {
            Content = new StringContent(
                """
                {
                  "title": "Date",
                  "instructions": "Instructions",
                  "startsAt": "2026-08-19T10:00:00",
                  "endsAt": "2026-08-19T14:00:00",
                  "cancellationDeadlineAt": "2026-08-19T08:00:00"
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, "\"0\"");

        using HttpResponseMessage response = await api.Client.SendAsync(request);
        using JsonDocument problem =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(DateApplicationErrorCodes.InvalidDateRequest, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("Bad request", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            "The service date timestamps must use the UTC offset (Z).",
            problem.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EditDateParserRejectsZoneLessTimestampsWhenLocalTimeZoneIsUtc()
    {
        using LocalTimeZoneOverride _ = LocalTimeZoneOverride.Utc();

        Result<ParsedDatePatch> parsed = await ParseDatePatchAsync(
            """
            {
              "title": "Date",
              "instructions": "Instructions",
              "startsAt": "2026-08-19T10:00:00",
              "endsAt": "2026-08-19T14:00:00",
              "cancellationDeadlineAt": "2026-08-19T08:00:00"
            }
            """);

        Assert.True(parsed.IsFailure);
        Assert.Equal(ErrorType.Validation, parsed.Error.Type);
        Assert.Equal(DateApplicationErrorCodes.InvalidDateRequest, parsed.Error.Code);
        Assert.Equal(
            "The service date timestamps must use the UTC offset (Z).",
            parsed.Error.Message);
    }

    private static Result<long> ParseIfMatch(string? header)
    {
        DefaultHttpContext context = new();
        if (header is not null)
        {
            context.Request.Headers.TryAdd(ApiDefaults.IfMatchHeaderName, header);
        }

        Type? support = typeof(ApiDefaults).Assembly.GetType(
            "HusayniaTabruk.Api.Endpoints.V1.Dates.Management.DateManagementEndpointSupport");
        Assert.NotNull(support);
        return (Result<long>)support!.GetMethod("ParseIfMatch")!.Invoke(null, [context.Request])!;
    }

    private static async Task<Result<ParsedDatePatch>> ParseDatePatchAsync(string body)
    {
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Type? support = typeof(ApiDefaults).Assembly.GetType(
            "HusayniaTabruk.Api.Endpoints.V1.Dates.Management.DateManagementEndpointSupport");
        Assert.NotNull(support);
        MethodInfo method = support!.GetMethod("ParseDatePatchAsync")
            ?? throw new InvalidOperationException("Missing ParseDatePatchAsync.");
        ValueTask<Result<ParsedDatePatch>> pending =
            (ValueTask<Result<ParsedDatePatch>>)method.Invoke(
                null,
                [context.Request, CancellationToken.None])!;
        return await pending;
    }

    private static async Task<Result<ParsedNeedPatch>> ParseNeedPatchAsync(string body)
    {
        DefaultHttpContext context = new();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Type? support = typeof(ApiDefaults).Assembly.GetType(
            "HusayniaTabruk.Api.Endpoints.V1.Dates.Management.DateManagementEndpointSupport");
        Assert.NotNull(support);
        MethodInfo method = support!.GetMethod("ParseNeedPatchAsync")
            ?? throw new InvalidOperationException("Missing ParseNeedPatchAsync.");
        ValueTask<Result<ParsedNeedPatch>> pending =
            (ValueTask<Result<ParsedNeedPatch>>)method.Invoke(
                null,
                [context.Request, CancellationToken.None])!;
        return await pending;
    }

    private sealed class LocalTimeZoneOverride : IDisposable
    {
        private static readonly object Gate = new();
        private readonly object cachedData;
        private readonly object? originalLocalTimeZone;
        private readonly object? originalOneYearLocalFromUtc;
        private readonly FieldInfo localTimeZoneField;
        private readonly FieldInfo? oneYearLocalFromUtcField;
        private bool disposed;

        private LocalTimeZoneOverride(TimeZoneInfo value)
        {
            Monitor.Enter(Gate);
            BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            Type cachedType = typeof(TimeZoneInfo).GetNestedType("CachedData", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing TimeZoneInfo.CachedData.");
            cachedData = typeof(TimeZoneInfo)
                             .GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
                             ?.GetValue(null)
                         ?? throw new InvalidOperationException("Missing TimeZoneInfo cache.");
            localTimeZoneField = cachedType.GetField("_localTimeZone", flags)
                ?? throw new InvalidOperationException("Missing TimeZoneInfo local cache field.");
            oneYearLocalFromUtcField = cachedType.GetField("_oneYearLocalFromUtc", flags);
            originalLocalTimeZone = localTimeZoneField.GetValue(cachedData);
            originalOneYearLocalFromUtc = oneYearLocalFromUtcField?.GetValue(cachedData);
            localTimeZoneField.SetValue(cachedData, value);
            oneYearLocalFromUtcField?.SetValue(cachedData, null);
        }

        public static LocalTimeZoneOverride Utc() => new(TimeZoneInfo.Utc);

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            localTimeZoneField.SetValue(cachedData, originalLocalTimeZone);
            oneYearLocalFromUtcField?.SetValue(cachedData, originalOneYearLocalFromUtc);
            disposed = true;
            Monitor.Exit(Gate);
        }
    }
}
