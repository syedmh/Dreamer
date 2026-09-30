using System.Reflection;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class T4RemediationAttackTests
{
    [Fact]
    public void RehydrateRejectsUndefinedMembershipStatus()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership invalidStatus = CreateActive(organizationId);
        SetStatus(invalidStatus, (MembershipStatus)999);

        Result<OrganizationAccountGovernance> statusResult =
            Rehydrate(organizationId, [invalidStatus]);

        Assert.True(statusResult.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, statusResult.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsUndefinedMembershipRole()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership invalidRole = CreateActive(organizationId);
        AddRole(invalidRole, (OrganizationRole)999);

        Result<OrganizationAccountGovernance> roleResult =
            Rehydrate(organizationId, [invalidRole]);

        Assert.True(roleResult.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, roleResult.Error.Code);
    }

    [Fact]
    public void ExposedCollectionsCannotBeCastBackToMutableArrays()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        Membership originalFirst = fixture.Governance.Memberships.First();
        IList<Membership> memberships = Assert.IsAssignableFrom<IList<Membership>>(
            fixture.Governance.Memberships);
        IList<RoleChangeRequest> requests = Assert.IsAssignableFrom<IList<RoleChangeRequest>>(
            fixture.Governance.RoleChangeRequests);
        IList<OrganizationRole> roles = Assert.IsAssignableFrom<IList<OrganizationRole>>(
            fixture.Proposer.ActiveRoles);

        Assert.Null(fixture.Governance.Memberships as Membership[]);
        Assert.Null(fixture.Governance.RoleChangeRequests as RoleChangeRequest[]);
        Assert.Null(fixture.Proposer.ActiveRoles as OrganizationRole[]);
        Assert.Throws<NotSupportedException>(
            () => memberships[0] = CreateActive(OrganizationId.New()));
        Assert.Throws<NotSupportedException>(() => requests.Clear());
        Assert.Throws<NotSupportedException>(() => roles.Clear());
        Assert.NotSame(originalFirst, fixture.Governance.Memberships.First());
        Assert.NotSame(request, fixture.Governance.RoleChangeRequests.Single());
        Assert.Equal(originalFirst.Id, fixture.Governance.Memberships.First().Id);
        Assert.Equal(request.Id, fixture.Governance.RoleChangeRequests.Single().Id);
    }

    [Fact]
    public void RehydrateRejectsSealedBootstrapWithFewerThanTwoActiveAdministrators()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership administrator = CreateActive(organizationId);
        Membership member = CreateActive(organizationId);
        AddRole(administrator, OrganizationRole.Admin);

        Result<OrganizationAccountGovernance> result =
            OrganizationAccountGovernance.Rehydrate(
                organizationId,
                1,
                AdministratorBootstrapStatus.Sealed,
                Utc(1),
                [administrator, member],
                []);

        Assert.True(result.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, result.Error.Code);
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    public void RoleChangeRequestRehydrateRejectsNonCanonicalLifetime(int lifetimeHours)
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership proposer = CreateActive(organizationId);
        Membership target = CreateActive(organizationId);

        Result<RoleChangeRequest> result = RoleChangeRequest.Rehydrate(
            RoleChangeRequestId.New(),
            organizationId,
            target.Id,
            AdministratorRoleChangeAction.Grant,
            proposer.Id,
            null,
            "Reason",
            Utc(1),
            Utc(1).AddHours(lifetimeHours),
            null,
            RoleChangeRequestStatus.Pending);

        Assert.True(result.IsFailure);
        Assert.Equal(AccountErrorCodes.RoleChangeRequestChronologyInvalid, result.Error.Code);
    }

    [Fact]
    public void RehydrateRoundTripPreservesVersionSealMembershipsAndRequests()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        long persistedVersion = fixture.Governance.Version;

        OrganizationAccountGovernance restored = OrganizationAccountGovernance.Rehydrate(
            fixture.OrganizationId,
            persistedVersion,
            fixture.Governance.BootstrapStatus,
            fixture.Governance.BootstrapSealedAt,
            fixture.Governance.Memberships,
            fixture.Governance.RoleChangeRequests).Value;

        Assert.Equal(persistedVersion, restored.OriginalVersion);
        Assert.Equal(persistedVersion, restored.Version);
        Assert.Equal(AdministratorBootstrapStatus.Sealed, restored.BootstrapStatus);
        Assert.Equal(Utc(1), restored.BootstrapSealedAt);
        Assert.Equal(4, restored.Memberships.Count);
        Assert.Single(restored.RoleChangeRequests);
        Assert.NotSame(request, restored.RoleChangeRequests.Single());
        Assert.Equal(request.Id, restored.RoleChangeRequests.Single().Id);
    }

    [Fact]
    public void RehydratedRootsDoNotShareMembershipState()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership first = CreateActive(organizationId);
        Membership second = CreateActive(organizationId);
        OrganizationAccountGovernance firstRoot =
            Rehydrate(organizationId, [first, second]).Value;
        OrganizationAccountGovernance secondRoot =
            Rehydrate(organizationId, [first, second]).Value;

        Assert.True(firstRoot.BootstrapAdministrators([first, second], Utc(1)).IsSuccess);

        Assert.Equal(1, firstRoot.Version);
        Assert.Equal(0, secondRoot.Version);
        Assert.All(
            secondRoot.Memberships,
            membership => Assert.False(membership.HasRole(OrganizationRole.Admin)));
    }

    [Fact]
    public void RetainedMembershipInputsAndGetterValuesCannotMutateAggregateState()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership retainedInput = CreateInvited(organizationId);
        OrganizationAccountGovernance governance =
            Rehydrate(organizationId, [retainedInput]).Value;
        Membership retainedGetterValue = governance.Memberships.Single();

        Assert.True(retainedInput.Activate(Utc(1)).IsSuccess);
        Assert.True(retainedGetterValue.Activate(Utc(2)).IsSuccess);

        Assert.Equal(MembershipStatus.Invited, governance.Memberships.Single().Status);
        Assert.NotSame(retainedGetterValue, governance.Memberships.Single());
        Assert.Equal(0, governance.Version);
    }

    [Fact]
    public void RehydratedRootsDoNotShareRoleChangeRequestState()
    {
        Fixture fixture = Fixture.Create();
        fixture.ProposeGrant(Utc(10));
        OrganizationAccountGovernance firstRoot = OrganizationAccountGovernance.Rehydrate(
            fixture.OrganizationId,
            fixture.Governance.Version,
            fixture.Governance.BootstrapStatus,
            fixture.Governance.BootstrapSealedAt,
            fixture.Governance.Memberships,
            fixture.Governance.RoleChangeRequests).Value;
        OrganizationAccountGovernance secondRoot = OrganizationAccountGovernance.Rehydrate(
            fixture.OrganizationId,
            fixture.Governance.Version,
            fixture.Governance.BootstrapStatus,
            fixture.Governance.BootstrapSealedAt,
            fixture.Governance.Memberships,
            fixture.Governance.RoleChangeRequests).Value;
        RoleChangeRequest request = firstRoot.RoleChangeRequests.Single();
        Membership proposer = firstRoot.Memberships.Single(membership => membership.Id == fixture.Proposer.Id);
        Membership approver = firstRoot.Memberships.Single(membership => membership.Id == fixture.Approver.Id);
        Membership target = firstRoot.Memberships.Single(membership => membership.Id == fixture.Target.Id);
        long secondVersion = secondRoot.Version;

        Assert.True(firstRoot.ApproveAdministratorRoleChange(
            request,
            proposer,
            approver,
            target,
            Utc(11)).IsSuccess);

        Assert.Equal(RoleChangeRequestStatus.Approved, firstRoot.RoleChangeRequests.Single().Status);
        Assert.Equal(RoleChangeRequestStatus.Pending, secondRoot.RoleChangeRequests.Single().Status);
        Assert.Equal(secondVersion, secondRoot.Version);
    }

    [Fact]
    public void BootstrapFailureIsAtomicAndSuccessfulSealIsOneWayAcrossRoots()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership first = CreateActive(organizationId);
        Membership second = CreateActive(organizationId);
        Membership outsider = CreateActive(OrganizationId.New());
        OrganizationAccountGovernance governance =
            Rehydrate(organizationId, [first, second]).Value;

        Result<AdministratorBootstrapCompleted> failed =
            governance.BootstrapAdministrators([first, outsider], Utc(1));

        Assert.True(failed.IsFailure);
        Assert.Equal(0, governance.Version);
        Assert.Equal(AdministratorBootstrapStatus.Unsealed, governance.BootstrapStatus);
        Assert.Null(governance.BootstrapSealedAt);
        Assert.False(first.HasRole(OrganizationRole.Admin));
        Assert.False(second.HasRole(OrganizationRole.Admin));

        Assert.True(governance.BootstrapAdministrators([first, second], Utc(2)).IsSuccess);
        OrganizationAccountGovernance restored = OrganizationAccountGovernance.Rehydrate(
            organizationId,
            governance.Version,
            governance.BootstrapStatus,
            governance.BootstrapSealedAt,
            governance.Memberships,
            governance.RoleChangeRequests).Value;
        long sealedVersion = restored.Version;

        Assert.Equal(
            AccountErrorCodes.BootstrapSealed,
            restored.BootstrapAdministrators([first, second], Utc(3)).Error.Code);
        Assert.Equal(sealedVersion, restored.Version);
    }

    [Fact]
    public void DuplicateRequestIdAndApprovedRequestReplayDoNotMutate()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequestId requestId = RoleChangeRequestId.New();
        RoleChangeRequest request = fixture.Governance.ProposeAdministratorRoleChange(
            requestId,
            AdministratorRoleChangeAction.Grant,
            fixture.Proposer,
            fixture.Target,
            "Grant",
            Utc(10)).Value;
        long proposedVersion = fixture.Governance.Version;

        Result<RoleChangeRequest> duplicate = fixture.Governance.ProposeAdministratorRoleChange(
            requestId,
            AdministratorRoleChangeAction.Grant,
            fixture.Proposer,
            fixture.Target,
            "Replay",
            Utc(11));

        Assert.True(duplicate.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, duplicate.Error.Code);
        Assert.Equal(proposedVersion, fixture.Governance.Version);
        Assert.Single(fixture.Governance.RoleChangeRequests);

        Assert.True(fixture.Approve(request, Utc(12)).IsSuccess);
        long approvedVersion = fixture.Governance.Version;
        DateTimeOffset? approvedAt = request.ApprovedAt;

        Result<AdministratorRoleChangeApproved> replay = fixture.Approve(request, Utc(13));

        Assert.True(replay.IsFailure);
        Assert.Equal(AccountErrorCodes.InvalidRoleTransition, replay.Error.Code);
        Assert.Equal(approvedVersion, fixture.Governance.Version);
        RoleChangeRequest storedRequest = fixture.Governance.RoleChangeRequests.Single();
        Assert.Equal(approvedAt, request.ApprovedAt);
        Assert.NotEqual(approvedAt, storedRequest.ApprovedAt);
        Assert.Equal(RoleChangeRequestStatus.Approved, storedRequest.Status);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
    }

    [Fact]
    public void ExactlyTwoAdministratorsCannotBeDisabledOrRevoked()
    {
        Fixture fixture = Fixture.Create();
        long initialVersion = fixture.Governance.Version;

        Result<MembershipDisabled> disable = fixture.Governance.DisableMembership(
            fixture.Proposer,
            fixture.Approver,
            Utc(10));
        RoleChangeRequest revoke = fixture.Governance.ProposeAdministratorRoleChange(
            RoleChangeRequestId.New(),
            AdministratorRoleChangeAction.Revoke,
            fixture.Proposer,
            fixture.Approver,
            "Revoke",
            Utc(11)).Value;
        long proposalVersion = fixture.Governance.Version;
        Result<AdministratorRoleChangeApproved> approval =
            fixture.Governance.ApproveAdministratorRoleChange(
                revoke,
                fixture.Proposer,
                fixture.Third,
                fixture.Approver,
                Utc(12));

        Assert.True(disable.IsFailure);
        Assert.Equal(AccountErrorCodes.MinimumAdministratorsRequired, disable.Error.Code);
        Assert.Equal(initialVersion, proposalVersion - 1);
        Assert.True(approval.IsFailure);
        Assert.Equal(proposalVersion, fixture.Governance.Version);
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Proposer.Id)
            .HasRole(OrganizationRole.Admin));
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Approver.Id)
            .HasRole(OrganizationRole.Admin));
        Assert.Equal(RoleChangeRequestStatus.Pending, fixture.Governance.RoleChangeRequests.Single().Status);
    }

    [Fact]
    public void CrossOrganizationParticipantsAreRejectedAndSameIdSnapshotsUseOwnedState()
    {
        Fixture fixture = Fixture.Create();
        RoleChangeRequest request = fixture.ProposeGrant(Utc(10));
        Membership outsider = CreateActive(OrganizationId.New());
        Membership lookAlikeTarget = CreateActive(fixture.OrganizationId, fixture.Target.Id);
        long version = fixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> outsiderResult =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                outsider,
                fixture.Target,
                Utc(11));
        Result<AdministratorRoleChangeApproved> lookAlikeResult =
            fixture.Governance.ApproveAdministratorRoleChange(
                request,
                fixture.Proposer,
                fixture.Approver,
                lookAlikeTarget,
                Utc(11));

        Assert.True(outsiderResult.IsFailure);
        Assert.True(lookAlikeResult.IsSuccess);
        Assert.Equal(version + 1, fixture.Governance.Version);
        Assert.Equal(RoleChangeRequestStatus.Approved, fixture.Governance.RoleChangeRequests.Single().Status);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.False(lookAlikeTarget.HasRole(OrganizationRole.Admin));
        Assert.True(fixture.Governance.Memberships
            .Single(membership => membership.Id == fixture.Target.Id)
            .HasRole(OrganizationRole.Admin));
        Assert.False(fixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void DisabledAndRevokedProposersCannotAuthorizePendingGrant()
    {
        Fixture disabledFixture = Fixture.Create(administratorCount: 3);
        RoleChangeRequest disabledRequest = disabledFixture.ProposeGrant(Utc(10));
        Assert.True(disabledFixture.Governance.DisableMembership(
            disabledFixture.Third,
            disabledFixture.Proposer,
            Utc(11)).IsSuccess);
        long disabledVersion = disabledFixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> disabled =
            disabledFixture.Approve(disabledRequest, Utc(12));

        Assert.Equal(AccountErrorCodes.ActiveMembershipRequired, disabled.Error.Code);
        Assert.Equal(disabledVersion, disabledFixture.Governance.Version);
        Assert.False(disabledFixture.Target.HasRole(OrganizationRole.Admin));

        Fixture revokedFixture = Fixture.Create(administratorCount: 3);
        RoleChangeRequest pending = revokedFixture.ProposeGrant(Utc(10));
        RoleChangeRequest revoke = revokedFixture.Governance.ProposeAdministratorRoleChange(
            RoleChangeRequestId.New(),
            AdministratorRoleChangeAction.Revoke,
            revokedFixture.Approver,
            revokedFixture.Proposer,
            "Revoke proposer",
            Utc(11)).Value;
        Assert.True(revokedFixture.Governance.ApproveAdministratorRoleChange(
            revoke,
            revokedFixture.Approver,
            revokedFixture.Third,
            revokedFixture.Proposer,
            Utc(12)).IsSuccess);
        long revokedVersion = revokedFixture.Governance.Version;

        Result<AdministratorRoleChangeApproved> revoked =
            revokedFixture.Approve(pending, Utc(13));

        Assert.Equal(AccountErrorCodes.AdministratorRoleRequired, revoked.Error.Code);
        Assert.Equal(revokedVersion, revokedFixture.Governance.Version);
        Assert.False(revokedFixture.Target.HasRole(OrganizationRole.Admin));
    }

    [Fact]
    public void DefaultIdentifiersAndUnknownEnumsAreRejectedAtBoundaries()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership proposer = CreateActive(organizationId);
        Membership target = CreateActive(organizationId);

        Assert.Throws<InvalidOperationException>(
            () => OrganizationAccountGovernance.Rehydrate(
                default,
                0,
                AdministratorBootstrapStatus.Unsealed,
                null,
                [proposer, target],
                []));
        Assert.Throws<InvalidOperationException>(
            () => RoleChangeRequest.Rehydrate(
                default,
                organizationId,
                target.Id,
                AdministratorRoleChangeAction.Grant,
                proposer.Id,
                null,
                "Reason",
                Utc(1),
                Utc(2),
                null,
                RoleChangeRequestStatus.Pending));

        Result<OrganizationAccountGovernance> bootstrap =
            OrganizationAccountGovernance.Rehydrate(
                organizationId,
                0,
                (AdministratorBootstrapStatus)999,
                null,
                [proposer, target],
                []);
        Result<RoleChangeRequest> status = RoleChangeRequest.Rehydrate(
            RoleChangeRequestId.New(),
            organizationId,
            target.Id,
            AdministratorRoleChangeAction.Grant,
            proposer.Id,
            null,
            "Reason",
            Utc(1),
            Utc(2),
            null,
            (RoleChangeRequestStatus)999);

        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, bootstrap.Error.Code);
        Assert.Equal(AccountErrorCodes.InvalidAccountGovernanceState, status.Error.Code);
    }

    private static Result<OrganizationAccountGovernance> Rehydrate(
        OrganizationId organizationId,
        IReadOnlyCollection<Membership> memberships) =>
        OrganizationAccountGovernance.Rehydrate(
            organizationId,
            0,
            AdministratorBootstrapStatus.Unsealed,
            null,
            memberships,
            []);

    private static Membership CreateActive(OrganizationId organizationId, MembershipId? id = null)
    {
        Membership membership = CreateInvited(organizationId, id);
        Assert.True(membership.Activate(Utc(0)).IsSuccess);
        return membership;
    }

    private static Membership CreateInvited(OrganizationId organizationId, MembershipId? id = null) =>
        Membership.Invite(
            id ?? MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            true).Value;

    private static void SetStatus(Membership membership, MembershipStatus status)
    {
        FieldInfo field = typeof(Membership).GetField(
            "<Status>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(membership, status);
    }

    private static void AddRole(Membership membership, OrganizationRole role)
    {
        FieldInfo field = typeof(Membership).GetField(
            "activeRoles",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HashSet<OrganizationRole>)field.GetValue(membership)!).Add(role);
    }

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 14, hour, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        OrganizationId OrganizationId,
        OrganizationAccountGovernance Governance,
        Membership Proposer,
        Membership Approver,
        Membership Third,
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
            Assert.True(governance.BootstrapAdministrators(
                administratorCount == 3 ? [proposer, approver, third] : [proposer, approver],
                Utc(1)).IsSuccess);
            return new(organizationId, governance, proposer, approver, third, target);
        }

        public RoleChangeRequest ProposeGrant(DateTimeOffset now) =>
            Governance.ProposeAdministratorRoleChange(
                RoleChangeRequestId.New(),
                AdministratorRoleChangeAction.Grant,
                Proposer,
                Target,
                "Grant",
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
