using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Signups.Submit;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;

public sealed class SignupSubmitEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule AccountRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.SignupSubmissionsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule OrganizationRateLimit =
        new(
            ApiRateLimitPartitions.Organization,
            ApplicationLimits.SignupSubmissionsPerHourPerOrganization,
            TimeSpan.FromHours(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/needs/{needId}/signups", SubmitAsync)
            .WithName("SubmitSignup")
            .Accepts<SubmitSignupRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumSignupRequestBytes)
            .WithRateLimit(AccountRateLimit, OrganizationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiResponseHeader(StatusCodes.Status201Created, HeaderNames.ETag)
            .WithOpenApiResponseHeader(
                StatusCodes.Status429TooManyRequests,
                ApiDefaults.RetryAfterHeaderName)
            .Produces<SignupResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext httpContext,
        HelpNeedId needId,
        IUnitOfWork unitOfWork,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<IdempotencyKey> key =
            SignupEndpointSupport.ParseIdempotencyKey(httpContext.Request);
        if (key.IsFailure)
        {
            return SignupEndpointSupport.Problem(
                key.Error,
                new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = ["A valid Idempotency-Key header is required."],
                });
        }

        Result<ParsedSubmitSignupRequest> request =
            await SignupEndpointSupport.ParseSubmissionAsync(
                httpContext.Request,
                cancellationToken);
        if (request.IsFailure)
        {
            return SignupEndpointSupport.Problem(request.Error);
        }

        SubmitSignupService service = new(
            unitOfWork,
            signupRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<SignupSummary> result = await service.ExecuteAsync(
            new SubmitSignupCommand(
                needId,
                request.Value.Kind,
                request.Value.Label,
                request.Value.MemberParticipantIds,
                request.Value.UnnamedParticipantCount,
                key.Value),
            cancellationToken);
        return SignupEndpointSupport.FromResult(
            result,
            signup =>
            {
                SignupEndpointSupport.WriteEtag(
                    httpContext.Response,
                    signup.SignupVersion);
                SignupResponse response = SignupEndpointSupport.ToResponse(signup);
                return Results.Created(
                    $"{ApiDefaults.BasePath}/signups/{response.Id}",
                    response);
            });
    }
}
