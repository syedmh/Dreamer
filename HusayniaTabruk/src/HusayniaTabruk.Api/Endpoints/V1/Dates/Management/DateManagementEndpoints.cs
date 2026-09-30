using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Dates;
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

namespace HusayniaTabruk.Api.Endpoints.V1.Dates.Management;

public sealed class DateManagementEndpoints : IApiEndpoint
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
        endpoints.MapPatch("/dates/{dateId}", EditAsync)
            .WithName("EditServiceDate")
            .Accepts<PatchServiceDateRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
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

        endpoints.MapPost("/dates/{dateId}/close", CloseAsync)
            .WithName("CloseServiceDate")
            .Accepts<CloseServiceDateRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
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

        endpoints.MapPost("/dates/{dateId}/cancel", CancelAsync)
            .WithName("CancelServiceDate")
            .Accepts<CancelServiceDateRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AccountMutationRateLimit, OrganizationMutationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
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

    private static async Task<IResult> EditAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
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

        Result<ParsedDatePatch> body = await DateManagementEndpointSupport.ParseDatePatchAsync(
            httpContext.Request,
            cancellationToken);
        if (body.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(body.Error);
        }

        DateManagementService service = CreateService(
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
        Result<ServiceDateSummary> result = await service.EditDateAsync(
            new EditServiceDateCommand(
                dateId,
                body.Value.Title,
                body.Value.Instructions,
                body.Value.StartsAt,
                body.Value.EndsAt,
                body.Value.CancellationDeadlineAt),
            version.Value,
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateManagementEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                return Results.Ok(DateManagementEndpointSupport.ToResponse(date));
            });
    }

    private static async Task<IResult> CloseAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
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
        Result<(IdempotencyKey Key, long Version)> common =
            ParseMutationHeaders(httpContext.Request);
        if (common.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(common.Error);
        }

        Result<string?> reason = await DateManagementEndpointSupport.ParseReasonAsync(
            httpContext.Request,
            cancellationToken);
        if (reason.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(reason.Error);
        }

        DateManagementService service = CreateService(
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
        Result<ServiceDateSummary> result = await service.CloseAsync(
            new CloseServiceDateCommand(dateId, reason.Value, common.Value.Key),
            common.Value.Version,
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateManagementEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                return Results.Ok(DateManagementEndpointSupport.ToResponse(date));
            });
    }

    private static async Task<IResult> CancelAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
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
        Result<(IdempotencyKey Key, long Version)> common =
            ParseMutationHeaders(httpContext.Request);
        if (common.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(common.Error);
        }

        Result<string?> reason = await DateManagementEndpointSupport.ParseReasonAsync(
            httpContext.Request,
            cancellationToken);
        if (reason.IsFailure)
        {
            return DateManagementEndpointSupport.Problem(reason.Error);
        }

        DateManagementService service = CreateService(
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
        Result<ServiceDateSummary> result = await service.CancelAsync(
            new CancelServiceDateCommand(dateId, reason.Value, common.Value.Key),
            common.Value.Version,
            cancellationToken);
        return DateEndpointSupport.FromResult(
            result,
            date =>
            {
                DateManagementEndpointSupport.WriteEtag(httpContext.Response, date.Version);
                return Results.Ok(DateManagementEndpointSupport.ToResponse(date));
            });
    }

    private static Result<(IdempotencyKey Key, long Version)> ParseMutationHeaders(HttpRequest request)
    {
        Result<IdempotencyKey> key = DateManagementEndpointSupport.ParseIdempotencyKey(request);
        if (key.IsFailure)
        {
            return Result.Failure<(IdempotencyKey, long)>(key.Error);
        }

        Result<long> version = DateManagementEndpointSupport.ParseIfMatch(request);
        return version.IsFailure
            ? Result.Failure<(IdempotencyKey, long)>(version.Error)
            : Result.Success((key.Value, version.Value));
    }

    private static DateManagementService CreateService(
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
        IClock clock) =>
        new(
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
}
