using System.Net;
using System.Reflection;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace HusayniaTabruk.IntegrationTests.Signups.Decisions;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Signups")]
[Trait("Category", "T15")]
public sealed class SignupDecisionConcurrencyTests : PostgresPersistenceTest
{
    [RequiresPostgresFact]
    public async Task SimultaneousCapacityOneApprovalsFromTheSameRootYieldOneWinnerAndOneStaleVersion()
    {
        ConcurrencyResult result = await RunCapacityOneRaceAsync();

        Assert.Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.PreconditionFailed },
            result.Statuses.Order().ToArray());
        Assert.Equal(1, result.ApprovedCount);
        Assert.Equal(1, result.PendingCount);
    }

    [RequiresPostgresFact]
    public async Task SimultaneousCapacityOneApprovalsNeverOverbookAndPersistWinnerOnlyEffects()
    {
        ConcurrencyResult result = await RunCapacityOneRaceAsync();

        Assert.Equal(1, result.ApprovedCount);
        Assert.Equal(1, result.ApprovedParticipantCount);
        Assert.Equal(EffectCounts.OneSuccess, result.Effects);
    }

    [RequiresPostgresFact]
    public async Task CategoryCloseCommittedAfterLoadBeforeSaveReturnsCategoryClosedAndRollsBack()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        SaveInterceptionCoordinator coordinator = new(
            participants: 1,
            beforeRelease: () => CloseCategoryForRaceAsync(database));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartInterceptedHostAsync(
            database,
            clock,
            coordinator);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        DatabaseState before = await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);

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
            ErrorCodes.CategoryClosed);
        DatabaseState after = await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);
        Assert.Equal(before.Signups, after.Signups);
        Assert.Equal(before.SignupVersion, after.SignupVersion);
        Assert.Equal(before.HighWater, after.HighWater);
        Assert.Equal(EffectCounts.Zero, after.Effects);
    }

    [RequiresPostgresFact]
    public async Task ForcedSignupVersionCasLossRollsBackDecisionEffectsAndNewReceipt()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        SaveInterceptionCoordinator coordinator = new(
            participants: 1,
            beforeRelease: () => AdvanceSignupVersionAsync(database));
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartInterceptedHostAsync(
            database,
            clock,
            coordinator);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
            host.Client,
            seed.SignupId,
            "approve",
            manager.AccessToken,
            IdempotencyKey.New(),
            1);

        await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        Assert.Equal(SignupStatus.Pending, await SignupDecisionTestSupport.ReadStatusAsync(database, seed.SignupId));
        Assert.Equal(EffectCounts.Zero, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
    }

    [RequiresPostgresFact]
    public async Task AuditNotificationOutboxAndIdempotencyCompletionFailuresRollBackAllWrites()
    {
        foreach ((string Table, string TriggerEvent) failure in new[]
        {
            ("audit_events", "INSERT"),
            ("notifications", "INSERT"),
            ("outbox_messages", "INSERT"),
            ("idempotency_records", "UPDATE"),
        })
        {
            await using PostgresTestDatabase database = await CreateDatabaseAsync();
            DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
            await CreateFailureTriggerAsync(database, failure.Table, failure.TriggerEvent);
            MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
            await using AuthApiHost host =
                await AuthApiHost.StartAsync(database.ConnectionString, clock);
            TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
                host,
                SignupDecisionTestSupport.ManagerEmail);
            DatabaseState before = await SignupDecisionTestSupport.ReadDatabaseStateAsync(database);

            using HttpResponseMessage response = await SignupDecisionTestSupport.DecideAsync(
                host.Client,
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

    private static async Task<ConcurrencyResult> RunCapacityOneRaceAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MembershipId otherPrimary = await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed);
        SignupId otherSignup = await SignupDecisionTestSupport.AddPendingSignupAsync(
            database,
            seed.Seed,
            otherPrimary);
        await SetCapacityAsync(database, 1);
        long expectedVersion = await SignupDecisionTestSupport.ReadSignupVersionAsync(
            database,
            seed.Seed.HelpNeedId);
        SaveInterceptionCoordinator coordinator = new(participants: 2);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartInterceptedHostAsync(
            database,
            clock,
            coordinator);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        Task<HttpResponseMessage>[] requests =
        [
            SignupDecisionTestSupport.DecideAsync(
                host.Client,
                seed.SignupId,
                "approve",
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion),
            SignupDecisionTestSupport.DecideAsync(
                host.Client,
                otherSignup,
                "approve",
                manager.AccessToken,
                IdempotencyKey.New(),
                expectedVersion),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        try
        {
            HttpStatusCode[] statuses = responses.Select(response => response.StatusCode).ToArray();
            HttpResponseMessage stale = responses.Single(
                response => response.StatusCode == HttpStatusCode.PreconditionFailed);
            await SignupDecisionTestSupport.AssertProblemAsync(
                stale,
                HttpStatusCode.PreconditionFailed,
                ErrorCodes.StaleVersion);
            await using TabrukDbContext context = database.CreateContext();
            int approvedCount = await context.Signups.CountAsync(
                signup => signup.Status == (short)SignupStatus.Approved);
            int pendingCount = await context.Signups.CountAsync(
                signup => signup.Status == (short)SignupStatus.Pending);
            int approvedParticipants = await context.Signups
                .Where(signup => signup.Status == (short)SignupStatus.Approved)
                .SumAsync(signup => 1 + signup.UnnamedParticipantCount);
            return new ConcurrencyResult(
                statuses,
                approvedCount,
                pendingCount,
                approvedParticipants,
                await SignupDecisionTestSupport.ReadEffectCountsAsync(database));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }
    }

    private static Task<AuthApiHost> StartInterceptedHostAsync(
        PostgresTestDatabase database,
        MutableClock clock,
        SaveInterceptionCoordinator coordinator) =>
        AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
            {
                services.RemoveAll<ISignupRepository>();
                services.AddScoped<ISignupRepository>(
                    provider =>
                    {
                        PostgresSignupRepository inner = new(
                            provider.GetRequiredService<TabrukDbContext>());
                        return DecisionRepositoryProxy.Create(inner, coordinator);
                    });
            });

    private static async Task SetCapacityAsync(
        PostgresTestDatabase database,
        int capacity)
    {
        await using TabrukDbContext context = database.CreateContext();
        (await context.HelpNeeds.SingleAsync()).Capacity = capacity;
        await context.SaveChangesAsync();
    }

    private static async Task CloseCategoryForRaceAsync(PostgresTestDatabase database)
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

    private static async Task AdvanceSignupVersionAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        need.SignupVersion++;
        await context.SaveChangesAsync();
    }

    private static async Task CreateFailureTriggerAsync(
        PostgresTestDatabase database,
        string table,
        string triggerEvent)
    {
        string functionName = $"fail_t15_{table}_{triggerEvent}".ToLowerInvariant();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            CREATE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected t15 dependency write failure'
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

    public class DecisionRepositoryProxy : DispatchProxy
    {
        private PostgresSignupRepository inner = null!;
        private SaveInterceptionCoordinator coordinator = null!;

        internal static ISignupRepository Create(
            PostgresSignupRepository inner,
            SaveInterceptionCoordinator coordinator)
        {
            ISignupRepository value =
                DispatchProxy.Create<ISignupRepository, DecisionRepositoryProxy>();
            DecisionRepositoryProxy proxy = (DecisionRepositoryProxy)value;
            proxy.inner = inner;
            proxy.coordinator = coordinator;
            return value;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            MethodInfo method =
                targetMethod ?? throw new InvalidOperationException("Missing target method.");
            if (method.Name == "SaveDecisionAsync")
            {
                return new ValueTask<Result>(
                    coordinator.InterceptAsync(inner, method, args!).AsTask());
            }

            return method.Invoke(inner, args);
        }
    }

    internal sealed class SaveInterceptionCoordinator(
        int participants,
        Func<Task>? beforeRelease = null)
    {
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public async ValueTask<Result> InterceptAsync(
            PostgresSignupRepository inner,
            MethodInfo method,
            object?[] args)
        {
            int arrived = Interlocked.Increment(ref arrivals);
            if (arrived == participants)
            {
                try
                {
                    if (beforeRelease is not null)
                    {
                        await beforeRelease();
                    }
                    release.TrySetResult();
                }
                catch (Exception exception)
                {
                    release.TrySetException(exception);
                }
            }

            await release.Task;
            return await (ValueTask<Result>)method.Invoke(inner, args)!;
        }
    }

    private sealed record ConcurrencyResult(
        IReadOnlyList<HttpStatusCode> Statuses,
        int ApprovedCount,
        int PendingCount,
        int ApprovedParticipantCount,
        EffectCounts Effects);
}
