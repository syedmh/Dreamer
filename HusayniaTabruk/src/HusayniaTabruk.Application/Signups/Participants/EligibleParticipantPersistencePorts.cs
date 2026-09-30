using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Participants;

public sealed record EligibleSignupParticipantSlice(
    IReadOnlyList<EligibleSignupParticipantSummary> Items,
    bool HasMore);

public interface IEligibleSignupParticipantRepository
{
    ValueTask<Result<EligibleSignupParticipantSlice>> ListEligibleAsync(
        OrganizationId organizationId,
        MembershipId excludedMembershipId,
        int startIndex,
        int pageSize,
        CancellationToken cancellationToken = default);
}
