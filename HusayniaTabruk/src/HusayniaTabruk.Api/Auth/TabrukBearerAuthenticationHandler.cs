using System.Security.Claims;
using System.Text.Encodings.Web;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Infrastructure.Identity.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HusayniaTabruk.Api.Auth;

internal sealed class TabrukBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccessTokenCodec accessTokenCodec,
    IClock clock,
    IMembershipRepository membershipRepository)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authorizationHeader = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorizationHeader)
            || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string accessToken = authorizationHeader["Bearer ".Length..].Trim();
        if (!accessTokenCodec.TryRead(accessToken, clock.UtcNow, out AccessTokenPayload? payload)
            || payload is null)
        {
            return AuthenticateResult.Fail("Invalid access token.");
        }

        Result<ActiveMembershipContext> actor = await membershipRepository.ResolveActiveActorAsync(
            payload.UserId,
            payload.MembershipId,
            payload.OrganizationId,
            Context.RequestAborted);
        if (actor.IsFailure)
        {
            return AuthenticateResult.Fail("Invalid access token.");
        }

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, actor.Value.UserId.ToString()),
            new Claim(AuthClaimTypes.UserId, actor.Value.UserId.ToString()),
            new Claim(AuthClaimTypes.MembershipId, actor.Value.MembershipId.ToString()),
            new Claim(AuthClaimTypes.OrganizationId, actor.Value.OrganizationId.ToString()),
            new Claim(AuthClaimTypes.DeviceId, payload.DeviceId.ToString()),
            new Claim(ClaimTypes.Name, actor.Value.DisplayName),
            new Claim(AuthClaimTypes.DisplayName, actor.Value.DisplayName),
            new Claim(AuthClaimTypes.OrganizationName, actor.Value.OrganizationName),
            new Claim(AuthClaimTypes.OrganizationTimeZone, actor.Value.OrganizationTimeZone),
            new Claim(AuthClaimTypes.EligibleAsNamedParticipant, actor.Value.EligibleAsNamedParticipant ? "true" : "false"),
            .. actor.Value.Roles.Select(role => new Claim(ClaimTypes.Role, role.ToString())),
        ];

        ClaimsIdentity identity = new(claims, ApiDefaults.BearerScheme);
        return AuthenticateResult.Success(
            new AuthenticationTicket(
                new ClaimsPrincipal(identity),
                ApiDefaults.BearerScheme));
    }
}
