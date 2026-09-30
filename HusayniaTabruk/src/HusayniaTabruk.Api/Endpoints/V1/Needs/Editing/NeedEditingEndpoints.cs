using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Dates;
using HusayniaTabruk.Api.Endpoints.V1.Dates.Management;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Application.Dates.Management;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Needs.Editing;

public sealed class NeedEditingEndpoints : IApiEndpoint
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
        endpoints.MapPatch("/needs/{needId}", EditAsync)
            .WithName("EditHelpNeed")
            .Accepts<PatchHelpNeedRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces<HelpNeedResponse>(StatusCodes.Status200OK)
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

    private static async Task<IResult> EditAsync(
        HttpContext httpContext,
        HelpNeedId needId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        INotificationWriter notificationWriter,
        IAuditWriter auditWriter,
        IOutboxWriter outboxWriter,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> version = DateManagementEndpointSupport.ParseIfMatch(httpContext.Request);
        if (version.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(
                version.Error,
                new Dictionary<string, string[]>
                {
                    ["ifMatch"] = ["A valid If-Match header is required."],
                });
        }

        Result<ParsedNeedPatch> body = await DateManagementEndpointSupport.ParseNeedPatchAsync(
            httpContext.Request,
            cancellationToken);
        if (body.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(body.Error);
        }

        DateManagementService service = new(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            notificationWriter,
            auditWriter,
            outboxWriter,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<HelpNeedSummary> result = await service.EditNeedAsync(
            new EditHelpNeedCommand(
                needId,
                body.Value.Instructions,
                body.Value.Capacity,
                body.Value.Status),
            version.Value,
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            need =>
            {
                DateManagementEndpointSupport.WriteEtag(httpContext.Response, need.Version);
                return Results.Ok(DateManagementEndpointSupport.ToResponse(need));
            });
    }
}
