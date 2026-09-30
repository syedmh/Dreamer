using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface IMembershipRepository
{
    ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    ValueTask<Result> SaveGovernanceAsync(
        OrganizationAccountGovernance aggregate,
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt,
        MembershipAdministrationPersistenceEffects effects,
        CancellationToken cancellationToken = default);

    ValueTask<Result> IssueInvitationAsync(
        IssueMembershipInvitationPersistenceRequest request,
        CancellationToken cancellationToken = default);
}
