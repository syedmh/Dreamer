using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Identity.Services;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using HusayniaTabruk.IntegrationTests.Signups.Decisions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Threads;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Threads")]
[Trait("Category", "T19")]
public sealed class ThreadIntegrationTests : PostgresPersistenceTest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task SenderDisplayLookupReturnsOnlyRequestedSameOrganizationMemberships()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using var context = database.CreateContext();
        PostgresAuthenticationMembershipRepository repository = new(
            context,
            new MutableClock(seed.Now));

        var result = await repository.GetThreadSenderDisplaysAsync(
            seed.OrganizationId,
            [
                seed.ManagerMembershipId,
                seed.MemberMembershipId,
            ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal("Manager", result.Value[seed.ManagerMembershipId]);
        Assert.Equal("Member", result.Value[seed.MemberMembershipId]);
    }

    [RequiresPostgresFact]
    public async Task ParticipantAccessTracksLiveApprovalAndWithdrawalState()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Approved,
            seed.Seed.Now.AddMinutes(1));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(2));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);

        using HttpResponseMessage approved = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            member.AccessToken);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Withdrawn,
            seed.Seed.Now.AddMinutes(3));

        using HttpResponseMessage withdrawn = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            member.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, withdrawn.StatusCode);
    }

    [RequiresPostgresTheory]
    [InlineData(SignupStatus.Pending)]
    [InlineData(SignupStatus.Waitlisted)]
    [InlineData(SignupStatus.Declined)]
    [InlineData(SignupStatus.Cancelled)]
    public async Task NonApprovedSignupStatesAreConcealed(SignupStatus status)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            status,
            seed.Seed.Now.AddMinutes(1));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(2));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);

        using HttpResponseMessage response = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            member.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task MissingThreadIsConcealedAndIsNotAutoProvisioned()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.DeleteThreadAsync(database, seed.Seed.ThreadId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.DateThreads.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task OrdinaryPostUsesCasAllowsOnlyExactReplayAndCreatesNoOutbox()
    {
        const string body = "Body that must stay out of logs and outbox.";
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await using (TabrukDbContext beforeContext = database.CreateContext())
        {
            Assert.Empty(await beforeContext.OutboxMessages.ToListAsync());
        }

        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage created = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            key,
            expectedVersion: 0,
            body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);

        using HttpResponseMessage replay = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            key,
            expectedVersion: 0,
            body);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("\"1\"", replay.Headers.ETag?.Tag);

        using HttpResponseMessage stale = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 0,
            body: "Stale body.");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Single(await context.ThreadMessages.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
        Assert.DoesNotContain(
            host.Logs.Records,
            record => record.Message.Contains(body, StringComparison.Ordinal));
    }

    [RequiresPostgresFact]
    public async Task OrdinaryPostHoldsManagerAuthorizationLocksUntilMutationCommits()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        const string applicationName = "t19-authorization-lock-test";
        NpgsqlConnectionStringBuilder hostConnection = new(database.ConnectionString)
        {
            ApplicationName = applicationName,
        };
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            hostConnection.ConnectionString,
            clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        NpgsqlConnection blocker =
            await ThreadIntegrationTestSupport.HoldThreadMutationBoundaryAsync(database);
        Task<HttpResponseMessage> postTask =
            ThreadIntegrationTestSupport.PostMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 0,
                body: "Authorization must remain locked through commit.");

        try
        {
            await ThreadIntegrationTestSupport.WaitForThreadMutationBoundaryAsync(
                database,
                applicationName,
                postTask);
            PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(
                () => ThreadIntegrationTestSupport.ReassignManagerWithShortLockTimeoutAsync(
                    database,
                    seed.Seed.OrganizationId,
                    seed.Seed.ServiceDateId,
                    seed.Seed.MemberMembershipId));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
        }
        finally
        {
            await ThreadIntegrationTestSupport.ReleaseThreadMutationBoundaryAsync(blocker);
        }

        using HttpResponseMessage created = await postTask;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await ThreadIntegrationTestSupport.ReassignManagerWithShortLockTimeoutAsync(
            database,
            seed.Seed.OrganizationId,
            seed.Seed.ServiceDateId,
            seed.Seed.MemberMembershipId);
        using HttpResponseMessage denied = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 1,
            body: "Revoked managers must be denied.");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task OrdinaryPostHoldsApprovedSignupLockUntilMutationCommits()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Approved,
            seed.Seed.Now.AddMinutes(1));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(2));
        const string applicationName = "t19-signup-authorization-lock-test";
        NpgsqlConnectionStringBuilder hostConnection = new(database.ConnectionString)
        {
            ApplicationName = applicationName,
        };
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            hostConnection.ConnectionString,
            clock);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);
        NpgsqlConnection blocker =
            await ThreadIntegrationTestSupport.HoldThreadMutationBoundaryAsync(database);
        Task<HttpResponseMessage> postTask =
            ThreadIntegrationTestSupport.PostMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                member.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 0,
                body: "Approved signup authorization must remain locked.");

        try
        {
            await ThreadIntegrationTestSupport.WaitForThreadMutationBoundaryAsync(
                database,
                applicationName,
                postTask);
            PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(
                () => ThreadIntegrationTestSupport.WithdrawSignupWithShortLockTimeoutAsync(
                    database,
                    seed.SignupId,
                    seed.Seed.Now.AddMinutes(3)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
        }
        finally
        {
            await ThreadIntegrationTestSupport.ReleaseThreadMutationBoundaryAsync(blocker);
        }

        using HttpResponseMessage created = await postTask;
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Withdrawn,
            seed.Seed.Now.AddMinutes(4));
        using HttpResponseMessage denied = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            member.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 1,
            body: "Withdrawn participants must be denied.");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [RequiresPostgresTheory]
    [InlineData("withdrawal", true)]
    [InlineData("manager-reassignment", false)]
    [InlineData("role-revocation", false)]
    [InlineData("membership-disable", false)]
    [InlineData("date-cancellation", false)]
    public async Task RevocationThatStartsBeforeAuthorizationWinsWithoutThreadMutation(
        string scenario,
        bool participantActor)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        if (participantActor)
        {
            await ThreadIntegrationTestSupport.SetSignupStatusAsync(
                database,
                seed.SignupId,
                SignupStatus.Approved,
                seed.Seed.Now.AddMinutes(1));
        }

        MutableClock clock = new(seed.Seed.Now.AddMinutes(2));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse actor = await SignupDecisionTestSupport.LoginAsync(
            host,
            participantActor
                ? SignupDecisionTestSupport.MemberEmail
                : SignupDecisionTestSupport.ManagerEmail);
        MembershipId actorMembershipId = participantActor
            ? seed.Seed.MemberMembershipId
            : seed.Seed.ManagerMembershipId;

        await using ThreadIntegrationTestSupport.PendingOrdinaryRevocation revocation =
            await ThreadIntegrationTestSupport.BeginOrdinaryRevocationAsync(
                database,
                scenario,
                seed.Seed.OrganizationId,
                seed.Seed.ServiceDateId,
                actorMembershipId,
                seed.Seed.MemberMembershipId,
                seed.SignupId,
                seed.Seed.Now.AddMinutes(3));
        Task<HttpResponseMessage> postTask =
            ThreadIntegrationTestSupport.PostMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                actor.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 0,
                body: $"Revocation must win: {scenario}.");

        Task completed = await Task.WhenAny(postTask, Task.Delay(TimeSpan.FromMilliseconds(500)));
        Assert.NotSame(
            postTask,
            completed);

        await revocation.CommitAsync();
        using HttpResponseMessage response = await postTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.ThreadMessages.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task OrdinaryAdministratorIsConcealedFromExistingThread()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");

        using HttpResponseMessage response = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task AdministrativeThreadRoutesReturn413ForOversizedBodies()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");
        string stepUpToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);

        using HttpRequestMessage hideRequest = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{seed.Seed.ServiceDateId}/thread/messages/{MessageId.New()}/hide")
        {
            Content = new ByteArrayContent(
                new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]),
        };
        hideRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", manager.AccessToken);
        hideRequest.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, "\"0\"");

        using HttpRequestMessage lockRequest = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{seed.Seed.ServiceDateId}/thread/lock")
        {
            Content = new ByteArrayContent(
                new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]),
        };
        lockRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", manager.AccessToken);
        lockRequest.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, "\"0\"");

        using HttpRequestMessage privilegedRequest = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/admin/moderation/thread-reads")
        {
            Content = new ByteArrayContent(
                new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]),
        };
        privilegedRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        privilegedRequest.Headers.TryAddWithoutValidation(AuthHeaders.StepUpToken, stepUpToken);

        using HttpResponseMessage hideResponse = await host.Client.SendAsync(hideRequest);
        using HttpResponseMessage lockResponse = await host.Client.SendAsync(lockRequest);
        using HttpResponseMessage privilegedResponse =
            await host.Client.SendAsync(privilegedRequest);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hideResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, lockResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, privilegedResponse.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.PrivilegedAccessEvents.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task ReportHideRedactPrivilegedReadAndLockPreserveBodiesAndVersions()
    {
        const string originalBody = "Sensitive original body known only to privileged moderation.";
        const string redactedBody = "This message was hidden by a moderator.";
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.SetSignupStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Approved,
            seed.Seed.Now.AddMinutes(1));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(2));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");

        using HttpResponseMessage created = await ThreadIntegrationTestSupport.PostMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 0,
            originalBody);
        ThreadIntegrationTestSupport.ThreadMessageResponse createdMessage =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<
                ThreadIntegrationTestSupport.ThreadMessageResponse>(
                created,
                HttpStatusCode.Created);
        MessageId messageId = MessageId.From(Guid.Parse(createdMessage.Id));
        Assert.Equal("\"1\"", created.Headers.ETag?.Tag);

        using HttpResponseMessage reported =
            await ThreadIntegrationTestSupport.ReportMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                messageId,
                member.AccessToken,
                expectedVersion: 1,
                MessageReportReason.Spam,
                "Same normalized report");
        Assert.Equal(HttpStatusCode.Accepted, reported.StatusCode);
        Assert.Equal("\"2\"", reported.Headers.ETag?.Tag);

        using HttpResponseMessage duplicate =
            await ThreadIntegrationTestSupport.ReportMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                messageId,
                member.AccessToken,
                expectedVersion: 1,
                MessageReportReason.Spam,
                " Same normalized report ");
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal("\"2\"", duplicate.Headers.ETag?.Tag);

        using HttpResponseMessage mismatch =
            await ThreadIntegrationTestSupport.ReportMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                messageId,
                member.AccessToken,
                expectedVersion: 2,
                MessageReportReason.Other,
                "Different report");
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);

        using HttpResponseMessage hidden = await ThreadIntegrationTestSupport.HideMessageAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            messageId,
            manager.AccessToken,
            expectedVersion: 2,
            reason: "Contains sensitive information.");
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.Equal("\"3\"", hidden.Headers.ETag?.Tag);

        using HttpResponseMessage ordinary = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            member.AccessToken);
        ThreadIntegrationTestSupport.ThreadMessagePageResponse ordinaryPage =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<
                ThreadIntegrationTestSupport.ThreadMessagePageResponse>(
                ordinary,
                HttpStatusCode.OK);
        ThreadIntegrationTestSupport.ThreadMessageResponse ordinaryMessage =
            Assert.Single(ordinaryPage.Items);
        Assert.Equal(redactedBody, ordinaryMessage.Body);
        Assert.DoesNotContain(originalBody, await ordinary.Content.ReadAsStringAsync());

        string stepUpToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        using HttpResponseMessage privileged =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                stepUpToken,
                reason: "Review hidden content.",
                caseId: "CASE-HIDDEN-1");
        ThreadIntegrationTestSupport.PrivilegedThreadMessagePageResponse privilegedPage =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<
                ThreadIntegrationTestSupport.PrivilegedThreadMessagePageResponse>(
                privileged,
                HttpStatusCode.OK);
        Assert.Equal(originalBody, Assert.Single(privilegedPage.Items).Body);

        using HttpResponseMessage locked = await ThreadIntegrationTestSupport.LockThreadAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 3,
            reason: "Coordination completed.");
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal("\"4\"", locked.Headers.ETag?.Tag);

        using HttpResponseMessage postAfterLock =
            await ThreadIntegrationTestSupport.PostMessageAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 4,
                body: "Must not be accepted.");
        Assert.Equal(HttpStatusCode.Conflict, postAfterLock.StatusCode);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Single(await context.MessageReports.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
        Assert.DoesNotContain(
            host.Logs.Records,
            record => record.Message.Contains(originalBody, StringComparison.Ordinal));
    }

    [RequiresPostgresFact]
    public async Task PrivilegedAuthorizationHoldsAdminRoleRowLockUntilTransactionCommits()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        Guid adminUserId;
        await using (TabrukDbContext lookup = database.CreateContext())
        {
            adminUserId = await lookup.Memberships
                .Where(membership =>
                    membership.Id == seed.SecondAdministratorMembershipId.Value)
                .Select(membership => membership.UserId)
                .SingleAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        PostgresThreadRepository repository = new(context);
        PostgresUnitOfWork unitOfWork = new(context);
        TaskCompletionSource locked = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Result<bool>> transaction = unitOfWork.ExecuteAsync(
            async cancellationToken =>
            {
                Result<ThreadPrivilegedAuthorizationContext> authorization =
                    await repository.LockPrivilegedAuthorizationAsync(
                        UserId.From(adminUserId),
                        seed.SecondAdministratorMembershipId,
                        seed.OrganizationId,
                        cancellationToken);
                if (authorization.IsFailure)
                {
                    return Result.Failure<bool>(authorization.Error);
                }

                locked.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Result.Success(true);
            }).AsTask();
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        PostgresException blocked = await Assert.ThrowsAsync<PostgresException>(
            () => ThreadIntegrationTestSupport.LockRoleForUpdateNowaitAsync(
                database,
                seed.OrganizationId,
                seed.SecondAdministratorMembershipId,
                OrganizationRole.Admin));
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);

        release.SetResult();
        Result<bool> committed = await transaction;
        Assert.True(committed.IsSuccess);

        await ThreadIntegrationTestSupport.LockRoleForUpdateNowaitAsync(
            database,
            seed.OrganizationId,
            seed.SecondAdministratorMembershipId,
            OrganizationRole.Admin);
    }

    [RequiresPostgresFact]
    public async Task PrivilegedReadAuditsExactCursorAndResponseHashThenRejectsReplay()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.AppendMessageAsync(
            database,
            seed.Seed,
            seed.Seed.ManagerMembershipId,
            "First privileged message.");
        await ThreadIntegrationTestSupport.AppendMessageAsync(
            database,
            seed.Seed,
            seed.Seed.MemberMembershipId,
            "Second privileged message.");
        MutableClock clock = new(seed.Seed.Now.AddMinutes(10));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");

        string firstToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        using HttpResponseMessage firstResponse = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            firstToken,
            reason: "Investigate first page.",
            caseId: "CASE-123",
            pageSize: 1);
        string firstJson = await firstResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        ThreadIntegrationTestSupport.PrivilegedThreadMessagePageResponse firstPage =
            JsonSerializer.Deserialize<ThreadIntegrationTestSupport.PrivilegedThreadMessagePageResponse>(
                firstJson,
                JsonOptions)
            ?? throw new InvalidOperationException("Missing privileged page.");
        Assert.NotNull(firstPage.NextCursor);

        string secondToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        using HttpResponseMessage secondResponse = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            secondToken,
            reason: "Investigate second page.",
            caseId: "CASE-123",
            cursor: firstPage.NextCursor,
            pageSize: 1);
        string secondJson = await secondResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        await using (TabrukDbContext context = database.CreateContext())
        {
            var audits = await context.PrivilegedAccessEvents
                .OrderBy(access => access.OccurredAt)
                .ToListAsync();
            Assert.Equal(2, audits.Count);
            var secondAudit = Assert.Single(
                audits,
                audit => audit.PageCursor == firstPage.NextCursor);
            Assert.Equal("thread", secondAudit.ResourceType);
            Assert.Equal(seed.Seed.ThreadId.ToString(), secondAudit.ResourceId);
            Assert.Equal("CASE-123", secondAudit.CaseId);
            Assert.Equal(ThreadIntegrationTestSupport.HashResponse(secondJson), secondAudit.PageHash);
        }

        using HttpResponseMessage replay = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            secondToken,
            reason: "Investigate second page.",
            caseId: "CASE-123",
            cursor: firstPage.NextCursor,
            pageSize: 1);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        await using TabrukDbContext replayContext = database.CreateContext();
        Assert.Equal(2, await replayContext.PrivilegedAccessEvents.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task PrivilegedAuditFailureDeniesContentAndRollsBackStepUpConsumption()
    {
        const string secretBody = "Privileged body must not escape a failed audit transaction.";
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.AppendMessageAsync(
            database,
            seed.Seed,
            seed.Seed.ManagerMembershipId,
            secretBody);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(10));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");
        string stepUpToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        await ThreadIntegrationTestSupport.CreatePrivilegedAuditFailureTriggerAsync(database);

        using HttpResponseMessage failed = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            stepUpToken,
            reason: "Audit failure proof.",
            caseId: "CASE-ROLLBACK");
        string failedBody = await failed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.DoesNotContain(secretBody, failedBody, StringComparison.Ordinal);
        await using (TabrukDbContext failedContext = database.CreateContext())
        {
            Assert.Empty(await failedContext.PrivilegedAccessEvents.ToListAsync());
        }

        await ThreadIntegrationTestSupport.DropPrivilegedAuditFailureTriggersAsync(database);
        using HttpResponseMessage recovered = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            stepUpToken,
            reason: "Audit failure proof.",
            caseId: "CASE-ROLLBACK");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await using TabrukDbContext recoveredContext = database.CreateContext();
        Assert.Single(await recoveredContext.PrivilegedAccessEvents.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task PrivilegedCaseIdIsStricterThanPersistenceAndInvalidInputDoesNotConsumeStepUp()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(10));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");
        string stepUpToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);

        using HttpResponseMessage invalid = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            stepUpToken,
            reason: "Validate case identifier.",
            caseId: new string('A', 101));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using HttpResponseMessage recovered = await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            admin.AccessToken,
            stepUpToken,
            reason: "Validate case identifier.",
            caseId: new string('A', ApplicationLimits.MaximumCaseIdAsciiCharacters));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        var audit = Assert.Single(await context.PrivilegedAccessEvents.ToListAsync());
        Assert.Equal(ApplicationLimits.MaximumCaseIdAsciiCharacters, audit.CaseId.Length);
    }

    [RequiresPostgresFact]
    public async Task PrivilegedReadRejectsWrongPurposeAndExactExpiryWithoutAudit()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(10));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");

        string wrongPurpose = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken,
            purpose: "governance.assign");
        using HttpResponseMessage purposeDenied =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                wrongPurpose,
                reason: "Purpose isolation.",
                caseId: "CASE-PURPOSE");
        Assert.Equal(HttpStatusCode.Unauthorized, purposeDenied.StatusCode);

        string expiring = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        clock.Set(clock.UtcNow.AddMinutes(ApplicationLimits.StepUpLifetimeMinutes));
        using HttpResponseMessage expiryDenied =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                expiring,
                reason: "Expiry boundary.",
                caseId: "CASE-EXPIRY");
        Assert.Equal(HttpStatusCode.Unauthorized, expiryDenied.StatusCode);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.PrivilegedAccessEvents.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task PrivilegedAndOrdinaryPagingEnforceExactFrozenBounds()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        for (int index = 0; index < ApplicationLimits.DefaultPageSize + 1; index++)
        {
            await ThreadIntegrationTestSupport.AppendMessageAsync(
                database,
                seed.Seed,
                seed.Seed.ManagerMembershipId,
                $"Paged message {index:D2}");
        }

        MutableClock clock = new(seed.Seed.Now.AddMinutes(100));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse admin = await SignupDecisionTestSupport.LoginAsync(
            host,
            "admin@example.test");

        using HttpResponseMessage defaultPage = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);
        ThreadIntegrationTestSupport.ThreadMessagePageResponse defaultBody =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<
                ThreadIntegrationTestSupport.ThreadMessagePageResponse>(
                defaultPage,
                HttpStatusCode.OK);
        Assert.Equal(ApplicationLimits.DefaultPageSize, defaultBody.Items.Count);
        Assert.NotNull(defaultBody.NextCursor);

        using HttpResponseMessage maximumPage = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            pageSize: ApplicationLimits.MaximumPageSize);
        ThreadIntegrationTestSupport.ThreadMessagePageResponse maximumBody =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<
                ThreadIntegrationTestSupport.ThreadMessagePageResponse>(
                maximumPage,
                HttpStatusCode.OK);
        Assert.Equal(ApplicationLimits.DefaultPageSize + 1, maximumBody.Items.Count);
        Assert.Null(maximumBody.NextCursor);

        using HttpResponseMessage overMaximum = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            pageSize: ApplicationLimits.MaximumPageSize + 1);
        Assert.Equal(HttpStatusCode.BadRequest, overMaximum.StatusCode);

        using HttpResponseMessage malformedCursor = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            cursor: "not-base64!");
        Assert.Equal(HttpStatusCode.BadRequest, malformedCursor.StatusCode);

        string token = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        string exactReason = new('r', ApplicationLimits.MaximumReasonUnicodeScalars);
        using HttpResponseMessage privilegedMaximum =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                token,
                reason: exactReason,
                caseId: new string('Z', ApplicationLimits.MaximumCaseIdAsciiCharacters),
                pageSize: ApplicationLimits.MaximumPageSize);
        Assert.Equal(HttpStatusCode.OK, privilegedMaximum.StatusCode);

        string reasonToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        using HttpResponseMessage reasonOverMaximum =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                reasonToken,
                reason: new string('r', ApplicationLimits.MaximumReasonUnicodeScalars + 1),
                caseId: "CASE-REASON-LIMIT",
                pageSize: ApplicationLimits.MaximumPageSize);
        Assert.Equal(HttpStatusCode.BadRequest, reasonOverMaximum.StatusCode);
        using HttpResponseMessage reasonTokenStillUsable =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                reasonToken,
                reason: "Valid after rejected reason.",
                caseId: "CASE-REASON-LIMIT",
                pageSize: ApplicationLimits.MaximumPageSize);
        Assert.Equal(HttpStatusCode.OK, reasonTokenStillUsable.StatusCode);

        string reusableToken = await ThreadIntegrationTestSupport.IssueStepUpAsync(
            host.Client,
            admin.AccessToken);
        using HttpResponseMessage privilegedOverMaximum =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                reusableToken,
                reason: "Page limit validation.",
                caseId: "CASE-PAGE-LIMIT",
                pageSize: ApplicationLimits.MaximumPageSize + 1);
        Assert.Equal(HttpStatusCode.BadRequest, privilegedOverMaximum.StatusCode);
        using HttpResponseMessage tokenStillUsable =
            await ThreadIntegrationTestSupport.ReadPrivilegedAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                admin.AccessToken,
                reusableToken,
                reason: "Page limit validation.",
                caseId: "CASE-PAGE-LIMIT",
                pageSize: ApplicationLimits.MaximumPageSize);
        Assert.Equal(HttpStatusCode.OK, tokenStillUsable.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task ManagerCanListExistingThreadMessages()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await ThreadIntegrationTestSupport.AppendMessageAsync(
            database,
            seed.Seed,
            seed.Seed.ManagerMembershipId,
            "Manager seeded thread message.");
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await ThreadIntegrationTestSupport.GetMessagesAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);
        ThreadIntegrationTestSupport.ThreadMessagePageResponse page =
            await ThreadIntegrationTestSupport.ReadRequiredAsync<ThreadIntegrationTestSupport.ThreadMessagePageResponse>(
                response,
                HttpStatusCode.OK);

        Assert.Equal(seed.Seed.ServiceDateId.ToString(), page.ServiceDateId);
        Assert.Equal("open", page.Status, ignoreCase: true);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        ThreadIntegrationTestSupport.ThreadMessageResponse message = Assert.Single(page.Items);
        Assert.Equal("Manager", message.SenderDisplayName);
        Assert.Equal("Manager seeded thread message.", message.Body);
        Assert.Equal("visible", message.Visibility, ignoreCase: true);
    }
}
