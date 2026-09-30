using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using HusayniaTabruk.IntegrationTests.Signups.Decisions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Signups.CancellationReassignment;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Signups")]
[Trait("Category", "T16")]
public sealed class CancellationReassignmentIntegrationTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task CancellationAndReassignmentUseLiveActorTenantOwnershipAndManagingAuthority()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        TestActor unrelated =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse unrelatedToken =
            await SignupDecisionTestSupport.LoginAsync(host, unrelated.Email);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);

        using HttpResponseMessage wrongOwner = await WithdrawAsync(
            host.Client,
            seed.SignupId,
            unrelatedToken.AccessToken,
            IdempotencyKey.New(),
            version);
        await SignupDecisionTestSupport.AssertProblemAsync(
            wrongOwner,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);

        using HttpResponseMessage nonManager = await ReassignAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            seed.SignupId,
            unrelatedToken.AccessToken,
            IdempotencyKey.New(),
            version);
        await SignupDecisionTestSupport.AssertProblemAsync(
            nonManager,
            HttpStatusCode.NotFound,
            SignupApplicationErrorCodes.SignupNotFound);

        await SignupDecisionTestSupport.DisableMembershipAsync(
            database,
            seed.Seed.MemberMembershipId);
        using HttpResponseMessage disabled = await WithdrawAsync(
            host.Client,
            seed.SignupId,
            member.AccessToken,
            IdempotencyKey.New(),
            version);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
        Assert.Equal(
            SignupStatus.Pending,
            await SignupDecisionTestSupport.ReadStatusAsync(database, seed.SignupId));
        Assert.Equal(
            EffectCounts.Zero,
            await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task ApprovedPrimaryCancelsBeforeDeadlineRetryIsStableAndThreadReplayIsDenied()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);

        SignupContract approved = await ApproveAsync(
            database,
            host,
            seed,
            manager,
            seed.SignupId);
        DateThread thread = DateThread.Create(
            seed.Seed.ThreadId,
            seed.Seed.OrganizationId,
            seed.Seed.ServiceDateId).Value;
        (_, Domain.Accounts.Membership memberDomain, Domain.Dates.ServiceDate date) =
            seed.Seed.CreateThreadDomainView();
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            memberDomain,
            date,
            seed.SignupId)).IsSuccess);

        IdempotencyKey key = IdempotencyKey.New();
        using HttpResponseMessage first = await WithdrawAsync(
            host.Client,
            seed.SignupId,
            member.AccessToken,
            key,
            approved.SignupVersion);
        SignupContract cancelled = await SignupDecisionTestSupport.ReadSignupAsync(
            first,
            HttpStatusCode.OK);
        Assert.Equal("cancelled", cancelled.Status, ignoreCase: true);
        Assert.True((await SignupDecisionTestSupport.ThreadAccessAsync(
            database,
            thread,
            memberDomain,
            date,
            seed.SignupId)).IsFailure);
        SignupPageContract memberProjection = await SignupDecisionTestSupport.GetMineAsync(
            host.Client,
            member.AccessToken);
        Assert.Equal(
            "cancelled",
            memberProjection.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);
        RosterContract rosterProjection = await SignupDecisionTestSupport.GetRosterAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);
        Assert.Equal(
            "cancelled",
            rosterProjection.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);

        Result<MessagePosted> replay = await QueuedPostAsync(
            database,
            thread,
            memberDomain,
            date,
            seed.SignupId,
            clock.UtcNow);
        Assert.True(replay.IsFailure);

        using HttpResponseMessage retry = await WithdrawAsync(
            host.Client,
            seed.SignupId,
            member.AccessToken,
            key,
            approved.SignupVersion);
        SignupContract retried = await SignupDecisionTestSupport.ReadSignupAsync(
            retry,
            HttpStatusCode.OK);
        Assert.Equal(cancelled, retried);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(2, await context.Notifications.CountAsync());
        Assert.Equal(2, await context.AuditEvents.CountAsync());
        Assert.Equal(3, await context.OutboxMessages.CountAsync());
        Assert.Equal(
            "thread.access_changed",
            (await context.OutboxMessages
                .SingleAsync(message => message.Type == "thread.access_changed")).Type);
        Assert.Equal(2, await context.IdempotencyRecords.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task PostDeadlineSelfWithdrawalIsDeniedButManagingFoodInchargeCanCancelWithAuditReason()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);
        SignupContract approved = await ApproveAsync(
            database,
            host,
            seed,
            manager,
            seed.SignupId);
        clock.Advance(TimeSpan.FromHours(23));
        manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);

        using HttpResponseMessage denied = await WithdrawAsync(
            host.Client,
            seed.SignupId,
            member.AccessToken,
            IdempotencyKey.New(),
            approved.SignupVersion);
        ProblemContract deadlineProblem = await SignupDecisionTestSupport.AssertProblemAsync(
            denied,
            HttpStatusCode.Conflict,
            ErrorCodes.CancellationDeadlinePassed);
        Assert.Contains(
            "Food Incharge",
            deadlineProblem.Detail,
            StringComparison.Ordinal);
        Assert.Equal(
            SignupStatus.Approved,
            await SignupDecisionTestSupport.ReadStatusAsync(database, seed.SignupId));

        const string reason = "Member contacted the managing Food Incharge";
        using HttpResponseMessage overridden = await OverrideAsync(
            host.Client,
            seed.SignupId,
            manager.AccessToken,
            IdempotencyKey.New(),
            approved.SignupVersion,
            reason);
        SignupContract cancelled = await SignupDecisionTestSupport.ReadSignupAsync(
            overridden,
            HttpStatusCode.OK);
        Assert.Equal("cancelled", cancelled.Status, ignoreCase: true);
        SignupPageContract memberProjection = await SignupDecisionTestSupport.GetMineAsync(
            host.Client,
            member.AccessToken);
        Assert.Equal(
            "cancelled",
            memberProjection.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);
        RosterContract rosterProjection = await SignupDecisionTestSupport.GetRosterAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);
        Assert.Equal(
            "cancelled",
            rosterProjection.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);

        await using TabrukDbContext context = database.CreateContext();
        AuditEventEntity audit = await context.AuditEvents.SingleAsync(
            entry => entry.Action == "signup.cancelled");
        Assert.Equal(reason, audit.Reason);
        Assert.Equal("signup_cancellation_override", audit.Purpose);
        Assert.Equal(2, await context.Notifications.CountAsync());
        NotificationEntity notification = await context.Notifications.SingleAsync(
            entry => entry.Title == "Signup cancelled");
        Assert.Equal(seed.Seed.MemberMembershipId.Value, notification.RecipientMembershipId);
        Assert.Equal(seed.SignupId.Value, notification.ResourceId);
        Assert.Equal(
            "Your signup was cancelled by the managing Food Incharge.",
            notification.Body);
        Assert.Equal(3, await context.OutboxMessages.CountAsync());
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "thread.access_changed"));
    }

    [RequiresPostgresFact]
    public async Task SelectedLaterWaiterMayBeReassignedButOmittedApprovedSignupStillBlocksCapacity()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        TestActor laterMember =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        SignupId laterSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            laterMember.MembershipId);
        await SetCapacityAsync(database, 1);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse earlierToken = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);
        TokenSetResponse laterToken =
            await SignupDecisionTestSupport.LoginAsync(host, laterMember.Email);

        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract firstWaiter = await DecideAsync(
            host,
            manager,
            seed.SignupId,
            "waitlist",
            version);
        SignupContract laterWaiter = await DecideAsync(
            host,
            manager,
            laterSignup,
            "waitlist",
            firstWaiter.SignupVersion);
        Assert.True(firstWaiter.WaitlistOrder < laterWaiter.WaitlistOrder);

        using HttpResponseMessage selected = await ReassignAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            laterSignup,
            manager.AccessToken,
            IdempotencyKey.New(),
            laterWaiter.SignupVersion);
        SignupContract reassigned = await SignupDecisionTestSupport.ReadSignupAsync(
            selected,
            HttpStatusCode.OK);
        Assert.Equal("approved", reassigned.Status, ignoreCase: true);
        Assert.Equal(
            SignupStatus.Waitlisted,
            await SignupDecisionTestSupport.ReadStatusAsync(database, seed.SignupId));

        SignupPageContract mine = await SignupDecisionTestSupport.GetMineAsync(
            host.Client,
            laterToken.AccessToken);
        Assert.Equal(
            "approved",
            mine.Items.Single(item => item.Id == laterSignup.ToString()).Status,
            ignoreCase: true);
        SignupPageContract earlierMine = await SignupDecisionTestSupport.GetMineAsync(
            host.Client,
            earlierToken.AccessToken);
        Assert.Equal(
            "waitlisted",
            earlierMine.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);
        RosterContract roster = await SignupDecisionTestSupport.GetRosterAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);
        Assert.Equal(
            "waitlisted",
            roster.Items.Single(item => item.Id == seed.SignupId.ToString()).Status,
            ignoreCase: true);
        Assert.Equal(
            "approved",
            roster.Items.Single(item => item.Id == laterSignup.ToString()).Status,
            ignoreCase: true);

        TestActor thirdMember =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        SignupId thirdSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            thirdMember.MembershipId);
        long currentVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract thirdWaiter = await DecideAsync(
            host,
            manager,
            thirdSignup,
            "waitlist",
            currentVersion);
        using HttpResponseMessage overbook = await ReassignAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            thirdSignup,
            manager.AccessToken,
            IdempotencyKey.New(),
            thirdWaiter.SignupVersion);
        await SignupDecisionTestSupport.AssertProblemAsync(
            overbook,
            HttpStatusCode.Conflict,
            ErrorCodes.CapacityUnavailable);
        Assert.Equal(
            1,
            await CountStatusAsync(database, SignupStatus.Approved));
    }

    [RequiresPostgresFact]
    public async Task WithdrawingMaximumWaitlistOrderThenRehydratingDoesNotReuseIt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        TestActor secondMember =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        SignupId secondSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            secondMember.MembershipId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse secondToken =
            await SignupDecisionTestSupport.LoginAsync(host, secondMember.Email);
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract first = await DecideAsync(
            host,
            manager,
            seed.SignupId,
            "waitlist",
            version);
        SignupContract second = await DecideAsync(
            host,
            manager,
            secondSignup,
            "waitlist",
            first.SignupVersion);
        Assert.Equal(2, second.WaitlistOrder);

        using HttpResponseMessage withdrawnResponse = await WithdrawAsync(
            host.Client,
            secondSignup,
            secondToken.AccessToken,
            IdempotencyKey.New(),
            second.SignupVersion);
        SignupContract withdrawn = await SignupDecisionTestSupport.ReadSignupAsync(
            withdrawnResponse,
            HttpStatusCode.OK);

        TestActor thirdMember =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        SignupId thirdSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            thirdMember.MembershipId);
        long rehydratedVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract third = await DecideAsync(
            host,
            manager,
            thirdSignup,
            "waitlist",
            rehydratedVersion);

        Assert.Equal("withdrawn", withdrawn.Status, ignoreCase: true);
        Assert.Equal(3, third.WaitlistOrder);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(3, (await context.HelpNeeds.SingleAsync()).WaitlistOrderHighWater);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentReassignmentAndWaitlistingCannotSplitHighWaterOrChildren()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        TestActor pendingMember =
            await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        SignupId pendingSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            pendingMember.MembershipId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract waiter = await DecideAsync(
            host,
            manager,
            seed.SignupId,
            "waitlist",
            version);

        IdempotencyKey reassignKey = IdempotencyKey.New();
        IdempotencyKey waitlistKey = IdempotencyKey.New();
        Task<HttpResponseMessage>[] requests =
        [
            ReassignAsync(
                host.Client,
                seed.Seed.HelpNeedId,
                seed.SignupId,
                manager.AccessToken,
                reassignKey,
                waiter.SignupVersion),
            SignupDecisionTestSupport.DecideAsync(
                host.Client,
                pendingSignup,
                "waitlist",
                manager.AccessToken,
                waitlistKey,
                waiter.SignupVersion),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        try
        {
            Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Contains(
                responses,
                response => response.StatusCode == HttpStatusCode.PreconditionFailed);
            await using TabrukDbContext context = database.CreateContext();
            HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
            SignupEntity[] signups = await context.Signups
                .OrderBy(signup => signup.Id)
                .ToArrayAsync();
            Assert.True(
                signups.Count(signup => signup.Status == (short)SignupStatus.Approved) <= 1);
            long[] orders = signups
                .Where(signup => signup.WaitlistOrder.HasValue)
                .Select(signup => signup.WaitlistOrder!.Value)
                .ToArray();
            Assert.Equal(orders.Length, orders.Distinct().Count());
            Assert.All(orders, order => Assert.InRange(order, 1, need.WaitlistOrderHighWater));
            Assert.Equal(waiter.SignupVersion + 1, need.SignupVersion);
            Assert.InRange(need.WaitlistOrderHighWater, 1, 2);
            Assert.True(orders.DefaultIfEmpty(0).Max() <= need.WaitlistOrderHighWater);

            long retryVersion = need.SignupVersion;
            using HttpResponseMessage recovered =
                responses[0].StatusCode == HttpStatusCode.PreconditionFailed
                    ? await ReassignAsync(
                        host.Client,
                        seed.Seed.HelpNeedId,
                        seed.SignupId,
                        manager.AccessToken,
                        reassignKey,
                        retryVersion)
                    : await SignupDecisionTestSupport.DecideAsync(
                        host.Client,
                        pendingSignup,
                        "waitlist",
                        manager.AccessToken,
                        waitlistKey,
                        retryVersion);
            await SignupDecisionTestSupport.ReadSignupAsync(
                recovered,
                HttpStatusCode.OK);

            context.ChangeTracker.Clear();
            HelpNeedEntity recoveredNeed = await context.HelpNeeds.SingleAsync();
            SignupEntity[] recoveredSignups = await context.Signups.ToArrayAsync();
            Assert.Equal(
                1,
                recoveredSignups.Count(
                    signup => signup.Status == (short)SignupStatus.Approved));
            Assert.Equal(
                recoveredSignups
                    .Where(signup => signup.WaitlistOrder.HasValue)
                    .Select(signup => signup.WaitlistOrder!.Value)
                    .Distinct()
                    .Count(),
                recoveredSignups.Count(signup => signup.WaitlistOrder.HasValue));
            Assert.Equal(retryVersion + 1, recoveredNeed.SignupVersion);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    [RequiresPostgresFact]
    public async Task ReassignmentEffectFailureRollsBackChildVersionHighWaterAndIdempotency()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SignupContract waiter = await DecideAsync(
            host,
            manager,
            seed.SignupId,
            "waitlist",
            version);
        DatabaseState before =
            await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);
        await CreateFailureTriggerAsync(database, "outbox_messages", "INSERT");

        IdempotencyKey key = IdempotencyKey.New();
        using HttpResponseMessage response = await ReassignAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            seed.SignupId,
            manager.AccessToken,
            key,
            waiter.SignupVersion);

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            ErrorCodes.DependencyUnavailable);
        Assert.Equal(
            before,
            await SignupDecisionTestSupport.ReadDatabaseStateAsync(database));
        await DropFailureTriggerAsync(database, "outbox_messages", "INSERT");

        using HttpResponseMessage retry = await ReassignAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            seed.SignupId,
            manager.AccessToken,
            key,
            waiter.SignupVersion);
        SignupContract recovered = await SignupDecisionTestSupport.ReadSignupAsync(
            retry,
            HttpStatusCode.OK);
        Assert.Equal("approved", recovered.Status, ignoreCase: true);
    }

    private static async Task<SignupContract> ApproveAsync(
        PostgresTestDatabase database,
        AuthApiHost host,
        DecisionSeed seed,
        TokenSetResponse manager,
        SignupId signupId)
    {
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        return await DecideAsync(host, manager, signupId, "approve", version);
    }

    private static async Task<SignupContract> DecideAsync(
        AuthApiHost host,
        TokenSetResponse manager,
        SignupId signupId,
        string action,
        long version)
    {
        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            signupId,
            action,
            manager.AccessToken,
            IdempotencyKey.New(),
            version);
        return await SignupDecisionTestSupport.ReadSignupAsync(
            response,
            HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> WithdrawAsync(
        HttpClient client,
        SignupId signupId,
        string accessToken,
        IdempotencyKey key,
        long version) =>
        SendAsync(
            client,
            $"/api/v1/signups/{signupId}/withdraw",
            accessToken,
            key,
            version,
            new { });

    private static Task<HttpResponseMessage> OverrideAsync(
        HttpClient client,
        SignupId signupId,
        string accessToken,
        IdempotencyKey key,
        long version,
        string reason) =>
        SendAsync(
            client,
            $"/api/v1/signups/{signupId}/override",
            accessToken,
            key,
            version,
            new { targetState = "cancelled", reason });

    private static Task<HttpResponseMessage> ReassignAsync(
        HttpClient client,
        HelpNeedId helpNeedId,
        SignupId signupId,
        string accessToken,
        IdempotencyKey key,
        long version) =>
        SendAsync(
            client,
            $"/api/v1/needs/{helpNeedId}/reassign",
            accessToken,
            key,
            version,
            new { signupId = signupId.ToString(), reason = "Selected for available capacity" });

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string path,
        string accessToken,
        IdempotencyKey key,
        long version,
        object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Idempotency-Key", key.ToString());
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return await client.SendAsync(request);
    }

    private static async Task<Result<MessagePosted>> QueuedPostAsync(
        PostgresTestDatabase database,
        DateThread thread,
        Domain.Accounts.Membership member,
        Domain.Dates.ServiceDate date,
        SignupId signupId,
        DateTimeOffset now)
    {
        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<Domain.Signups.HelpNeedSignups> aggregate = await repository.GetAsync(
            member.OrganizationId,
            date.HelpNeeds.Single().Id);
        Assert.True(aggregate.IsSuccess);
        Domain.Signups.Signup signup = aggregate.Value.Signups.Single(
            candidate => candidate.Id == signupId);
        return thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "queued message",
            member,
            date,
            signup,
            now);
    }

    private static async Task<int> CountStatusAsync(
        PostgresTestDatabase database,
        SignupStatus status)
    {
        await using TabrukDbContext context = database.CreateContext();
        return await context.Signups.CountAsync(
            signup => signup.Status == (short)status);
    }

    private static async Task SetCapacityAsync(
        PostgresTestDatabase database,
        int capacity)
    {
        await using TabrukDbContext context = database.CreateContext();
        (await context.HelpNeeds.SingleAsync()).Capacity = capacity;
        await context.SaveChangesAsync();
    }

    private static async Task CreateFailureTriggerAsync(
        PostgresTestDatabase database,
        string table,
        string triggerEvent)
    {
        string functionName = $"fail_t16_{table}_{triggerEvent}".ToLowerInvariant();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            CREATE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected t16 dependency write failure'
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

    private static async Task DropFailureTriggerAsync(
        PostgresTestDatabase database,
        string table,
        string triggerEvent)
    {
        string functionName = $"fail_t16_{table}_{triggerEvent}".ToLowerInvariant();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            DROP TRIGGER {functionName}_trigger ON {table};
            DROP FUNCTION {functionName}();
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
