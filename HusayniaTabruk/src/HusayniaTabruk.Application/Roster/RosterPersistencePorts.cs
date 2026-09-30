using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface ISignupRepository
{
    ValueTask<Result<RosterPage>> GetManagedRosterAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        MembershipId managerMembershipId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default);
}
