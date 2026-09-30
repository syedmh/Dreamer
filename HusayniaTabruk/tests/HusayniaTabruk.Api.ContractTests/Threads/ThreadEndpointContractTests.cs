using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HusayniaTabruk.Api.ContractTests.Threads;

public sealed class ThreadEndpointContractTests
{
    private static readonly Guid ValidDateId = Guid.Parse("0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52a");
    private static readonly Guid ValidMessageId = Guid.Parse("0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52b");

    [Fact]
    public async Task ThreadRoutesExposeFrozenOperationsSchemasHeadersAndAdminRole()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement paths = document.RootElement.GetProperty("paths");

        AssertOperation(
            paths,
            "/dates/{dateId}/thread/messages",
            "get",
            "ListThreadMessages",
            responseSchema: "#/components/schemas/ThreadMessagePageResponse");
        AssertResponseHeader(paths, "/dates/{dateId}/thread/messages", "get", "200", "ETag");
        AssertParameterReferences(
            paths,
            "/dates/{dateId}/thread/messages",
            "get",
            "#/components/parameters/cursor",
            "#/components/parameters/pageSize");

        AssertOperation(
            paths,
            "/dates/{dateId}/thread/messages",
            "post",
            "PostThreadMessage",
            requestSchema: "#/components/schemas/PostThreadMessageRequest",
            responseSchema: "#/components/schemas/ThreadMessageResponse");
        AssertParameterReferences(
            paths,
            "/dates/{dateId}/thread/messages",
            "post",
            "#/components/parameters/idempotencyKey",
            "#/components/parameters/ifMatch");
        AssertResponseHeader(paths, "/dates/{dateId}/thread/messages", "post", "201", "ETag");
        AssertResponseHeader(paths, "/dates/{dateId}/thread/messages", "post", "429", "Retry-After");

        AssertOperation(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/report",
            "post",
            "ReportThreadMessage",
            requestSchema: "#/components/schemas/ReportThreadMessageRequest");
        AssertParameterReferences(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/report",
            "post",
            "#/components/parameters/ifMatch");
        Assert.False(
            paths.GetProperty("/dates/{dateId}/thread/messages/{messageId}/report")
                .GetProperty("post")
                .GetProperty("responses")
                .GetProperty("202")
                .TryGetProperty("content", out _));
        AssertResponseHeader(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/report",
            "post",
            "202",
            "ETag");
        AssertResponseHeader(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/report",
            "post",
            "429",
            "Retry-After");

        AssertOperation(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/hide",
            "post",
            "HideThreadMessage",
            requestSchema: "#/components/schemas/ReasonRequest",
            responseSchema: "#/components/schemas/ThreadMessageResponse");
        AssertParameterReferences(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/hide",
            "post",
            "#/components/parameters/ifMatch");
        AssertResponseHeader(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/hide",
            "post",
            "200",
            "ETag");
        AssertResponseHeader(
            paths,
            "/dates/{dateId}/thread/messages/{messageId}/hide",
            "post",
            "429",
            "Retry-After");

        AssertOperation(
            paths,
            "/dates/{dateId}/thread/lock",
            "post",
            "LockThread",
            requestSchema: "#/components/schemas/ReasonRequest",
            responseSchema: "#/components/schemas/ThreadStateResponse");
        AssertParameterReferences(
            paths,
            "/dates/{dateId}/thread/lock",
            "post",
            "#/components/parameters/ifMatch");
        AssertResponseHeader(paths, "/dates/{dateId}/thread/lock", "post", "200", "ETag");
        AssertResponseHeader(paths, "/dates/{dateId}/thread/lock", "post", "429", "Retry-After");

        AssertOperation(
            paths,
            "/admin/moderation/thread-reads",
            "post",
            "ReadPrivilegedThreadMessages",
            requestSchema: "#/components/schemas/PrivilegedThreadReadRequest",
            responseSchema: "#/components/schemas/PrivilegedThreadMessagePageResponse");
        AssertParameterReferences(
            paths,
            "/admin/moderation/thread-reads",
            "post",
            "#/components/parameters/stepUpToken");
        AssertResponseHeader(paths, "/admin/moderation/thread-reads", "post", "429", "Retry-After");

        Assert.False(paths.TryGetProperty("/messages/{messageId}/report", out _));
        Assert.False(paths.TryGetProperty("/thread/lock", out _));

        RouteEndpoint privilegedRoute = Assert.Single(
            api.Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/admin/moderation/thread-reads");
        Assert.Contains(
            privilegedRoute.Metadata
                .GetOrderedMetadata<AuthorizationPolicy>()
                .SelectMany(policy => policy.Requirements)
                .OfType<RolesAuthorizationRequirement>(),
            requirement => requirement.AllowedRoles.Contains(
                "Admin",
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task ThreadSchemasDoNotExposeSensitiveContactsOrIdentifiers()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        JsonElement message = schemas.GetProperty("ThreadMessageResponse");
        AssertPropertyNames(
            message,
            "id",
            "senderDisplayName",
            "body",
            "visibility",
            "createdAt");
        AssertForbiddenProperties(message, "membershipId", "userId", "email", "phone", "hiddenAt");

        JsonElement page = schemas.GetProperty("ThreadMessagePageResponse");
        AssertPropertyNames(
            page,
            "serviceDateId",
            "status",
            "lockedAt",
            "items",
            "nextCursor");
        AssertForbiddenProperties(page, "threadId", "membershipId", "userId", "email", "phone");

        JsonElement privilegedMessage = schemas.GetProperty("PrivilegedThreadMessageResponse");
        AssertPropertyNames(
            privilegedMessage,
            "id",
            "senderDisplayName",
            "body",
            "visibility",
            "createdAt",
            "hiddenAt");
        AssertForbiddenProperties(
            privilegedMessage,
            "membershipId",
            "userId",
            "email",
            "phone");
    }

    [Fact]
    public async Task ThreadMutationRoutesExposeFrozenBodyAndRateLimits()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");

        AssertLimits(
            FindRoute(api, "/api/v1/dates/{dateId}/thread/messages", "POST"),
            ApplicationLimits.MaximumMessageRequestBytes,
            (ApiRateLimitPartitions.Account, 3, TimeSpan.FromSeconds(10)),
            (ApiRateLimitPartitions.Account, 10, TimeSpan.FromMinutes(1)),
            (ApiRateLimitPartitions.Account, 60, TimeSpan.FromHours(1)),
            (ApiRateLimitPartitions.Organization, 300, TimeSpan.FromHours(1)));
        AssertLimits(
            FindRoute(
                api,
                "/api/v1/dates/{dateId}/thread/messages/{messageId}/report",
                "POST"),
            ApplicationLimits.MaximumReportRequestBytes,
            (ApiRateLimitPartitions.Account, 5, TimeSpan.FromHours(1)),
            (ApiRateLimitPartitions.Account, 20, TimeSpan.FromDays(1)),
            (ApiRateLimitPartitions.Organization, 100, TimeSpan.FromDays(1)));
        AssertLimits(
            FindRoute(
                api,
                "/api/v1/dates/{dateId}/thread/messages/{messageId}/hide",
                "POST"),
            ApplicationLimits.MaximumAdministrativeRequestBytes,
            (
                ApiRateLimitPartitions.Account,
                ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
                TimeSpan.FromMinutes(1)),
            (
                ApiRateLimitPartitions.Organization,
                ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
                TimeSpan.FromHours(1)));
        AssertLimits(
            FindRoute(api, "/api/v1/dates/{dateId}/thread/lock", "POST"),
            ApplicationLimits.MaximumAdministrativeRequestBytes,
            (
                ApiRateLimitPartitions.Account,
                ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
                TimeSpan.FromMinutes(1)),
            (
                ApiRateLimitPartitions.Organization,
                ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
                TimeSpan.FromHours(1)));
        AssertLimits(
            FindRoute(api, "/api/v1/admin/moderation/thread-reads", "POST"),
            ApplicationLimits.MaximumAdministrativeRequestBytes,
            (
                ApiRateLimitPartitions.Account,
                ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
                TimeSpan.FromMinutes(1)),
            (
                ApiRateLimitPartitions.Organization,
                ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
                TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task ThreadMutationRoutesRejectOversizedBodiesWithPayloadTooLarge()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage postMessage = await api.Client.PostAsync(
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages",
            new ByteArrayContent(new byte[ApplicationLimits.MaximumMessageRequestBytes + 1]));
        using HttpResponseMessage reportMessage = await api.Client.PostAsync(
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages/{ValidMessageId:D}/report",
            new ByteArrayContent(new byte[ApplicationLimits.MaximumReportRequestBytes + 1]));
        using HttpResponseMessage hideMessage = await api.Client.PostAsync(
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages/{ValidMessageId:D}/hide",
            new ByteArrayContent(new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]));
        using HttpResponseMessage lockThread = await api.Client.PostAsync(
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/lock",
            new ByteArrayContent(new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]));
        using JsonDocument postProblem =
            JsonDocument.Parse(await postMessage.Content.ReadAsStringAsync());
        using JsonDocument reportProblem =
            JsonDocument.Parse(await reportMessage.Content.ReadAsStringAsync());
        using JsonDocument hideProblem =
            JsonDocument.Parse(await hideMessage.Content.ReadAsStringAsync());
        using JsonDocument lockProblem =
            JsonDocument.Parse(await lockThread.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, postMessage.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reportMessage.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hideMessage.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, lockThread.StatusCode);
        Assert.Equal(ErrorCodes.PayloadTooLarge, postProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.PayloadTooLarge, reportProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.PayloadTooLarge, hideProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.PayloadTooLarge, lockProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ThreadMutationRoutesEmitRetryAfterWhenRateLimited()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        HttpResponseMessage limitedPost = await ExhaustPostThreadMessageLimitAsync(api);
        HttpResponseMessage limitedReport = await ExhaustReportThreadMessageLimitAsync(api);
        using JsonDocument postProblem =
            JsonDocument.Parse(await limitedPost.Content.ReadAsStringAsync());
        using JsonDocument reportProblem =
            JsonDocument.Parse(await limitedReport.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedPost.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedReport.StatusCode);
        Assert.True(limitedPost.Headers.TryGetValues(ApiDefaults.RetryAfterHeaderName, out IEnumerable<string>? postRetry));
        Assert.True(reportProblem.RootElement.TryGetProperty("code", out JsonElement reportCode));
        Assert.Equal(ErrorCodes.RateLimited, postProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(ErrorCodes.RateLimited, reportCode.GetString());
        Assert.True(int.TryParse(Assert.Single(postRetry), out int postRetrySeconds));
        Assert.True(postRetrySeconds > 0);
        Assert.True(limitedReport.Headers.TryGetValues(ApiDefaults.RetryAfterHeaderName, out IEnumerable<string>? reportRetry));
        Assert.True(int.TryParse(Assert.Single(reportRetry), out int reportRetrySeconds));
        Assert.True(reportRetrySeconds > 0);

        limitedPost.Dispose();
        limitedReport.Dispose();
    }

    [Fact]
    public async Task ThreadEndpointsRequireFrozenHeadersBeforeReachingApplicationLayer()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage missingIdempotency = await SendJsonAsync(
            api.Client,
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages",
            new { body = "Message body" },
            headers: [new KeyValuePair<string, string>(ApiDefaults.IfMatchHeaderName, "\"0\"")]);
        using HttpResponseMessage invalidIfMatch = await SendJsonAsync(
            api.Client,
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages/{ValidMessageId:D}/report",
            new { reason = "spam", comment = "Please review" },
            headers: []);
        using HttpResponseMessage missingStepUp = await SendJsonAsync(
            api.Client,
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/admin/moderation/thread-reads",
            new
            {
                serviceDateId = ValidDateId,
                reason = "Moderation review",
                purpose = "moderation",
                caseId = "CASE-123",
            },
            headers: []);
        using JsonDocument missingIdempotencyProblem =
            JsonDocument.Parse(await missingIdempotency.Content.ReadAsStringAsync());
        using JsonDocument invalidIfMatchProblem =
            JsonDocument.Parse(await invalidIfMatch.Content.ReadAsStringAsync());
        using JsonDocument missingStepUpProblem =
            JsonDocument.Parse(await missingStepUp.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, missingIdempotency.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidIfMatch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missingStepUp.StatusCode);
        Assert.Equal("invalid_thread_request", missingIdempotencyProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal("invalid_thread_request", invalidIfMatchProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A valid Idempotency-Key header is required.",
            missingIdempotencyProblem.RootElement
                .GetProperty("fieldErrors")
                .GetProperty("idempotencyKey")[0]
                .GetString());
        Assert.Equal(
            "A valid If-Match header is required.",
            invalidIfMatchProblem.RootElement
                .GetProperty("fieldErrors")
                .GetProperty("ifMatch")[0]
                .GetString());
        Assert.Equal("forbidden", missingStepUpProblem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public void ThreadIfMatchParserAcceptsOnlyStrongQuotedNonnegativeIntegers()
    {
        foreach ((string Header, bool Valid, long Value) testCase in new[]
                 {
                     ("\"0\"", true, 0L),
                     ("\"42\"", true, 42L),
                     ("0", false, 0L),
                     ("*", false, 0L),
                     ("W/\"0\"", false, 0L),
                     ("\"-1\"", false, 0L),
                     ("\"abc\"", false, 0L),
                 })
        {
            Result<long> parsed = ParseIfMatch(testCase.Header);
            Assert.Equal(testCase.Valid, parsed.IsSuccess);
            if (testCase.Valid)
            {
                Assert.Equal(testCase.Value, parsed.Value);
            }
            else
            {
                Assert.Equal("invalid_thread_request", parsed.Error.Code);
            }
        }
    }

    [Fact]
    public void ThreadStepUpParserRejectsMissingBlankAndMultipleHeaders()
    {
        foreach (string[] values in new[]
                 {
                     Array.Empty<string>(),
                     [" "],
                     ["token-one", "token-two"],
                 })
        {
            DefaultHttpContext context = new();
            if (values.Length > 0)
            {
                context.Request.Headers.Append("X-Step-Up-Token", values);
            }

            Type support = typeof(ApiDefaults).Assembly.GetType(
                               "HusayniaTabruk.Api.Endpoints.V1.Threads.ThreadEndpointSupport")
                           ?? throw new InvalidOperationException("Missing ThreadEndpointSupport.");
            MethodInfo method = support.GetMethod(
                                    "ParseStepUpToken",
                                    BindingFlags.Public | BindingFlags.Static)
                                ?? throw new InvalidOperationException("Missing ParseStepUpToken.");
            object parsed = method.Invoke(null, [context.Request])!;
            PropertyInfo isFailure = parsed.GetType().GetProperty("IsFailure")
                                     ?? throw new InvalidOperationException("Missing IsFailure.");
            Assert.True((bool)isFailure.GetValue(parsed)!);
        }
    }

    private static Result<long> ParseIfMatch(string? header)
    {
        DefaultHttpContext context = new();
        if (header is not null)
        {
            context.Request.Headers.TryAdd(ApiDefaults.IfMatchHeaderName, header);
        }

        Type? support = typeof(ApiDefaults).Assembly.GetType(
            "HusayniaTabruk.Api.Endpoints.V1.Threads.ThreadEndpointSupport");
        Assert.NotNull(support);
        MethodInfo method = support!.GetMethod(
                                "ParseIfMatch",
                                BindingFlags.Public | BindingFlags.Static)
                            ?? throw new InvalidOperationException("Missing ParseIfMatch.");
        return (Result<long>)method.Invoke(null, [context.Request])!;
    }

    private static async Task<HttpResponseMessage> ExhaustPostThreadMessageLimitAsync(ContractApiHost api)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using HttpResponseMessage response = await SendJsonAsync(
                api.Client,
                HttpMethod.Post,
                $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages",
                new { body = $"Message {attempt}" },
                headers:
                [
                    new KeyValuePair<string, string>(ApiDefaults.IdempotencyHeaderName, Guid.NewGuid().ToString("D")),
                    new KeyValuePair<string, string>(ApiDefaults.IfMatchHeaderName, "\"0\""),
                ]);
        }

        return await SendJsonAsync(
            api.Client,
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages",
            new { body = "Message limited" },
            headers:
            [
                new KeyValuePair<string, string>(ApiDefaults.IdempotencyHeaderName, Guid.NewGuid().ToString("D")),
                new KeyValuePair<string, string>(ApiDefaults.IfMatchHeaderName, "\"0\""),
            ]);
    }

    private static async Task<HttpResponseMessage> ExhaustReportThreadMessageLimitAsync(ContractApiHost api)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            using HttpResponseMessage response = await SendJsonAsync(
                api.Client,
                HttpMethod.Post,
                $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages/{ValidMessageId:D}/report",
                new { reason = "spam", comment = $"Report {attempt}" },
                headers:
                [
                    new KeyValuePair<string, string>(ApiDefaults.IfMatchHeaderName, "\"0\""),
                ]);
        }

        return await SendJsonAsync(
            api.Client,
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{ValidDateId:D}/thread/messages/{ValidMessageId:D}/report",
            new { reason = "spam", comment = "Report limited" },
            headers:
            [
                new KeyValuePair<string, string>(ApiDefaults.IfMatchHeaderName, "\"0\""),
            ]);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        IReadOnlyCollection<KeyValuePair<string, string>> headers)
    {
        HttpRequestMessage request = new(method, path)
        {
            Content = JsonContent.Create(body),
        };

        foreach (KeyValuePair<string, string> header in headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return await client.SendAsync(request);
    }

    private static void AssertOperation(
        JsonElement paths,
        string path,
        string method,
        string operationId,
        string? requestSchema = null,
        string? responseSchema = null)
    {
        JsonElement operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());

        if (requestSchema is not null)
        {
            Assert.Equal(
                requestSchema,
                operation.GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString());
        }

        if (responseSchema is not null)
        {
            Assert.Equal(
                responseSchema,
                operation.GetProperty("responses")
                    .GetProperty(responseSchema.Contains("Page", StringComparison.Ordinal) && method == "get" ? "200" : GetSuccessStatusCode(method, path))
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString());
        }
    }

    private static void AssertParameterReferences(
        JsonElement paths,
        string path,
        string method,
        params string[] expectedReferences)
    {
        string[] actualReferences = paths.GetProperty(path)
            .GetProperty(method)
            .GetProperty("parameters")
            .EnumerateArray()
            .Where(parameter => parameter.TryGetProperty("$ref", out _))
            .Select(parameter => parameter.GetProperty("$ref").GetString()!)
            .ToArray();
        Assert.Equal(expectedReferences, actualReferences);
    }

    private static void AssertResponseHeader(
        JsonElement paths,
        string path,
        string method,
        string statusCode,
        string headerName)
    {
        JsonElement header = paths.GetProperty(path)
            .GetProperty(method)
            .GetProperty("responses")
            .GetProperty(statusCode)
            .GetProperty("headers")
            .GetProperty(headerName);

        Assert.True(header.GetProperty("required").GetBoolean());
    }

    private static void AssertPropertyNames(JsonElement schema, params string[] expectedProperties)
    {
        string[] actual = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(expectedProperties, actual);
    }

    private static void AssertForbiddenProperties(JsonElement schema, params string[] forbiddenProperties)
    {
        foreach (string forbiddenProperty in forbiddenProperties)
        {
            Assert.False(
                schema.GetProperty("properties").TryGetProperty(forbiddenProperty, out _),
                $"Schema unexpectedly exposed '{forbiddenProperty}'.");
        }
    }

    private static RouteEndpoint FindRoute(
        ContractApiHost api,
        string pattern,
        string method) =>
        Assert.Single(
            api.Endpoints.OfType<RouteEndpoint>(),
            endpoint =>
                endpoint.RoutePattern.RawText == pattern
                && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(
                    method,
                    StringComparer.Ordinal) == true);

    private static void AssertLimits(
        RouteEndpoint endpoint,
        int maximumBytes,
        params (string Partition, int PermitLimit, TimeSpan Window)[] expectedRules)
    {
        RequestBodyLimitMetadata bodyLimit =
            Assert.IsType<RequestBodyLimitMetadata>(
                endpoint.Metadata.GetMetadata<RequestBodyLimitMetadata>());
        Assert.Equal(maximumBytes, bodyLimit.MaximumBytes);

        ApiRateLimitMetadata rateLimit =
            Assert.IsType<ApiRateLimitMetadata>(
                endpoint.Metadata.GetMetadata<ApiRateLimitMetadata>());
        Assert.Equal(expectedRules.Length, rateLimit.Rules.Count);
        Assert.Equal(
            expectedRules,
            rateLimit.Rules
                .Select(rule => (rule.Partition, rule.PermitLimit, rule.Window))
                .ToArray());
    }

    private static string GetSuccessStatusCode(string method, string path) =>
        path switch
        {
            "/dates/{dateId}/thread/messages" when method == "post" => "201",
            "/dates/{dateId}/thread/messages/{messageId}/report" => "202",
            _ => "200",
        };
}
