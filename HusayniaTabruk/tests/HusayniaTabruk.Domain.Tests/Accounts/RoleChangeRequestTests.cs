using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class RoleChangeRequestTests
{
    [Fact]
    public void GrantRequiresDistinctProposerApproverAndTarget()
    {
        Fixture fixture = Fixture.Create();
        Result<RoleChangeRequest> selfProposal =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Revoke,
                fixture.Proposer,
                fixture.Proposer,
                "Self revocation",
                fixture.Now);
        RoleChangeRequest request = fixture.ProposeGrant();

        Result<AdministratorRoleChangeApproved> proposerApproval =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Proposer,
                fixture.Target,
                fixture.Now.AddHours(1));

        Assert.Equal(AccountErrorCodes.RoleParticipantsNotDistinct, selfProposal.Error.Code);
        Assert.Equal(AccountErrorCodes.RoleParticipantsNotDistinct, proposerApproval.Error.Code);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void ActiveSecondAdministratorApprovalGrantsRoleImmediately()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant();

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                fixture.Now.AddHours(1));

        Assert.True(result.IsSuccess);
        RoleChangeRequest storedRequest = fixture.Governance.RoleChangeRequests.Single();
        Assert.Equal(RoleChangeRequestStatus.Approved, storedRequest.Status);
        Assert.Equal(fixture.Approver.Id, storedRequest.ApproverMembershipId);
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Target.Id)
            .HasRole(OrganizationRole.Admin));
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void RequestExpiresAtTheTwentyFourHourBoundary()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant();

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.Target,
                fixture.Now.AddHours(24));

        Assert.Equal(fixture.Now.AddHours(24), request.ExpiresAt);
        Assert.Equal(AccountErrorCodes.RoleChangeRequestExpired, result.Error.Code);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void DuplicateAndInvalidAdministratorTransitionsAreRejected()
    {
        Fixture fixture = Fixture.Create();

        Result<RoleChangeRequest> duplicateGrant =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Grant,
                fixture.Proposer,
                fixture.Approver,
                "Duplicate grant",
                fixture.Now);
        Result<RoleChangeRequest> absentRevoke =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Revoke,
                fixture.Proposer,
                fixture.Target,
                "Invalid revoke",
                fixture.Now);

        Assert.Equal(AccountErrorCodes.InvalidRoleTransition, duplicateGrant.Error.Code);
        Assert.Equal(AccountErrorCodes.InvalidRoleTransition, absentRevoke.Error.Code);
    }

    [Fact]
    public void NormalGovernanceCannotRevokeBelowTwoActiveAdministrators()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Revoke,
                fixture.Proposer,
                fixture.Approver,
                "Retire administrator access",
                fixture.Now).Value;
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.ThirdAdministrator,
                fixture.Approver,
                fixture.Now.AddHours(1));

        Assert.Equal(AccountErrorCodes.AdministratorRoleRequired, result.Error.Code);
        Assert.Equal(version, fixture.Governance.Version);
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Approver.Id)
            .HasRole(OrganizationRole.Admin));
    }

    private sealed record Fixture(
        OrganizationAccountGovernance Governance,
        Membership Proposer,
        Membership Approver,
        Membership ThirdAdministrator,
        Membership Target,
        DateTimeOffset Now)
    {
        public static Fixture Create()
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership proposer = CreateActive(organizationId);
            Membership approver = CreateActive(organizationId);
            Membership third = CreateActive(organizationId);
            Membership target = CreateActive(organizationId);
            OrganizationAccountGovernance governance =
                OrganizationAccountGovernance.Rehydrate(
                    organizationId,
                    0,
                    AdministratorBootstrapStatus.Unsealed,
                    null,
                    [proposer, approver, third, target],
                    []).Value;
            Assert.True(governance.BootstrapAdministrators([proposer, approver], Utc(1)).IsSuccess);
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

        private static Membership CreateActive(OrganizationId organizationId)
        {
            Membership membership =
                Membership.Invite(MembershipId.New(), organizationId, UserId.New(), "Member", true).Value;
            Assert.True(membership.Activate(Utc(0)).IsSuccess);
            return membership;
        }
    }

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 14, hour, 0, 0, TimeSpan.Zero);
}
