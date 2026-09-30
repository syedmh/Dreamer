using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Abstractions.Time;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Tests.Admin;

public sealed class AdministratorMembershipServiceTests
{
    [Fact]
    public async Task ListAsyncReturnsPagedMembersAndPendingRoleRequests()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership proposer = ActiveMembership(organizationId, "Manager");
        Membership approver = ActiveMembership(organizationId, "Approver");
        Membership target = ActiveMembership(organizationId, "Target");

        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [proposer, approver, target],
            root =>
            {
                Assert.True(
                    root.BootstrapAdministrators(
                        root.Memberships.Where(
                            membership => membership.Id == proposer.Id || membership.Id == approver.Id).ToArray(),
                        Utc(1)).IsSuccess);
                Assert.True(
                    root.ProposeAdministratorRoleChange(
                        RoleChangeRequestId.New(),
                        AdministratorRoleChangeAction.Grant,
                        root.Memberships.Single(membership => membership.Id == proposer.Id),
                        root.Memberships.Single(membership => membership.Id == target.Id),
                        "Promote the target.",
                        Utc(2)).IsSuccess);
            });
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(UserId.New(), proposer.Id, organizationId));

        Result<AdminMembersPage> result = await service.ListAsync(
            new ListAdminMembersCommand(Cursor: null, PageSize: 2));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(governance.Version, result.Value.Version);
        Assert.Equal(2, result.Value.Items.Count);
        Assert.NotNull(result.Value.NextCursor);
        AdminMembersPage secondPage = (await service.ListAsync(
            new ListAdminMembersCommand(result.Value.NextCursor, PageSize: 2))).Value;
        AdminMemberSummary targetSummary = Assert.Single(
            result.Value.Items.Concat(secondPage.Items),
            item => item.Id == target.Id);
        AdminRoleChangeRequestSummary request = Assert.Single(targetSummary.PendingAdministratorRoleRequests);
        Assert.Equal(AdministratorRoleChangeAction.Grant, request.Action);
        Assert.Equal(proposer.Id, request.ProposerMembershipId);
    }

    [Fact]
    public async Task IssueInvitationAsyncPersistsGeneratedInvitationAndAuditForAdministrators()
    {
        OrganizationId organizationId = OrganizationId.New();
        UserId actorUserId = UserId.New();
        MembershipId actorMembershipId = MembershipId.New();
        RecordingMembershipRepository repository = new()
        {
            ResolveActiveActorResult = Result.Success(
                new ActiveMembershipContext(
                    actorUserId,
                    actorMembershipId,
                    organizationId,
                    "Manager",
                    true,
                    [OrganizationRole.Admin],
                    "Org",
                    "America/Los_Angeles")),
        };
        RecordingStepUpVerifier stepUpVerifier = new();
        DateTimeOffset now = Utc(10);
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(actorUserId, actorMembershipId, organizationId),
            stepUpVerifier,
            now);

        Result<IssuedMembershipInvitation> result = await service.IssueInvitationAsync(
            new IssueMembershipInvitationCommand("invitee@example.test", 24),
            new StepUpToken("invitation-step-up"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.NotEqual(Guid.Empty, result.Value.MembershipId.Value);
        Assert.Equal(now.AddHours(24), result.Value.ExpiresAt);
        Assert.Matches("^[A-Za-z0-9_-]{64}$", result.Value.InvitationToken);
        Assert.Equal(MemberAdministrationStepUpPurposes.InvitationIssue, stepUpVerifier.LastPurpose);
        Assert.NotNull(repository.InvitedMembershipRequest);
        Assert.Equal("invitee@example.test", repository.InvitedMembershipRequest!.Email);
        Assert.Equal(actorMembershipId, repository.InvitedMembershipRequest.IssuedByMembershipId);
        AuditEntry audit = Assert.Single(repository.InvitedMembershipRequest.Effects.AuditEntries);
        Assert.Equal("admin_invitation_issued", audit.Action);
        Assert.Equal(actorMembershipId, audit.ActorMembershipId);
    }

    [Fact]
    public async Task DisableMembershipAsyncConsumesMembershipDisablePurposeBeforeSaving()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership actor = ActiveMembership(organizationId, "Manager");
        Membership second = ActiveMembership(organizationId, "Approver");
        Membership target = ActiveMembership(organizationId, "Target");
        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [actor, second, target],
            root => Assert.True(
                root.BootstrapAdministrators(
                    root.Memberships.Where(
                        membership => membership.Id == actor.Id || membership.Id == second.Id).ToArray(),
                    Utc(1)).IsSuccess));
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        RecordingStepUpVerifier stepUpVerifier = new();
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(UserId.New(), actor.Id, organizationId),
            stepUpVerifier,
            utcNow: Utc(3));

        Result<VersionedResult<AdminMemberSummary>> result = await service.DisableMembershipAsync(
            new DisableMembershipCommand(target.Id, "Disable the target."),
            governance.OriginalVersion,
            new StepUpToken("disable-step-up"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(MemberAdministrationStepUpPurposes.MembershipDisable, stepUpVerifier.LastPurpose);
        Assert.Equal(1, repository.SaveGovernanceCount);
    }

    [Fact]
    public async Task AssignFoodInchargeAsyncReturnsStaleVersionBeforeConsumingStepUpOrSaving()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership actor = ActiveMembership(organizationId, "Manager");
        Membership second = ActiveMembership(organizationId, "Approver");
        Membership target = ActiveMembership(organizationId, "Target");
        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [actor, second, target],
            root => Assert.True(
                root.BootstrapAdministrators(
                    root.Memberships.Where(
                        membership => membership.Id == actor.Id || membership.Id == second.Id).ToArray(),
                    Utc(1)).IsSuccess));
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        RecordingStepUpVerifier stepUpVerifier = new();
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(UserId.New(), actor.Id, organizationId),
            stepUpVerifier: stepUpVerifier);

        Result<long> result = await service.AssignFoodInchargeAsync(
            new AssignFoodInchargeCommand(target.Id, "Grant Food Incharge."),
            expectedVersion: governance.OriginalVersion - 1,
            new StepUpToken("step-up-token"));

        Assert.True(result.IsFailure);
        Assert.Equal("stale_version", result.Error.Code);
        Assert.Equal(0, stepUpVerifier.ConsumeCount);
        Assert.Equal(0, repository.SaveGovernanceCount);
    }

    [Fact]
    public async Task AssignFoodInchargeAsyncConsumesStepUpAndPersistsNotificationAuditAndOutbox()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership actor = ActiveMembership(organizationId, "Manager");
        Membership second = ActiveMembership(organizationId, "Approver");
        Membership target = ActiveMembership(organizationId, "Target");
        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [actor, second, target],
            root => Assert.True(
                root.BootstrapAdministrators(
                    root.Memberships.Where(
                        membership => membership.Id == actor.Id || membership.Id == second.Id).ToArray(),
                    Utc(1)).IsSuccess));
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        RecordingStepUpVerifier stepUpVerifier = new();
        DateTimeOffset now = Utc(5);
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(UserId.New(), actor.Id, organizationId),
            stepUpVerifier,
            now);

        Result<long> result = await service.AssignFoodInchargeAsync(
            new AssignFoodInchargeCommand(target.Id, "Grant Food Incharge."),
            governance.OriginalVersion,
            new StepUpToken("step-up-token"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(MemberAdministrationStepUpPurposes.GovernanceAssign, stepUpVerifier.LastPurpose);
        Assert.Equal(1, repository.SaveGovernanceCount);
        Assert.Equal(actor.Id, repository.SavedActorMembershipId);
        Assert.Equal(now, repository.SavedOccurredAt);
        Assert.Single(repository.SavedEffects!.Notifications);
        Assert.Single(repository.SavedEffects.AuditEntries);
        Assert.Single(repository.SavedEffects.OutboxMessages);
    }

    [Fact]
    public async Task ApproveAdministratorRoleChangeAsyncUsesApprovalPurposeAndNotifiesProposerAndTarget()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership proposer = ActiveMembership(organizationId, "Manager");
        Membership approver = ActiveMembership(organizationId, "Approver");
        Membership target = ActiveMembership(organizationId, "Target");

        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [proposer, approver, target],
            root =>
            {
                Assert.True(
                    root.BootstrapAdministrators(
                        root.Memberships.Where(
                            membership => membership.Id == proposer.Id || membership.Id == approver.Id).ToArray(),
                        Utc(1)).IsSuccess);
                Assert.True(
                    root.ProposeAdministratorRoleChange(
                        RoleChangeRequestId.New(),
                        AdministratorRoleChangeAction.Grant,
                        root.Memberships.Single(membership => membership.Id == proposer.Id),
                        root.Memberships.Single(membership => membership.Id == target.Id),
                        "Promote the target.",
                        Utc(2)).IsSuccess);
            });
        RoleChangeRequest request = Assert.Single(governance.RoleChangeRequests);
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        RecordingStepUpVerifier stepUpVerifier = new();
        AdministratorMembershipService service = CreateService(
            repository,
            new StubCurrentActor(UserId.New(), approver.Id, organizationId),
            stepUpVerifier,
            utcNow: Utc(3));

        Result<VersionedResult<AdminRoleChangeRequestSummary>> result =
            await service.ApproveAdministratorRoleChangeAsync(
                new ApproveAdministratorRoleChangeCommand(request.Id, "Second approval."),
                governance.OriginalVersion,
                new StepUpToken("approval-token"));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(MemberAdministrationStepUpPurposes.GovernanceApprove, stepUpVerifier.LastPurpose);
        Assert.Equal(RoleChangeRequestStatus.Approved, result.Value.Value.Status);
        Assert.Equal(2, repository.SavedEffects!.Notifications.Count);
        Assert.Contains(
            repository.SavedEffects.Notifications,
            notification => notification.RecipientMembershipId == proposer.Id);
        Assert.Contains(
            repository.SavedEffects.Notifications,
            notification => notification.RecipientMembershipId == target.Id);
    }

    [Fact]
    public async Task BootstrapAdministratorsPersistsSealStateAndUsesFirstAdministratorAsAuditActor()
    {
        OrganizationId organizationId = OrganizationId.New();
        Membership first = ActiveMembership(organizationId, "First");
        Membership second = ActiveMembership(organizationId, "Second");
        Membership third = ActiveMembership(organizationId, "Third");
        OrganizationAccountGovernance governance = PersistedGovernance(
            organizationId,
            [first, second, third]);
        RecordingMembershipRepository repository = new()
        {
            GovernanceResult = Result.Success(governance),
        };
        DateTimeOffset now = Utc(7);
        AdministratorBootstrapService service = new(
            new RecordingUnitOfWork(),
            repository,
            new StubClock(now));

        Result<BootstrapAdministratorsResult> result = await service.ExecuteAsync(
            new BootstrapAdministratorsCommand(
                organizationId,
                [first.Id, second.Id],
                "Bootstrap the initial administrators."));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(AdministratorBootstrapStatus.Sealed, repository.SavedGovernance!.BootstrapStatus);
        Assert.Equal(first.Id, repository.SavedActorMembershipId);
        AuditEntry audit = Assert.Single(repository.SavedEffects!.AuditEntries);
        Assert.Equal(first.Id, audit.ActorMembershipId);
        Assert.Equal("administrator_bootstrap_completed", audit.Action);
    }

    private static AdministratorMembershipService CreateService(
        RecordingMembershipRepository repository,
        StubCurrentActor currentActor,
        RecordingStepUpVerifier? stepUpVerifier = null,
        DateTimeOffset? utcNow = null) =>
        new(
            new RecordingUnitOfWork(),
            repository,
            currentActor,
            stepUpVerifier ?? new RecordingStepUpVerifier(),
            new StubClock(utcNow ?? Utc(1)));

    private static Membership ActiveMembership(OrganizationId organizationId, string displayName)
    {
        Result<Membership> membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            displayName,
            eligibleAsNamedParticipant: true);
        Assert.True(membership.IsSuccess, membership.IsFailure ? membership.Error.Message : null);
        Assert.True(membership.Value.Activate(DateTimeOffset.UnixEpoch).IsSuccess);
        return membership.Value;
    }

    private static OrganizationAccountGovernance PersistedGovernance(
        OrganizationId organizationId,
        IReadOnlyCollection<Membership> memberships,
        Action<OrganizationAccountGovernance>? mutate = null)
    {
        Result<OrganizationAccountGovernance> governance = OrganizationAccountGovernance.Rehydrate(
            organizationId,
            version: 0,
            AdministratorBootstrapStatus.Unsealed,
            bootstrapSealedAt: null,
            memberships,
            []);
        Assert.True(governance.IsSuccess, governance.IsFailure ? governance.Error.Message : null);
        mutate?.Invoke(governance.Value);
        Result<OrganizationAccountGovernance> persisted = OrganizationAccountGovernance.Rehydrate(
            governance.Value.OrganizationId,
            governance.Value.Version,
            governance.Value.BootstrapStatus,
            governance.Value.BootstrapSealedAt,
            governance.Value.Memberships,
            governance.Value.RoleChangeRequests);
        Assert.True(persisted.IsSuccess, persisted.IsFailure ? persisted.Error.Message : null);
        return persisted.Value;
    }

    private static DateTimeOffset Utc(int minutes) =>
        new(2026, 8, 16, 18, minutes, 0, TimeSpan.Zero);

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public async ValueTask<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<Result<T>>> operation,
            CancellationToken cancellationToken = default) =>
            await operation(cancellationToken);
    }

    private sealed class RecordingMembershipRepository : IMembershipRepository
    {
        public Result<OrganizationAccountGovernance>? GovernanceResult { get; set; }
        public Result<ActiveMembershipContext>? ResolveActiveActorResult { get; set; }
        public Result SaveGovernanceResult { get; set; } = Result.Success();
        public Result IssueInvitationResult { get; set; } = Result.Success();

        public IssueMembershipInvitationPersistenceRequest? InvitedMembershipRequest { get; private set; }
        public int SaveGovernanceCount { get; private set; }
        public OrganizationAccountGovernance? SavedGovernance { get; private set; }
        public MembershipId SavedActorMembershipId { get; private set; }
        public DateTimeOffset SavedOccurredAt { get; private set; }
        public MembershipAdministrationPersistenceEffects? SavedEffects { get; private set; }

        public ValueTask<Result<InvitationAcceptanceContext>> GetInvitationAcceptanceContextAsync(
            string invitationToken,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result.Failure<InvitationAcceptanceContext>(
                    MemberAdministrationErrorCodes.Validation("Not used by these tests.")));

        public ValueTask<Result> AcceptInvitationAsync(
            string invitationToken,
            MembershipId membershipId,
            string displayName,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
            UserId userId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ResolveActiveActorResult
                ?? Result.Failure<ActiveMembershipContext>(
                    MemberAdministrationErrorCodes.Validation("Not configured.")));

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                ResolveActiveActorResult
                ?? Result.Failure<ActiveMembershipContext>(
                    MemberAdministrationErrorCodes.Validation("Not configured.")));

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                GovernanceResult
                ?? Result.Failure<OrganizationAccountGovernance>(
                    MemberAdministrationErrorCodes.Validation("Not configured.")));

        public ValueTask<Result> SaveGovernanceAsync(
            OrganizationAccountGovernance aggregate,
            MembershipId actorMembershipId,
            DateTimeOffset occurredAt,
            MembershipAdministrationPersistenceEffects effects,
            CancellationToken cancellationToken = default)
        {
            SaveGovernanceCount++;
            SavedGovernance = aggregate;
            SavedActorMembershipId = actorMembershipId;
            SavedOccurredAt = occurredAt;
            SavedEffects = effects;
            return ValueTask.FromResult(SaveGovernanceResult);
        }

        public ValueTask<Result> IssueInvitationAsync(
            IssueMembershipInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default)
        {
            InvitedMembershipRequest = request;
            return ValueTask.FromResult(IssueInvitationResult);
        }
    }

    private sealed class RecordingStepUpVerifier : IStepUpVerifier
    {
        public int ConsumeCount { get; private set; }
        public StepUpPurpose? LastPurpose { get; private set; }

        public ValueTask<Result<StepUpGrant>> IssueAsync(
            UserId userId,
            string credential,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result.Failure<StepUpGrant>(
                    MemberAdministrationErrorCodes.Validation("Not used by these tests.")));

        public ValueTask<Result> ConsumeAsync(
            UserId userId,
            StepUpToken token,
            StepUpPurpose purpose,
            CancellationToken cancellationToken = default)
        {
            ConsumeCount++;
            LastPurpose = purpose;
            return ValueTask.FromResult(Result.Success());
        }
    }

    private sealed record StubCurrentActor(
        UserId UserId,
        MembershipId MembershipId,
        OrganizationId OrganizationId) : ICurrentActor;

    private sealed class StubClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
