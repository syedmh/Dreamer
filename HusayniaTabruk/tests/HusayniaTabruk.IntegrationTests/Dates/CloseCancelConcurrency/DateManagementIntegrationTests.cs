using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Dates;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using HusayniaTabruk.IntegrationTests.Signups.Decisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Dates.CloseCancelConcurrency;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Dates")]
[Trait("Category", "T17")]
public sealed class DateManagementIntegrationTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task ClosePreservesApprovedSignupHistoryAndLocksExistingThread()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            (short)ServiceDateStatus.Closed,
            (await context.ServiceDates.SingleAsync()).Status);
        Assert.Equal(
            (short)HelpNeedStatus.Closed,
            (await context.HelpNeeds.SingleAsync()).Status);
        Assert.Equal(
            (short)SignupStatus.Approved,
            (await context.Signups.SingleAsync()).Status);
        Assert.Equal(
            (short)ThreadStatus.Locked,
            (await context.DateThreads.SingleAsync()).Status);
    }

    [RequiresPostgresFact]
    public async Task CloseWithoutAnExistingThreadDoesNotCreateOrBackfillThread()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await DeleteThreadAsync(database, seed.Seed.ThreadId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            (short)ServiceDateStatus.Closed,
            (await context.ServiceDates.SingleAsync()).Status);
        Assert.Equal(
            (short)HelpNeedStatus.Closed,
            (await context.HelpNeeds.SingleAsync()).Status);
        Assert.Equal(
            (short)SignupStatus.Approved,
            (await context.Signups.SingleAsync()).Status);
        Assert.Empty(await context.DateThreads.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task CancelCancelsActiveSignupsCreatesDistinctContactEffectsAndRevokesApprovedThreadAccess()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Weather");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            (short)ServiceDateStatus.Cancelled,
            (await context.ServiceDates.SingleAsync()).Status);
        Assert.Equal(
            (short)SignupStatus.Cancelled,
            (await context.Signups.SingleAsync()).Status);
        Assert.Equal(
            1,
            await context.Notifications.CountAsync(
                notification => notification.Type == (short)NotificationType.ServiceDateCancelled));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "thread.access_changed"));
    }

    [RequiresPostgresFact]
    public async Task CancelDeduplicatesEffectsForTheSamePrimaryAcrossMultipleNeeds()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        await AddSecondHelpNeedWithApprovedSignupAsync(
            database,
            seed.Seed,
            seed.Seed.MemberMembershipId);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 3,
            reason: "Weather");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            2,
            await context.Signups.CountAsync(signup => signup.Status == (short)SignupStatus.Cancelled));
        Assert.Equal(
            1,
            await context.Notifications.CountAsync(
                notification => notification.Type == (short)NotificationType.ServiceDateCancelled));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "thread.access_changed"));
    }

    [RequiresPostgresFact]
    public async Task CancelAfterCloseCancelsPreservedActiveSignupsAndCreatesEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        MembershipId pendingPrimary = await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed);
        SignupId pendingSignupId = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            pendingPrimary);
        MembershipId waitlistedPrimary = await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed);
        SignupId waitlistedSignupId = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            waitlistedPrimary);
        long waitlistVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        using (HttpResponseMessage waitlisted = await SignupDecisionTestSupport.DecideAsync(
                   host.Client,
                   waitlistedSignupId,
                   "waitlist",
                   manager.AccessToken,
                   IdempotencyKey.New(),
                   waitlistVersion))
        {
            await SignupDecisionTestSupport.ReadSignupAsync(waitlisted, HttpStatusCode.OK);
        }

        using HttpResponseMessage close = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        EffectCounts beforeCancel = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage cancel = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 3,
            reason: "Weather");

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        EffectCounts afterCancel = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        Dictionary<Guid, SignupEntity> signups = await context.Signups
            .ToDictionaryAsync(signup => signup.Id);

        Assert.Equal((short)ServiceDateStatus.Cancelled, date.Status);
        Assert.Equal(4, date.Version);
        Assert.Equal((short)HelpNeedStatus.Closed, need.Status);
        Assert.Equal(1, need.Version);
        Assert.Equal(8, need.SignupVersion);
        Assert.Equal((short)SignupStatus.Cancelled, signups[seed.SignupId.Value].Status);
        Assert.Equal(2, signups[seed.SignupId.Value].Version);
        Assert.Equal((short)SignupStatus.Cancelled, signups[pendingSignupId.Value].Status);
        Assert.Equal(1, signups[pendingSignupId.Value].Version);
        Assert.Equal((short)SignupStatus.Cancelled, signups[waitlistedSignupId.Value].Status);
        Assert.Equal(2, signups[waitlistedSignupId.Value].Version);
        Assert.Null(signups[waitlistedSignupId.Value].WaitlistOrder);
        Assert.Equal((short)ThreadStatus.Locked, thread.Status);
        Assert.Equal(1, thread.Version);
        Assert.Equal(
            3,
            await context.Notifications.CountAsync(
                notification => notification.Type == (short)NotificationType.ServiceDateCancelled));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "thread.access_changed"));
        Assert.Equal(4, afterCancel.Audits - beforeCancel.Audits);
        Assert.Equal(3, afterCancel.Notifications - beforeCancel.Notifications);
        Assert.Equal(4, afterCancel.Outbox - beforeCancel.Outbox);
        Assert.Equal(1, afterCancel.Idempotency - beforeCancel.Idempotency);
    }

    [RequiresPostgresFact]
    public async Task NeedPatchReducingCapacityBelowApprovedParticipantsReturnsConflictWithoutWrites()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        MembershipId secondPrimary = await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed);
        await AddApprovedSignupAsync(database, seed.Seed, secondPrimary);
        DateMutationState before = await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId);

        using HttpResponseMessage response = await SendNeedPatchAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            manager.AccessToken,
            expectedVersion: 0,
            instructions: "Chop vegetables.",
            capacity: 1,
            status: "open");

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ErrorCodes.CapacityUnavailable);
        Assert.Equal(before, await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId));
    }

    [RequiresPostgresFact]
    public async Task NeedPatchSignupVersionCasLossReturnsPreconditionFailedWithoutNeedWriteOrEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeSaveNeed: () => AdvanceSignupVersionAsync(database, seed.Seed.HelpNeedId));
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        EffectCounts beforeEffects = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage response = await SendNeedPatchAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            manager.AccessToken,
            expectedVersion: 0,
            instructions: "Updated instructions",
            capacity: 9,
            status: "open");

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        Assert.Equal("Chop vegetables.", need.Instructions);
        Assert.Equal(10, need.Capacity);
        Assert.Equal(0, need.Version);
        Assert.Equal(2, need.SignupVersion);
        Assert.Equal(beforeEffects, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresTheory]
    [InlineData("edit-date", "revoke")]
    [InlineData("edit-date", "disable")]
    [InlineData("edit-need", "revoke")]
    [InlineData("edit-need", "disable")]
    [InlineData("close", "revoke")]
    [InlineData("close", "disable")]
    [InlineData("cancel", "revoke")]
    [InlineData("cancel", "disable")]
    public async Task AuthorityLostBetweenInitialAuthorizationAndWriteDoesNotCommit(
        string mutation,
        string authorityLoss)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        DateMutationState before = await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        Func<Task> beforeRevalidate = authorityLoss switch
        {
            "revoke" => () => SignupDecisionTestSupport.RevokeFoodInchargeAsync(database, seed.Seed),
            "disable" => () => SignupDecisionTestSupport.DisableMembershipAsync(
                database,
                seed.Seed.ManagerMembershipId),
            _ => throw new InvalidOperationException($"Unknown authority loss '{authorityLoss}'."),
        };
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeRevalidate: beforeRevalidate);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SendManagedMutationAsync(
            host.Client,
            seed.Seed,
            manager.AccessToken,
            mutation,
            expectedDateVersion: 2,
            expectedNeedVersion: 0);

        (HttpStatusCode expectedStatus, string expectedCode) = authorityLoss switch
        {
            "revoke" => (HttpStatusCode.Forbidden, "forbidden"),
            "disable" => (HttpStatusCode.Forbidden, "forbidden"),
            _ => throw new InvalidOperationException($"Unknown authority loss '{authorityLoss}'."),
        };
        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            expectedStatus,
            expectedCode);
        Assert.Equal(before, await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId));
    }

    [RequiresPostgresFact]
    public async Task DatePatchConcurrentNeedPatchPreservesTheNeedEdit()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        const string concurrentInstructions = "Concurrent need edit";
        const int concurrentCapacity = 8;
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeRevalidate: () => ApplyConcurrentNeedPatchAsync(
                database,
                seed.Seed.HelpNeedId,
                concurrentInstructions,
                concurrentCapacity,
                HelpNeedStatus.Open,
                version: 1));
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SendDatePatchAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 2,
            title: "Edited title",
            instructions: "Prepare food.",
            startsAt: seed.Seed.Now.AddDays(1),
            endsAt: seed.Seed.Now.AddDays(1).AddHours(4),
            cancellationDeadlineAt: seed.Seed.Now.AddHours(22));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        Assert.Equal("Edited title", date.Title);
        Assert.Equal(3, date.Version);
        Assert.Equal(concurrentInstructions, need.Instructions);
        Assert.Equal(concurrentCapacity, need.Capacity);
        Assert.Equal((short)HelpNeedStatus.Open, need.Status);
        Assert.Equal(1, need.Version);
    }

    [RequiresPostgresTheory]
    [InlineData("close")]
    [InlineData("cancel")]
    public async Task CloseOrCancelConcurrentNeedPatchReturnsStaleVersionWithoutPartialWrites(
        string action)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        if (action == "cancel")
        {
            await DeleteSignupAsync(database, seed.SignupId, seed.Seed.HelpNeedId);
        }

        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        const string concurrentInstructions = "Concurrent need edit";
        const int concurrentCapacity = 7;
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeSaveDate: () => ApplyConcurrentNeedPatchAsync(
                database,
                seed.Seed.HelpNeedId,
                concurrentInstructions,
                concurrentCapacity,
                HelpNeedStatus.Open,
                version: 1));
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        if (action == "close")
        {
            await ApproveAsync(database, host, seed, manager);
        }

        EffectCounts beforeEffects = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            action,
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Weather");

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        Assert.Equal((short)ServiceDateStatus.Open, date.Status);
        Assert.Equal(2, date.Version);
        Assert.Equal(concurrentInstructions, need.Instructions);
        Assert.Equal(concurrentCapacity, need.Capacity);
        Assert.Equal((short)HelpNeedStatus.Open, need.Status);
        Assert.Equal(1, need.Version);
        if (action == "close")
        {
            SignupEntity signup = await context.Signups.SingleAsync(candidate => candidate.Id == seed.SignupId.Value);
            Assert.Equal((short)SignupStatus.Approved, signup.Status);
            Assert.Equal(1, signup.Version);
        }
        else
        {
            Assert.Empty(await context.Signups.ToListAsync());
        }

        Assert.Equal((short)ThreadStatus.Open, thread.Status);
        Assert.Equal(0, thread.Version);
        Assert.Equal(beforeEffects, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task CancelRetryWithSameKeyReturnsCurrentProjectionAndDoesNotDuplicateEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage first = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            key,
            expectedVersion: 2,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        DateMutationState afterFirst = await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId);

        using HttpResponseMessage retry = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            key,
            expectedVersion: 2,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(afterFirst, await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId));
    }

    [RequiresPostgresTheory]
    [InlineData("close")]
    [InlineData("cancel")]
    public async Task SameIdempotencyKeyWithDifferentIfMatchDoesNotReplayCompletedMutation(
        string action)
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

        using HttpResponseMessage first = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            action,
            manager.AccessToken,
            key,
            expectedVersion: 2,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        EffectCounts afterFirst = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage retry = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            action,
            manager.AccessToken,
            key,
            expectedVersion: 3,
            reason: "Weather");

        await SignupDecisionTestSupport.AssertProblemAsync(
            retry,
            HttpStatusCode.Conflict,
            DateApplicationErrorCodes.IdempotencyMismatch);
        Assert.Equal(afterFirst, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task CancelRollbackOnNotificationOrIdempotencyUpdateFailureLeavesDateNeedSignupAndThreadUnchanged()
    {
        foreach ((string Table, string TriggerEvent) failure in new[]
                 {
                     ("notifications", "INSERT"),
                     ("idempotency_records", "UPDATE"),
                 })
        {
            await using PostgresTestDatabase database = await CreateDatabaseAsync();
            DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
            MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
            await using AuthApiHost host =
                await AuthApiHost.StartAsync(database.ConnectionString, clock);
            TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
                host,
                SignupDecisionTestSupport.ManagerEmail);

            await ApproveAsync(database, host, seed, manager);
            await CreateFailureTriggerAsync(database, failure.Table, failure.TriggerEvent);
            DateMutationState before = await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId);

            using HttpResponseMessage response = await SendDateMutationAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                "cancel",
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 2,
                reason: "Weather");

            await SignupDecisionTestSupport.AssertProblemAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                ErrorCodes.DependencyUnavailable);
            Assert.Equal(before, await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId));
        }
    }

    [RequiresPostgresFact]
    public async Task CancelAfterCloseRollbackOnNotificationOrIdempotencyUpdateFailureLeavesClosedDateAndSignupUnchanged()
    {
        foreach ((string Table, string TriggerEvent) failure in new[]
                 {
                     ("notifications", "INSERT"),
                     ("idempotency_records", "UPDATE"),
                 })
        {
            await using PostgresTestDatabase database = await CreateDatabaseAsync();
            DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
            MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
            await using AuthApiHost host =
                await AuthApiHost.StartAsync(database.ConnectionString, clock);
            TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
                host,
                SignupDecisionTestSupport.ManagerEmail);

            await ApproveAsync(database, host, seed, manager);
            using HttpResponseMessage close = await SendDateMutationAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                "close",
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 2,
                reason: "Coordination complete");
            Assert.Equal(HttpStatusCode.OK, close.StatusCode);

            await CreateFailureTriggerAsync(database, failure.Table, failure.TriggerEvent);
            DateMutationState before = await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId);

            using HttpResponseMessage response = await SendDateMutationAsync(
                host.Client,
                seed.Seed.ServiceDateId,
                "cancel",
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion: 3,
                reason: "Weather");

            await SignupDecisionTestSupport.AssertProblemAsync(
                response,
                HttpStatusCode.ServiceUnavailable,
                ErrorCodes.DependencyUnavailable);
            Assert.Equal(before, await ReadStateAsync(database, seed.Seed.HelpNeedId, seed.SignupId));
        }
    }

    [RequiresPostgresFact]
    public async Task EditDateWithStaleIfMatchReturnsPreconditionFailedWithoutApplyingSecondMutation()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage first = await SendDatePatchAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 2,
            title: "Edited title",
            instructions: "Prepare food.",
            startsAt: seed.Seed.Now.AddDays(1),
            endsAt: seed.Seed.Now.AddDays(1).AddHours(4),
            cancellationDeadlineAt: seed.Seed.Now.AddHours(22));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using HttpResponseMessage stale = await SendDatePatchAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 2,
            title: "Should fail",
            instructions: "Changed",
            startsAt: seed.Seed.Now.AddDays(1),
            endsAt: seed.Seed.Now.AddDays(1).AddHours(5),
            cancellationDeadlineAt: seed.Seed.Now.AddHours(21));

        await SignupDecisionTestSupport.AssertProblemAsync(
            stale,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        Assert.Equal("Edited title", date.Title);
        Assert.Equal("Prepare food.", date.Instructions);
        Assert.Equal(seed.Seed.Now.AddHours(22), date.CancellationDeadlineAt);
        Assert.Equal(3, date.Version);
    }

    [RequiresPostgresTheory]
    [InlineData("close")]
    [InlineData("cancel")]
    public async Task CloseOrCancelStaleIfMatchRaceReturnsPreconditionFailedWithoutPartialWrites(
        string action)
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeRevalidate: () => AdvanceServiceDateVersionAsync(database, seed.Seed.ServiceDateId));
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        await ApproveAsync(database, host, seed, manager);
        EffectCounts beforeEffects = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            action,
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Weather");

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        SignupEntity signup = await context.Signups.SingleAsync(candidate => candidate.Id == seed.SignupId.Value);
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        Assert.Equal((short)ServiceDateStatus.Open, date.Status);
        Assert.Equal(3, date.Version);
        Assert.Equal((short)HelpNeedStatus.Open, need.Status);
        Assert.Equal(0, need.Version);
        Assert.Equal((short)SignupStatus.Approved, signup.Status);
        Assert.Equal(1, signup.Version);
        Assert.Equal((short)ThreadStatus.Open, thread.Status);
        Assert.Equal(0, thread.Version);
        Assert.Equal(beforeEffects, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task CloseCommittedAfterSubmissionLoadStillRejectsSignupSaveWithoutWrites()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<HelpNeedSignups> loaded = await repository.GetAsync(seed.OrganizationId, seed.HelpNeedId);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        SignupId signupId = SignupId.New();
        Assert.True(
            loaded.Value.Submit(
                signupId,
                PersistenceSeed.ActiveMembership(seed.MemberMembershipId, seed.OrganizationId, "Member"),
                SignupKind.Individual,
                [],
                0,
                seed.Now).IsSuccess);

        await using (TabrukDbContext closing = database.CreateContext())
        {
            ServiceDateEntity date = await closing.ServiceDates.SingleAsync();
            HelpNeedEntity need = await closing.HelpNeeds.SingleAsync();
            date.Status = (short)ServiceDateStatus.Closed;
            date.Version = 3;
            need.Status = (short)HelpNeedStatus.Closed;
            need.Version = 1;
            await closing.SaveChangesAsync();
        }

        Result saved = await repository.SaveSubmissionAsync(
            loaded.Value,
            new SignupSubmissionWrite(
                signupId,
                null,
                CreateSubmissionEffects(seed, signupId)));

        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCodes.CategoryClosed, saved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        Assert.Empty(await verification.Signups.ToListAsync());
        Assert.Empty(await verification.Notifications.ToListAsync());
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
    }

    private static async Task ApproveAsync(
        PostgresTestDatabase database,
        AuthApiHost host,
        DecisionSeed seed,
        TokenSetResponse manager)
    {
        long signupVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            signupVersion);
        await SignupDecisionTestSupport.ReadSignupAsync(response, HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> SendDateMutationAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string action,
        string accessToken,
        IdempotencyKey idempotencyKey,
        long expectedVersion,
        string? reason)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}/{action}")
        {
            Content = JsonContent.Create(new { reason }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            idempotencyKey.ToString());
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendNeedPatchAsync(
        HttpClient client,
        HelpNeedId helpNeedId,
        string accessToken,
        long expectedVersion,
        string instructions,
        int? capacity,
        string status)
    {
        HttpRequestMessage request = new(
            HttpMethod.Patch,
            $"{ApiDefaults.BasePath}/needs/{helpNeedId}")
        {
            Content = JsonContent.Create(new { instructions, capacity, status }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendDatePatchAsync(
        HttpClient client,
        ServiceDateId serviceDateId,
        string accessToken,
        long expectedVersion,
        string title,
        string instructions,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset cancellationDeadlineAt)
    {
        HttpRequestMessage request = new(
            HttpMethod.Patch,
            $"{ApiDefaults.BasePath}/dates/{serviceDateId}")
        {
            Content = JsonContent.Create(
                new
                {
                    title,
                    instructions,
                    startsAt,
                    endsAt,
                    cancellationDeadlineAt,
                }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IfMatchHeaderName,
            $"\"{expectedVersion}\"");
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendManagedMutationAsync(
        HttpClient client,
        PersistenceSeed seed,
        string accessToken,
        string mutation,
        long expectedDateVersion,
        long expectedNeedVersion) =>
        mutation switch
        {
            "edit-date" => SendDatePatchAsync(
                client,
                seed.ServiceDateId,
                accessToken,
                expectedDateVersion,
                title: "Edited title",
                instructions: "Prepare food.",
                startsAt: seed.Now.AddDays(1),
                endsAt: seed.Now.AddDays(1).AddHours(4),
                cancellationDeadlineAt: seed.Now.AddHours(22)),
            "edit-need" => SendNeedPatchAsync(
                client,
                seed.HelpNeedId,
                accessToken,
                expectedNeedVersion,
                instructions: "Updated instructions",
                capacity: 9,
                status: "open"),
            "close" => SendDateMutationAsync(
                client,
                seed.ServiceDateId,
                "close",
                accessToken,
                IdempotencyKey.New(),
                expectedDateVersion,
                reason: "Coordination complete"),
            "cancel" => SendDateMutationAsync(
                client,
                seed.ServiceDateId,
                "cancel",
                accessToken,
                IdempotencyKey.New(),
                expectedDateVersion,
                reason: "Weather"),
            _ => throw new InvalidOperationException($"Unknown mutation '{mutation}'."),
        };

    private static async Task AddApprovedSignupAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        MembershipId primaryMembershipId)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(candidate => candidate.Id == seed.HelpNeedId.Value);
        context.Signups.Add(
            new SignupEntity
            {
                Id = SignupId.New().Value,
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                HelpNeedId = seed.HelpNeedId.Value,
                PrimaryMembershipId = primaryMembershipId.Value,
                Kind = (short)SignupKind.Individual,
                UnnamedParticipantCount = 0,
                Status = (short)SignupStatus.Approved,
                SubmittedAt = seed.Now,
                LastTransitionAt = seed.Now.AddMinutes(1),
                Version = 1,
            });
        need.SignupVersion++;
        await context.SaveChangesAsync();
    }

    private static async Task AddSecondHelpNeedWithApprovedSignupAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        MembershipId primaryMembershipId)
    {
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync(
            candidate => candidate.Id == seed.ServiceDateId.Value);
        HelpNeedId helpNeedId = HelpNeedId.New();
        context.HelpNeeds.Add(
            new HelpNeedEntity
            {
                Id = helpNeedId.Value,
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                Category = (short)HelpCategory.Serving,
                Instructions = "Serve food.",
                Capacity = 10,
                Status = (short)HelpNeedStatus.Open,
                Version = 0,
                SignupVersion = 1,
                WaitlistOrderHighWater = 0,
            });
        context.Signups.Add(
            new SignupEntity
            {
                Id = SignupId.New().Value,
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                HelpNeedId = helpNeedId.Value,
                PrimaryMembershipId = primaryMembershipId.Value,
                Kind = (short)SignupKind.Individual,
                UnnamedParticipantCount = 0,
                Status = (short)SignupStatus.Approved,
                SubmittedAt = seed.Now,
                LastTransitionAt = seed.Now.AddMinutes(1),
                Version = 1,
            });
        date.Version = 3;
        await context.SaveChangesAsync();
    }

    private static async Task DeleteThreadAsync(
        PostgresTestDatabase database,
        ThreadId threadId)
    {
        await using TabrukDbContext context = database.CreateContext();
        DateThreadEntity thread = await context.DateThreads.SingleAsync(
            candidate => candidate.Id == threadId.Value);
        context.DateThreads.Remove(thread);
        await context.SaveChangesAsync();
    }

    private static async Task DeleteSignupAsync(
        PostgresTestDatabase database,
        SignupId signupId,
        HelpNeedId helpNeedId)
    {
        await using TabrukDbContext context = database.CreateContext();
        SignupEntity signup = await context.Signups.SingleAsync(
            candidate => candidate.Id == signupId.Value);
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == helpNeedId.Value);
        context.Signups.Remove(signup);
        need.SignupVersion--;
        await context.SaveChangesAsync();
    }

    private static async Task ApplyConcurrentNeedPatchAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId,
        string instructions,
        int? capacity,
        HelpNeedStatus status,
        long version)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(
            candidate => candidate.Id == helpNeedId.Value);
        need.Instructions = instructions;
        need.Capacity = capacity;
        need.Status = (short)status;
        need.Version = version;
        await context.SaveChangesAsync();
    }

    private static async Task AdvanceServiceDateVersionAsync(
        PostgresTestDatabase database,
        ServiceDateId serviceDateId)
    {
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync(
            candidate => candidate.Id == serviceDateId.Value);
        date.Version++;
        await context.SaveChangesAsync();
    }

    private static async Task AdvanceSignupVersionAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(candidate => candidate.Id == helpNeedId.Value);
        need.SignupVersion++;
        await context.SaveChangesAsync();
    }

    private static async Task<AuthApiHost> StartWithDateRepositoryProxyAsync(
        PostgresTestDatabase database,
        MutableClock clock,
        Func<Task>? beforeRevalidate = null,
        Func<Task>? beforeSaveDate = null,
        Func<Task>? beforeSaveNeed = null) =>
        await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
            {
                services.RemoveAll<IServiceDateRepository>();
                services.AddScoped<IServiceDateRepository>(
                    provider =>
                        DateRepositoryProxy.Create(
                            new PostgresServiceDateRepository(provider.GetRequiredService<TabrukDbContext>()),
                            beforeRevalidate,
                            beforeSaveDate,
                            beforeSaveNeed));
            });

    private static async Task CreateFailureTriggerAsync(
        PostgresTestDatabase database,
        string table,
        string triggerEvent)
    {
        string functionName = $"fail_t17_{table}_{triggerEvent}".ToLowerInvariant();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            CREATE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected t17 dependency write failure'
                    USING ERRCODE = '08006';
            END
            $$;
            CREATE TRIGGER {functionName}_trigger
            BEFORE {triggerEvent} ON {table}
            FOR EACH ROW EXECUTE FUNCTION {functionName}();
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static SignupPersistenceEffects CreateSubmissionEffects(
        PersistenceSeed seed,
        SignupId signupId)
    {
        Result<NotificationCreated> notification = Notification.Create(
            NotificationId.New(),
            seed.OrganizationId,
            seed.ManagerMembershipId,
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            signupId.Value,
            "New signup request",
            "A new signup request is pending.",
            seed.Now);
        Assert.True(notification.IsSuccess, notification.IsFailure ? notification.Error.Message : null);
        return new SignupPersistenceEffects(
            [notification.Value.Notification],
            [
                new AuditEntry(
                    AuditEventId.New(),
                    seed.OrganizationId,
                    seed.MemberMembershipId,
                    "signup.submitted",
                    "signup",
                    signupId.ToString(),
                    "Integration test submission",
                    "signup_submission",
                    signupId.ToString(),
                    beforeState: null,
                    afterState: "{\"status\":\"pending\"}",
                    seed.Now),
            ],
            [
                new HusayniaTabruk.Application.Abstractions.Messaging.OutboxMessage(
                    OutboxMessageId.New(),
                    seed.OrganizationId,
                    "notification.push_requested",
                    "{}",
                    seed.Now),
            ]);
    }

    private static async Task<DateMutationState> ReadStateAsync(
        PostgresTestDatabase database,
        HelpNeedId helpNeedId,
        SignupId signupId)
    {
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync(candidate => candidate.Id == helpNeedId.Value);
        SignupEntity signup = await context.Signups.SingleAsync(candidate => candidate.Id == signupId.Value);
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        return new DateMutationState(
            date.Status,
            date.Version,
            need.Capacity,
            need.Status,
            need.Version,
            need.SignupVersion,
            signup.Status,
            signup.Version,
            thread.Status,
            thread.Version,
            await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    private sealed record DateMutationState(
        short DateStatus,
        long DateVersion,
        int? NeedCapacity,
        short NeedStatus,
        long NeedVersion,
        long NeedSignupVersion,
        short SignupStatus,
        long SignupVersion,
        short ThreadStatus,
        long ThreadVersion,
        EffectCounts Effects);

    public class DateRepositoryProxy : DispatchProxy
    {
        private PostgresServiceDateRepository inner = null!;
        private Func<Task> beforeRevalidate = static () => Task.CompletedTask;
        private Func<Task> beforeSaveDate = static () => Task.CompletedTask;
        private Func<Task> beforeSaveNeed = static () => Task.CompletedTask;
        private int revalidated;
        private int savedDate;
        private int savedNeed;

        internal static IServiceDateRepository Create(
            PostgresServiceDateRepository inner,
            Func<Task>? beforeRevalidate,
            Func<Task>? beforeSaveDate,
            Func<Task>? beforeSaveNeed)
        {
            IServiceDateRepository value =
                DispatchProxy.Create<IServiceDateRepository, DateRepositoryProxy>();
            DateRepositoryProxy proxy = (DateRepositoryProxy)value;
            proxy.inner = inner;
            proxy.beforeRevalidate = beforeRevalidate ?? (static () => Task.CompletedTask);
            proxy.beforeSaveDate = beforeSaveDate ?? (static () => Task.CompletedTask);
            proxy.beforeSaveNeed = beforeSaveNeed ?? (static () => Task.CompletedTask);
            return value;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            MethodInfo method =
                targetMethod ?? throw new InvalidOperationException("Missing target method.");
            if (method.Name == "RevalidateManagedAuthorityAsync")
            {
                return new ValueTask<Result>(InterceptRevalidateAsync(args!).AsTask());
            }

            if (method.Name == "SaveAsync")
            {
                return new ValueTask<Result>(InterceptSaveAsync(args!).AsTask());
            }

            if (method.Name == "SaveNeedAsync")
            {
                return new ValueTask<Result>(InterceptSaveNeedAsync(args!).AsTask());
            }

            return method.Invoke(inner, args);
        }

        private async ValueTask<Result> InterceptRevalidateAsync(object?[] args)
        {
            if (Interlocked.Exchange(ref revalidated, 1) == 0)
            {
                await beforeRevalidate();
            }

            return await inner.RevalidateManagedAuthorityAsync(
                (OrganizationId)args[0]!,
                (ServiceDateId)args[1]!,
                (MembershipId)args[2]!,
                (CancellationToken)args[3]!);
        }

        private async ValueTask<Result> InterceptSaveAsync(object?[] args)
        {
            if (Interlocked.Exchange(ref savedDate, 1) == 0)
            {
                await beforeSaveDate();
            }

            return await inner.SaveAsync(
                (LoadedServiceDate)args[0]!,
                (DatePersistenceEffects)args[1]!,
                (CancellationToken)args[2]!);
        }

        private async ValueTask<Result> InterceptSaveNeedAsync(object?[] args)
        {
            if (Interlocked.Exchange(ref savedNeed, 1) == 0)
            {
                await beforeSaveNeed();
            }

            return await inner.SaveNeedAsync(
                (LoadedServiceDate)args[0]!,
                (HelpNeedId)args[1]!,
                (long)args[2]!,
                (long?)args[3],
                (DatePersistenceEffects)args[4]!,
                (CancellationToken)args[5]!);
        }
    }
}
