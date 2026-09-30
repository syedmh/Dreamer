using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Accounts;

public enum AdministratorBootstrapStatus
{
    Unsealed,
    Sealed,
}

public sealed record AdministratorBootstrapCompleted(
    OrganizationId OrganizationId,
    IReadOnlyList<MembershipId> AdministratorMembershipIds,
    DateTimeOffset OccurredAt);
