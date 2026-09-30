using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Dates;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Needs.Management;

public sealed class NeedManagementEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule AccountMutationRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule OrganizationMutationRateLimit =
        new(
            ApiRateLimitPartitions.Organization,
            ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
            TimeSpan.FromHours(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/dates/{dateId}/needs", AddNeedAsync)
            .WithName("CreateHelpNeed")
            .Accepts<CreateHelpNeedRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiResponseHeader(StatusCodes.Status201Created, HeaderNames.ETag)
            .Produces<HelpNeedResponse>(StatusCodes.Status201Created)
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

    private static async Task<IResult> AddNeedAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        [FromBody] CreateHelpNeedRequest request,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ServiceDateService service = new(
            unitOfWork,
            serviceDateRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<HelpNeedSummary> result = await service.AddNeedAsync(
            new AddHelpNeedCommand(
                dateId,
                request.Category,
                request.Instructions ?? string.Empty,
                request.Capacity),
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            need =>
            {
                DateEndpointSupport.WriteEtag(httpContext.Response, need.Version);
                return Results.Created(
                    $"/api/v1/needs/{need.Id}",
                    DateEndpointSupport.ToResponse(need));
            });
    }
}
