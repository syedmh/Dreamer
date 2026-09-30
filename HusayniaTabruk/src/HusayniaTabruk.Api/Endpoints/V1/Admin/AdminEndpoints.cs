using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HusayniaTabruk.Api.Endpoints.V1.Admin;

public sealed class AdminEndpoints : IApiEndpoint
{
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
        RouteGroupBuilder admin = endpoints
            .MapGroup("/admin")
            .RequireAuthorization(policy => policy.RequireRole(nameof(OrganizationRole.Admin)));

        admin.MapGet("/members", ListMembersAsync)
            .WithName("ListAdminMembers")
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<CursorPage<AdminMemberResponse>>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapPost("/invitations", IssueInvitationAsync)
            .WithName("IssueMembershipInvitation")
            .Accepts<IssueInvitationRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .Produces<IssueInvitationResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapPut("/members/{id}/food-incharge", AssignFoodInchargeAsync)
            .WithName("AssignFoodIncharge")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status204NoContent, HeaderNames.ETag)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapDelete("/members/{id}/food-incharge", RevokeFoodInchargeAsync)
            .WithName("RevokeFoodIncharge")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status204NoContent, HeaderNames.ETag)
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapPost("/admin-role-requests", ProposeAdministratorRoleChangeAsync)
            .WithName("ProposeAdministratorRoleChange")
            .Accepts<ProposeRoleChangeRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<AdminRoleChangeRequestResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapPost("/admin-role-requests/{id}/approve", ApproveAdministratorRoleChangeAsync)
            .WithName("ApproveAdministratorRoleChange")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<AdminRoleChangeRequestResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status412PreconditionFailed, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");

        admin.MapPost("/members/{id}/disable", DisableMembershipAsync)
            .WithName("DisableMembership")
            .Accepts<ReasonRequest>("application/json")
            .WithRequestBodyLimit(ApplicationLimits.MaximumAdministrativeRequestBytes)
            .WithRateLimit(AdministrativeAccountRateLimit, AdministrativeOrganizationRateLimit)
            .WithOpenApiParameterReference(OpenApiParameterComponents.IfMatch)
            .WithOpenApiParameterReference(OpenApiParameterComponents.StepUpToken)
            .WithOpenApiResponseHeader(StatusCodes.Status200OK, HeaderNames.ETag)
            .Produces<AdminMemberResponse>(StatusCodes.Status200OK)
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

    private static async Task<IResult> ListMembersAsync(
        HttpContext httpContext,
        string? cursor,
        int? pageSize,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<AdminMembersPage> result = await service.ListAsync(
            new ListAdminMembersCommand(cursor, pageSize),
            cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            page =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, page.Version);
                return Results.Ok(
                    new CursorPage<AdminMemberResponse>(
                        page.Items.Select(item => item.ToResponse()).ToArray(),
                        page.NextCursor));
            });
    }

    private static async Task<IResult> IssueInvitationAsync(
        HttpContext httpContext,
        [FromBody] IssueInvitationRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<TabrukAuthOptions> authOptions,
        CancellationToken cancellationToken)
    {
        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<IssuedMembershipInvitation> result = await service.IssueInvitationAsync(
            new IssueMembershipInvitationCommand(
                request.Email ?? string.Empty,
                request.ExpiresInHours),
            stepUpToken.Value,
            cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            invitation => Results.Ok(
                new IssueInvitationResponse(
                    AdminEndpointInputs.BuildInvitationUrl(authOptions.Value, invitation.InvitationToken),
                    invitation.ExpiresAt)));
    }

    private static async Task<IResult> AssignFoodInchargeAsync(
        HttpContext httpContext,
        MembershipId id,
        [FromBody] ReasonRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> expectedVersion = AdminEndpointInputs.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return AdminEndpointResults.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.IfMatchField] = ["A valid If-Match header is required."],
                });
        }

        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<long> result = await service.AssignFoodInchargeAsync(
            new AssignFoodInchargeCommand(id, request.Reason ?? string.Empty),
            expectedVersion.Value,
            stepUpToken.Value,
            cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            version =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, version);
                return Results.NoContent();
            });
    }

    private static async Task<IResult> RevokeFoodInchargeAsync(
        HttpContext httpContext,
        MembershipId id,
        [FromBody] ReasonRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> expectedVersion = AdminEndpointInputs.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return AdminEndpointResults.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.IfMatchField] = ["A valid If-Match header is required."],
                });
        }

        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<long> result = await service.RevokeFoodInchargeAsync(
            new RevokeFoodInchargeCommand(id, request.Reason ?? string.Empty),
            expectedVersion.Value,
            stepUpToken.Value,
            cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            version =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, version);
                return Results.NoContent();
            });
    }

    private static async Task<IResult> ProposeAdministratorRoleChangeAsync(
        HttpContext httpContext,
        [FromBody] ProposeRoleChangeRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<MembershipId> targetMembershipId =
            AdminEndpointInputs.ParseMembershipId(request.TargetMembershipId);
        if (targetMembershipId.IsFailure)
        {
            return AdminEndpointResults.Problem(
                targetMembershipId.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.TargetMembershipIdField] = ["A valid target membership ID is required."],
                });
        }

        Result<long> expectedVersion = AdminEndpointInputs.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return AdminEndpointResults.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.IfMatchField] = ["A valid If-Match header is required."],
                });
        }

        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<VersionedResult<AdminRoleChangeRequestSummary>> result =
            await service.ProposeAdministratorRoleChangeAsync(
                new ProposeAdministratorRoleChangeCommand(
                    targetMembershipId.Value,
                    request.Action,
                    request.Reason ?? string.Empty),
                expectedVersion.Value,
                stepUpToken.Value,
                cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            value =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, value.Version);
                return Results.Ok(value.Value.ToResponse());
            });
    }

    private static async Task<IResult> ApproveAdministratorRoleChangeAsync(
        HttpContext httpContext,
        RoleChangeRequestId id,
        [FromBody] ReasonRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> expectedVersion = AdminEndpointInputs.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return AdminEndpointResults.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.IfMatchField] = ["A valid If-Match header is required."],
                });
        }

        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<VersionedResult<AdminRoleChangeRequestSummary>> result =
            await service.ApproveAdministratorRoleChangeAsync(
                new ApproveAdministratorRoleChangeCommand(id, request.Reason ?? string.Empty),
                expectedVersion.Value,
                stepUpToken.Value,
                cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            value =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, value.Version);
                return Results.Ok(value.Value.ToResponse());
            });
    }

    private static async Task<IResult> DisableMembershipAsync(
        HttpContext httpContext,
        MembershipId id,
        [FromBody] ReasonRequest request,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        IStepUpVerifier stepUpVerifier,
        IUnitOfWork unitOfWork,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<long> expectedVersion = AdminEndpointInputs.ParseIfMatch(httpContext.Request);
        if (expectedVersion.IsFailure)
        {
            return AdminEndpointResults.Problem(
                expectedVersion.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.IfMatchField] = ["A valid If-Match header is required."],
                });
        }

        Result<StepUpToken> stepUpToken = AdminEndpointInputs.ParseStepUpToken(httpContext.Request);
        if (stepUpToken.IsFailure)
        {
            return AdminEndpointResults.Problem(
                stepUpToken.Error,
                new Dictionary<string, string[]>
                {
                    [AdminEndpointInputs.StepUpField] = ["A valid X-Step-Up-Token header is required."],
                });
        }

        AdministratorMembershipService service = new(
            unitOfWork,
            membershipRepository,
            currentActor,
            stepUpVerifier,
            clock);
        Result<VersionedResult<AdminMemberSummary>> result = await service.DisableMembershipAsync(
            new DisableMembershipCommand(id, request.Reason ?? string.Empty),
            expectedVersion.Value,
            stepUpToken.Value,
            cancellationToken);

        return AdminEndpointResults.FromResult(
            result,
            value =>
            {
                AdminEndpointHeaders.WriteEtag(httpContext.Response, value.Version);
                return Results.Ok(value.Value.ToResponse());
            });
    }

    private sealed record IssueInvitationRequest(
        string Email,
        int ExpiresInHours);

    private sealed record ProposeRoleChangeRequest(
        string TargetMembershipId,
        AdministratorRoleChangeAction Action,
        string Reason);

    private sealed record ReasonRequest(string Reason);
}
