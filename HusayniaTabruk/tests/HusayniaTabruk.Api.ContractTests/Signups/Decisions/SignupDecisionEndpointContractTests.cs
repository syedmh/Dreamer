using System.Net;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.ContractTests.Conventions;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HusayniaTabruk.Api.ContractTests.Signups.Decisions;

public sealed class SignupDecisionEndpointContractTests
{
    private const string SignupId = "0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52a";
    private static readonly string[] Actions = ["approve", "decline", "waitlist"];
    private static readonly string[] OperationIds =
        ["ApproveSignup", "DeclineSignup", "WaitlistSignup"];
    private static readonly string[] FrozenStatuses =
        ["200", "400", "401", "404", "409", "412", "413", "429", "503"];

    [Fact]
    public async Task DecisionRoutesExposeFrozenMethodsOperationIdsAndRequestSchemas()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;

        for (int index = 0; index < Actions.Length; index++)
        {
            JsonElement operation = Operation(root, Actions[index]);
            Assert.Equal(OperationIds[index], operation.GetProperty("operationId").GetString());
            Assert.Equal(
                $"#/components/schemas/{char.ToUpperInvariant(Actions[index][0])}{Actions[index][1..]}SignupRequest",
                operation.GetProperty("requestBody")
                    .GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString());
        }
    }

    [Fact]
    public async Task DecisionRoutesRequireIdempotencyKeyAndIfMatch()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;

        foreach (string action in Actions)
        {
            string[] references = Operation(root, action)
                .GetProperty("parameters")
                .EnumerateArray()
                .Where(parameter => parameter.TryGetProperty("$ref", out _))
                .Select(parameter => parameter.GetProperty("$ref").GetString()!)
                .ToArray();
            Assert.Contains("#/components/parameters/idempotencyKey", references);
            Assert.Contains("#/components/parameters/ifMatch", references);
        }
    }

    [Fact]
    public async Task DecisionSuccessUsesExistingSignupResponseAndRequiredEtag()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;

        foreach (string action in Actions)
        {
            JsonElement success = Operation(root, action)
                .GetProperty("responses")
                .GetProperty("200");
            Assert.Equal(
                "#/components/schemas/SignupResponse",
                success.GetProperty("content")
                    .GetProperty("application/json")
                    .GetProperty("schema")
                    .GetProperty("$ref")
                    .GetString());
            JsonElement etag = success.GetProperty("headers").GetProperty("ETag");
            Assert.True(etag.GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task DecisionRoutesDeclareExactlyTheFrozenResponseStatusesAndRetryAfter()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");
        using JsonDocument document =
            JsonDocument.Parse(await api.Client.GetStringAsync("/openapi/v1.json"));
        JsonElement root = document.RootElement;

        foreach (string action in Actions)
        {
            JsonElement responses = Operation(root, action).GetProperty("responses");
            Assert.Equal(
                FrozenStatuses,
                responses.EnumerateObject().Select(property => property.Name).ToArray());
            JsonElement retryAfter = responses.GetProperty("429")
                .GetProperty("headers")
                .GetProperty(ApiDefaults.RetryAfterHeaderName);
            Assert.True(retryAfter.GetProperty("required").GetBoolean());
            Assert.Equal(
                "integer",
                retryAfter.GetProperty("schema").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task DecisionBodiesRejectContextTargetStateVersionUnknownAndDuplicateProperties()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();
        string[] invalidBodies =
        [
            """{"reason":"ok","actorMembershipId":"0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52b"}""",
            """{"reason":"ok","organizationId":"0198c9e8-0d8b-7c2d-b3d5-6d98a8c3e52b"}""",
            """{"reason":"ok","targetState":"approved"}""",
            """{"reason":"ok","expectedSignupVersion":0}""",
            """{"reason":"ok","unexpected":true}""",
            """{"reason":"first","reason":"second"}""",
        ];

        foreach (string body in invalidBodies)
        {
            using HttpResponseMessage response = await SendAsync(
                api.Client,
                "approve",
                body,
                ifMatch: "\"0\"");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using JsonDocument problem =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                problem.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task DeclineRequiresReasonWhileApproveAndWaitlistPermitNullOrAbsentReason()
    {
        await using ContractApiHost api = await ContractApiHost.StartAsync();

        foreach (string body in new[] { "{}", """{"reason":null}""", """{"reason":"   "}""" })
        {
            using HttpResponseMessage response = await SendAsync(
                api.Client,
                "decline",
                body,
                ifMatch: "\"0\"");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using JsonDocument problem =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                problem.RootElement.GetProperty("code").GetString());
        }

        foreach (string action in new[] { "approve", "waitlist" })
        {
            foreach (string body in new[] { "{}", """{"reason":null}""" })
            {
                using HttpResponseMessage response = await SendAsync(
                    api.Client,
                    action,
                    body,
                    ifMatch: "invalid");
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                using JsonDocument problem =
                    JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    problem.RootElement.GetProperty("code").GetString());
                Assert.True(
                    problem.RootElement.GetProperty("fieldErrors").TryGetProperty(
                        "ifMatch",
                        out _));
            }
        }
    }

    [Fact]
    public void IfMatchAcceptsOneStrongQuotedOrUnquotedNonnegativeInt64()
    {
        foreach ((string Header, long Expected) item in new[]
        {
            ("0", 0L),
            ("\"0\"", 0L),
            (long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), long.MaxValue),
            ($"\"{long.MaxValue}\"", long.MaxValue),
        })
        {
            Result<long> parsed = ParseIfMatch(item.Header);
            Assert.True(parsed.IsSuccess, parsed.IsFailure ? parsed.Error.Message : null);
            Assert.Equal(item.Expected, parsed.Value);
        }
    }

    [Fact]
    public void InvalidIfMatchReturnsInvalidSignupInputWithIfMatchFieldError()
    {
        foreach (string? header in new string?[]
        {
            null, "", " ", "W/\"0\"", "\"0\",\"1\"", "0,1", "-1", "\"-1\"",
            "\"\"", "\"1", "1\"", "abc", "9223372036854775808",
        })
        {
            Result<long> parsed = ParseIfMatch(header);
            Assert.True(parsed.IsFailure);
            Assert.Equal(SignupApplicationErrorCodes.InvalidSignupRequest, parsed.Error.Code);
            Assert.Equal(ErrorType.Validation, parsed.Error.Type);
        }
    }

    [Fact]
    public async Task DecisionRoutesUseFourKiBBodyAndAdministrativeMutationLimits()
    {
        await using ContractApiHost api =
            await ContractApiHost.StartAsync(environmentName: "Production");

        foreach (string action in Actions)
        {
            RouteEndpoint endpoint = Assert.Single(
                api.Endpoints.OfType<RouteEndpoint>(),
                candidate => candidate.RoutePattern.RawText ==
                    $"{ApiDefaults.BasePath}/signups/{{signupId}}/{action}");
            RequestBodyLimitMetadata bodyLimit =
                endpoint.Metadata.GetMetadata<RequestBodyLimitMetadata>()!;
            ApiRateLimitMetadata rateLimit =
                endpoint.Metadata.GetMetadata<ApiRateLimitMetadata>()!;
            Assert.Equal(ApplicationLimits.MaximumAdministrativeRequestBytes, bodyLimit.MaximumBytes);
            Assert.Collection(
                rateLimit.Rules,
                account =>
                {
                    Assert.Equal(ApiRateLimitPartitions.Account, account.Partition);
                    Assert.Equal(
                        ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
                        account.PermitLimit);
                    Assert.Equal(TimeSpan.FromMinutes(1), account.Window);
                },
                organization =>
                {
                    Assert.Equal(ApiRateLimitPartitions.Organization, organization.Partition);
                    Assert.Equal(
                        ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
                        organization.PermitLimit);
                    Assert.Equal(TimeSpan.FromHours(1), organization.Window);
                });
        }
    }

    [Fact]
    public async Task DecisionFailuresUseStableProblemDetailsCodes()
    {
        (DomainError Error, int Status)[] cases =
        [
            (DomainError.Validation(SignupApplicationErrorCodes.InvalidSignupRequest, "invalid"), 400),
            (DomainError.Unauthorized("unauthorized", "unauthorized"), 401),
            (DomainError.NotFound(SignupApplicationErrorCodes.SignupNotFound, "not found"), 404),
            (DomainError.Conflict(ErrorCodes.InvalidTransition, "invalid transition"), 409),
            (DomainError.PreconditionFailed(ErrorCodes.StaleVersion, "stale"), 412),
            (DomainError.PayloadTooLarge("large"), 413),
            (DomainError.RateLimited("limited"), 429),
            (DomainError.DependencyUnavailable(ErrorCodes.DependencyUnavailable, "dependency"), 503),
        ];

        foreach ((DomainError error, int expectedStatus) in cases)
        {
            DefaultHttpContext context = new();
            ServiceCollection services = new();
            services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>();
            context.RequestServices = services.BuildServiceProvider();
            context.Response.Body = new MemoryStream();
            IResult problem = CreateProblem(error);

            await problem.ExecuteAsync(context);

            context.Response.Body.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(context.Response.Body);
            Assert.Equal(expectedStatus, context.Response.StatusCode);
            Assert.Equal(error.Code, document.RootElement.GetProperty("code").GetString());
        }
    }

    private static JsonElement Operation(JsonElement root, string action) =>
        root.GetProperty("paths")
            .GetProperty($"/signups/{{signupId}}/{action}")
            .GetProperty("post");

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string action,
        string body,
        string? ifMatch)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/signups/{SignupId}/{action}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            Guid.CreateVersion7().ToString("D"));
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, ifMatch);
        }

        return await client.SendAsync(request);
    }

    private static Result<long> ParseIfMatch(string? header)
    {
        DefaultHttpContext context = new();
        if (header is not null)
        {
            context.Request.Headers.TryAdd(ApiDefaults.IfMatchHeaderName, header);
        }

        Type support = RequireDecisionSupport();
        return (Result<long>)support.GetMethod("ParseIfMatch")!.Invoke(
            null,
            [context.Request])!;
    }

    private static IResult CreateProblem(DomainError error)
    {
        Type support = RequireDecisionSupport();
        return (IResult)support.GetMethod(
            "Problem",
            [typeof(DomainError), typeof(IReadOnlyDictionary<string, string[]>)])!.Invoke(
            null,
            [error, null])!;
    }

    private static Type RequireDecisionSupport()
    {
        Type? support = typeof(ApiDefaults).Assembly.GetType(
            "HusayniaTabruk.Api.Endpoints.V1.Signups.Decisions.SignupDecisionEndpointSupport");
        Assert.NotNull(support);
        return support;
    }
}
