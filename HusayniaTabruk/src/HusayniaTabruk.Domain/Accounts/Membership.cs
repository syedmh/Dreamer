using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Accounts;

public sealed class Membership
{
    private readonly HashSet<OrganizationRole> activeRoles = [];

    private Membership(
        MembershipId id,
        OrganizationId organizationId,
        UserId userId,
        string displayName,
        bool eligibleAsNamedParticipant)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        UserId = userId.EnsureValid();
        DisplayName = displayName;
        EligibleAsNamedParticipant = eligibleAsNamedParticipant;
        Status = MembershipStatus.Invited;
    }

    public MembershipId Id { get; }
    public OrganizationId OrganizationId { get; }
    public UserId UserId { get; }
    public string DisplayName { get; }
    public MembershipStatus Status { get; private set; }
    public bool EligibleAsNamedParticipant { get; }
    public bool IsEligibleAsNamedParticipant =>
        Status == MembershipStatus.Active && EligibleAsNamedParticipant;
    public IReadOnlyCollection<OrganizationRole> ActiveRoles =>
        Array.AsReadOnly(activeRoles.ToArray());

    public static Result<Membership> Invite(
        MembershipId id,
        OrganizationId organizationId,
        UserId userId,
        string displayName,
        bool eligibleAsNamedParticipant)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Result.Failure<Membership>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountInput,
                    "A membership display name is required."));
        }

        return Result.Success(
            new Membership(id, organizationId, userId, displayName.Trim(), eligibleAsNamedParticipant));
    }

    public static Result<Membership> Rehydrate(
        MembershipId id,
        OrganizationId organizationId,
        UserId userId,
        string displayName,
        MembershipStatus status,
        bool eligibleAsNamedParticipant,
        IReadOnlyCollection<OrganizationRole> activeRoles)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        userId.EnsureValid();
        ArgumentNullException.ThrowIfNull(activeRoles);

        if (string.IsNullOrWhiteSpace(displayName)
            || !Enum.IsDefined(status)
            || activeRoles.Any(role => !Enum.IsDefined(role))
            || activeRoles.Distinct().Count() != activeRoles.Count
            || (status != MembershipStatus.Active && activeRoles.Count != 0))
        {
            return Result.Failure<Membership>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountInput,
                    "The membership state is invalid."));
        }

        Membership membership = new(
            id,
            organizationId,
            userId,
            displayName.Trim(),
            eligibleAsNamedParticipant)
        {
            Status = status,
        };
        membership.activeRoles.UnionWith(activeRoles);
        return Result.Success(membership);
    }

    public bool HasRole(OrganizationRole role) => activeRoles.Contains(role);

    public Result<MembershipActivated> Activate(DateTimeOffset occurredAt)
    {
        EnsureUtc(occurredAt);

        if (Status != MembershipStatus.Invited)
        {
            return Result.Failure<MembershipActivated>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidMembershipTransition,
                    $"A {Status} membership cannot be activated."));
        }

        Status = MembershipStatus.Active;
        return Result.Success(new MembershipActivated(Id, occurredAt));
    }

    internal Result<MembershipDisabled> Disable(MembershipId actorMembershipId, DateTimeOffset occurredAt)
    {
        actorMembershipId.EnsureValid();
        EnsureUtc(occurredAt);

        if (Status == MembershipStatus.Disabled)
        {
            return Result.Failure<MembershipDisabled>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidMembershipTransition,
                    "The membership is already disabled."));
        }

        IReadOnlyCollection<OrganizationRole> revokedRoles =
            Array.AsReadOnly(activeRoles.Order().ToArray());
        activeRoles.Clear();
        Status = MembershipStatus.Disabled;

        return Result.Success(new MembershipDisabled(Id, actorMembershipId, revokedRoles, occurredAt));
    }

    internal Result<FoodInchargeAssigned> AssignFoodIncharge(
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt)
    {
        actorMembershipId.EnsureValid();
        EnsureUtc(occurredAt);

        Result eligibility = ValidateRoleActorAndTarget(actorMembershipId);
        if (eligibility.IsFailure)
        {
            return Result.Failure<FoodInchargeAssigned>(eligibility.Error);
        }

        if (!activeRoles.Add(OrganizationRole.FoodIncharge))
        {
            return Result.Failure<FoodInchargeAssigned>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "The membership already has the Food Incharge role."));
        }

        return Result.Success(new FoodInchargeAssigned(Id, actorMembershipId, occurredAt));
    }

    internal Result<FoodInchargeRevoked> RevokeFoodIncharge(
        MembershipId actorMembershipId,
        DateTimeOffset occurredAt)
    {
        actorMembershipId.EnsureValid();
        EnsureUtc(occurredAt);

        Result eligibility = ValidateRoleActorAndTarget(actorMembershipId);
        if (eligibility.IsFailure)
        {
            return Result.Failure<FoodInchargeRevoked>(eligibility.Error);
        }

        if (!activeRoles.Remove(OrganizationRole.FoodIncharge))
        {
            return Result.Failure<FoodInchargeRevoked>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "The membership does not have the Food Incharge role."));
        }

        return Result.Success(new FoodInchargeRevoked(Id, actorMembershipId, occurredAt));
    }

    internal void GrantAdministrator() => activeRoles.Add(OrganizationRole.Admin);
    internal void RevokeAdministrator() => activeRoles.Remove(OrganizationRole.Admin);

    internal Membership DeepCopy()
    {
        Membership copy = new(
            Id,
            OrganizationId,
            UserId,
            DisplayName,
            EligibleAsNamedParticipant)
        {
            Status = Status,
        };
        copy.activeRoles.UnionWith(activeRoles);
        return copy;
    }

    internal bool HasValidPersistedState() =>
        Enum.IsDefined(Status)
        && activeRoles.All(role => Enum.IsDefined(role))
        && (Status == MembershipStatus.Active || activeRoles.Count == 0);

    private Result ValidateRoleActorAndTarget(MembershipId actorMembershipId)
    {
        if (actorMembershipId == Id)
        {
            return Result.Failure(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleParticipantsNotDistinct,
                    "A role actor cannot target their own membership."));
        }

        if (Status != MembershipStatus.Active)
        {
            return Result.Failure(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.ActiveMembershipRequired,
                    "Roles may be changed only for an active membership."));
        }

        return Result.Success();
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record MembershipActivated(MembershipId MembershipId, DateTimeOffset OccurredAt);

public sealed record MembershipDisabled(
    MembershipId MembershipId,
    MembershipId ActorMembershipId,
    IReadOnlyCollection<OrganizationRole> RevokedRoles,
    DateTimeOffset OccurredAt);

public sealed record FoodInchargeAssigned(
    MembershipId MembershipId,
    MembershipId ActorMembershipId,
    DateTimeOffset OccurredAt);

public sealed record FoodInchargeRevoked(
    MembershipId MembershipId,
    MembershipId ActorMembershipId,
    DateTimeOffset OccurredAt);
