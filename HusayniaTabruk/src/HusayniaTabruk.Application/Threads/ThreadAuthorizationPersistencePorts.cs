using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public enum ThreadAuthorizationRequirement
{
    ParticipantOrManager = 0,
    ManagingFoodIncharge = 1,
}

public sealed record ThreadOrdinaryAuthorizationContext(
    Membership Actor,
    ServiceDate ServiceDate,
    Signup? ApprovedPrimarySignup);

public sealed record ThreadPrivilegedAuthorizationContext(
    UserId UserId,
    MembershipId MembershipId,
    OrganizationId OrganizationId);

public partial interface IThreadRepository
{
    ValueTask<Result<ThreadOrdinaryAuthorizationContext>> LockOrdinaryAuthorizationAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        ThreadAuthorizationRequirement requirement,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This thread repository does not support transaction-held authorization.");

    ValueTask<Result<ThreadPrivilegedAuthorizationContext>> LockPrivilegedAuthorizationAsync(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This thread repository does not support transaction-held privileged authorization.");
}
