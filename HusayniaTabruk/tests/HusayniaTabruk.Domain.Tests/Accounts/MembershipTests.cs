using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class MembershipTests
{
    [Fact]
    public void InvitedMembershipBecomesEligibleOnlyAfterActivation()
    {
        Membership membership = CreateMembership(eligibleAsNamedParticipant: true);

        Assert.Equal(MembershipStatus.Invited, membership.Status);
        Assert.False(membership.IsEligibleAsNamedParticipant);

        Result<MembershipActivated> result = membership.Activate(Utc(0));

        Assert.True(result.IsSuccess);
        Assert.True(membership.IsEligibleAsNamedParticipant);
    }

    [Fact]
    public void InvalidMembershipTransitionsFailWithoutChangingState()
    {
        Membership membership = CreateMembership();
        Assert.True(membership.Activate(Utc(0)).IsSuccess);

        Result<MembershipActivated> duplicateActivation = membership.Activate(Utc(1));

        Assert.True(duplicateActivation.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidMembershipTransition, duplicateActivation.Error.Code);
        Assert.Equal(MembershipStatus.Active, membership.Status);
    }

    [Fact]
    public void GovernanceDisableIsImmediateAndRevokesEveryActiveRole()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership actor = CreateActiveMembership(organizationId);
        Membership secondAdministrator = CreateActiveMembership(organizationId);
        Membership target = CreateActiveMembership(organizationId);
        OrganizationAccountGovernance governance = Rehydrate(
            organizationId,
            [actor, secondAdministrator, target]);
        Assert.True(
            governance.BootstrapAdministrators(
                [actor, secondAdministrator],
                Utc(1)).IsSuccess);
        Assert.True(governance.AssignFoodIncharge(actor, target, Utc(2)).IsSuccess);

        Result<MembershipDisabled> result = governance.DisableMembership(actor, target, Utc(3));

        Assert.True(result.IsSuccess);
        Membership storedTarget = governance.Memberships.Single(membership => membership.Id == target.Id);
        Assert.Equal(MembershipStatus.Disabled, storedTarget.Status);
        Assert.Empty(storedTarget.ActiveRoles);
        Assert.Equal(MembershipStatus.Active, target.Status);
        Assert.Contains(OrganizationRole.FoodIncharge, result.Value.RevokedRoles);
    }

    [Fact]
    public void FoodInchargeTransitionsRejectSelfAndDuplicatesWithoutChangingVersion()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership actor = CreateActiveMembership(organizationId);
        Membership secondAdministrator = CreateActiveMembership(organizationId);
        Membership target = CreateActiveMembership(organizationId);
        OrganizationAccountGovernance governance = Rehydrate(
            organizationId,
            [actor, secondAdministrator, target]);
        Assert.True(
            governance.BootstrapAdministrators(
                [actor, secondAdministrator],
                Utc(1)).IsSuccess);
        long version = governance.Version;

        Result<FoodInchargeAssigned> selfAssignment =
            governance.AssignFoodIncharge(actor, actor, Utc(2));
        Assert.True(selfAssignment.IsFailure);
        Assert.Equal(version, governance.Version);

        Assert.True(governance.AssignFoodIncharge(actor, target, Utc(3)).IsSuccess);
        long assignedVersion = governance.Version;
        Assert.True(governance.AssignFoodIncharge(actor, target, Utc(4)).IsFailure);
        Assert.Equal(assignedVersion, governance.Version);

        Assert.True(governance.RevokeFoodIncharge(actor, target, Utc(5)).IsSuccess);
        long revokedVersion = governance.Version;
        Assert.True(governance.RevokeFoodIncharge(actor, target, Utc(6)).IsFailure);
        Assert.Equal(revokedVersion, governance.Version);
    }

    private static Membership CreateMembership(bool eligibleAsNamedParticipant = true) =>
        Membership.Invite(
            MembershipId.New(),
            OrganizationId.From(Guid.Parse("1b37574c-e0ef-4af7-9cad-0d46fc1dc222")),
            UserId.New(),
            "Member",
            eligibleAsNamedParticipant).Value;

    private static Membership CreateActiveMembership(OrganizationId organizationId)
    {
        Membership membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            true).Value;
        Assert.True(membership.Activate(Utc(0)).IsSuccess);
        return membership;
    }

    private static OrganizationAccountGovernance Rehydrate(
        OrganizationId organizationId,
        IReadOnlyCollection<Membership> memberships) =>
        OrganizationAccountGovernance.Rehydrate(
            organizationId,
            0,
            AdministratorBootstrapStatus.Unsealed,
            null,
            memberships,
            []).Value;

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 14, hour, 0, 0, TimeSpan.Zero);
}
