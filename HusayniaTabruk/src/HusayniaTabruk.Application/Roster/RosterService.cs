using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Roster;

public sealed class RosterService(
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    ICurrentActor currentActor)
{
    public async ValueTask<Result<RosterPage>> GetManagedAsync(
        ServiceDateId serviceDateId,
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        Result<ActiveMembershipContext> actor =
            await membershipRepository.ResolveActiveActorAsync(
                currentActor.UserId,
                currentActor.MembershipId,
                currentActor.OrganizationId,
                cancellationToken);
        if (actor.IsFailure)
        {
            return Result.Failure<RosterPage>(actor.Error);
        }

        if (!actor.Value.Roles.Contains(OrganizationRole.FoodIncharge))
        {
            return Result.Failure<RosterPage>(RosterApplicationErrorCodes.Concealed());
        }

        return await signupRepository.GetManagedRosterAsync(
            currentActor.OrganizationId,
            serviceDateId,
            currentActor.MembershipId,
            cursor,
            pageSize,
            cancellationToken);
    }
}
