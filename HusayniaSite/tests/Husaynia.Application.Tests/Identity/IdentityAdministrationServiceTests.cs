using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Tests.Identity;

public sealed class IdentityAdministrationServiceTests
{
    private static readonly AdministrativeRequestActor SiteAdministrator =
        new(
            IsAuthenticated: true,
            UserId: "site-admin",
            Roles: new HashSet<string>(StringComparer.Ordinal) { RoleNames.SiteAdministrator },
            HasSatisfiedMfa: true,
            CorrelationId: "corr-site-admin");

    private static readonly AdministrativeRequestActor ReadOnlyAuditor =
        new(
            IsAuthenticated: true,
            UserId: "auditor",
            Roles: new HashSet<string>(StringComparer.Ordinal) { RoleNames.ReadOnlyAuditor },
            HasSatisfiedMfa: true,
            CorrelationId: "corr-auditor");

    private static readonly AdministrativeRequestActor DonationOperator =
        new(
            IsAuthenticated: true,
            UserId: "donation",
            Roles: new HashSet<string>(StringComparer.Ordinal) { RoleNames.DonationOperator },
            HasSatisfiedMfa: true,
            CorrelationId: "corr-donation");

    private static readonly AdministrativeRequestActor SiteAdministratorWithoutMfa =
        SiteAdministrator with { CorrelationId = "corr-no-mfa", HasSatisfiedMfa = false };

    [Fact]
    public async Task InviteDeniesReadOnlyAuditorWriteAndAuditsDenied()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.InviteAsync(
            new InviteIdentityUserCommand("invitee@example.test", [RoleNames.ContentEditor]),
            ReadOnlyAuditor,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("forbidden", result.Error.Code);
        Assert.Empty(store.InviteCommands);
        AssertFinalization(
            finalizer,
            "identity.user.invite",
            PrivilegedAttemptOutcome.Denied,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "forbidden",
                ["message"] = "The signed-in user does not have the required access for this administrative operation.",
                ["grantedAccess"] = (CapabilityAccess.Reports | CapabilityAccess.Read).ToString(),
            },
            expectedActorId: "auditor");
    }

    [Fact]
    public async Task InviteRejectsInvalidEmailBeforeStoreAndFinalizesWithCanceledRequestToken()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();

        var result = await service.InviteAsync(
            new InviteIdentityUserCommand("not-an-email", [RoleNames.ContentEditor]),
            SiteAdministrator,
            requestCancellation.Token);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_email", result.Error.Code);
        Assert.Empty(store.InviteCommands);
        AssertFinalization(
            finalizer,
            "identity.user.invite",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_email",
                ["message"] = "A valid email address is required.",
            });
    }

    [Fact]
    public async Task InviteRejectsInvalidRoleBeforeStoreAndAuditsAllowedAttempt()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.InviteAsync(
            new InviteIdentityUserCommand("invitee@example.test", ["ContentEditor "]),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_role", result.Error.Code);
        Assert.Empty(store.InviteCommands);
        AssertFinalization(
            finalizer,
            "identity.user.invite",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_role",
                ["message"] = "Assigned roles must exactly match the frozen role names.",
            });
    }

    [Fact]
    public async Task DisableRejectsInvalidUserIdBeforeStore()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.DisableAsync(
            new DisableIdentityUserCommand("not-a-guid", "stamp", null),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_user_id", result.Error.Code);
        Assert.Equal(0, store.DisableCalls);
        AssertFinalization(
            finalizer,
            "identity.user.disable",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_user_id",
                ["message"] = "A valid user identifier is required.",
            });
    }

    [Fact]
    public async Task SetRolesRejectsMissingConcurrencyStampBeforeStore()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.SetRolesAsync(
            new SetIdentityUserRolesCommand(
                "b54d89fb-1324-4f9f-b53d-3e4bb7f6c2d0",
                " ",
                [RoleNames.ContentEditor]),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_concurrency_token", result.Error.Code);
        Assert.Empty(store.RoleCommands);
        AssertFinalization(
            finalizer,
            "identity.user.roles.set",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_concurrency_token",
                ["message"] = "A concurrency token is required.",
            });
    }

    [Fact]
    public async Task DisableRejectsOversizedReasonBeforeStoreWithoutAuditingReason()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);
        var oversizedReason = new string('x', 1_001);

        var result = await service.DisableAsync(
            new DisableIdentityUserCommand(
                "b54d89fb-1324-4f9f-b53d-3e4bb7f6c2d0",
                "stamp",
                oversizedReason),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_reason", result.Error.Code);
        Assert.Equal(0, store.DisableCalls);
        AssertFinalization(
            finalizer,
            "identity.user.disable",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_reason",
                ["message"] = "The disable reason must be 1,000 characters or fewer.",
            });
        Assert.DoesNotContain(oversizedReason, Assert.Single(finalizer.Events).Details.Values);
    }

    [Fact]
    public async Task ReadAuditEventsDeniesDonationOperatorLimitedAccessAndAuditsDenied()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.ReadAuditEventsAsync(
            new ReadIdentityAuditEventsQuery(10),
            DonationOperator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("limited_access", result.Error.Code);
        Assert.Equal(0, store.ReadEventsCalls);
        AssertFinalization(
            finalizer,
            "identity.audit.events.read",
            PrivilegedAttemptOutcome.Denied,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "limited_access",
                ["message"] = "This administrative operation requires full access.",
                ["grantedAccess"] = (CapabilityAccess.Limited | CapabilityAccess.Read).ToString(),
            },
            expectedActorId: "donation");
    }

    [Fact]
    public async Task ReadAuditEventsRejectsInvalidTakeBeforeStore()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.ReadAuditEventsAsync(
            new ReadIdentityAuditEventsQuery(201),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("invalid_take", result.Error.Code);
        Assert.Equal(0, store.ReadEventsCalls);
        AssertFinalization(
            finalizer,
            "identity.audit.events.read",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "invalid_take",
                ["message"] = "Audit queries must request between 1 and 200 events.",
            });
    }

    [Fact]
    public async Task ProbeCapabilityWritesAllowedAuditAndReturnsGrantedAccess()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.ProbeCapabilityAsync(
            AdministrativeCapability.PagesAnnouncementsReligiousContent,
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            CapabilityAccess.Read | CapabilityAccess.Write,
            result.Success.GrantedAccess);
        AssertFinalization(
            finalizer,
            "identity.capability.read",
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["capability"] = AdministrativeCapability.PagesAnnouncementsReligiousContent.ToString(),
                ["policy"] = AdministrativeCapabilityAuthorizer.GetPolicyName(
                    AdministrativeCapability.PagesAnnouncementsReligiousContent),
                ["grantedAccess"] = (CapabilityAccess.Read | CapabilityAccess.Write).ToString(),
            });
    }

    [Fact]
    public async Task SetRolesNormalizesExactRolesBeforeCallingStore()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.SetRolesAsync(
            new SetIdentityUserRolesCommand(
                "b54d89fb-1324-4f9f-b53d-3e4bb7f6c2d0",
                "stamp",
                [RoleNames.MediaEditor, RoleNames.ContentEditor]),
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var command = Assert.Single(store.RoleCommands);
        Assert.Equal([RoleNames.ContentEditor, RoleNames.MediaEditor], command.Roles);
        Assert.Empty(finalizer.Events);
    }

    [Fact]
    public async Task InviteDeniesPrivilegedActorWithoutMfa()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);

        var result = await service.InviteAsync(
            new InviteIdentityUserCommand("invitee@example.test", [RoleNames.ContentEditor]),
            SiteAdministratorWithoutMfa,
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("mfa_required", result.Error.Code);
        Assert.Empty(store.InviteCommands);
        AssertFinalization(
            finalizer,
            "identity.user.invite",
            PrivilegedAttemptOutcome.Denied,
            new Dictionary<string, string?>
            {
                ["errorCode"] = "mfa_required",
                ["message"] = "Multi-factor authentication is required for privileged administration.",
                ["grantedAccess"] = (CapabilityAccess.Read | CapabilityAccess.Write).ToString(),
            });
    }

    [Fact]
    public async Task SuccessfulStoreBoundPathsLeaveAuditOwnershipToStore()
    {
        var store = new StubStore();
        var finalizer = new SpyAuditFinalizer();
        var service = CreateService(store, finalizer);
        const string userId = "b54d89fb-1324-4f9f-b53d-3e4bb7f6c2d0";

        var invite = await service.InviteAsync(
            new InviteIdentityUserCommand("invitee@example.test", [RoleNames.ContentEditor]),
            SiteAdministrator,
            CancellationToken.None);
        var disable = await service.DisableAsync(
            new DisableIdentityUserCommand(userId, "stamp", null),
            SiteAdministrator,
            CancellationToken.None);
        var roles = await service.SetRolesAsync(
            new SetIdentityUserRolesCommand(userId, "stamp", [RoleNames.ContentEditor]),
            SiteAdministrator,
            CancellationToken.None);
        var events = await service.ReadAuditEventsAsync(
            new ReadIdentityAuditEventsQuery(10),
            SiteAdministrator,
            CancellationToken.None);
        var summary = await service.ReadAuditSummaryAsync(
            SiteAdministrator,
            CancellationToken.None);

        Assert.True(invite.IsSuccess);
        Assert.True(disable.IsSuccess);
        Assert.True(roles.IsSuccess);
        Assert.True(events.IsSuccess);
        Assert.True(summary.IsSuccess);
        Assert.Empty(finalizer.Events);
        Assert.Single(store.InviteCommands);
        Assert.Equal(1, store.DisableCalls);
        Assert.Single(store.RoleCommands);
        Assert.Equal(1, store.ReadEventsCalls);
        Assert.Equal(1, store.ReadSummaryCalls);
    }

    private static IdentityAdministrationService CreateService(
        StubStore store,
        SpyAuditFinalizer finalizer) =>
        new(store, finalizer, new AdministrativeCapabilityAuthorizer());

    private static void AssertFinalization(
        SpyAuditFinalizer finalizer,
        string expectedAction,
        PrivilegedAttemptOutcome expectedOutcome,
        Dictionary<string, string?> expectedDetails,
        string? expectedActorId = "site-admin")
    {
        var audit = Assert.Single(finalizer.Events);
        Assert.Equal(expectedAction, audit.Descriptor.Action);
        Assert.Equal(expectedActorId, audit.Descriptor.ActorId);
        Assert.Equal(expectedOutcome, audit.Outcome);
        Assert.Null(audit.CompletePersistenceAsync);
        Assert.Equal(expectedDetails.Count, audit.Details.Count);
        foreach (var expectedDetail in expectedDetails)
        {
            Assert.True(audit.Details.TryGetValue(expectedDetail.Key, out var actualValue));
            Assert.Equal(expectedDetail.Value, actualValue);
        }
    }

    private sealed class SpyAuditFinalizer : IIdentityAuditFinalizer
    {
        internal List<CapturedAuditFinalization> Events { get; } = [];

        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            Events.Add(new CapturedAuditFinalization(
                descriptor,
                outcome,
                details,
                completePersistenceAsync));
            return Task.CompletedTask;
        }
    }

    private sealed class StubStore : IIdentityAdministrationStore
    {
        internal List<InviteIdentityUserCommand> InviteCommands { get; } = [];

        internal List<SetIdentityUserRolesCommand> RoleCommands { get; } = [];

        internal int DisableCalls { get; private set; }

        internal int ReadEventsCalls { get; private set; }

        internal int ReadSummaryCalls { get; private set; }

        public Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
            InviteIdentityUserCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            InviteCommands.Add(command);
            return Task.FromResult(Result.Succeed<InviteIdentityUserReceipt, IdentityAdministrationError>(
                new InviteIdentityUserReceipt(
                    new IdentityUserView(
                        "user-1",
                        command.Email,
                        false,
                        false,
                        false,
                        "stamp-1",
                        command.Roles.Order(StringComparer.Ordinal).ToArray()),
                    true,
                    "token",
                    DateTimeOffset.UtcNow.AddDays(7))));
        }

        public Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
            DisableIdentityUserCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            DisableCalls++;
            return Task.FromResult(Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(
                    new IdentityUserView(
                        command.UserId,
                        "user@example.test",
                        true,
                        false,
                        true,
                        "stamp-2",
                        []))));
        }

        public Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
            SetIdentityUserRolesCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            RoleCommands.Add(command);
            return Task.FromResult(Result.Succeed<IdentityAccountMutationReceipt, IdentityAdministrationError>(
                new IdentityAccountMutationReceipt(
                    new IdentityUserView(
                        command.UserId,
                        "user@example.test",
                        false,
                        false,
                        true,
                        "stamp-2",
                        command.Roles.Order(StringComparer.Ordinal).ToArray()))));
        }

        public Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>> ReadAuditEventsAsync(
            ReadIdentityAuditEventsQuery query,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            ReadEventsCalls++;
            return Task.FromResult(Result.Succeed<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>(
                Array.Empty<IdentityAuditEventView>()));
        }

        public Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            ReadSummaryCalls++;
            return Task.FromResult(Result.Succeed<IdentityAuditSummaryView, IdentityAdministrationError>(
                new IdentityAuditSummaryView(1, 1, 0, DateTimeOffset.UtcNow)));
        }
    }

    private sealed record CapturedAuditFinalization(
        IdentityAuditDescriptor Descriptor,
        PrivilegedAttemptOutcome Outcome,
        IReadOnlyDictionary<string, string?> Details,
        Func<CancellationToken, Task>? CompletePersistenceAsync);
}
