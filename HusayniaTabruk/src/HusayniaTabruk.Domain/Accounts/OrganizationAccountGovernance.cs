using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Accounts;

public sealed class OrganizationAccountGovernance
{
    private readonly List<Membership> memberships;
    private readonly List<RoleChangeRequest> roleChangeRequests;

    private OrganizationAccountGovernance(
        OrganizationId organizationId,
        long version,
        AdministratorBootstrapStatus bootstrapStatus,
        DateTimeOffset? bootstrapSealedAt,
        IReadOnlyCollection<Membership> memberships,
        IReadOnlyCollection<RoleChangeRequest> roleChangeRequests)
    {
        OrganizationId = organizationId;
        OriginalVersion = version;
        Version = version;
        BootstrapStatus = bootstrapStatus;
        BootstrapSealedAt = bootstrapSealedAt;
        this.memberships = memberships.Select(membership => membership.DeepCopy()).ToList();
        this.roleChangeRequests = roleChangeRequests.Select(request => request.DeepCopy()).ToList();
    }

    public OrganizationId OrganizationId { get; }
    public long OriginalVersion { get; }
    public long Version { get; private set; }
    public AdministratorBootstrapStatus BootstrapStatus { get; private set; }
    public DateTimeOffset? BootstrapSealedAt { get; private set; }
    public IReadOnlyCollection<Membership> Memberships =>
        Array.AsReadOnly(memberships.Select(membership => membership.DeepCopy()).ToArray());
    public IReadOnlyCollection<RoleChangeRequest> RoleChangeRequests =>
        Array.AsReadOnly(roleChangeRequests.Select(request => request.DeepCopy()).ToArray());

    public static Result<OrganizationAccountGovernance> Rehydrate(
        OrganizationId organizationId,
        long version,
        AdministratorBootstrapStatus bootstrapStatus,
        DateTimeOffset? bootstrapSealedAt,
        IReadOnlyCollection<Membership> memberships,
        IReadOnlyCollection<RoleChangeRequest> roleChangeRequests)
    {
        organizationId.EnsureValid();
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roleChangeRequests);

        if (version < 0
            || !Enum.IsDefined(bootstrapStatus)
            || (bootstrapSealedAt.HasValue && bootstrapSealedAt.Value.Offset != TimeSpan.Zero)
            || (bootstrapStatus == AdministratorBootstrapStatus.Unsealed && bootstrapSealedAt is not null)
            || (bootstrapStatus == AdministratorBootstrapStatus.Sealed && bootstrapSealedAt is null))
        {
            return InvalidState("The account governance version or bootstrap state is invalid.");
        }

        if (memberships.Any(membership => membership is null)
            || memberships.Select(membership => membership.Id).Distinct().Count() != memberships.Count
            || memberships.Any(membership => membership.OrganizationId != organizationId)
            || memberships.Any(membership => !membership.HasValidPersistedState()))
        {
            return InvalidState("Account governance memberships are incomplete or inconsistent.");
        }

        int activeAdministratorCount = memberships.Count(
            membership => membership.Status == MembershipStatus.Active
                && membership.HasRole(OrganizationRole.Admin));
        if ((bootstrapStatus == AdministratorBootstrapStatus.Unsealed
                && memberships.Any(membership => membership.HasRole(OrganizationRole.Admin)))
            || (bootstrapStatus == AdministratorBootstrapStatus.Sealed
                && activeAdministratorCount < 2))
        {
            return InvalidState("The account governance bootstrap state is unreachable.");
        }

        if (roleChangeRequests.Any(request => request is null)
            || roleChangeRequests.Select(request => request.Id).Distinct().Count() != roleChangeRequests.Count
            || roleChangeRequests.Any(request => request.OrganizationId != organizationId))
        {
            return InvalidState("Account governance role change requests are inconsistent.");
        }

        HashSet<MembershipId> membershipIds = memberships.Select(membership => membership.Id).ToHashSet();
        if (roleChangeRequests.Any(
                request => !membershipIds.Contains(request.TargetMembershipId)
                    || !membershipIds.Contains(request.ProposerMembershipId)
                    || (request.ApproverMembershipId.HasValue
                        && !membershipIds.Contains(request.ApproverMembershipId.Value))))
        {
            return InvalidState("Account governance requests reference unknown memberships.");
        }

        return Result.Success(
            new OrganizationAccountGovernance(
                organizationId,
                version,
                bootstrapStatus,
                bootstrapSealedAt,
                memberships,
                roleChangeRequests));
    }

    public Result<AdministratorBootstrapCompleted> BootstrapAdministrators(
        IReadOnlyCollection<Membership> administrators,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(administrators);
        EnsureUtc(now);

        if (BootstrapStatus == AdministratorBootstrapStatus.Sealed)
        {
            return Result.Failure<AdministratorBootstrapCompleted>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.BootstrapSealed,
                    "Administrator bootstrap has already been sealed."));
        }

        List<Membership> ownedAdministrators = [];
        foreach (Membership administrator in administrators)
        {
            Result<Membership> owned = ResolveOwnedMembership(administrator);
            if (owned.IsFailure)
            {
                return Result.Failure<AdministratorBootstrapCompleted>(owned.Error);
            }

            ownedAdministrators.Add(owned.Value);
        }

        Membership[] distinctAdministrators = ownedAdministrators
            .DistinctBy(membership => membership.Id)
            .ToArray();
        if (distinctAdministrators.Length < 2)
        {
            return Result.Failure<AdministratorBootstrapCompleted>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.BootstrapRequiresTwoAdministrators,
                    "Bootstrap requires at least two distinct administrator memberships."));
        }

        if (distinctAdministrators.Any(membership => membership.Status != MembershipStatus.Active))
        {
            return Result.Failure<AdministratorBootstrapCompleted>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.ActiveMembershipRequired,
                    "Bootstrap administrator memberships must be active."));
        }

        if (distinctAdministrators.Any(membership => membership.HasRole(OrganizationRole.Admin)))
        {
            return Result.Failure<AdministratorBootstrapCompleted>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "Bootstrap cannot reassign an existing administrator role."));
        }

        foreach (Membership administrator in distinctAdministrators)
        {
            administrator.GrantAdministrator();
        }

        BootstrapStatus = AdministratorBootstrapStatus.Sealed;
        BootstrapSealedAt = now;
        Version++;
        return Result.Success(
            new AdministratorBootstrapCompleted(
                OrganizationId,
                Array.AsReadOnly(
                    distinctAdministrators.Select(membership => membership.Id).ToArray()),
                now));
    }

    public Result<MembershipDisabled> DisableMembership(
        Membership actor,
        Membership target,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        Result<(Membership Actor, Membership Target)> authorization =
            ResolveAdministratorActorAndTarget(actor, target, requireActiveTarget: false);
        if (authorization.IsFailure)
        {
            return Result.Failure<MembershipDisabled>(authorization.Error);
        }

        Membership ownedActor = authorization.Value.Actor;
        Membership ownedTarget = authorization.Value.Target;
        if (ownedTarget.Status == MembershipStatus.Active
            && ownedTarget.HasRole(OrganizationRole.Admin)
            && ActiveAdministratorCount <= 2)
        {
            return Result.Failure<MembershipDisabled>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.MinimumAdministratorsRequired,
                    "Normal governance must retain at least two active administrators."));
        }

        Result<MembershipDisabled> result = ownedTarget.Disable(ownedActor.Id, now);
        if (result.IsSuccess)
        {
            Version++;
        }

        return result;
    }

    public Result<FoodInchargeAssigned> AssignFoodIncharge(
        Membership actor,
        Membership target,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        Result<(Membership Actor, Membership Target)> authorization =
            ResolveAdministratorActorAndTarget(actor, target, requireActiveTarget: true);
        if (authorization.IsFailure)
        {
            return Result.Failure<FoodInchargeAssigned>(authorization.Error);
        }

        Membership ownedActor = authorization.Value.Actor;
        Membership ownedTarget = authorization.Value.Target;
        Result<FoodInchargeAssigned> result = ownedTarget.AssignFoodIncharge(ownedActor.Id, now);
        if (result.IsSuccess)
        {
            Version++;
        }

        return result;
    }

    public Result<FoodInchargeRevoked> RevokeFoodIncharge(
        Membership actor,
        Membership target,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        Result<(Membership Actor, Membership Target)> authorization =
            ResolveAdministratorActorAndTarget(actor, target, requireActiveTarget: true);
        if (authorization.IsFailure)
        {
            return Result.Failure<FoodInchargeRevoked>(authorization.Error);
        }

        Membership ownedActor = authorization.Value.Actor;
        Membership ownedTarget = authorization.Value.Target;
        Result<FoodInchargeRevoked> result = ownedTarget.RevokeFoodIncharge(ownedActor.Id, now);
        if (result.IsSuccess)
        {
            Version++;
        }

        return result;
    }

    public Result<RoleChangeRequest> ProposeAdministratorRoleChange(
        RoleChangeRequestId requestId,
        AdministratorRoleChangeAction action,
        Membership proposer,
        Membership target,
        string reason,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        if (!Enum.IsDefined(action))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAdministratorRoleChangeAction,
                    "The administrator role change action is invalid."));
        }

        if (roleChangeRequests.Any(request => request.Id == requestId))
        {
            return InvalidState<RoleChangeRequest>("The role change request ID is already owned by this aggregate.");
        }

        Result<(Membership Actor, Membership Target)> authorization =
            ResolveAdministratorActorAndTarget(proposer, target, requireActiveTarget: true);
        if (authorization.IsFailure)
        {
            return Result.Failure<RoleChangeRequest>(authorization.Error);
        }

        Membership ownedProposer = authorization.Value.Actor;
        Membership ownedTarget = authorization.Value.Target;
        Result<RoleChangeRequest> result =
            RoleChangeRequest.Propose(requestId, action, ownedProposer, ownedTarget, reason, now);
        if (result.IsSuccess)
        {
            roleChangeRequests.Add(result.Value);
            Version++;
            return Result.Success(result.Value.DeepCopy());
        }

        return result;
    }

    public Result<AdministratorRoleChangeApproved> ApproveAdministratorRoleChange(
        RoleChangeRequest request,
        Membership proposer,
        Membership approver,
        Membership target,
        DateTimeOffset now)
    {
        EnsureUtc(now);
        Result<RoleChangeRequest> ownedRequest = ResolveOwnedRequest(request);
        if (ownedRequest.IsFailure)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(ownedRequest.Error);
        }

        Result<Membership> proposerOwned = ResolveOwnedMembership(proposer);
        if (proposerOwned.IsFailure)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(proposerOwned.Error);
        }

        RoleChangeRequest authoritativeRequest = ownedRequest.Value;
        Membership ownedProposer = proposerOwned.Value;
        if (ownedProposer.Id != authoritativeRequest.ProposerMembershipId)
        {
            return InvalidState<AdministratorRoleChangeApproved>(
                "The supplied proposer does not match the stored role change request proposer.");
        }

        if (ownedProposer.Status != MembershipStatus.Active)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.ActiveMembershipRequired,
                    "The proposer must still be active."));
        }

        if (!ownedProposer.HasRole(OrganizationRole.Admin))
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.AdministratorRoleRequired,
                    "The proposer must still be an administrator."));
        }

        Result<(Membership Actor, Membership Target)> authorization =
            ResolveAdministratorActorAndTarget(approver, target, requireActiveTarget: true);
        if (authorization.IsFailure)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(authorization.Error);
        }

        Membership ownedApprover = authorization.Value.Actor;
        Membership ownedTarget = authorization.Value.Target;
        if (ownedTarget.Id != authoritativeRequest.TargetMembershipId)
        {
            return InvalidState<AdministratorRoleChangeApproved>(
                "The supplied target does not match the stored role change request target.");
        }

        if (!Enum.IsDefined(authoritativeRequest.Action))
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAdministratorRoleChangeAction,
                    "The administrator role change action is invalid."));
        }

        if (authoritativeRequest.Action == AdministratorRoleChangeAction.Revoke
            && ActiveAdministratorCount <= 2)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.MinimumAdministratorsRequired,
                    "Normal governance must retain at least two active administrators."));
        }

        Result<AdministratorRoleChangeApproved> result =
            authoritativeRequest.Approve(ownedApprover, ownedTarget, now);
        if (result.IsSuccess)
        {
            Version++;
        }

        return result;
    }

    private int ActiveAdministratorCount =>
        memberships.Count(
            membership => membership.Status == MembershipStatus.Active
                && membership.HasRole(OrganizationRole.Admin));

    private Result<(Membership Actor, Membership Target)> ResolveAdministratorActorAndTarget(
        Membership actor,
        Membership target,
        bool requireActiveTarget)
    {
        Result<Membership> actorOwned = ResolveOwnedMembership(actor);
        if (actorOwned.IsFailure)
        {
            return Result.Failure<(Membership, Membership)>(actorOwned.Error);
        }

        Result<Membership> targetOwned = ResolveOwnedMembership(target);
        if (targetOwned.IsFailure)
        {
            return Result.Failure<(Membership, Membership)>(targetOwned.Error);
        }

        Membership ownedActor = actorOwned.Value;
        Membership ownedTarget = targetOwned.Value;
        if (ownedActor.Id == ownedTarget.Id)
        {
            return Result.Failure<(Membership, Membership)>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleParticipantsNotDistinct,
                    "A governance actor cannot target their own membership."));
        }

        if (ownedActor.Status != MembershipStatus.Active
            || (requireActiveTarget && ownedTarget.Status != MembershipStatus.Active))
        {
            return Result.Failure<(Membership, Membership)>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.ActiveMembershipRequired,
                    "Governance requires active memberships."));
        }

        if (!ownedActor.HasRole(OrganizationRole.Admin))
        {
            return Result.Failure<(Membership, Membership)>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.AdministratorRoleRequired,
                    "The governance actor must be an active administrator."));
        }

        return Result.Success((ownedActor, ownedTarget));
    }

    private Result<Membership> ResolveOwnedMembership(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        if (membership.OrganizationId != OrganizationId)
        {
            return Result.Failure<Membership>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.OrganizationMismatch,
                    "The membership belongs to another organization."));
        }

        Membership? owned = memberships.SingleOrDefault(candidate => candidate.Id == membership.Id);
        if (owned is null)
        {
            return Result.Failure<Membership>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountGovernanceState,
                    "The membership is not owned by this aggregate."));
        }

        return Result.Success(owned);
    }

    private Result<RoleChangeRequest> ResolveOwnedRequest(RoleChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.OrganizationId != OrganizationId)
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.OrganizationMismatch,
                    "The role change request belongs to another organization."));
        }

        RoleChangeRequest? owned = roleChangeRequests.SingleOrDefault(candidate => candidate.Id == request.Id);
        if (owned is null)
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountGovernanceState,
                    "The role change request is not owned by this aggregate."));
        }

        return Result.Success(owned);
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }

    private static Result<OrganizationAccountGovernance> InvalidState(string message) =>
        InvalidState<OrganizationAccountGovernance>(message);

    private static Result<T> InvalidState<T>(string message) =>
        Result.Failure<T>(
            AccountErrorCodes.Validation(
                AccountErrorCodes.InvalidAccountGovernanceState,
                message));
}
