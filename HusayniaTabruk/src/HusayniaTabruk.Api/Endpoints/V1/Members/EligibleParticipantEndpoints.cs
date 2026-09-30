using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Signups.Participants;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Api.Endpoints.V1.Members;

public sealed class EligibleParticipantEndpoints : IApiEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/members/eligible-participants",
                ListEligibleAsync)
            .WithName("ListEligibleSignupParticipants")
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .Produces<CursorPage<EligibleSignupParticipantResponse>>(
                StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(
                StatusCodes.Status400BadRequest,
                "application/problem+json")
            .Produces<ApiProblemDetails>(
                StatusCodes.Status401Unauthorized,
                "application/problem+json")
            .Produces<ApiProblemDetails>(
                StatusCodes.Status503ServiceUnavailable,
                "application/problem+json");
    }

    private static async Task<IResult> ListEligibleAsync(
        string? cursor,
        int? pageSize,
        IEligibleSignupParticipantRepository participantRepository,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        CancellationToken cancellationToken)
    {
        EligibleParticipantQueryService service = new(
            participantRepository,
            membershipRepository,
            currentActor);
        Result<EligibleSignupParticipantPage> result = await service.ListAsync(
            cursor,
            pageSize,
            cancellationToken);
        return SignupEndpointSupport.FromResult(
            result,
            page => Results.Ok(
                new CursorPage<EligibleSignupParticipantResponse>(
                    page.Items
                        .Select(
                            participant => new EligibleSignupParticipantResponse(
                                participant.MembershipId.Value,
                                participant.DisplayName))
                        .ToArray(),
                    page.NextCursor)));
    }
}

public sealed record EligibleSignupParticipantResponse(
    Guid MembershipId,
    string DisplayName);
