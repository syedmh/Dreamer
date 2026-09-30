using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAdministrationStoreAuditFinalizationTests
{
    [Fact]
    public async Task InviteSuccessAppendsBeforeCommitAndCommitsOnlyThroughCallback()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            InviteSuccessAppendsBeforeCommitAndCommitsOnlyThroughCallback));

        var result = await harness.Store.InviteAsync(
            new InviteIdentityUserCommand("invite-audit@example.test", [RoleNames.ContentEditor]),
            Audit("identity.user.invite", "corr-invite-success"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var attempt = Assert.Single(harness.Finalizer.Attempts);
        Assert.Equal(["append", "commit"], attempt.Sequence);
        Assert.Equal(1, attempt.CallbackInvocations);
        Assert.Equal("created", attempt.Details["result"]);
        Assert.NotNull(await harness.Factory.FindUserAsync("invite-audit@example.test"));
        Assert.Single(
            await harness.Factory.ReadAuditEventsAsync(),
            entry => entry.CorrelationId == "corr-invite-success");
    }

    [Fact]
    public async Task DisableAndRoleSuccessEachFinalizeOnceWithCommitCallback()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            DisableAndRoleSuccessEachFinalizeOnceWithCommitCallback));
        var disableUser = await harness.Factory.SeedUserAsync(
            "disable-audit@example.test",
            "DisableAudit!234",
            [],
            enableMfa: false);
        var roleUser = await harness.Factory.SeedUserAsync(
            "role-audit@example.test",
            "RoleAuditPass!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        var disable = await harness.Store.DisableAsync(
            new DisableIdentityUserCommand(
                disableUser.UserId,
                disableUser.ConcurrencyStamp,
                "security review"),
            Audit("identity.user.disable", "corr-disable-success"),
            CancellationToken.None);
        var roles = await harness.Store.SetRolesAsync(
            new SetIdentityUserRolesCommand(
                roleUser.UserId,
                roleUser.ConcurrencyStamp,
                [RoleNames.EventEditor]),
            Audit("identity.user.roles", "corr-role-success"),
            CancellationToken.None);

        Assert.True(disable.IsSuccess);
        Assert.True(roles.IsSuccess);
        Assert.Equal(2, harness.Finalizer.Attempts.Count);
        Assert.All(harness.Finalizer.Attempts, attempt =>
        {
            Assert.Equal(["append", "commit"], attempt.Sequence);
            Assert.Equal(1, attempt.CallbackInvocations);
        });
        Assert.True((await harness.Factory.ReadUserViewAsync(disableUser.UserId)).IsDisabled);
        Assert.Equal(
            [RoleNames.EventEditor],
            (await harness.Factory.ReadUserViewAsync(roleUser.UserId)).Roles);
    }

    [Fact]
    public async Task ReadSuccessesFinalizeOnceWithoutCommitCallbacks()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            ReadSuccessesFinalizeOnceWithoutCommitCallbacks));

        var events = await harness.Store.ReadAuditEventsAsync(
            new ReadIdentityAuditEventsQuery(10),
            Audit("identity.audit.read", "corr-read-events"),
            CancellationToken.None);
        var summary = await harness.Store.ReadAuditSummaryAsync(
            Audit("identity.audit.summary", "corr-read-summary"),
            CancellationToken.None);

        Assert.True(events.IsSuccess);
        Assert.True(summary.IsSuccess);
        Assert.Equal(2, harness.Finalizer.Attempts.Count);
        Assert.All(harness.Finalizer.Attempts, attempt =>
        {
            Assert.Equal(["append"], attempt.Sequence);
            Assert.Equal(0, attempt.CallbackInvocations);
        });
    }

    [Fact]
    public async Task ConflictAndDependencyFailuresFinalizeOnceWithoutCommitCallbacks()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            ConflictAndDependencyFailuresFinalizeOnceWithoutCommitCallbacks));
        var user = await harness.Factory.SeedUserAsync(
            "conflict-audit@example.test",
            "ConflictAudit!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        var invite = await harness.Store.InviteAsync(
            new InviteIdentityUserCommand("dependency-audit@example.test", ["MissingRole"]),
            Audit("identity.user.invite", "corr-dependency"),
            CancellationToken.None);
        var disable = await harness.Store.DisableAsync(
            new DisableIdentityUserCommand(user.UserId, "stale-stamp", null),
            Audit("identity.user.disable", "corr-disable-conflict"),
            CancellationToken.None);
        var roles = await harness.Store.SetRolesAsync(
            new SetIdentityUserRolesCommand(
                user.UserId,
                "stale-stamp",
                [RoleNames.EventEditor]),
            Audit("identity.user.roles", "corr-role-conflict"),
            CancellationToken.None);

        Assert.Equal("role_not_seeded", invite.Error.Code);
        Assert.Equal("concurrency_conflict", disable.Error.Code);
        Assert.Equal("concurrency_conflict", roles.Error.Code);
        Assert.Equal(3, harness.Finalizer.Attempts.Count);
        Assert.All(harness.Finalizer.Attempts, attempt =>
        {
            Assert.Equal(["append"], attempt.Sequence);
            Assert.Equal(0, attempt.CallbackInvocations);
            Assert.Equal("failed", attempt.Details["result"]);
        });
        Assert.Null(await harness.Factory.FindUserAsync("dependency-audit@example.test"));
    }

    [Fact]
    public async Task SelectedFailureAuditIsIndependentOfRequestCancellation()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            SelectedFailureAuditIsIndependentOfRequestCancellation));
        var user = await harness.Factory.SeedUserAsync(
            "cancel-audit@example.test",
            "CancelAuditPass!234",
            [],
            enableMfa: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Store.DisableAsync(
            new DisableIdentityUserCommand(user.UserId, "stale-stamp", null),
            Audit("identity.user.disable", "corr-cancelled"),
            cancellation.Token);

        Assert.Equal("concurrency_conflict", result.Error.Code);
        var attempt = Assert.Single(harness.Finalizer.Attempts);
        Assert.Equal(["append"], attempt.Sequence);
        Assert.Single(
            await harness.Factory.ReadAuditEventsAsync(),
            entry => entry.CorrelationId == "corr-cancelled");
    }

    [Fact]
    public async Task AuditFailureRollsBackMutationAndIsNotReclassifiedOrReaudited()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            AuditFailureRollsBackMutationAndIsNotReclassifiedOrReaudited));
        harness.Finalizer.FailureMode = FinalizerFailureMode.BeforeAppend;

        var exception = await Assert.ThrowsAsync<IdentityAuditFinalizationException>(() =>
            harness.Store.InviteAsync(
                new InviteIdentityUserCommand(
                    "audit-failure@example.test",
                    [RoleNames.ContentEditor]),
                Audit("identity.user.invite", "corr-audit-failure"),
                CancellationToken.None));

        Assert.Equal("audit finalization failed", exception.Message);
        Assert.Single(harness.Finalizer.Attempts);
        Assert.Equal(0, harness.Finalizer.Attempts[0].CallbackInvocations);
        Assert.Null(await harness.Factory.FindUserAsync("audit-failure@example.test"));
        Assert.DoesNotContain(
            await harness.Factory.ReadAuditEventsAsync(),
            entry => entry.CorrelationId == "corr-audit-failure");
    }

    [Fact]
    public async Task CommitCallbackFailureRollsBackMutationAndAuditWithoutSecondAttempt()
    {
        await using var harness = await StoreHarness.CreateAsync(nameof(
            CommitCallbackFailureRollsBackMutationAndAuditWithoutSecondAttempt));
        var user = await harness.Factory.SeedUserAsync(
            "callback-failure@example.test",
            "CallbackFail!234",
            [],
            enableMfa: false);
        harness.Finalizer.FailureMode = FinalizerFailureMode.CancelCommit;

        await Assert.ThrowsAsync<IdentityAuditFinalizationException>(() =>
            harness.Store.DisableAsync(
                new DisableIdentityUserCommand(
                    user.UserId,
                    user.ConcurrencyStamp,
                    "callback failure"),
                Audit("identity.user.disable", "corr-callback-failure"),
                CancellationToken.None));

        var attempt = Assert.Single(harness.Finalizer.Attempts);
        Assert.Equal(["append", "commit"], attempt.Sequence);
        Assert.Equal(1, attempt.CallbackInvocations);
        Assert.False((await harness.Factory.ReadUserViewAsync(user.UserId)).IsDisabled);
        Assert.DoesNotContain(
            await harness.Factory.ReadAuditEventsAsync(),
            entry => entry.CorrelationId == "corr-callback-failure");
    }

    private static IdentityAuditDescriptor Audit(string action, string correlationId) =>
        new(
            "audit-test-actor",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            action,
            "identity_user",
            null,
            correlationId);

    private sealed class StoreHarness : IAsyncDisposable
    {
        private readonly AsyncServiceScope scope;
        private readonly IdentitySqlServerTestDatabase database;

        private StoreHarness(
            IdentitySqlServerTestDatabase database,
            IdentityWebApplicationFactory factory,
            AsyncServiceScope scope,
            RecordingFinalizer finalizer,
            IdentityAdministrationStore store)
        {
            this.database = database;
            Factory = factory;
            this.scope = scope;
            Finalizer = finalizer;
            Store = store;
        }

        internal IdentityWebApplicationFactory Factory { get; }

        internal RecordingFinalizer Finalizer { get; }

        internal IdentityAdministrationStore Store { get; }

        internal static async Task<StoreHarness> CreateAsync(string testName)
        {
            var database = await IdentitySqlServerTestDatabase.CreateAsync(testName);
            var factory = new IdentityWebApplicationFactory(database);
            await factory.EnsureFrozenRolesAsync();
            var scope = factory.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var writer = services.GetRequiredService<IAuditWriter>();
            var finalizer = new RecordingFinalizer(writer);
            var store = new IdentityAdministrationStore(
                services.GetRequiredService<HusayniaIdentityDbContext>(),
                services.GetRequiredService<UserManager<HusayniaIdentityUser>>(),
                services.GetRequiredService<RoleManager<HusayniaIdentityRole>>(),
                finalizer,
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<IdentityModuleOptions>());
            return new StoreHarness(database, factory, scope, finalizer, store);
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private sealed class RecordingFinalizer(IAuditWriter writer) : IIdentityAuditFinalizer
    {
        internal List<FinalizationAttempt> Attempts { get; } = [];

        internal FinalizerFailureMode FailureMode { get; set; }

        public async Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            var attempt = new FinalizationAttempt(
                descriptor,
                outcome,
                new Dictionary<string, string?>(details, StringComparer.Ordinal));
            Attempts.Add(attempt);

            if (FailureMode == FinalizerFailureMode.BeforeAppend)
            {
                throw new IdentityAuditFinalizationException("audit finalization failed");
            }

            await writer.AppendAsync(
                descriptor,
                outcome,
                details,
                CancellationToken.None);
            attempt.Sequence.Add("append");

            if (completePersistenceAsync is null)
            {
                return;
            }

            attempt.Sequence.Add("commit");
            attempt.CallbackInvocations++;
            try
            {
                if (FailureMode == FinalizerFailureMode.CancelCommit)
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    await completePersistenceAsync(cancellation.Token);
                }
                else
                {
                    await completePersistenceAsync(CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                throw new IdentityAuditFinalizationException(
                    "audit finalization failed",
                    exception);
            }
        }
    }

    private sealed class FinalizationAttempt(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details)
    {
        internal IdentityAuditDescriptor Descriptor { get; } = descriptor;

        internal PrivilegedAttemptOutcome Outcome { get; } = outcome;

        internal IReadOnlyDictionary<string, string?> Details { get; } = details;

        internal List<string> Sequence { get; } = [];

        internal int CallbackInvocations { get; set; }
    }

    private enum FinalizerFailureMode
    {
        None,
        BeforeAppend,
        CancelCommit,
    }
}
