using System.Security.Claims;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;

namespace HusayniaTabruk.Api.Endpoints.V1.MemberSelf;

public sealed class MeEndpoints : IApiEndpoint
{
    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(user.ToMeResponse()))
            .WithName("GetCurrentActor")
            .Produces<MeResponse>(StatusCodes.Status200OK)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, "application/problem+json")
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json");
    }
}
