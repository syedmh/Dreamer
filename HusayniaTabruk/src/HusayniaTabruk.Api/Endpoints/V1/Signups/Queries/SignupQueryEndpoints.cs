using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Endpoints.V1.Signups.Submit;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Api.OpenApi;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;

namespace HusayniaTabruk.Api.Endpoints.V1.Signups.Queries;

public sealed class SignupQueryEndpoints : IApiEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/signups/mine", ListMineAsync)
            .WithName("ListMySignups")
            .WithOpenApiParameterReference(OpenApiParameterComponents.Cursor)
            .WithOpenApiParameterReference(OpenApiParameterComponents.PageSize)
            .Produces<CursorPage<SignupResponse>>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }

    private static async Task<IResult> ListMineAsync(
        string? cursor,
        int? pageSize,
        ISignupRepository signupRepository,
        IMembershipRepository membershipRepository,
        ICurrentActor currentActor,
        CancellationToken cancellationToken)
    {
        SignupQueryService service = new(
            signupRepository,
            membershipRepository,
            currentActor);
        Result<SignupPage> result = await service.ListMineAsync(
            cursor,
            pageSize,
            cancellationToken);
        return SignupEndpointSupport.FromResult(
            result,
            page => Results.Ok(
                new CursorPage<SignupResponse>(
                    page.Items.Select(SignupEndpointSupport.ToResponse).ToArray(),
                    page.NextCursor)));
    }
}
