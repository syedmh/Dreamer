using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Api.Auth;

public sealed class ClaimsCurrentActor(IHttpContextAccessor httpContextAccessor) : ICurrentActor
{
    public UserId UserId => Parse(value => UserId.Parse(value, provider: null), AuthClaimTypes.UserId);
    public MembershipId MembershipId => Parse(value => MembershipId.Parse(value, provider: null), AuthClaimTypes.MembershipId);
    public OrganizationId OrganizationId => Parse(value => OrganizationId.Parse(value, provider: null), AuthClaimTypes.OrganizationId);

    private TIdentifier Parse<TIdentifier>(
        Func<string, TIdentifier> parser,
        string claimType)
    {
        string value = httpContextAccessor.HttpContext?.User.FindFirst(claimType)?.Value
            ?? throw new InvalidOperationException(
                $"The authenticated actor is missing the '{claimType}' claim.");
        return parser(value);
    }
}
