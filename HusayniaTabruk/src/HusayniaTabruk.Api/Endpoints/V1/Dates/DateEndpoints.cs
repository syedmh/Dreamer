using HusayniaTabruk.Api.Configuration;
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

namespace HusayniaTabruk.Api.Endpoints.V1.Dates;

public sealed class DateEndpoints : IApiEndpoint
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
        RouteGroupBuilder dates = endpoints.MapGroup("/dates");

        dates.MapGet("", ListOpenAsync)
            .WithName("ListOpenServiceDates")
            .WithOpenApiParameterReference(OpenApiParameterComponents.DateScope)
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .Produces<CursorPage<ServiceDateResponse>>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        dates.MapGet("/{dateId}", GetAsync)
            .WithName("GetServiceDate")
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<ServiceDateResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        dates.MapPost("", CreateAsync)
            .WithName("CreateServiceDate")
            .Accepts<CreateServiceDateRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiResponseHeader(StatusCodes.Status201Created, HeaderNames.ETag)
            .Produces<ServiceDateResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        dates.MapPost("/{dateId}/open", OpenAsync)
            .WithName("OpenServiceDate")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<ServiceDateResponse>(StatusCodes.Status200OK)
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

    private static async Task<IResult> ListOpenAsync(
        string? scope,
        string? cursor,
        int? pageSize,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(scope)
            && !string.Equals(scope, "open", StringComparison.OrdinalIgnoreCase))
        {
            return DateEndpointSupport.Problem(
                DateApplicationErrorCodes.Validation("T11 supports only the open date scope."));
        }

        ServiceDateService service = new(
            unitOfWork,
            serviceDateRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<OpenServiceDatesPage> result = await service.ListOpenAsync(
            cursor,
            pageSize,
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            page => Results.Ok(
                new CursorPage<ServiceDateResponse>(
                    page.Items.Select(DateEndpointSupport.ToResponse).ToArray(),
                    page.NextCursor)));
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
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
        Result<ServiceDateSummary> result = await service.GetAsync(dateId, cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                return Results.Ok(DateEndpointSupport.ToResponse(date));
            });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        [FromBody] CreateServiceDateRequest request,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!MembershipId.TryParse(request.ManagerMembershipId, out MembershipId managerMembershipId))
        {
            return DateEndpointSupport.Problem(
                DateApplicationErrorCodes.Validation("A valid manager membership ID is required."),
                new Dictionary<string, string[]>
                {
                    ["managerMembershipId"] = ["A valid manager membership ID is required."],
                });
        }

        ServiceDateService service = new(
            unitOfWork,
            serviceDateRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<ServiceDateSummary> result = await service.CreateAsync(
            new CreateServiceDateCommand(
                request.Title ?? string.Empty,
                request.Instructions ?? string.Empty,
                request.StartsAt,
                request.EndsAt,
                request.CancellationDeadlineAt,
                managerMembershipId),
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                ServiceDateResponse response = DateEndpointSupport.ToResponse(date);
                return Results.Created($"/v1/dates/{response.Id}", response);
            });
    }

    private static async Task<IResult> OpenAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        IMembershipRepository membershipRepository,
        IIdempotencyStore idempotencyStore,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<IdempotencyKey> key = DateEndpointSupport.ParseIdempotencyKey(httpContext.Request);
        if (key.IsFailure)
        {
            return DateEndpointSupport.Problem(
                key.Error,
                new Dictionary<string, string[]>
                {
                    ["idempotencyKey"] = ["A valid Idempotency-Key header is required."],
                });
        }

        ServiceDateService service = new(
            unitOfWork,
            serviceDateRepository,
            membershipRepository,
            idempotencyStore,
            currentActor,
            clock);
        Result<ServiceDateSummary> result = await service.OpenAsync(
            new OpenServiceDateCommand(dateId, key.Value),
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                return Results.Ok(DateEndpointSupport.ToResponse(date));
            });
    }
}
