using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface IMembershipRepository
{
    ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(
        string invitationToken,
        CancellationToken cancellationToken = default);

    ValueTask<Result> AcceptInvitationAsync(
        string invitationToken,
        MembershipId membershipId,
        string displayName,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken = default);

    ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
        UserId userId,
        CancellationToken cancellationToken = default);

    ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
