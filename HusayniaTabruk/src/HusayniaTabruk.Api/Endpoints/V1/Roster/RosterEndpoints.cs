using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Endpoints.V1.Roster;

public sealed class RosterEndpoints : IApiEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/dates/{dateId}/roster", GetAsync)
            .WithName("GetManagedRoster")
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .Produces<RosterResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> GetAsync(
        ServiceDateId dateId,
        string? cursor,
        int? pageSize,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        CancellationToken cancellationToken)
    {
        RosterService service = new(
            signupRepository,
            membershipRepository,
            currentActor);
        Result<RosterPage> result = await service.GetManagedAsync(
            dateId,
            cursor,
            pageSize,
            cancellationToken);
        return SignupEndpointSupport.FromResult(
            result,
            roster => Results.Ok(
                new RosterResponse(
                    roster.ServiceDateId.ToString(),
                    roster.Items.Select(SignupEndpointSupport.ToResponse).ToArray(),
                    roster.NextCursor)));
    }
}

public sealed record RosterResponse(
    string ServiceDateId,
    IReadOnlyList<SignupResponse> Items,
    string? NextCursor);
