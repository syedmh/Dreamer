using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Signups.Queries;

public sealed class SignupQueryService(
    ISignupRepository signupRepository,
    IMembershipRepository membershipRepository,
    ICurrentActor currentActor)
{
    public async ValueTask<Result<SignupPage>> ListMineAsync(
        string? cursor,
        int? pageSize,
        CancellationToken cancellationToken = default)
    {
        Result<ActiveMembershipContext> actor = await ResolveActorAsync(cancellationToken);
        return actor.IsFailure
            ? Result.Failure<SignupPage>(actor.Error)
            : await signupRepository.ListMineAsync(
                currentActor.OrganizationId,
                currentActor.MembershipId,
                cursor,
                pageSize,
                cancellationToken);
    }

    private ValueTask<Result<ActiveMembershipContext>> ResolveActorAsync(
        CancellationToken cancellationToken) =>
        membershipRepository.ResolveActiveActorAsync(
            currentActor.UserId,
            currentActor.MembershipId,
            currentActor.OrganizationId,
            cancellationToken);
}
