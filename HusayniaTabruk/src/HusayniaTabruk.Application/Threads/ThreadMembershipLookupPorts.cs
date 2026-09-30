using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public partial interface IMembershipRepository
{
    ValueTask<Result<IReadOnlyDictionary<MembershipId, string>>> GetThreadSenderDisplaysAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<MembershipId> membershipIds,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This membership repository does not support thread sender display lookup.");
}
