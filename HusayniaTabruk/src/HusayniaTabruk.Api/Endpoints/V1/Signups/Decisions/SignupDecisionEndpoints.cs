using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Decisions;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Decisions;

public sealed class SignupDecisionEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule AccountRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule OrganizationRateLimit =
        new(
            ApiRateLimitPartitions.Organization,
            ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
            TimeSpan.FromHours(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        MapDecision<ApproveSignupRequest>(
            endpoints,
            "approve",
            "ApproveSignup",
            ApproveAsync);
        MapDecision<DeclineSignupRequest>(
            endpoints,
            "decline",
            "DeclineSignup",
            DeclineAsync);
        MapDecision<WaitlistSignupRequest>(
            endpoints,
            "waitlist",
            "WaitlistSignup",
            WaitlistAsync);
    }

    private static void MapDecision<TRequest>(
        IEndpointRouteBuilder endpoints,
        string action,
        string operationId,
        Delegate handler)
        where TRequest : notnull
    {
        endpoints.MapPost($"/signups/{{signupId}}/{action}", handler)
            .WithName(operationId)
            .Accepts<TRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountRateLimit, OrganizationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(
                StatusCodes.Status429TooManyRequests,
                ApiDefaults.RetryAfterHeaderName)
            .Produces<SignupResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static Task<IResult> ApproveAsync(
        HttpContext httpContext,
        string signupId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            signupId,
            reasonRequired: false,
            (service, parsedSignupId, reason, key, version, token) =>
                service.ApproveAsync(
                    new ApproveSignupCommand(parsedSignupId, reason, key),
                    version,
                    token),
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock,
            cancellationToken);

    private static Task<IResult> DeclineAsync(
        HttpContext httpContext,
        string signupId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            signupId,
            reasonRequired: true,
            (service, parsedSignupId, reason, key, version, token) =>
                service.DeclineAsync(
                    new DeclineSignupCommand(parsedSignupId, reason!, key),
                    version,
                    token),
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock,
            cancellationToken);

    private static Task<IResult> WaitlistAsync(
        HttpContext httpContext,
        string signupId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            httpContext,
            signupId,
            reasonRequired: false,
            (service, parsedSignupId, reason, key, version, token) =>
                service.WaitlistAsync(
                    new WaitlistSignupCommand(parsedSignupId, reason, key),
                    version,
                    token),
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock,
            cancellationToken);

    private static async Task<IResult> ExecuteAsync(
        HttpContext httpContext,
        string signupId,
        bool reasonRequired,
        Func<
            SignupDecisionService,
            SignupId,
            string?,
            IdempotencyKey,
            long,
            CancellationToken,
            ValueTask<Result<SignupSummary>>> execute,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!SignupId.TryParse(signupId, out SignupId parsedSignupId))
        {
            return SignupDecisionEndpointSupport.Problem(
                Domain.Common.Errors.DomainError.Validation(
                    SignupApplicationErrorCodes.InvalidSignupRequest,
                    "A valid signup ID is required."),
                new Dictionary<string, string[]>
                {
                    ["signupId"] = ["A valid signup ID is required."],
                });
        }

        Result<IdempotencyKey> key =
            SignupDecisionEndpointSupport.ParseIdempotencyKey(httpContext.Request);
        if (key.IsFailure)
        {
            return SignupDecisionEndpointSupport.Problem(
                key.Error,
                new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = ["A valid Idempotency-Key header is required."],
                });
        }

        Result<long> expectedVersion =
            SignupDecisionEndpointSupport.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return SignupDecisionEndpointSupport.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    ["ifMatch"] = ["A valid If-Match header is required."],
                });
        }

        Result<string?> reason = await SignupDecisionEndpointSupport.ParseReasonAsync(
            httpContext.Request,
            reasonRequired,
            cancellationToken);
        if (reason.IsFailure)
        {
            return SignupDecisionEndpointSupport.Problem(reason.Error);
        }

        SignupDecisionService service = new(
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<SignupSummary> result = await execute(
            service,
            parsedSignupId,
            reason.Value,
            key.Value,
            expectedVersion.Value,
            cancellationToken);
        return result.IsSuccess
            ? Success(httpContext, result.Value)
            : SignupDecisionEndpointSupport.Problem(result.Error);
    }

    private static IResult Success(HttpContext httpContext, SignupSummary signup)
    {
        SignupDecisionEndpointSupport.WriteEtag(
            httpContext.Response,
            signup.SignupVersion);
        return Results.Ok(SignupDecisionEndpointSupport.ToResponse(signup));
    }
}
