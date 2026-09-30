using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.IntegrationTests.Signups.Decisions;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Signups")]
[Trait("Category", "T15")]
public sealed class SignupDecisionIntegrationTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task ApproveDeclineAndWaitlistAgreeAcrossResponseMineRosterAndDatabaseRows()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        foreach ((string Action, SignupStatus Status) scenario in new[]
        {
            ("approve", SignupStatus.Approved),
            ("decline", SignupStatus.Declined),
            ("waitlist", SignupStatus.Waitlisted),
        })
        {
            TestActor target = await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(
                database,
                seed.Seed);
            TokenSetResponse targetMember = await SignupDecisionTestSupport.LoginAsync(
                host,
                target.Email);
            SignupId signupId = await SignupDecisionTestSupport.AddPendingSignupAsync(
                database,
                seed.Seed,
                primaryMembershipId: target.MembershipId);
            long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(database, seed.Seed.HelpNeedId);

            using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
                host.Client,
                signupId,
                scenario.Action,
                manager.AccessToken,
                IdempotencyKey.New(),
                version,
                scenario.Action == "decline" ? "Not selected" : null);
            SignupContract result = await SignupDecisionTestSupport.ReadSignupAsync(
                response,
                HttpStatusCode.OK);

            Assert.Equal(scenario.Status.ToString(), result.Status, ignoreCase: true);
            Assert.Equal($"\"{result.SignupVersion}\"", response.Headers.ETag?.Tag);
            Assert.Equal(
                scenario.Status,
                await SignupDecisionTestSupport.ReadStatusAsync(database, signupId));

            SignupPageContract mine = await SignupDecisionTestSupport.GetMineAsync(
                host.Client,
                targetMember.AccessToken);
            SignupContract memberProjection =
                mine.Items.Single(item => item.Id == signupId.ToString());
            Assert.Equal(result, memberProjection);
            RosterContract roster = await SignupDecisionTestSupport.GetRosterAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                manager.AccessToken);
            SignupContract rosterProjection =
                roster.Items.Single(item => item.Id == signupId.ToString());
            Assert.Equal(result, rosterProjection);
            SignupDatabaseContract databaseProjection =
                await SignupDecisionTestSupport.ReadSignupDatabaseContractAsync(
                    database,
                    seed.Seed.HelpNeedId,
                    signupId);
            Assert.Equal(target.MembershipId, databaseProjection.PrimaryMembershipId);
            Assert.Equal(result, databaseProjection.Signup);
        }
    }

    [RequiresPostgresFact]
    public async Task SuccessfulDecisionPersistsExactlyOneAtomicScopedEffectSet()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        SignupId signupId = seed.SignupId;
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            signupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1,
            "Approved for service");
        await SignupDecisionTestSupport.ReadSignupAsync(response, HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        NotificationEntity notification = await context.Notifications.SingleAsync();
        AuditEventEntity audit = await context.AuditEvents.SingleAsync();
        OutboxMessageEntity outbox = await context.OutboxMessages.SingleAsync();
        Assert.Equal(seed.Seed.MemberMembershipId.Value, notification.RecipientMembershipId);
        Assert.Equal(signupId.Value, notification.ResourceId);
        Assert.Equal("Signup approved", notification.Title);
        Assert.Equal(seed.Seed.ManagerMembershipId.Value, audit.ActorMembershipId);
        Assert.Equal("signup.approved", audit.Action);
        Assert.Equal("signup_decision", audit.Purpose);
        Assert.Equal("notification.push_requested", outbox.Type);
        Assert.Equal(1, await context.IdempotencyRecords.CountAsync());
        Assert.Equal((short)IdempotencyStatus.Completed, (await context.IdempotencyRecords.SingleAsync()).Status);
    }

    [RequiresPostgresFact]
    public async Task ReasonAppearsOnlyInTheInsertOnlyAuditRow()
    {
        const string reason = "Private operational decision";
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "decline",
            manager.AccessToken,
            IdempotencyKey.New(),
            1,
            reason);
        string responseJson = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(reason, (await context.AuditEvents.SingleAsync()).Reason);
        Assert.DoesNotContain(
            reason,
            JsonSerializer.Serialize(await context.Notifications.SingleAsync()),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            reason,
            (await context.OutboxMessages.SingleAsync()).Payload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(reason, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(
            reason,
            JsonSerializer.Serialize(await context.Signups.SingleAsync()),
            StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task AnonymousDisabledRevokedUnrelatedManagerAndCrossTenantAttacksWriteNothing()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage anonymous = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            null,
            IdempotencyKey.New(),
            1);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        await SignupDecisionTestSupport.DisableMembershipAsync(
            database,
            seed.Seed.ManagerMembershipId);
        using HttpResponseMessage disabled = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        await SignupDecisionTestSupport.EnableMembershipAsync(
            database,
            seed.Seed.ManagerMembershipId);

        await SignupDecisionTestSupport.RevokeFoodInchargeAsync(database, seed.Seed);
        using HttpResponseMessage revoked = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        await SignupDecisionTestSupport.AssertProblemAsync(
            revoked,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);
        await SignupDecisionTestSupport.RestoreFoodInchargeAsync(database, seed.Seed);

        TestActor unrelated = await SignupDecisionTestSupport.AddAuthenticatedManagerAsync(
            database,
            seed.Seed.OrganizationId,
            "unrelated@example.test");
        TokenSetResponse unrelatedToken = await SignupDecisionTestSupport.LoginAsync(
            host,
            unrelated.Email);
        using HttpResponseMessage unrelatedResponse = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            unrelatedToken.AccessToken,
            IdempotencyKey.New(),
            1);
        await SignupDecisionTestSupport.AssertProblemAsync(
            unrelatedResponse,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);

        TestActor crossTenant = await SignupDecisionTestSupport.AddCrossTenantManagerAsync(database);
        TokenSetResponse crossTenantToken = await SignupDecisionTestSupport.LoginAsync(
            host,
            crossTenant.Email);
        using HttpResponseMessage crossTenantResponse = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            crossTenantToken.AccessToken,
            IdempotencyKey.New(),
            1);
        await SignupDecisionTestSupport.AssertProblemAsync(
            crossTenantResponse,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);

        Assert.Equal(SignupStatus.Pending, await SignupDecisionTestSupport.ReadStatusAsync(database, seed.SignupId));
        Assert.Equal(EffectCounts.Zero, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task RecordSpecificDenialsConcealTargetExistence()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TestActor unrelated = await SignupDecisionTestSupport.AddAuthenticatedManagerAsync(
            database,
            seed.Seed.OrganizationId,
            "concealed@example.test");
        TokenSetResponse unrelatedToken = await SignupDecisionTestSupport.LoginAsync(host, unrelated.Email);

        foreach ((SignupId Id, string Token) attempt in new[]
        {
            (SignupId.New(), manager.AccessToken),
            (seed.SignupId, unrelatedToken.AccessToken),
        })
        {
            using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
                host.Client,
                attempt.Id,
                "approve",
                attempt.Token,
                IdempotencyKey.New(),
                1);
            ProblemContract problem = await SignupDecisionTestSupport.AssertProblemAsync(
                response,
                HttpStatusCode.NotFound,
                SignupApplicationErrorCodes.SignupNotFound);
            Assert.DoesNotContain(seed.SignupId.ToString(), problem.Detail, StringComparison.Ordinal);
        }
    }

    [RequiresPostgresFact]
    public async Task CompletedRetryReturnsCurrentAuthoritativeProjectionWithoutDuplicateEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage first = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            key,
            1);
        SignupContract approved = await SignupDecisionTestSupport.ReadSignupAsync(first, HttpStatusCode.OK);
        EffectCounts afterFirst = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        await SignupDecisionTestSupport.SetReachableStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Declined,
            version: 2,
            seed.Seed.Now.AddMinutes(2));
        using HttpResponseMessage retry = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            key,
            1);
        SignupContract authoritative = await SignupDecisionTestSupport.ReadSignupAsync(
            retry,
            HttpStatusCode.OK);

        Assert.Equal("approved", approved.Status, ignoreCase: true);
        Assert.Equal("declined", authoritative.Status, ignoreCase: true);
        Assert.Equal(afterFirst, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task SameKeyWithDifferentReasonActionTargetOrVersionReturnsIdempotencyMismatch()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        SignupId otherSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed));
        long currentVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage first = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            key,
            currentVersion,
            "original");
        await SignupDecisionTestSupport.ReadSignupAsync(first, HttpStatusCode.OK);

        foreach ((SignupId Id, string Action, long Version, string? Reason) mismatch in new[]
        {
            (seed.SignupId, "approve", currentVersion, "changed"),
            (seed.SignupId, "decline", currentVersion, "original"),
            (otherSignup, "approve", currentVersion, "original"),
            (seed.SignupId, "approve", currentVersion + 1, "original"),
        })
        {
            using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
                host.Client,
                mismatch.Id,
                mismatch.Action,
                manager.AccessToken,
                key,
                mismatch.Version,
                mismatch.Reason);
            await SignupDecisionTestSupport.AssertProblemAsync(
                response,
                HttpStatusCode.Conflict,
                SignupApplicationErrorCodes.IdempotencyMismatch);
        }
    }

    [RequiresPostgresFact]
    public async Task StaleUnauthorizedAndInvalidAttemptsPersistNoReceiptAndTheKeyCanRecover()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        IdempotencyKey staleKey = IdempotencyKey.New();
        using HttpResponseMessage stale = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            staleKey,
            0);
        await SignupDecisionTestSupport.AssertProblemAsync(
            stale,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        Assert.Equal(0, await SignupDecisionTestSupport.IdempotencyCountAsync(database));
        using HttpResponseMessage recovered = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            staleKey,
            1);
        await SignupDecisionTestSupport.ReadSignupAsync(recovered, HttpStatusCode.OK);

        SignupId second = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed));
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(database, seed.Seed.HelpNeedId);
        IdempotencyKey unauthorizedKey = IdempotencyKey.New();
        await SignupDecisionTestSupport.RevokeFoodInchargeAsync(database, seed.Seed);
        using HttpResponseMessage unauthorized = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            second,
            "approve",
            manager.AccessToken,
            unauthorizedKey,
            version);
        await SignupDecisionTestSupport.AssertProblemAsync(
            unauthorized,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);
        Assert.Equal(1, await SignupDecisionTestSupport.IdempotencyCountAsync(database));
        await SignupDecisionTestSupport.RestoreFoodInchargeAsync(database, seed.Seed);
        using HttpResponseMessage unauthorizedRecovered = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            second,
            "approve",
            manager.AccessToken,
            unauthorizedKey,
            version);
        await SignupDecisionTestSupport.ReadSignupAsync(unauthorizedRecovered, HttpStatusCode.OK);

        IdempotencyKey invalidKey = IdempotencyKey.New();
        long latest = await SignupDecisionTestSupport.ReadSignupVersionAsync(database, seed.Seed.HelpNeedId);
        using HttpResponseMessage invalid = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            second,
            "approve",
            manager.AccessToken,
            invalidKey,
            latest);
        await SignupDecisionTestSupport.AssertProblemAsync(
            invalid,
            HttpStatusCode.Conflict,
            ErrorCodes.InvalidTransition);
        Assert.Equal(2, await SignupDecisionTestSupport.IdempotencyCountAsync(database));
    }

    [RequiresPostgresFact]
    public async Task InvalidTransitionCapacityChronologyExhaustionAndClosedCategoryRollBackEverything()
    {
        await SignupDecisionTestSupport.AssertFailureRollsBackAsync(
            prepare: async (database, seed) =>
                await SignupDecisionTestSupport.SetReachableStatusAsync(
                    database,
                    seed.SignupId,
                    SignupStatus.Approved,
                    1,
                    seed.Seed.Now.AddSeconds(1)),
            expectedCode: ErrorCodes.InvalidTransition);
        await SignupDecisionTestSupport.AssertFailureRollsBackAsync(
            prepare: SignupDecisionTestSupport.FillCapacityAsync,
            expectedCode: ErrorCodes.CapacityUnavailable);
        await SignupDecisionTestSupport.AssertFailureRollsBackAsync(
            prepare: static (_, _) => Task.CompletedTask,
            expectedCode: SignupErrorCodes.SignupChronologyInvalid,
            clockOffset: TimeSpan.FromMinutes(-2));
        await SignupDecisionTestSupport.AssertFailureRollsBackAsync(
            prepare: SignupDecisionTestSupport.ExhaustRootVersionAsync,
            expectedCode: SignupErrorCodes.VersionExhausted,
            expectedVersion: long.MaxValue);
        await SignupDecisionTestSupport.AssertFailureRollsBackAsync(
            prepare: SignupDecisionTestSupport.CloseCategoryAsync,
            expectedCode: ErrorCodes.CategoryClosed);
    }

    [RequiresPostgresFact]
    public async Task RepositoryRejectsOmittedApprovedChildAndPreservesRowsHighWaterVersionAndEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        SignupId approvedId = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed));
        await SignupDecisionTestSupport.SetReachableStatusAsync(
            database,
            approvedId,
            SignupStatus.Approved,
            1,
            seed.Seed.Now.AddSeconds(1));
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(database, seed.Seed.HelpNeedId);
        DatabaseState before = await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);

        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        PostgresUnitOfWork unitOfWork = new(context);
        Result<object?> result = await unitOfWork.ExecuteAsync<object?>(
            async cancellationToken =>
            {
                object decisionContext = await SignupDecisionTestSupport.GetDecisionContextAsync(
                    repository,
                    seed.Seed.OrganizationId,
                    seed.Seed.ManagerMembershipId,
                    seed.SignupId,
                    cancellationToken);
                HelpNeedSignups aggregate = (HelpNeedSignups)decisionContext.GetType()
                    .GetProperty("Aggregate")!.GetValue(decisionContext)!;
                List<Signup> signups = (List<Signup>)typeof(HelpNeedSignups)
                    .GetField("signups", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .GetValue(aggregate)!;
                signups.RemoveAll(signup => signup.Id == approvedId);
                Assert.True(aggregate.Approve(seed.SignupId, seed.Seed.Now.AddMinutes(1)).IsSuccess);
                Result saved = await SignupDecisionTestSupport.SaveDecisionAsync(
                    repository,
                    aggregate,
                    seed.SignupId,
                    seed.Seed.ManagerMembershipId,
                    seed.Seed.MemberMembershipId,
                    IdempotencyKey.New(),
                    seed.Seed.Now.AddMinutes(1),
                    cancellationToken);
                return saved.IsSuccess
                    ? Result.Success<object?>(null)
                    : Result.Failure<object?>(saved.Error);
            });

        Assert.True(result.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, result.Error.Code);
        Assert.Equal(before, await SignupDecisionTestSupport.ReadDatabaseStateAsync(database));
        Assert.Equal(version, before.SignupVersion);
    }

    [RequiresPostgresFact]
    public async Task WaitlistPersistsTheNextHighWaterValueAtomically()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await SignupDecisionTestSupport.SetHighWaterAsync(database, seed.Seed.HelpNeedId, 5);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "waitlist",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        SignupContract result = await SignupDecisionTestSupport.ReadSignupAsync(
            response,
            HttpStatusCode.OK);

        Assert.Equal(6, result.WaitlistOrder);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(6, (await context.HelpNeeds.SingleAsync()).WaitlistOrderHighWater);
        Assert.Equal(6, (await context.Signups.SingleAsync()).WaitlistOrder);
        Assert.Equal(EffectCounts.OneSuccess, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task PendingWaitlistedAndDeclinedRemainThreadDeniedWhilePersistedApprovedIsAllowed()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        DateThread thread = DateThread.Create(
            seed.Seed.ThreadId,
            seed.Seed.OrganizationId,
            seed.Seed.ServiceDateId).Value;
        (Membership _, Membership member, Domain.Dates.ServiceDate date) =
            seed.Seed.CreateThreadDomainView();

        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            seed.SignupId)).IsFailure);

        using HttpResponseMessage waitlisted = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "waitlist",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        SignupContract waitlistedResult = await SignupDecisionTestSupport.ReadSignupAsync(
            waitlisted,
            HttpStatusCode.OK);
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            seed.SignupId)).IsFailure);

        using HttpResponseMessage declined = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "decline",
            manager.AccessToken,
            IdempotencyKey.New(),
            waitlistedResult.SignupVersion,
            "Declined");
        SignupContract declinedResult = await SignupDecisionTestSupport.ReadSignupAsync(
            declined,
            HttpStatusCode.OK);
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            seed.SignupId)).IsFailure);

        SignupId approvedId = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            seed.Seed.MemberMembershipId);
        using HttpResponseMessage approved = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            approvedId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            declinedResult.SignupVersion + 1);
        await SignupDecisionTestSupport.ReadSignupAsync(approved, HttpStatusCode.OK);
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            approvedId)).IsSuccess);
    }

    [RequiresPostgresFact]
    public async Task WaitlistedApprovalEndpointIsRejectedAndExistingReassignSetupGrantsAccessOnlyAfterApprovedPersists()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await SignupDecisionTestSupport.SetReachableStatusAsync(
            database,
            seed.SignupId,
            SignupStatus.Waitlisted,
            1,
            seed.Seed.Now.AddSeconds(1),
            waitlistOrder: 1);
        await SignupDecisionTestSupport.SetHighWaterAsync(database, seed.Seed.HelpNeedId, 1);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        DateThread thread = DateThread.Create(
            seed.Seed.ThreadId,
            seed.Seed.OrganizationId,
            seed.Seed.ServiceDateId).Value;
        (Membership _, Membership member, Domain.Dates.ServiceDate date) =
            seed.Seed.CreateThreadDomainView();

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ErrorCodes.InvalidTransition);
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            seed.SignupId)).IsFailure);

        await using (TabrukDbContext context = database.CreateContext())
        {
            PostgresSignupRepository repository = new(context);
            Result<HelpNeedSignups> loaded = await repository.GetAsync(
                seed.Seed.OrganizationId,
                seed.Seed.HelpNeedId);
            Assert.True(loaded.IsSuccess);
            Assert.True(loaded.Value.Reassign(seed.SignupId, seed.Seed.Now.AddMinutes(2)).IsSuccess);
            Assert.True((await repository.SaveAsync(loaded.Value)).IsSuccess);
            await context.SaveChangesAsync();
        }

        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            member,
            date,
            seed.SignupId)).IsSuccess);
    }

    [RequiresPostgresFact]
    public async Task PostgresDependencyFailureReturnsDependencyUnavailableAndWritesNothing()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        DatabaseState before = await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        TokenSetResponse manager;
        await using (AuthApiHost healthy =
                     await AuthApiHost.StartAsync(database.ConnectionString, clock))
        {
            manager = await SignupDecisionTestSupport.LoginAsync(
                healthy,
                SignupDecisionTestSupport.ManagerEmail);
        }

        string unavailable = new Npgsql.NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1,
        }.ConnectionString;
        await using AuthApiHost broken = await AuthApiHost.StartAsync(unavailable, clock);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            broken.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);
        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            ErrorCodes.DependencyUnavailable);
        Assert.Equal(before, await SignupDecisionTestSupport.ReadDatabaseStateAsync(database));
    }
}

internal static class SignupDecisionTestSupport
{
    public const string SharedPassword = "Passw0rd!Passw0rd!";
    public const string ManagerEmail = "manager@example.test";
    public const string MemberEmail = "member@example.test";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<DecisionSeed> CreateSeedAsync(PostgresTestDatabase database)
    {
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await SeedPasswordAsync(database, ManagerEmail);
        await SeedPasswordAsync(database, "admin@example.test");
        await SeedPasswordAsync(database, MemberEmail);
        SignupId signupId = await AddPendingSignupAsync(
            database,
            seed,
            seed.MemberMembershipId);
        return new DecisionSeed(seed, signupId);
    }

    public static async Task<SignupId> AddPendingSignupAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        MembershipId primaryMembershipId)
    {
        SignupId signupId = SignupId.New();
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == seed.HelpNeedId.Value);
        context.Signups.Add(
            new SignupEntity
            {
                Id = signupId.Value,
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                HelpNeedId = seed.HelpNeedId.Value,
                PrimaryMembershipId = primaryMembershipId.Value,
                Kind = (short)SignupKind.Individual,
                UnnamedParticipantCount = 0,
                Status = (short)SignupStatus.Pending,
                SubmittedAt = seed.Now,
                Version = 0,
            });
        need.SignupVersion++;
        await context.SaveChangesAsync();
        return signupId;
    }

    public static async Task<MembershipId> AddMemberAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed) =>
        (await AddAuthenticatedMemberAsync(database, seed)).MembershipId;

    public static async Task<TestActor> AddAuthenticatedMemberAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        MembershipId membershipId = MembershipId.New();
        Guid userId = UserId.New().Value;
        string email = $"member-{Guid.NewGuid():N}@example.test";
        await using TabrukDbContext context = database.CreateContext();
        context.Users.Add(CreateUser(userId, email));
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId.Value,
                OrganizationId = seed.OrganizationId.Value,
                UserId = userId,
                DisplayName = $"Member {membershipId}",
                Status = (short)MembershipStatus.Active,
                EligibleAsNamedParticipant = true,
            });
        await context.SaveChangesAsync();
        return new TestActor(email, membershipId, seed.OrganizationId);
    }

    public static async Task<TestActor> AddAuthenticatedManagerAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        string email)
    {
        MembershipId membershipId = MembershipId.New();
        Guid userId = UserId.New().Value;
        await using TabrukDbContext context = database.CreateContext();
        context.Users.Add(CreateUser(userId, email));
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId.Value,
                OrganizationId = organizationId.Value,
                UserId = userId,
                DisplayName = "Unrelated manager",
                Status = (short)MembershipStatus.Active,
                EligibleAsNamedParticipant = true,
            });
        context.RoleAssignments.Add(
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId.Value,
                MembershipId = membershipId.Value,
                Role = (short)OrganizationRole.FoodIncharge,
                AssignedByMembershipId = membershipId.Value,
                AssignedAt = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero),
            });
        await context.SaveChangesAsync();
        return new TestActor(email, membershipId, organizationId);
    }

    public static async Task<TestActor> AddCrossTenantManagerAsync(PostgresTestDatabase database)
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId membershipId = MembershipId.New();
        Guid userId = UserId.New().Value;
        string email = "cross-tenant-manager@example.test";
        DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        await using TabrukDbContext context = database.CreateContext();
        context.Organizations.Add(
            new OrganizationEntity
            {
                Id = organizationId.Value,
                Name = "Cross tenant",
                TimeZone = "America/Los_Angeles",
                DefaultCancellationLeadMinutes = 60,
                Status = 0,
                BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                BootstrapSealedAt = now,
                Version = 0,
            });
        context.Users.Add(CreateUser(userId, email));
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId.Value,
                OrganizationId = organizationId.Value,
                UserId = userId,
                DisplayName = "Cross tenant manager",
                Status = (short)MembershipStatus.Active,
                EligibleAsNamedParticipant = true,
            });
        context.RoleAssignments.Add(
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId.Value,
                MembershipId = membershipId.Value,
                Role = (short)OrganizationRole.FoodIncharge,
                AssignedByMembershipId = membershipId.Value,
                AssignedAt = now,
            });
        await context.SaveChangesAsync();
        return new TestActor(email, membershipId, organizationId);
    }

    public static async Task<TokenSetResponse> LoginAsync(AuthApiHost host, string email)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password = SharedPassword }),
        };
        request.Headers.TryAddWithoutValidation(
            AuthHeaders.InstallationId,
            Guid.CreateVersion7().ToString("D"));
        using HttpResponseMessage response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TokenSetResponse>(JsonOptions)
            ?? throw new InvalidOperationException("Missing login response.");
    }

    public static async Task<HttpResponseMessage> DecideAsync(
        HttpClient client,
        SignupId signupId,
        string action,
        string? bearerToken,
        IdempotencyKey key,
        long expectedVersion,
        string? reason = null)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/v1/signups/{signupId}/{action}")
        {
            Content = JsonContent.Create(new { reason }),
        };
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            key.ToString());
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return await client.SendAsync(request);
    }

    public static async Task<SignupContract> ReadSignupAsync(
        HttpResponseMessage response,
        HttpStatusCode expected)
    {
        Assert.True(
            response.StatusCode == expected,
            $"Expected {expected}, received {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<SignupContract>(JsonOptions)
            ?? throw new InvalidOperationException("Missing signup response.");
    }

    public static async Task<ProblemContract> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected {expectedStatus}, received {response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        ProblemContract problem =
            await response.Content.ReadFromJsonAsync<ProblemContract>(JsonOptions)
            ?? throw new InvalidOperationException("Missing ProblemDetails response.");
        Assert.Equal(expectedCode, problem.Code);
        return problem;
    }

    public static async Task<SignupPageContract> GetMineAsync(
        HttpClient client,
        string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/signups/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<SignupPageContract>(JsonOptions)
            ?? throw new InvalidOperationException("Missing mine response.");
    }

    public static async Task<RosterContract> GetRosterAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken)
    {
        using HttpRequestMessage request =
            new(HttpMethod.Get, $"/api/v1/dates/{serviceDateId}/roster");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<RosterContract>(JsonOptions)
            ?? throw new InvalidOperationException("Missing roster response.");
    }

    public static async Task<SignupStatus> ReadStatusAsync(
        PostgresTestDatabase database,
        SignupId signupId)
    {
        await using TabrukDbContext context = database.CreateContext();
        return (SignupStatus)(await context.Signups.SingleAsync(
            candidate => candidate.Id == signupId.Value)).Status;
    }

    public static async Task<SignupDatabaseContract> ReadSignupDatabaseContractAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId,
        SignupId signupId)
    {
        await using TabrukDbContext context = database.CreateContext();
        SignupEntity signup = await context.Signups.SingleAsync(
            candidate => candidate.Id == signupId.Value);
        long signupVersion = (await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == helpNeedId.Value)).SignupVersion;
        return new SignupDatabaseContract(
            new SignupContract(
                signup.Id.ToString(),
                ((SignupStatus)signup.Status).ToString().ToLowerInvariant(),
                signup.WaitlistOrder,
                signup.Version,
                signupVersion),
            MembershipId.From(signup.PrimaryMembershipId));
    }

    public static async Task<long> ReadSignupVersionAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId)
    {
        await using TabrukDbContext context = database.CreateContext();
        return (await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == helpNeedId.Value)).SignupVersion;
    }

    public static async Task<int> IdempotencyCountAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        return await context.IdempotencyRecords.CountAsync();
    }

    public static async Task<EffectCounts> ReadEffectCountsAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        return new EffectCounts(
            await context.AuditEvents.CountAsync(),
            await context.Notifications.CountAsync(),
            await context.OutboxMessages.CountAsync(),
            await context.IdempotencyRecords.CountAsync());
    }

    public static async Task<DatabaseState> ReadDatabaseStateAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        return new DatabaseState(
            await context.Signups.OrderBy(signup => signup.Id)
                .Select(signup => new SignupRowState(
                    signup.Id,
                    signup.Status,
                    signup.Version,
                    signup.WaitlistOrder))
                .ToArrayAsync(),
            need.SignupVersion,
            need.WaitlistOrderHighWater,
            await ReadEffectCountsAsync(database));
    }

    public static async Task SetReachableStatusAsync(
        PostgresTestDatabase database,
        SignupId signupId,
        SignupStatus status,
        long version,
        DateTimeOffset transitionedAt,
        long? waitlistOrder = null)
    {
        await using TabrukDbContext context = database.CreateContext();
        SignupEntity signup = await context.Signups.SingleAsync(
            candidate => candidate.Id == signupId.Value);
        signup.Status = (short)status;
        signup.Version = version;
        signup.LastTransitionAt = transitionedAt;
        signup.WaitlistOrder = waitlistOrder;
        await context.SaveChangesAsync();
    }

    public static async Task SetHighWaterAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId,
        long value)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == helpNeedId.Value);
        need.WaitlistOrderHighWater = value;
        await context.SaveChangesAsync();
    }

    public static async Task DisableMembershipAsync(
        PostgresTestDatabase database,
        MembershipId membershipId)
    {
        await using TabrukDbContext context = database.CreateContext();
        (await context.Memberships.SingleAsync(
            candidate => candidate.Id == membershipId.Value)).Status =
            (short)MembershipStatus.Disabled;
        await context.SaveChangesAsync();
    }

    public static async Task EnableMembershipAsync(
        PostgresTestDatabase database,
        MembershipId membershipId)
    {
        await using TabrukDbContext context = database.CreateContext();
        (await context.Memberships.SingleAsync(
            candidate => candidate.Id == membershipId.Value)).Status =
            (short)MembershipStatus.Active;
        await context.SaveChangesAsync();
    }

    public static async Task RevokeFoodInchargeAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        RoleAssignmentEntity role = await context.RoleAssignments.SingleAsync(
            candidate => candidate.OrganizationId == seed.OrganizationId.Value
                && candidate.MembershipId == seed.ManagerMembershipId.Value
                && candidate.Role == (short)OrganizationRole.FoodIncharge
                && candidate.RevokedAt == null);
        role.RevokedAt = seed.Now.AddMinutes(1);
        role.RevokedByMembershipId = seed.SecondAdministratorMembershipId.Value;
        await context.SaveChangesAsync();
    }

    public static async Task RestoreFoodInchargeAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        context.RoleAssignments.Add(
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = seed.OrganizationId.Value,
                MembershipId = seed.ManagerMembershipId.Value,
                Role = (short)OrganizationRole.FoodIncharge,
                AssignedByMembershipId = seed.SecondAdministratorMembershipId.Value,
                AssignedAt = seed.Now.AddMinutes(2),
            });
        await context.SaveChangesAsync();
    }

    public static async Task FillCapacityAsync(
        PostgresTestDatabase database,
        DecisionSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        need.Capacity = 1;
        SignupId approved = SignupId.New();
        MembershipId primary = await AddMemberAsync(database, seed.Seed);
        context.Signups.Add(
            new SignupEntity
            {
                Id = approved.Value,
                OrganizationId = seed.Seed.OrganizationId.Value,
                ServiceDateId = seed.Seed.ServiceDateId.Value,
                HelpNeedId = seed.Seed.HelpNeedId.Value,
                PrimaryMembershipId = primary.Value,
                Kind = (short)SignupKind.Individual,
                UnnamedParticipantCount = 0,
                Status = (short)SignupStatus.Approved,
                SubmittedAt = seed.Seed.Now,
                LastTransitionAt = seed.Seed.Now.AddSeconds(1),
                Version = 1,
            });
        need.SignupVersion++;
        await context.SaveChangesAsync();
    }

    public static async Task ExhaustRootVersionAsync(
        PostgresTestDatabase database,
        DecisionSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        (await context.HelpNeeds.SingleAsync()).SignupVersion = long.MaxValue;
        await context.SaveChangesAsync();
    }

    public static async Task CloseCategoryAsync(
        PostgresTestDatabase database,
        DecisionSeed seed)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        need.Status = (short)HelpNeedStatus.Closed;
        need.Version = 1;
        date.Status = (short)ServiceDateStatus.Closed;
        date.Version = 3;
        await context.SaveChangesAsync();
    }

    public static async Task AssertFailureRollsBackAsync(
        Func<PostgresTestDatabase, DecisionSeed, Task> prepare,
        string expectedCode,
        TimeSpan? clockOffset = null,
        long? expectedVersion = null)
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await CreateSeedAsync(database);
        await prepare(database, seed);
        long version = expectedVersion
            ?? await ReadSignupVersionAsync(database, seed.Seed.HelpNeedId);
        DatabaseState before = await ReadDatabaseStateAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1).Add(clockOffset ?? TimeSpan.Zero));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await LoginAsync(host, ManagerEmail);

        using HttpResponseMessage response = await DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            version);
        HttpStatusCode expectedStatus =
            expectedCode == ErrorCodes.StaleVersion
                ? HttpStatusCode.PreconditionFailed
                : HttpStatusCode.Conflict;
        await AssertProblemAsync(response, expectedStatus, expectedCode);
        Assert.Equal(before, await ReadDatabaseStateAsync(database));
    }

    public static async Task<Result> ThreadAccessAsync(
        PostgresTestDatabase database,
        DateThread thread,
        Membership member,
        Domain.Dates.ServiceDate date,
        SignupId signupId)
    {
        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<HelpNeedSignups> aggregate = await repository.GetAsync(
            date.OrganizationId,
            date.HelpNeeds.Single().Id);
        Assert.True(aggregate.IsSuccess);
        Signup signup = aggregate.Value.Signups.Single(candidate => candidate.Id == signupId);
        return thread.AuthorizeRead(member, date, signup);
    }

    public static async Task<object> GetDecisionContextAsync(
        PostgresSignupRepository repository,
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        SignupId signupId,
        CancellationToken cancellationToken)
    {
        MethodInfo method = RequireRepositoryMethod("GetDecisionContextAsync");
        object valueTask = method.Invoke(
            repository,
            [organizationId, actorMembershipId, signupId, cancellationToken])!;
        Task task = (Task)valueTask.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.True((bool)result.GetType().GetProperty("IsSuccess")!.GetValue(result)!);
        return result.GetType().GetProperty("Value")!.GetValue(result)!;
    }

    public static async ValueTask<Result> SaveDecisionAsync(
        PostgresSignupRepository repository,
        HelpNeedSignups aggregate,
        SignupId signupId,
        MembershipId actorMembershipId,
        MembershipId recipientMembershipId,
        IdempotencyKey key,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        object write = CreateWrite(
            aggregate,
            signupId,
            actorMembershipId,
            recipientMembershipId,
            key,
            now);
        MethodInfo method = RequireRepositoryMethod("SaveDecisionAsync");
        return await (ValueTask<Result>)method.Invoke(
            repository,
            [aggregate, write, cancellationToken])!;
    }

    public static MethodInfo RequireRepositoryMethod(string name)
    {
        MethodInfo? method = typeof(PostgresSignupRepository).GetMethod(name);
        Assert.NotNull(method);
        return method;
    }

    private static object CreateWrite(
        HelpNeedSignups aggregate,
        SignupId signupId,
        MembershipId actorMembershipId,
        MembershipId recipientMembershipId,
        IdempotencyKey key,
        DateTimeOffset now)
    {
        Signup signup = aggregate.Signups.Single(candidate => candidate.Id == signupId);
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            aggregate.OrganizationId,
            recipientMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            "Signup approved",
            "Your signup request was approved.",
            now);
        Assert.True(notification.IsSuccess);
        AuditEntry audit = new(
            AuditEventId.New(),
            aggregate.OrganizationId,
            actorMembershipId,
            "signup.approved",
            "signup",
            signupId.ToString(),
            "integration test",
            "signup_decision",
            key.ToString(),
            JsonSerializer.Serialize(
                new
                {
                    status = SignupStatus.Pending.ToString().ToLowerInvariant(),
                    childVersion = signup.Version - 1,
                    rootSignupVersion = aggregate.OriginalVersion,
                    waitlistOrder = (long?)null,
                }),
            JsonSerializer.Serialize(
                new
                {
                    status = signup.Status.ToString().ToLowerInvariant(),
                    childVersion = signup.Version,
                    rootSignupVersion = aggregate.Version,
                    waitlistOrder = signup.WaitlistOrder,
                }),
            now);
        OutboxMessage outbox = new(
            OutboxMessageId.New(),
            aggregate.OrganizationId,
            "notification.push_requested",
            JsonSerializer.Serialize(
                new
                {
                    notificationId = notification.Value.Notification.Id.ToString(),
                    recipientMembershipId = recipientMembershipId.ToString(),
                    resourceType = "signup",
                    resourceId = signupId.ToString(),
                }),
            now);
        Type effectsType = typeof(IUnitOfWork).Assembly.GetType(
            "HusayniaTabruk.Application.Abstractions.Persistence.SignupDecisionEffects")!;
        Type writeType = typeof(IUnitOfWork).Assembly.GetType(
            "HusayniaTabruk.Application.Abstractions.Persistence.SignupDecisionWrite")!;
        Assert.NotNull(effectsType);
        Assert.NotNull(writeType);
        object effects = Activator.CreateInstance(
            effectsType,
            new Notification[] { notification.Value.Notification },
            new AuditEntry[] { audit },
            new OutboxMessage[] { outbox })!;
        return Activator.CreateInstance(writeType, signupId, actorMembershipId, effects)!;
    }

    private static async Task SeedPasswordAsync(
        PostgresTestDatabase database,
        string email)
    {
        string normalizedEmail = email.ToUpperInvariant();
        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.NormalizedEmail == normalizedEmail);
        PasswordHasher<TabrukIdentityUser> hasher = new();
        user.PasswordHash = hasher.HashPassword(user, SharedPassword);
        user.EmailConfirmed = true;
        await context.SaveChangesAsync();
    }

    private static TabrukIdentityUser CreateUser(Guid userId, string email)
    {
        TabrukIdentityUser user = new()
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };
        PasswordHasher<TabrukIdentityUser> hasher = new();
        user.PasswordHash = hasher.HashPassword(user, SharedPassword);
        return user;
    }
}

internal sealed record DecisionSeed(PersistenceSeed Seed, SignupId SignupId);
internal sealed record TestActor(string Email, MembershipId MembershipId, OrganizationId OrganizationId);
internal sealed record EffectCounts(int Audits, int Notifications, int Outbox, int Idempotency)
{
    public static EffectCounts Zero { get; } = new(0, 0, 0, 0);
    public static EffectCounts OneSuccess { get; } = new(1, 1, 1, 1);
}
internal sealed record SignupRowState(Guid Id, short Status, long Version, long? WaitlistOrder);
internal sealed class DatabaseState : IEquatable<DatabaseState>
{
    public DatabaseState(
        IReadOnlyList<SignupRowState> signups,
        long signupVersion,
        long highWater,
        EffectCounts effects)
    {
        Signups = signups;
        SignupVersion = signupVersion;
        HighWater = highWater;
        Effects = effects;
    }

    public IReadOnlyList<SignupRowState> Signups { get; }
    public long SignupVersion { get; }
    public long HighWater { get; }
    public EffectCounts Effects { get; }

    public bool Equals(DatabaseState? other) =>
        other is not null
        && SignupVersion == other.SignupVersion
        && HighWater == other.HighWater
        && Effects == other.Effects
        && Signups.SequenceEqual(other.Signups);

    public override bool Equals(object? obj) => Equals(obj as DatabaseState);
    public override int GetHashCode() => HashCode.Combine(SignupVersion, HighWater, Effects);
}
internal sealed record SignupContract(
    string Id,
    string Status,
    long? WaitlistOrder,
    long Version,
    long SignupVersion);
internal sealed record SignupDatabaseContract(
    SignupContract Signup,
    MembershipId PrimaryMembershipId);
internal sealed record SignupPageContract(IReadOnlyList<SignupContract> Items, string? NextCursor);
internal sealed record RosterContract(
    string ServiceDateId,
    IReadOnlyList<SignupContract> Items,
    string? NextCursor);
internal sealed record ProblemContract(
    int Status,
    string Code,
    string Detail,
    IReadOnlyDictionary<string, string[]>? FieldErrors);
