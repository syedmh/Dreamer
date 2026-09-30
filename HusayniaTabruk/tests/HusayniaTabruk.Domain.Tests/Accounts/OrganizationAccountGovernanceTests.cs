using System.Reflection;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class OrganizationAccountGovernanceTests
{
    [Fact]
    public void RehydrateRejectsInvalidVersionBootstrapAndChildState()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership member = CreateActive(organizationId);
        Membership duplicate = CreateActive(organizationId, member.Id);
        Membership outsider = CreateActive(OrganizationId.New());

        Result<OrganizationAccountGovernance> negativeVersion =
            Rehydrate(organizationId, [member], version: -1);
        Result<OrganizationAccountGovernance> inconsistentSeal =
            Rehydrate(
                organizationId,
                [member],
                bootstrapStatus: AdministratorBootstrapStatus.Sealed);
        Result<OrganizationAccountGovernance> duplicateMembership =
            Rehydrate(organizationId, [member, duplicate]);
        Result<OrganizationAccountGovernance> crossOrganization =
            Rehydrate(organizationId, [member, outsider]);

        Assert.All(
            [negativeVersion, inconsistentSeal, duplicateMembership, crossOrganization],
            result =>
            {
                Assert.True(result.IsFailure);
                Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, result.Error.Code);
            });
    }

    [Fact]
    public void RoleChangeRequestRehydrateRejectsUndefinedActionAndInvalidState()
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId proposerId = MembershipId.New();
        MembershipId targetId = MembershipId.New();

        Result<RoleChangeRequest> invalidAction = RoleChangeRequest.Rehydrate(
            RoleChangeRequestId.New(),
            organizationId,
            targetId,
            (AdministratorRoleChangeAction)999,
            proposerId,
            null,
            "Reason",
            Utc(1),
            Utc(1).AddHours(24),
            null,
            RoleChangeRequestStatus.Pending);
        Result<RoleChangeRequest> invalidState = RoleChangeRequest.Rehydrate(
            RoleChangeRequestId.New(),
            organizationId,
            targetId,
            AdministratorRoleChangeAction.Grant,
            proposerId,
            MembershipId.New(),
            "Reason",
            Utc(1),
            Utc(1).AddHours(24),
            null,
            RoleChangeRequestStatus.Pending);

        Assert.Equal(AccountErrorCodes.InvalidAdministratorRoleChangeAction, invalidAction.Error.Code);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, invalidState.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsNonUtcPersistedTimestampsWithTypedErrors()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership proposer = CreateActive(organizationId);
        Membership target = CreateActive(organizationId);
        DateTimeOffset nonUtc = Utc(1).ToOffset(TimeSpan.FromHours(-7));

        Result<RoleChangeRequest> request = RoleChangeRequest.Rehydrate(
            RoleChangeRequestId.New(),
            organizationId,
            target.Id,
            AdministratorRoleChangeAction.Grant,
            proposer.Id,
            null,
            "Reason",
            nonUtc,
            nonUtc.AddHours(24),
            null,
            RoleChangeRequestStatus.Pending);
        Result<OrganizationAccountGovernance> governance =
            Rehydrate(
                organizationId,
                [proposer, target],
                version: 1,
                bootstrapStatus: AdministratorBootstrapStatus.Sealed,
                bootstrapSealedAt: nonUtc);

        Assert.Equal(AccountErrorCodes.RoleChangeRequestChronologyInvalid, request.Error.Code);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, governance.Error.Code);
    }

    [Fact]
    public void UnsafeCallerControlledPublicMethodsAreAbsent()
    {
        MethodInfo[] membershipMethods = typeof(Membership).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        MethodInfo[] requestMethods = typeof(RoleChangeRequest).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.DoesNotContain(membershipMethods, method => method.Name is "Disable" or "AssignFoodIncharge" or "RevokeFoodIncharge");
        Assert.DoesNotContain(requestMethods, method => method.Name is "Propose" or "Approve");
        Assert.Null(typeof(OrganizationAccountGovernance).Assembly.GetType(
            "HusayniaTabruk.Domain.Accounts.AdministratorBootstrap"));
        Assert.DoesNotContain(
            typeof(OrganizationAccountGovernance).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.GetParameters().Any(parameter => parameter.ParameterType == typeof(int)));
    }

    [Fact]
    public void BootstrapSealSurvivesRehydrationAndEverySuccessIncrementsOnce()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership first = CreateActive(organizationId);
        Membership second = CreateActive(organizationId);
        Membership third = CreateActive(organizationId);
        OrganizationAccountGovernance governance = Rehydrate(organizationId, [first, second, third]).Value;

        Assert.True(governance.BootstrapAdministrators([first, second], Utc(1)).IsSuccess);
        Assert.Equal(1, governance.Version);
        OrganizationAccountGovernance rehydrated = OrganizationAccountGovernance.Rehydrate(
            organizationId,
            governance.Version,
            governance.BootstrapStatus,
            governance.BootstrapSealedAt,
            governance.Memberships,
            governance.RoleChangeRequests).Value;

        Result<AdministratorBootstrapCompleted> rerun =
            rehydrated.BootstrapAdministrators([first, third], Utc(2));

        Assert.Equal(AccountErrorCodes.BootstrapSealed, rerun.Error.Code);
        Assert.Equal(1, rehydrated.OriginalVersion);
        Assert.Equal(1, rehydrated.Version);
        Assert.False(third.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void ActorAuthorizationUsesOwnedStateAndAcceptsSameIdSnapshots()
    {
        Fixture fixture = Fixture.Create();
        Membership inactive = CreateInvited(fixture.OrganizationId);
        Membership nonAdministrator = fixture.Target;
        Membership outsider = CreateActive(OrganizationId.New());
        Membership lookAlike = CreateActive(fixture.OrganizationId, fixture.Proposer.Id);
        long version = fixture.Governance.Version;

        Result<MembershipDisabled>[] rejected =
        [
            fixture.Governance.DisableMembership(inactive, fixture.Target, Utc(2)),
            fixture.Governance.DisableMembership(nonAdministrator, fixture.ThirdAdministrator, Utc(3)),
            fixture.Governance.DisableMembership(outsider, fixture.Target, Utc(4)),
        ];

        Assert.All(rejected, result => Assert.True(result.IsFailure));
        Assert.True(fixture.Governance.DisableMembership(lookAlike, fixture.Target, Utc(5)).IsSuccess);
        Assert.Equal(version + 1, fixture.Governance.Version);
        Assert.Equal(
            MembershipStatus.Disabled,
            fixture.Governance.Memberships.Single(
                membership => membership.Id == fixture.Target.Id).Status);
        Assert.Equal(MembershipStatus.Active, fixture.Target.Status);
    }

    [Fact]
    public void FoodInchargeChangesUseOwnedActorStateAndRejectCrossOrganizationActors()
    {
        Fixture fixture = Fixture.Create();
        Membership lookAlike = CreateActive(fixture.OrganizationId, fixture.Proposer.Id);
        Membership outsider = CreateActive(OrganizationId.New());
        long version = fixture.Governance.Version;

        Assert.True(
            fixture.Governance.AssignFoodIncharge(
                lookAlike,
                fixture.Target,
                Utc(2)).IsSuccess);
        Assert.True(
            fixture.Governance.RevokeFoodIncharge(
                outsider,
                fixture.Target,
                Utc(3)).IsFailure);

        Assert.Equal(version + 1, fixture.Governance.Version);
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Target.Id)
            .HasRole(OrganizationRole.FoodIncharge));
        Assert.False(fixture.Target.HasRole(OrganizationRole.FoodIncharge));
    }

    [Fact]
    public void DisablingAdministratorDerivesQuorumInternally()
    {
        Fixture twoAdmins = Fixture.Create(administratorCount: 2);
        long failedVersion = twoAdmins.Governance.Version;

        Result<MembershipDisabled> denied =
            twoAdmins.Governance.DisableMembership(
                twoAdmins.Proposer,
                twoAdmins.Approver,
                Utc(2));

        Assert.Equal(AccountErrorCodes.MinimumAdministratorsRequired, denied.Error.Code);
        Assert.Equal(failedVersion, twoAdmins.Governance.Version);
        Assert.Equal(MembershipStatus.Active, twoAdmins.Approver.Status);

        Fixture threeAdmins = Fixture.Create(administratorCount: 3);
        long successVersion = threeAdmins.Governance.Version;
        Result<MembershipDisabled> allowed =
            threeAdmins.Governance.DisableMembership(
                threeAdmins.Proposer,
                threeAdmins.ThirdAdministrator,
                Utc(2));

        Assert.True(allowed.IsSuccess);
        Assert.Equal(successVersion + 1, threeAdmins.Governance.Version);
        Assert.Equal(
            MembershipStatus.Disabled,
            threeAdmins.Governance.Memberships.Single(
                membership => membership.Id == threeAdmins.ThirdAdministrator.Id).Status);
        Assert.Equal(MembershipStatus.Active, threeAdmins.ThirdAdministrator.Status);
    }

    [Fact]
    public void UndefinedActionFailsProposalWithoutMutation()
    {
        Fixture fixture = Fixture.Create();
        long version = fixture.Governance.Version;

        Result<RoleChangeRequest> result =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                (AdministratorRoleChangeAction)123,
                fixture.Proposer,
                fixture.Target,
                "Invalid action",
                Utc(2));

        Assert.Equal(AccountErrorCodes.InvalidAdministratorRoleChangeAction, result.Error.Code);
        Assert.Equal(version, fixture.Governance.Version);
        Assert.Empty(fixture.Governance.RoleChangeRequests);
    }

    [Fact]
    public void ApprovalChronologyEnforcesBothBoundariesWithoutMutation()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> before =
            fixture.Approve(request, Utc(9));
        Result<AdministratorRoleChangeApproved> expired =
            fixture.Approve(request, request.ExpiresAt);

        Assert.Equal(AccountErrorCodes.RoleChangeRequestChronologyInvalid, before.Error.Code);
        Assert.Equal(AccountErrorCodes.RoleChangeRequestExpired, expired.Error.Code);
        Assert.Equal(version, fixture.Governance.Version);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void ApprovalAtProposalTimeSucceeds()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> result = fixture.Approve(request, request.ProposedAt);

        Assert.True(result.IsSuccess);
        Assert.Equal(version + 1, fixture.Governance.Version);
    }

    [Fact]
    public void DisabledProposerCannotAuthorizePendingRequest()
    {
        Fixture fixture = Fixture.Create(administratorCount: 3);
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        Assert.True(
            fixture.Governance.DisableMembership(
                fixture.ThirdAdministrator,
                fixture.Proposer,
                Utc(11)).IsSuccess);
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> result = fixture.Approve(request, Utc(12));

        Assert.Equal(AccountErrorCodes.ActiveMembershipRequired, result.Error.Code);
        Assert.Equal(version, fixture.Governance.Version);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void ThreeAdministratorRevocationSucceedsAndLeavesTwo()
    {
        Fixture fixture = Fixture.Create(administratorCount: 3);
        RoleChangeRequest request =
            fixture.Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Revoke,
                fixture.Proposer,
                fixture.ThirdAdministrator,
                "Rotate administrator",
                Utc(10)).Value;
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                fixture.ThirdAdministrator,
                Utc(11));

        Assert.True(result.IsSuccess);
        Assert.Equal(version + 1, fixture.Governance.Version);
        Assert.False(fixture.ThirdAdministrator.HasRole(OrganizationRole.Admin));
        Assert.Equal(
            2,
            fixture.Governance.Memberships.Count(
                membership => membership.Status == MembershipStatus.Active
                    && membership.HasRole(OrganizationRole.Admin)));
    }

    [Fact]
    public void RequestAndMembershipSnapshotsResolveToOwnedState()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        RoleChangeRequest lookAlikeRequest = RoleChangeRequest.Rehydrate(
            request.Id,
            request.OrganizationId,
            request.TargetMembershipId,
            request.Action,
            request.ProposerMembershipId,
            null,
            request.Reason,
            request.ProposedAt,
            request.ExpiresAt,
            null,
            request.Status).Value;
        Membership lookAlikeProposer =
            CreateActive(fixture.OrganizationId, fixture.Proposer.Id);
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> result =
            fixture.Governance.ApproveAdministratorRoleChange(
                lookAlikeRequest,
                lookAlikeProposer,
                fixture.Approver,
                fixture.Target,
                Utc(11));

        Assert.True(result.IsSuccess);
        Assert.Equal(version + 1, fixture.Governance.Version);
        Assert.Equal(RoleChangeRequestStatus.Approved, fixture.Governance.RoleChangeRequests.Single().Status);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.False(lookAlikeProposer.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void NonUtcCommandsThrowBeforeAnyMutation()
    {
        Fixture fixture = Fixture.Create();
        long version = fixture.Governance.Version;
        DateTimeOffset nonUtc = Utc(2).ToOffset(TimeSpan.FromHours(3));

        Assert.Throws<ArgumentException>(
            () => fixture.Governance.DisableMembership(
                fixture.Proposer,
                fixture.Target,
                nonUtc));

        Assert.Equal(version, fixture.Governance.Version);
        Assert.Equal(MembershipStatus.Active, fixture.Target.Status);
    }

    private static Result<OrganizationAccountGovernance> Rehydrate(
        OrganizationId organizationId,
        IReadOnlyCollection<Membership> memberships,
        long version = 0,
        AdministratorBootstrapStatus bootstrapStatus = AdministratorBootstrapStatus.Unsealed,
        DateTimeOffset? bootstrapSealedAt = null) =>
        OrganizationAccountGovernance.Rehydrate(
            organizationId,
            version,
            bootstrapStatus,
            bootstrapSealedAt,
            memberships,
            []);

    private static Membership CreateInvited(
        OrganizationId organizationId,
        MembershipId? id = null) =>
        Membership.Invite(
            id ?? MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            true).Value;

    private static Membership CreateActive(
        OrganizationId organizationId,
        MembershipId? id = null)
    {
        Membership membership = CreateInvited(organizationId, id);
        Assert.True(membership.Activate(Utc(0)).IsSuccess);
        return membership;
    }

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 14, hour, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        OrganizationId OrganizationId,
        OrganizationAccountGovernance Governance,
        Membership Proposer,
        Membership Approver,
        Membership ThirdAdministrator,
        Membership Target)
    {
        public static Fixture Create(int administratorCount = 2)
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership proposer = CreateActive(organizationId);
            Membership approver = CreateActive(organizationId);
            Membership third = CreateActive(organizationId);
            Membership target = CreateActive(organizationId);
            OrganizationAccountGovernance governance =
                Rehydrate(organizationId, [proposer, approver, third, target]).Value;
            Membership[] administrators = administratorCount == 3
                ? [proposer, approver, third]
                : [proposer, approver];
            Assert.True(governance.BootstrapAdministrators(administrators, Utc(1)).IsSuccess);
            return new(organizationId, governance, proposer, approver, third, target);
        }

        public RoleChangeRequest ProposeGrant(DateTimeOffset now) =>
            Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Grant,
                Proposer,
                Target,
                "Grant administrator",
                now).Value;

        public Result<AdministratorRoleChangeApproved> Approve(
            RoleChangeRequest request,
            DateTimeOffset now) =>
            Governance.ApproveAdministratorRoleChange(
                request,
                Proposer,
                Approver,
                Target,
                now);
    }
}
