using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Cancellation;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Cancellation;

public sealed class SignupCancellationEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule MemberRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.SignupSubmissionsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));
    private static readonly RateLimitRule AdministrativeAccountRateLimit =
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
        Map<WithdrawSignupRequest>(
            endpoints,
            "/signups/{signupId}/withdraw",
            "WithdrawSignup",
            WithdrawAsync,
            MemberRateLimit);
        Map<OverrideSignupRequest>(
            endpoints,
            "/signups/{signupId}/override",
            "OverrideSignupCancellation",
            OverrideAsync,
            AdministrativeAccountRateLimit,
            OrganizationRateLimit);
    }

    private static void Map<TRequest>(
        IEndpointRouteBuilder endpoints,
        string pattern,
        string operationId,
        Delegate handler,
        params RateLimitRule[] rateLimits)
        where TRequest : notnull
    {
        endpoints.MapPost(pattern, handler)
            .WithName(operationId)
            .Accepts<TRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(rateLimits)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(
                StatusCodes.Status429TooManyRequests,
                ApiDefaults.RetryAfterHeaderName)
            .Produces<SignupResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> WithdrawAsync(
        HttpContext httpContext,
        string signupId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<(SignupId SignupId, IdempotencyKey Key, long Version)> common =
            ParseCommon(httpContext, signupId);
        if (common.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(common.Error);
        }

        Result body = await SignupCancellationEndpointSupport.ParseEmptyObjectAsync(
            httpContext.Request,
            cancellationToken);
        if (body.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(body.Error);
        }

        SignupCancellationService service = new(
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<SignupSummary> result = await service.WithdrawAsync(
            new WithdrawSignupCommand(common.Value.SignupId, common.Value.Key),
            common.Value.Version,
            cancellationToken);
        return ToResult(httpContext, result);
    }

    private static async Task<IResult> OverrideAsync(
        HttpContext httpContext,
        string signupId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<(SignupId SignupId, IdempotencyKey Key, long Version)> common =
            ParseCommon(httpContext, signupId);
        if (common.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(common.Error);
        }

        Result<ParsedCancellationOverride> body =
            await SignupCancellationEndpointSupport.ParseOverrideAsync(
                httpContext.Request,
                cancellationToken);
        if (body.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(body.Error);
        }

        SignupCancellationService service = new(
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<SignupSummary> result = await service.OverrideAsync(
            new OverrideSignupCancellationCommand(
                common.Value.SignupId,
                body.Value.TargetStatus,
                body.Value.Reason,
                common.Value.Key),
            common.Value.Version,
            cancellationToken);
        return ToResult(httpContext, result);
    }

    private static Result<(SignupId SignupId, IdempotencyKey Key, long Version)> ParseCommon(
        HttpContext httpContext,
        string signupId)
    {
        if (!SignupId.TryParse(signupId, out SignupId parsedSignupId))
        {
            return Invalid("A valid signup ID is required.");
        }

        Result<IdempotencyKey> key =
            SignupCancellationEndpointSupport.ParseIdempotencyKey(httpContext.Request);
        if (key.IsFailure)
        {
            return Result.Failure<(SignupId, IdempotencyKey, long)>(key.Error);
        }

        Result<long> version =
            SignupCancellationEndpointSupport.ParseIfMatch(httpContext.Request);
        return version.IsFailure
            ? Result.Failure<(SignupId, IdempotencyKey, long)>(version.Error)
            : Result.Success((parsedSignupId, key.Value, version.Value));
    }

    private static IResult ToResult(
        HttpContext httpContext,
        Result<SignupSummary> result)
    {
        if (result.IsFailure)
        {
            return SignupCancellationEndpointSupport.Problem(result.Error);
        }

        SignupCancellationEndpointSupport.WriteEtag(
            httpContext.Response,
            result.Value.SignupVersion);
        return Results.Ok(SignupCancellationEndpointSupport.ToResponse(result.Value));
    }

    private static Result<(SignupId, IdempotencyKey, long)> Invalid(string message) =>
        Result.Failure<(SignupId, IdempotencyKey, long)>(
            DomainError.Validation(
                SignupApplicationErrorCodes.InvalidSignupRequest,
                message));
}
