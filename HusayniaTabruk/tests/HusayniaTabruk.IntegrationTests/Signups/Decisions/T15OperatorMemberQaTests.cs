using System.Net;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.IntegrationTests.Signups.Decisions;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "T15QA")]
public sealed class T15OperatorMemberQaTests
{
    [RequiresPostgresFact]
    public async Task OperatorApprovalFlowsToMemberMineRosterNotificationAndOutbox()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.MemberEmail);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1,
            "Approved by duty operator");
        SignupContract approved = await SignupDecisionTestSupport.ReadSignupAsync(
            response,
            HttpStatusCode.OK);
        SignupPageContract mine = await SignupDecisionTestSupport.GetMineAsync(
            host.Client,
            member.AccessToken);
        RosterContract roster = await SignupDecisionTestSupport.GetRosterAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken);

        Assert.Equal("approved", mine.Items.Single(item => item.Id == seed.SignupId.ToString()).Status);
        Assert.Equal("approved", roster.Items.Single(item => item.Id == seed.SignupId.ToString()).Status);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal("Signup approved", (await context.Notifications.SingleAsync()).Title);
        Assert.Equal("notification.push_requested", (await context.OutboxMessages.SingleAsync()).Type);
        Assert.Equal(EffectCounts.OneSuccess, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));

        Console.WriteLine(
            $"PRIMARY decision={response.StatusCode} member={mine.Items.Single().Status} "
            + $"roster={roster.Items.Single().Status} notification=Signup approved "
            + $"outbox=notification.push_requested etag={response.Headers.ETag?.Tag} "
            + $"version={approved.SignupVersion}");
    }

    [RequiresPostgresFact]
    public async Task CapacityConflictRollsBackAndSameKeyRecoversAfterCapacityCorrection()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        await SignupDecisionTestSupport.FillCapacityAsync(database, seed);
        long version = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage blocked = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            key,
            version);
        ProblemContract problem = await SignupDecisionTestSupport.AssertProblemAsync(
            blocked,
            HttpStatusCode.Conflict,
            ErrorCodes.CapacityUnavailable);
        Assert.Equal(EffectCounts.Zero, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));

        await using (TabrukDbContext context = database.CreateContext())
        {
            (await context.HelpNeeds.SingleAsync()).Capacity = 2;
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage recovered = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            key,
            version);
        SignupContract approved = await SignupDecisionTestSupport.ReadSignupAsync(
            recovered,
            HttpStatusCode.OK);
        Assert.Equal("approved", approved.Status);
        Assert.Equal(EffectCounts.OneSuccess, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));

        Console.WriteLine(
            $"CAPACITY first={blocked.StatusCode}:{problem.Code} effects=0 "
            + $"retry={recovered.StatusCode}:{approved.Status} effects=1 sameKey=true");
    }
}
