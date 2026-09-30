using System.Net;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Domain.Common.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

public sealed class JsonAndProblemDetailsContractTests
{
    [Fact]
    public async Task EnumRequestAndResponseUseCamelCaseStrings()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using StringContent content =
            new("""{"status":"active"}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/json/enum", content);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("active", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EnumDictionaryKeyDeserializationAcceptsCamelCaseStrings()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapPost(
                "/_contract/json/enum-dictionary/read",
                (Dictionary<MembershipStatus, string> values) =>
                    Results.Ok(new { value = values[MembershipStatus.Active] })));
        using StringContent content =
            new("""{"active":"ok"}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_contract/json/enum-dictionary/read", content);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", document.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task EnumDictionaryKeySerializationUsesCamelCaseStrings()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapGet(
                "/_contract/json/enum-dictionary/write",
                () => Results.Ok(new Dictionary<MembershipStatus, string>
                {
                    [MembershipStatus.Active] = "ok",
                })));

        using HttpResponseMessage response =
            await api.Client.GetAsync("/api/v1/_contract/json/enum-dictionary/write");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"active":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("999")]
    [InlineData("active, disabled")]
    public async Task InvalidEnumDictionaryKeyInputReturnsSanitizedBadRequest(string key)
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapPost(
                "/_contract/json/enum-dictionary/read-invalid",
                (Dictionary<MembershipStatus, string> _) => Results.NoContent()));
        using StringContent content =
            new($$"""{"{{key}}":"bad"}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_contract/json/enum-dictionary/read-invalid", content);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bad_request", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The request body is malformed or invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("JsonException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dictionary", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(999)]
    public async Task InvalidEnumDictionaryKeyOutputReturnsSanitizedProblem(int invalidValue)
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapGet(
                "/_contract/json/enum-dictionary/write-invalid",
                () => Results.Ok(new Dictionary<MembershipStatus, string>
                {
                    [(MembershipStatus)invalidValue] = "bad",
                })));

        using HttpResponseMessage response =
            await api.Client.GetAsync("/api/v1/_contract/json/enum-dictionary/write-invalid");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        TestLogRecord error = Assert.Single(api.Logs.Records, record => record.EventId.Id == 1002);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("internal_error", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", problem.RootElement.GetProperty("detail").GetString());
        Assert.IsType<JsonException>(error.Exception);
        Assert.DoesNotContain("JsonException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dictionary", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NumericEnumInputReturnsSanitizedBadRequest()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using StringContent content = new("""{"status":1}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/json/enum", content);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bad_request", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The request body is malformed or invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("JsonException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("convert", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UndefinedNumericEnumInputReturnsSanitizedBadRequest()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using StringContent content = new("""{"status":999}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/json/enum", content);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bad_request", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The request body is malformed or invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("JsonException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("convert", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompositeNonFlagsEnumInputReturnsSanitizedBadRequest()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using StringContent content =
            new("""{"status":"active, disabled"}""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/json/enum", content);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bad_request", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The request body is malformed or invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("JsonException", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("convert", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedJsonReturnsSanitizedBadRequestAndWarningLog()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        using StringContent content = new("""{"status":""", Encoding.UTF8, "application/json");

        using HttpResponseMessage response =
            await api.Client.PostAsync("/api/v1/_conventions/json/enum", content);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        TestLogRecord warning = Assert.Single(api.Logs.Records, record => record.EventId.Id == 1001);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "The request body is malformed or invalid.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(
            problem.RootElement.GetProperty("traceId").GetString()!,
            warning.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("line number", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("byte position", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedExceptionReturnsSanitizedProblemAndCorrelatedErrorLog()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        using HttpResponseMessage response =
            await api.Client.GetAsync("/api/v1/_conventions/problem/unexpected");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        TestLogRecord error = Assert.Single(api.Logs.Records, record => record.EventId.Id == 1002);
        string traceId = problem.RootElement.GetProperty("traceId").GetString()!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("internal_error", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(traceId, Assert.Single(response.Headers.GetValues(ApiDefaults.TraceHeaderName)));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains(traceId, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("contract exception sentinel", body, StringComparison.Ordinal);
        Assert.Contains("contract exception sentinel", error.Exception?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependencyUnavailableExceptionReturnsSanitizedServiceUnavailableAndDedicatedLog()
    {
        InvalidOperationException providerException = new(
            "Host=database.internal; SQLSTATE=08006; SELECT secret FROM private_table");
        DependencyUnavailableException dependencyException = new(providerException);
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapGet(
                "/_contract/problem/dependency-unavailable",
                () => Task.FromException<IResult>(dependencyException)));

        using HttpResponseMessage response =
            await api.Client.GetAsync("/api/v1/_contract/problem/dependency-unavailable");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);
        TestLogRecord error = Assert.Single(api.Logs.Records, record => record.EventId.Id == 1004);
        string traceId = problem.RootElement.GetProperty("traceId").GetString()!;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("https://httpstatuses.com/503", problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("Service unavailable", problem.RootElement.GetProperty("title").GetString());
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("dependency_unavailable", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A required dependency is temporarily unavailable.",
            problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(traceId, Assert.Single(response.Headers.GetValues(ApiDefaults.TraceHeaderName)));
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Same(dependencyException, error.Exception);
        Assert.Contains(traceId, error.Message, StringComparison.Ordinal);
        Assert.Contains("GET", error.Message, StringComparison.Ordinal);
        Assert.Contains("/api/v1/_contract/problem/dependency-unavailable", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("database.internal", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSTATE", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_table", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(DependencyUnavailableException), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependencyUnavailableAfterResponseStartsUsesExistingLogAndRethrowBehavior()
    {
        TestLogCollector logs = new();
        using ILoggerFactory loggerFactory =
            LoggerFactory.Create(builder => builder.AddProvider(logs));
        DependencyUnavailableException dependencyException = new(
            new InvalidOperationException("provider diagnostic sentinel"));
        DefaultHttpContext context = new()
        {
            TraceIdentifier = "dependency-started-trace",
        };
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/_contract/problem/dependency-started";
        ApiProblemDetailsMiddleware middleware = new(
            _ => Task.FromException(dependencyException),
            loggerFactory.CreateLogger<ApiProblemDetailsMiddleware>());

        DependencyUnavailableException thrown = await Assert.ThrowsAsync<DependencyUnavailableException>(
            () => middleware.InvokeAsync(context));
        TestLogRecord error = Assert.Single(logs.Records, record => record.EventId.Id == 1003);

        Assert.Same(dependencyException, thrown);
        Assert.Same(dependencyException, error.Exception);
        Assert.DoesNotContain(logs.Records, record => record.EventId.Id == 1004);
        Assert.Contains(context.TraceIdentifier, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedExceptionClearsUnstartedResponseMetadataBeforeWritingProblemDetails()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync(
            configureApi: group => group.MapGet(
                "/_contract/problem/stale-response",
                ThrowAfterSettingStaleResponseMetadata));

        using HttpResponseMessage response =
            await api.Client.GetAsync("/api/v1/_contract/problem/stale-response");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), response.Content.Headers.ContentLength);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.False(response.Headers.Contains("X-Contract-Stale"));
        Assert.Equal("internal_error", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal("An unexpected error occurred.", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("stale response sentinel", body, StringComparison.Ordinal);
    }

    private static IResult ThrowAfterSettingStaleResponseMetadata(HttpContext context)
    {
        context.Response.ContentLength = 1;
        context.Response.ContentType = "application/x-stale";
        context.Response.Headers.ContentEncoding = "gzip";
        context.Response.Headers["X-Contract-Stale"] = "sentinel";
        throw new InvalidOperationException("stale response sentinel");
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = Stream.Null;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
