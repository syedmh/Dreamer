using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Threads;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Threads;

public sealed class ThreadEndpoints : IApiEndpoint
{
    private static readonly RateLimitRule PostBurstRateLimit =
        new(ApiRateLimitPartitions.Account, 3, TimeSpan.FromSeconds(10));

    private static readonly RateLimitRule PostMinuteRateLimit =
        new(ApiRateLimitPartitions.Account, 10, TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule PostHourRateLimit =
        new(ApiRateLimitPartitions.Account, 60, TimeSpan.FromHours(1));

    private static readonly RateLimitRule PostOrganizationRateLimit =
        new(ApiRateLimitPartitions.Organization, 300, TimeSpan.FromHours(1));

    private static readonly RateLimitRule ReportHourRateLimit =
        new(ApiRateLimitPartitions.Account, 5, TimeSpan.FromHours(1));

    private static readonly RateLimitRule ReportDayRateLimit =
        new(ApiRateLimitPartitions.Account, 20, TimeSpan.FromDays(1));

    private static readonly RateLimitRule ReportOrganizationRateLimit =
        new(ApiRateLimitPartitions.Organization, 100, TimeSpan.FromDays(1));

    private static readonly RateLimitRule AdministrativeAccountRateLimit =
        new(
            ApiRateLimitPartitions.Account,
            ApplicationLimits.AdministrativeMutationsPerMinutePerAccount,
            TimeSpan.FromMinutes(1));

    private static readonly RateLimitRule AdministrativeOrganizationRateLimit =
        new(
            ApiRateLimitPartitions.Organization,
            ApplicationLimits.AdministrativeMutationsPerHourPerOrganization,
            TimeSpan.FromHours(1));

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/dates/{dateId}/thread/messages", ListAsync)
            .WithName("ListThreadMessages")
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<ThreadMessagePageResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapPost("/dates/{dateId}/thread/messages", PostAsync)
            .WithName("PostThreadMessage")
            .Accepts<PostThreadMessageRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumMessageRequestBytes)
            .WithRateLimit(
                PostBurstRateLimit,
                PostMinuteRateLimit,
                PostHourRateLimit,
                PostOrganizationRateLimit)
            .WithOpenApiParameterReference("idempotencyKey")
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status201Created, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces<ThreadMessageResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapPost("/dates/{dateId}/thread/messages/{messageId}/report", ReportAsync)
            .WithName("ReportThreadMessage")
            .Accepts<ReportThreadMessageRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumReportRequestBytes)
            .WithRateLimit(
                ReportHourRateLimit,
                ReportDayRateLimit,
                ReportOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status202Accepted, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces(StatusCodes.Status202Accepted)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapPost("/dates/{dateId}/thread/messages/{messageId}/hide", HideAsync)
            .WithName("HideThreadMessage")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces<ThreadMessageResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        endpoints.MapPost("/dates/{dateId}/thread/lock", LockAsync)
            .WithName("LockThread")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces<ThreadStateResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        RouteGroupBuilder admin = endpoints
            .MapGroup("/admin")
            .RequireAuthorization(policy => policy.RequireRole(nameof(OrganizationRole.Admin)));

        admin.MapPost("/moderation/thread-reads", ReadPrivilegedAsync)
            .WithName("ReadPrivilegedThreadMessages")
            .Accepts<PrivilegedThreadReadRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status429TooManyRequests, ApiDefaults.RetryAfterHeaderName)
            .Produces<PrivilegedThreadMessagePageResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        string? cursor,
        int? pageSize,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<ThreadMessagePage> result =
            await service.ListAsync(dateId, cursor, pageSize, cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            page =>
            {
                ThreadEndpointSupport.WriteEtag(httpContext.Response, page.Version);
                return Results.Ok(ToResponse(page));
            });
    }

    private static async Task<IResult> PostAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<(IdempotencyKey Key, long Version)> headers = ParsePostHeaders(httpContext.Request);
        if (headers.IsFailure)
        {
            return HeaderProblem(headers.Error);
        }

        Result<ParsedPostThreadMessageRequest> request =
            await ThreadEndpointSupport.ParsePostAsync(httpContext.Request, cancellationToken);
        if (request.IsFailure)
        {
            return ThreadEndpointSupport.Problem(request.Error);
        }

        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<VersionedThreadMessage> result = await service.PostAsync(
            new PostThreadMessageCommand(dateId, request.Value.Body, headers.Value.Key),
            headers.Value.Version,
            cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            posted =>
            {
                ThreadEndpointSupport.WriteEtag(httpContext.Response, posted.Version);
                ThreadMessageResponse response = ToResponse(posted.Message);
                return Results.Created(
                    $"{ApiDefaults.BasePath}/dates/{dateId}/thread/messages/{response.Id}",
                    response);
            });
    }

    private static async Task<IResult> ReportAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        MessageId messageId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> version = ThreadEndpointSupport.ParseIfMatch(httpContext.Request);
        if (version.IsFailure)
        {
            return HeaderProblem(version.Error);
        }

        Result<ParsedReportThreadMessageRequest> request =
            await ThreadEndpointSupport.ParseReportAsync(httpContext.Request, cancellationToken);
        if (request.IsFailure)
        {
            return ThreadEndpointSupport.Problem(request.Error);
        }

        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<VersionedThreadReport> result = await service.ReportAsync(
            new ReportThreadMessageCommand(
                dateId,
                messageId,
                request.Value.Reason,
                request.Value.Comment),
            version.Value,
            cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            report =>
            {
                ThreadEndpointSupport.WriteEtag(httpContext.Response, report.Version);
                return Results.Accepted();
            });
    }

    private static async Task<IResult> HideAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        MessageId messageId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> version = ThreadEndpointSupport.ParseIfMatch(httpContext.Request);
        if (version.IsFailure)
        {
            return HeaderProblem(version.Error);
        }

        Result<string> reason =
            await ThreadEndpointSupport.ParseReasonAsync(httpContext.Request, cancellationToken);
        if (reason.IsFailure)
        {
            return ThreadEndpointSupport.Problem(reason.Error);
        }

        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<VersionedThreadMessage> result = await service.HideAsync(
            new HideThreadMessageCommand(dateId, messageId, reason.Value),
            version.Value,
            cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            hidden =>
            {
                ThreadEndpointSupport.WriteEtag(httpContext.Response, hidden.Version);
                return Results.Ok(ToResponse(hidden.Message));
            });
    }

    private static async Task<IResult> LockAsync(
        HttpContext httpContext,
        ServiceDateId dateId,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> version = ThreadEndpointSupport.ParseIfMatch(httpContext.Request);
        if (version.IsFailure)
        {
            return HeaderProblem(version.Error);
        }

        Result<string> reason =
            await ThreadEndpointSupport.ParseReasonAsync(httpContext.Request, cancellationToken);
        if (reason.IsFailure)
        {
            return ThreadEndpointSupport.Problem(reason.Error);
        }

        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<VersionedThreadState> result = await service.LockAsync(
            new LockThreadCommand(dateId, reason.Value),
            version.Value,
            cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            state =>
            {
                ThreadEndpointSupport.WriteEtag(httpContext.Response, state.Version);
                return Results.Ok(ToResponse(state));
            });
    }

    private static async Task<IResult> ReadPrivilegedAsync(
        HttpContext httpContext,
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<StepUpToken> stepUpToken =
            ThreadEndpointSupport.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return ThreadEndpointSupport.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    ["stepUpToken"] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        Result<ParsedPrivilegedThreadReadRequest> request =
            await ThreadEndpointSupport.ParsePrivilegedReadAsync(
                httpContext.Request,
                cancellationToken);
        if (request.IsFailure)
        {
            return ThreadEndpointSupport.Problem(request.Error);
        }

        ThreadService service = CreateService(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);
        Result<PrivilegedThreadMessagePage> result = await service.ReadPrivilegedAsync(
            new ReadPrivilegedThreadPageCommand(
                request.Value.ServiceDateId,
                request.Value.Reason,
                request.Value.Purpose,
                request.Value.CaseId,
                request.Value.Cursor,
                request.Value.PageSize),
            stepUpToken.Value,
            cancellationToken);
        return ThreadEndpointSupport.FromResult(
            result,
            page => Results.Ok(ToResponse(page)));
    }

    private static ThreadService CreateService(
        IUnitOfWork unitOfWork,
        IServiceDateRepository serviceDateRepository,
        ISignupRepository signupRepository,
        IThreadRepository threadRepository,
        IMembershipRepository membershipRepository,
        IPrivilegedAccessWriter privilegedAccessWriter,
        IStepUpVerifier stepUpVerifier,
        ICurrentActor currentActor,
        IClock clock) =>
        new(
            unitOfWork,
            serviceDateRepository,
            signupRepository,
            threadRepository,
            membershipRepository,
            privilegedAccessWriter,
            stepUpVerifier,
            currentActor,
            clock);

    private static Result<(IdempotencyKey Key, long Version)> ParsePostHeaders(HttpRequest request)
    {
        Result<IdempotencyKey> key = ThreadEndpointSupport.ParseIdempotencyKey(request);
        if (key.IsFailure)
        {
            return Result.Failure<(IdempotencyKey, long)>(key.Error);
        }

        Result<long> version = ThreadEndpointSupport.ParseIfMatch(request);
        return version.IsSuccess
            ? Result.Success((key.Value, version.Value))
            : Result.Failure<(IdempotencyKey, long)>(version.Error);
    }

    private static IResult HeaderProblem(DomainError error)
    {
        string field = error.Message.Contains("Idempotency", StringComparison.Ordinal)
            ? "idempotencyKey"
            : "ifMatch";
        return ThreadEndpointSupport.Problem(
            error,
            new Dictionary<string, string[]>
            {
                [field] =
                [
                    field == "idempotencyKey"
                        ? "A valid Idempotency-Key header is required."
                        : "A valid If-Match header is required.",
                ],
            });
    }

    private static ThreadMessageResponse ToResponse(ThreadMessageSummary message) =>
        new(
            message.Id.ToString(),
            message.SenderDisplayName,
            message.Body,
            message.Visibility,
            message.CreatedAt);

    private static ThreadMessagePageResponse ToResponse(ThreadMessagePage page) =>
        new(
            page.ServiceDateId.ToString(),
            page.Status,
            page.LockedAt,
            page.Items.Select(ToResponse).ToArray(),
            page.NextCursor);

    private static ThreadStateResponse ToResponse(VersionedThreadState state) =>
        new(
            state.ServiceDateId.ToString(),
            state.Status,
            state.LockedAt);

    private static PrivilegedThreadMessagePageResponse ToResponse(
        PrivilegedThreadMessagePage page) =>
        new(
            page.ThreadId.ToString(),
            page.ServiceDateId.ToString(),
            page.Status,
            page.LockedAt,
            page.Items
                .Select(message => new PrivilegedThreadMessageResponse(
                    message.Id.ToString(),
                    message.SenderDisplayName,
                    message.Body,
                    message.Visibility,
                    message.CreatedAt,
                    message.HiddenAt))
                .ToArray(),
            page.NextCursor);
}

public sealed record ThreadMessageResponse(
    string Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt);

public sealed record ThreadMessagePageResponse(
    string ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    IReadOnlyCollection<ThreadMessageResponse> Items,
    string? NextCursor);

public sealed record ThreadStateResponse(
    string ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt);

public sealed record PrivilegedThreadMessageResponse(
    string Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HiddenAt);

public sealed record PrivilegedThreadMessagePageResponse(
    string ThreadId,
    string ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    IReadOnlyCollection<PrivilegedThreadMessageResponse> Items,
    string? NextCursor);
