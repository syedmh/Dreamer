using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Tests.Accounts;

public sealed class T4TwoRootIsolationRegressionTests
{
    [Fact]
    public void BootstrapAdministratorsMutatesOnlyRootA()
    {
        Seed seed = Seed.CreateUnsealed();
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);

        AdministratorBootstrapCompleted completed =
            roots.A.BootstrapAdministrators(
                [seed.Proposer, seed.Approver],
                Utc(2)).Value;

        Assert.Equal(seed.OrganizationId, completed.OrganizationId);
        Assert.True(
            new[] { seed.Proposer.Id, seed.Approver.Id }
                .SequenceEqual(completed.AdministratorMembershipIds),
            "bootstrap: command event administrator IDs changed");
        Assert.Equal(Utc(2), completed.OccurredAt);
        AssertIsolation("bootstrap", seed, roots, retained);
    }

    [Fact]
    public void DisableMembershipMutatesOnlyRootA()
    {
        Seed seed = Seed.CreateSealed(administratorCount: 3);
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);

        MembershipDisabled completed =
            roots.A.DisableMembership(seed.Proposer, seed.Third, Utc(3)).Value;

        Assert.Equal(seed.Third.Id, completed.MembershipId);
        Assert.Equal(seed.Proposer.Id, completed.ActorMembershipId);
        Assert.True(
            new[] { OrganizationRole.Admin }.SequenceEqual(completed.RevokedRoles),
            "disable: command event revoked roles changed");
        Assert.Equal(Utc(3), completed.OccurredAt);
        AssertIsolation("disable", seed, roots, retained);
    }

    [Fact]
    public void AssignFoodInchargeMutatesOnlyRootA()
    {
        Seed seed = Seed.CreateSealed();
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);

        FoodInchargeAssigned completed =
            roots.A.AssignFoodIncharge(seed.Proposer, seed.Target, Utc(3)).Value;

        Assert.Equal(seed.Target.Id, completed.MembershipId);
        Assert.Equal(seed.Proposer.Id, completed.ActorMembershipId);
        Assert.Equal(Utc(3), completed.OccurredAt);
        AssertIsolation("assign Food Incharge", seed, roots, retained);
    }

    [Fact]
    public void RevokeFoodInchargeMutatesOnlyRootA()
    {
        Seed seed = Seed.CreateSealed(targetIsFoodIncharge: true);
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);

        FoodInchargeRevoked completed =
            roots.A.RevokeFoodIncharge(seed.Proposer, seed.Target, Utc(4)).Value;

        Assert.Equal(seed.Target.Id, completed.MembershipId);
        Assert.Equal(seed.Proposer.Id, completed.ActorMembershipId);
        Assert.Equal(Utc(4), completed.OccurredAt);
        AssertIsolation("revoke Food Incharge", seed, roots, retained);
    }

    [Theory]
    [InlineData(AdministratorRoleChangeAction.Grant)]
    [InlineData(AdministratorRoleChangeAction.Revoke)]
    public void ProposeAdministratorRoleChangeMutatesOnlyRootA(
        AdministratorRoleChangeAction action)
    {
        Seed seed = Seed.CreateSealed(administratorCount: action == AdministratorRoleChangeAction.Revoke ? 3 : 2);
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);
        Membership target = action == AdministratorRoleChangeAction.Grant
            ? seed.Target
            : seed.Third;
        RoleChangeRequestId requestId = RoleChangeRequestId.New();

        RoleChangeRequest request =
            roots.A.ProposeAdministratorRoleChange(
                requestId,
                action,
                seed.Proposer,
                target,
                $"{action} administrator",
                Utc(3)).Value;
        RequestSnapshot returnedSnapshot = RequestSnapshot.Capture(request);

        Assert.Equal(requestId, request.Id);
        Assert.Equal(seed.OrganizationId, request.OrganizationId);
        Assert.Equal(target.Id, request.TargetMembershipId);
        Assert.Equal(action, request.Action);
        Assert.Equal(seed.Proposer.Id, request.ProposerMembershipId);
        Assert.Equal(RoleChangeRequestStatus.Pending, request.Status);
        Assert.Equal(returnedSnapshot, RequestSnapshot.Capture(request));
        AssertIsolation($"propose administrator {action}", seed, roots, retained);
    }

    [Theory]
    [InlineData(AdministratorRoleChangeAction.Grant)]
    [InlineData(AdministratorRoleChangeAction.Revoke)]
    public void ApproveAdministratorRoleChangeMutatesOnlyRootA(
        AdministratorRoleChangeAction action)
    {
        Seed seed = Seed.CreateWithPendingRequest(action);
        Roots roots = Roots.Create(seed);
        RetainedSurfaces retained = RetainedSurfaces.Capture(seed, roots);
        RoleChangeRequest sourceRequest = Assert.Single(seed.Requests);
        RequestSnapshot originalRequest = RequestSnapshot.Capture(sourceRequest);
        Membership target = action == AdministratorRoleChangeAction.Grant
            ? seed.Target
            : seed.Third;

        AdministratorRoleChangeApproved completed =
            roots.A.ApproveAdministratorRoleChange(
                sourceRequest,
                seed.Proposer,
                seed.Approver,
                target,
                Utc(4)).Value;

        Assert.Equal(sourceRequest.Id, completed.RequestId);
        Assert.Equal(seed.OrganizationId, completed.OrganizationId);
        Assert.Equal(target.Id, completed.TargetMembershipId);
        Assert.Equal(action, completed.Action);
        Assert.Equal(seed.Proposer.Id, completed.ProposerMembershipId);
        Assert.Equal(seed.Approver.Id, completed.ApproverMembershipId);
        Assert.Equal(Utc(4), completed.OccurredAt);
        Assert.Equal(originalRequest, RequestSnapshot.Capture(sourceRequest));
        AssertIsolation($"approve administrator {action}", seed, roots, retained);
    }

    private static void AssertIsolation(
        string command,
        Seed seed,
        Roots roots,
        RetainedSurfaces retained)
    {
        Assert.True(
            retained.SourceState == GovernanceSnapshot.Capture(seed),
            $"{command}: original rehydration membership/request inputs changed");
        Assert.True(
            retained.RootAReturnedState == GovernanceSnapshot.Capture(
                retained.RootAMemberships,
                retained.RootARequests,
                retained.RootAState),
            $"{command}: a previously returned root A snapshot changed");
        Assert.True(
            retained.RootBReturnedState == GovernanceSnapshot.Capture(
                retained.RootBMemberships,
                retained.RootBRequests,
                retained.RootBState),
            $"{command}: a previously returned root B snapshot changed");
        Assert.True(
            retained.SourceRoles.SequenceEqual(RoleSnapshot.Capture(seed.Memberships)),
            $"{command}: original input nested ActiveRoles changed");
        Assert.True(
            retained.RootARoles.SequenceEqual(RoleSnapshot.Capture(retained.RootAMemberships)),
            $"{command}: returned root A nested ActiveRoles changed");
        Assert.True(
            retained.RootBRoles.SequenceEqual(RoleSnapshot.Capture(retained.RootBMemberships)),
            $"{command}: returned root B nested ActiveRoles changed");
        Assert.True(
            retained.RootBState == GovernanceSnapshot.Capture(roots.B),
            $"{command}: identically rehydrated root B changed");
        Assert.Equal(retained.RootBState.Version, roots.B.Version);
    }

    private static Membership CreateActive(OrganizationId organizationId)
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

    private static DateTimeOffset Utc(int hour) =>
        new(2026, 8, 14, hour, 0, 0, TimeSpan.Zero);

    private sealed record Roots(
        OrganizationAccountGovernance A,
        OrganizationAccountGovernance B)
    {
        public static Roots Create(Seed seed) =>
            new(seed.Rehydrate(), seed.Rehydrate());
    }

    private sealed record Seed(
        OrganizationId OrganizationId,
        long Version,
        AdministratorBootstrapStatus BootstrapStatus,
        DateTimeOffset? BootstrapSealedAt,
        IReadOnlyCollection<Membership> Memberships,
        IReadOnlyCollection<RoleChangeRequest> Requests,
        Membership Proposer,
        Membership Approver,
        Membership Third,
        Membership Target)
    {
        public static Seed CreateUnsealed()
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership proposer = CreateActive(organizationId);
            Membership approver = CreateActive(organizationId);
            Membership third = CreateActive(organizationId);
            Membership target = CreateActive(organizationId);
            List<Membership> memberships = [proposer, approver, third, target];
            return new(
                organizationId,
                0,
                AdministratorBootstrapStatus.Unsealed,
                null,
                memberships,
                new List<RoleChangeRequest>(),
                proposer,
                approver,
                third,
                target);
        }

        public static Seed CreateSealed(
            int administratorCount = 2,
            bool targetIsFoodIncharge = false)
        {
            Seed unsealed = CreateUnsealed();
            OrganizationAccountGovernance setup = unsealed.Rehydrate();
            Membership[] administrators = administratorCount == 3
                ? [unsealed.Proposer, unsealed.Approver, unsealed.Third]
                : [unsealed.Proposer, unsealed.Approver];
            Assert.True(setup.BootstrapAdministrators(administrators, Utc(1)).IsSuccess);
            if (targetIsFoodIncharge)
            {
                Assert.True(
                    setup.AssignFoodIncharge(
                        unsealed.Proposer,
                        unsealed.Target,
                        Utc(2)).IsSuccess);
            }

            return FromSetup(setup, unsealed);
        }

        public static Seed CreateWithPendingRequest(AdministratorRoleChangeAction action)
        {
            Seed seed = CreateSealed(
                administratorCount: action == AdministratorRoleChangeAction.Revoke ? 3 : 2);
            OrganizationAccountGovernance setup = seed.Rehydrate();
            Membership target = action == AdministratorRoleChangeAction.Grant
                ? seed.Target
                : seed.Third;
            Assert.True(
                setup.ProposeAdministratorRoleChange(
                    RoleChangeRequestId.New(),
                    action,
                    seed.Proposer,
                    target,
                    $"{action} administrator",
                    Utc(3)).IsSuccess);
            return FromSetup(setup, seed);
        }

        public OrganizationAccountGovernance Rehydrate() =>
            OrganizationAccountGovernance.Rehydrate(
                OrganizationId,
                Version,
                BootstrapStatus,
                BootstrapSealedAt,
                Memberships,
                Requests).Value;

        private static Seed FromSetup(OrganizationAccountGovernance setup, Seed identities)
        {
            IReadOnlyCollection<Membership> memberships = setup.Memberships.ToList();
            return new(
                identities.OrganizationId,
                setup.Version,
                setup.BootstrapStatus,
                setup.BootstrapSealedAt,
                memberships,
                setup.RoleChangeRequests.ToList(),
                memberships.Single(member => member.Id == identities.Proposer.Id),
                memberships.Single(member => member.Id == identities.Approver.Id),
                memberships.Single(member => member.Id == identities.Third.Id),
                memberships.Single(member => member.Id == identities.Target.Id));
        }
    }

    private sealed record RetainedSurfaces(
        GovernanceSnapshot SourceState,
        IReadOnlyCollection<Membership> RootAMemberships,
        IReadOnlyCollection<RoleChangeRequest> RootARequests,
        GovernanceSnapshot RootAState,
        GovernanceSnapshot RootAReturnedState,
        IReadOnlyCollection<Membership> RootBMemberships,
        IReadOnlyCollection<RoleChangeRequest> RootBRequests,
        GovernanceSnapshot RootBState,
        GovernanceSnapshot RootBReturnedState,
        RoleSnapshot[] SourceRoles,
        RoleSnapshot[] RootARoles,
        RoleSnapshot[] RootBRoles)
    {
        public static RetainedSurfaces Capture(Seed seed, Roots roots)
        {
            IReadOnlyCollection<Membership> aMemberships = roots.A.Memberships;
            IReadOnlyCollection<RoleChangeRequest> aRequests = roots.A.RoleChangeRequests;
            IReadOnlyCollection<Membership> bMemberships = roots.B.Memberships;
            IReadOnlyCollection<RoleChangeRequest> bRequests = roots.B.RoleChangeRequests;
            GovernanceSnapshot aState = GovernanceSnapshot.Capture(roots.A);
            GovernanceSnapshot bState = GovernanceSnapshot.Capture(roots.B);
            return new(
                GovernanceSnapshot.Capture(seed),
                aMemberships,
                aRequests,
                aState,
                GovernanceSnapshot.Capture(aMemberships, aRequests, aState),
                bMemberships,
                bRequests,
                bState,
                GovernanceSnapshot.Capture(bMemberships, bRequests, bState),
                RoleSnapshot.Capture(seed.Memberships),
                RoleSnapshot.Capture(aMemberships),
                RoleSnapshot.Capture(bMemberships));
        }
    }

    private sealed record GovernanceSnapshot(
        long Version,
        AdministratorBootstrapStatus BootstrapStatus,
        DateTimeOffset? BootstrapSealedAt,
        string Memberships,
        string Requests)
    {
        public static GovernanceSnapshot Capture(OrganizationAccountGovernance governance) =>
            new(
                governance.Version,
                governance.BootstrapStatus,
                governance.BootstrapSealedAt,
                CaptureMemberships(governance.Memberships),
                CaptureRequests(governance.RoleChangeRequests));

        public static GovernanceSnapshot Capture(Seed seed) =>
            new(
                seed.Version,
                seed.BootstrapStatus,
                seed.BootstrapSealedAt,
                CaptureMemberships(seed.Memberships),
                CaptureRequests(seed.Requests));

        public static GovernanceSnapshot Capture(
            IReadOnlyCollection<Membership> memberships,
            IReadOnlyCollection<RoleChangeRequest> requests,
            GovernanceSnapshot metadata) =>
            new(
                metadata.Version,
                metadata.BootstrapStatus,
                metadata.BootstrapSealedAt,
                CaptureMemberships(memberships),
                CaptureRequests(requests));

        private static string CaptureMemberships(IEnumerable<Membership> memberships) =>
            string.Join(
                "\n",
                memberships.Select(MembershipSnapshot.Capture));

        private static string CaptureRequests(IEnumerable<RoleChangeRequest> requests) =>
            string.Join(
                "\n",
                requests.Select(RequestSnapshot.Capture));
    }

    private sealed record MembershipSnapshot(
        MembershipId Id,
        MembershipStatus Status,
        string ActiveRoles)
    {
        public static MembershipSnapshot Capture(Membership membership) =>
            new(
                membership.Id,
                membership.Status,
                string.Join(",", membership.ActiveRoles.Order()));
    }

    private sealed record RequestSnapshot(
        RoleChangeRequestId Id,
        MembershipId TargetMembershipId,
        AdministratorRoleChangeAction Action,
        MembershipId ProposerMembershipId,
        MembershipId? ApproverMembershipId,
        string Reason,
        DateTimeOffset ProposedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ApprovedAt,
        RoleChangeRequestStatus Status)
    {
        public static RequestSnapshot Capture(RoleChangeRequest request) =>
            new(
                request.Id,
                request.TargetMembershipId,
                request.Action,
                request.ProposerMembershipId,
                request.ApproverMembershipId,
                request.Reason,
                request.ProposedAt,
                request.ExpiresAt,
                request.ApprovedAt,
                request.Status);
    }

    private sealed record RoleSnapshot(
        MembershipId MembershipId,
        string ActiveRoles)
    {
        public static RoleSnapshot[] Capture(IEnumerable<Membership> memberships) =>
            memberships
                .Select(
                    membership => new RoleSnapshot(
                        membership.Id,
                        string.Join(",", membership.ActiveRoles.Order())))
                .ToArray();
    }
}
