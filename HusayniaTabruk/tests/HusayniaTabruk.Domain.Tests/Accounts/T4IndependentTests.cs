using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class T4IndependentTests
{
    [Fact]
    public void ApprovalOneTickBeforeExpirySucceeds()
    {
        GovernanceFixture fixture = GovernanceFixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant();

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                request.ExpiresAt.AddTicks(-1));

        Assert.True(result.IsSuccess);
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Target.Id)
            .HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void DisabledTargetCannotReceiveFoodInchargeRole()
    {
        GovernanceFixture fixture = GovernanceFixture.Create();
        Assert.True(
            fixture.Governance.DisableMembership(
                fixture.Proposer,
                fixture.Target,
                Utc(13)).IsSuccess);

        Result<FoodInchargeAssigned> result =
            fixture.Governance.AssignFoodIncharge(
                fixture.Proposer,
                fixture.Target,
                Utc(14));

        Assert.True(result.IsFailure);
        Assert.Equal(AccountErrorCodes.ActiveMembershipRequired, result.Error.Code);
        Assert.False(fixture.Target.HasRole(OrganizationRole.FoodIncharge));
    }

    [Fact]
    public void DisabledTargetAfterProposalCannotReceiveAdministratorRole()
    {
        GovernanceFixture fixture = GovernanceFixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant();
        Assert.True(
            fixture.Governance.DisableMembership(
                fixture.Proposer,
                fixture.Target,
                Utc(13)).IsSuccess);

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                Utc(14));

        Assert.True(result.IsFailure);
        Assert.Equal(AccountErrorCodes.ActiveMembershipRequired, result.Error.Code);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void RevokedProposerCannotAuthorizeTheirPreviouslyPendingRequest()
    {
        GovernanceFixture fixture = GovernanceFixture.Create(administratorCount: 3);
        RoleChangeRequest pendingGrant = fixture.ProposeGrant();
        RoleChangeRequest revokeProposer =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Revoke,
                fixture.Approver,
                fixture.Proposer,
                "Revoke proposer authorization",
                Utc(13)).Value;
        Assert.True(
            fixture.Governance.ApproveAdministratorRoleChange(
                revokeProposer,
                fixture.Approver,
                fixture.ThirdAdministrator,
                fixture.Proposer,
                Utc(14)).IsSuccess);
        Assert.False(fixture.Proposer.HasRole(OrganizationRole.Admin));

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                pendingGrant,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                Utc(15));

        Assert.True(result.IsFailure);
        Assert.Equal(AccountErrorCodes.AdministratorRoleRequired, result.Error.Code);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
        Assert.Equal(RoleChangeRequestStatus.Pending, pendingGrant.Status);
    }

    [Fact]
    public void FailedBootstrapCanBeCorrectedAndThenSealed()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership first = CreateActiveMembership(organizationId);
        Membership second = CreateActiveMembership(organizationId);
        OrganizationAccountGovernance governance =
            Rehydrate(organizationId, [first, second]);

        Result<AdministratorBootstrapCompleted> failed =
            governance.BootstrapAdministrators([first], Utc(1));
        Result<AdministratorBootstrapCompleted> corrected =
            governance.BootstrapAdministrators([first, second], Utc(2));

        Assert.True(failed.IsFailure);
        Assert.True(corrected.IsSuccess);
        Assert.Equal(AdministratorBootstrapStatus.Sealed, governance.BootstrapStatus);
        Assert.All(
            governance.Memberships,
            membership => Assert.True(membership.HasRole(OrganizationRole.Admin)));
        Assert.False(first.HasRole(OrganizationRole.Admin));
        Assert.False(second.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void BlankMembershipAndRoleReasonsAreRejectedWithoutStateChange()
    {
        OrganizationId organizationId = OrganizationId.New();
        Result<Membership> blankMembership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "  ",
            eligibleAsNamedParticipant: true);
        GovernanceFixture fixture = GovernanceFixture.Create();
        long version = fixture.Governance.Version;
        Result<RoleChangeRequest> blankReason =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Grant,
                fixture.Proposer,
                fixture.Target,
                "\t",
                fixture.Now);

        Assert.True(blankMembership.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountInput, blankMembership.Error.Code);
        Assert.True(blankReason.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountInput, blankReason.Error.Code);
        Assert.Equal(version, fixture.Governance.Version);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void NonUtcApprovalThrowsBeforeChangingRequestOrTarget()
    {
        GovernanceFixture fixture = GovernanceFixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant();
        long version = fixture.Governance.Version;
        DateTimeOffset nonUtc = fixture.Now.ToOffset(TimeSpan.FromHours(-7));

        Assert.Throws<ArgumentException>(
            () => fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                nonUtc));
        Assert.Equal(version, fixture.Governance.Version);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    private static Membership CreateActiveMembership(OrganizationId organizationId)
    {
        Membership membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            eligibleAsNamedParticipant: true).Value;
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

    private sealed record GovernanceFixture(
        OrganizationAccountGovernance Governance,
        Membership Proposer,
        Membership Approver,
        Membership ThirdAdministrator,
        Membership Target,
        DateTimeOffset Now)
    {
        public static GovernanceFixture Create(int administratorCount = 2)
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership proposer = CreateActiveMembership(organizationId);
            Membership approver = CreateActiveMembership(organizationId);
            Membership third = CreateActiveMembership(organizationId);
            Membership target = CreateActiveMembership(organizationId);
            OrganizationAccountGovernance governance =
                Rehydrate(organizationId, [proposer, approver, third, target]);
            Membership[] administrators = administratorCount == 3
                ? [proposer, approver, third]
                : [proposer, approver];
            Assert.True(governance.BootstrapAdministrators(administrators, Utc(1)).IsSuccess);
            return new(governance, proposer, approver, third, target, Utc(12));
        }

        public RoleChangeRequest ProposeGrant() =>
            Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Grant,
                Proposer,
                Target,
                "Grant administrator",
                Now).Value;
    }
}
