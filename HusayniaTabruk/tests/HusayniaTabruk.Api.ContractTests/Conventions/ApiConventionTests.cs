using System.Net;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

public sealed class ApiConventionTests
{
    [Fact]
    public async Task GeneratedOpenApiMatchesTheCheckedInSnapshot()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(environmentName: "Production");
        string snapshotPath = Path.Combine(FindRepositoryRoot(), "docs", "api", "openapi.json");

        Assert.Equal(
            File.ReadAllText(snapshotPath).ReplaceLineEndings("\n"),
            await api.Client.GetStringAsync("/openapi/v1.json"));
    }

    [Fact]
    public void CursorPaginationDefaultsToFiftyAndCapsAtOneHundred()
    {
        Assert.Equal(ApplicationLimits.DefaultPageSize, CursorPagination.NormalizePageSize(null));
        Assert.Equal(1, CursorPagination.NormalizePageSize(1));
        Assert.Equal(ApplicationLimits.MaximumPageSize, CursorPagination.NormalizePageSize(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => CursorPagination.NormalizePageSize(0));
    }

    [Fact]
    public async Task OpenApiEndpointIsDeterministicVersionedAndContainsFrozenConventions()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(environmentName: "Production");

        string first = await api.Client.GetStringAsync("/openapi/v1.json");
        string second = await api.Client.GetStringAsync("/openapi/v1.json");
        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement root = document.RootElement;

        Assert.Equal(first, second);
        Assert.Equal("3.1.0", root.GetProperty("openapi").GetString());
        Assert.Equal(ApiDefaults.BasePath, root.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.Contains(
            root.GetProperty("paths").EnumerateObject(),
            property => property.NameEquals("/auth/login"));
        Assert.Contains(
            root.GetProperty("paths").EnumerateObject(),
            property => property.NameEquals("/me"));
        Assert.Equal(
            0,
            root.GetProperty("security")[0].GetProperty("bearerAuth").GetArrayLength());
        Assert.Equal(
            "bearer",
            root.GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty("bearerAuth")
                .GetProperty("scheme")
                .GetString());
        Assert.Equal(
            ApplicationLimits.MaximumPageSize,
            root.GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("cursorPage")
                .GetProperty("properties")
                .GetProperty("items")
                .GetProperty("maxItems")
                .GetInt32());
        Assert.Equal(
            ApiDefaults.IdempotencyHeaderName,
            root.GetProperty("components")
                .GetProperty("parameters")
                .GetProperty("idempotencyKey")
                .GetProperty("name")
                .GetString());
        Assert.Equal(
            ApiDefaults.IfMatchHeaderName,
            root.GetProperty("components")
                .GetProperty("parameters")
                .GetProperty("ifMatch")
                .GetProperty("name")
                .GetString());
        Assert.Equal(
            "scope",
            root.GetProperty("components")
                .GetProperty("parameters")
                .GetProperty("dateScope")
                .GetProperty("name")
                .GetString());
        Assert.Equal(
            "camelCaseStrings",
            root.GetProperty("x-conventions").GetProperty("jsonEnums").GetString());
        Assert.False(root.GetProperty("x-conventions").GetProperty("numericEnumsAccepted").GetBoolean());
    }

    [Fact]
    public async Task UnknownVersionedRouteReturnsCamelCaseProblemDetailsWithTraceId()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage response = await api.Client.GetAsync($"{ApiDefaults.BasePath}/not-a-route");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues(ApiDefaults.TraceHeaderName, out IEnumerable<string>? traceHeaders));
        string traceId = Assert.Single(traceHeaders);
        Assert.Matches("^[0-9a-f]{32}$", traceId);
        Assert.Equal("not_found", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(traceId, problem.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(404, problem.RootElement.GetProperty("status").GetInt32());
        Assert.False(problem.RootElement.TryGetProperty("TraceId", out _));
    }

    [Theory]
    [InlineData("/api/v1/_conventions/body/message", ApplicationLimits.MaximumMessageRequestBytes)]
    [InlineData("/api/v1/_conventions/body/report", ApplicationLimits.MaximumReportRequestBytes)]
    [InlineData("/api/v1/_conventions/body/signup", ApplicationLimits.MaximumSignupRequestBytes)]
    [InlineData("/api/v1/_conventions/body/administrative", ApplicationLimits.MaximumAdministrativeRequestBytes)]
    public async Task EndpointBodyLimitsAcceptTheExactMaximum(string path, int maximumBytes)
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using ByteArrayContent content = new(new byte[maximumBytes]);

        using HttpResponseMessage response = await api.Client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/_conventions/body/message", ApplicationLimits.MaximumMessageRequestBytes)]
    [InlineData("/api/v1/_conventions/body/report", ApplicationLimits.MaximumReportRequestBytes)]
    [InlineData("/api/v1/_conventions/body/signup", ApplicationLimits.MaximumSignupRequestBytes)]
    [InlineData("/api/v1/_conventions/body/administrative", ApplicationLimits.MaximumAdministrativeRequestBytes)]
    public async Task EndpointBodyLimitsReturnPayloadTooLargeProblem(string path, int maximumBytes)
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using ByteArrayContent content = new(new byte[maximumBytes + 1]);

        using HttpResponseMessage response = await api.Client.PostAsync(path, content);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.PayloadTooLarge, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExhaustedRateLimitReturnsRetryAfterAndProblemDetails()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage accepted = await api.Client.GetAsync("/api/v1/_conventions/rate");
        using HttpResponseMessage limited = await api.Client.GetAsync("/api/v1/_conventions/rate");
        using JsonDocument problem = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues(ApiDefaults.RetryAfterHeaderName, out IEnumerable<string>? values));
        Assert.True(int.TryParse(Assert.Single(values), out int retryAfter));
        Assert.True(retryAfter > 0);
        Assert.Equal(ErrorCodes.RateLimited, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthorizationFailsClosedForVersionedAndFallbackEndpoints()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(authenticated: false);
        using HttpRequestMessage versionedRequest = new(HttpMethod.Get, "/api/v1/_conventions/problem");
        versionedRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");

        using HttpResponseMessage versioned = await api.Client.SendAsync(versionedRequest);
        using HttpResponseMessage fallback = await api.Client.GetAsync("/openapi/v1.json");
        using JsonDocument versionedProblem = JsonDocument.Parse(await versioned.Content.ReadAsStringAsync());
        using JsonDocument fallbackProblem = JsonDocument.Parse(await fallback.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, versioned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fallback.StatusCode);
        Assert.Equal("unauthorized", versionedProblem.RootElement.GetProperty("code").GetString());
        Assert.Equal("unauthorized", fallbackProblem.RootElement.GetProperty("code").GetString());
        RouteEndpoint[] anonymousEndpoints = api.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .ToArray();
        Assert.NotEmpty(anonymousEndpoints);
        Assert.All(
            anonymousEndpoints,
            endpoint => Assert.StartsWith(
                $"{ApiDefaults.BasePath}/auth/",
                endpoint.RoutePattern.RawText,
                StringComparison.Ordinal));

        RouteEndpoint[] versionedEndpoints = api.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith(ApiDefaults.BasePath, StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(versionedEndpoints);
        Assert.All(
            versionedEndpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null),
            endpoint => Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()));

        RouteEndpoint openApiEndpoint = Assert.Single(
            api.Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/openapi/v1.json");
        Assert.Empty(openApiEndpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }

    [Fact]
    public async Task InjectedEndpointAppearsInGeneratedOpenApi()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            environmentName: "Production",
            configureApi: group => group
                .MapGet("/_contract/injected", () => Results.NoContent())
                .WithName("ContractInjected"));

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/_contract/injected")
            .GetProperty("get");

        Assert.Equal("ContractInjected", operation.GetProperty("operationId").GetString());
        Assert.True(operation.TryGetProperty("responses", out _));
    }

    [Fact]
    public async Task InjectedConstrainedRouteUsesNormalizedOpenApiParameter()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            environmentName: "Production",
            configureApi: group => group.MapGet(
                "/_contract/items/{itemId:guid}",
                (Guid itemId) => Results.Ok(itemId)));

        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/_contract/items/{itemId}")
            .GetProperty("get");
        JsonElement parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray());

        Assert.Equal("itemId", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task DuplicateNormalizedOpenApiOperationsFailClosed()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            environmentName: "Production",
            configureApi: group =>
            {
                group.MapGet("/_contract/duplicate/{itemId}", (string itemId) => Results.Ok(itemId));
                group.MapGet("/_contract/duplicate/{itemId:guid}", (Guid itemId) => Results.Ok(itemId));
            });

        using HttpResponseMessage response = await api.Client.GetAsync("/openapi/v1.json");
        using JsonDocument problem =
            JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("internal_error", problem.RootElement.GetProperty("code").GetString());
        Assert.Single(api.Logs.Records, record => record.EventId.Id == 1002);
    }

    [Fact]
    public async Task InboundTraceparentCannotChooseResponseTraceId()
    {
        const string inboundTraceId = "11111111111111111111111111111111";
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/not-found");
        request.Headers.TryAddWithoutValidation(
            "traceparent",
            $"00-{inboundTraceId}-2222222222222222-01");

        using HttpResponseMessage response = await api.Client.SendAsync(request);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string responseTraceId = Assert.Single(response.Headers.GetValues(ApiDefaults.TraceHeaderName));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Matches("^[0-9a-f]{32}$", responseTraceId);
        Assert.NotEqual(inboundTraceId, responseTraceId);
        Assert.Equal(responseTraceId, problem.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task StreamedBodyWithoutContentLengthAcceptsExactLimit()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using UnknownLengthStreamingContent content =
            new(new byte[ApplicationLimits.MaximumMessageRequestBytes]);

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/body/message", content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task StreamedBodyWithoutContentLengthRejectsLimitPlusOne()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using UnknownLengthStreamingContent content =
            new(new byte[ApplicationLimits.MaximumMessageRequestBytes + 1]);

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/body/message", content);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.PayloadTooLarge, problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RejectedAccountPartitionDoesNotConsumeOrganizationPartition()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage first = await SendMultiRateRequestAsync(api, "account-a", "organization-a");
        using HttpResponseMessage rejected = await SendMultiRateRequestAsync(api, "account-a", "organization-a");
        using HttpResponseMessage otherAccount = await SendMultiRateRequestAsync(api, "account-b", "organization-a");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, otherAccount.StatusCode);
    }

    [Fact]
    public async Task RejectedOrganizationPartitionDoesNotConsumeAccountPartition()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage first = await SendMultiRateRequestAsync(api, "account-a", "organization-a");
        using HttpResponseMessage second = await SendMultiRateRequestAsync(api, "account-b", "organization-a");
        using HttpResponseMessage rejected = await SendMultiRateRequestAsync(api, "account-c", "organization-a");
        using HttpResponseMessage otherOrganization =
            await SendMultiRateRequestAsync(api, "account-c", "organization-b");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, otherOrganization.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendMultiRateRequestAsync(
        ContractApiHost api,
        string membershipId,
        string organizationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/_conventions/rate/multi");
        request.Headers.Add(ContractApiHost.MembershipHeaderName, membershipId);
        request.Headers.Add(ContractApiHost.OrganizationHeaderName, organizationId);
        return await api.Client.SendAsync(request);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HusayniaTabruk.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the HusayniaTabruk repository root.");
    }
}
