using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
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
using HusayniaTabruk.IntegrationTests.Dates.CloseCancelConcurrency;
using HusayniaTabruk.IntegrationTests.Persistence;
using HusayniaTabruk.IntegrationTests.Signups.Decisions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

internal static class HarnessProgram
{
    private static readonly IReadOnlyDictionary<string, Func<Task<ScenarioResult>>> Scenarios =
        new Dictionary<string, Func<Task<ScenarioResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["edit-ifmatch"] = RunEditIfMatchScenarioAsync,
            ["capacity-reduction"] = RunCapacityReductionScenarioAsync,
            ["close-preserves-thread"] = RunClosePreservesThreadScenarioAsync,
            ["close-no-thread-blocks-signup"] = RunCloseNoThreadBlocksSignupScenarioAsync,
            ["close-then-cancel"] = RunCloseThenCancelScenarioAsync,
            ["cancel-idempotency"] = RunCancelIdempotencyScenarioAsync,
            ["authority-revocation-race"] = RunAuthorityRevocationRaceScenarioAsync,
            ["cancel-rollback"] = RunCancelRollbackScenarioAsync,
        };

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--list", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Available scenarios:");
            foreach (string key in Scenarios.Keys.Order(StringComparer.Ordinal))
            {
                Console.WriteLine($"- {key}");
            }

            return 0;
        }

        IReadOnlyList<KeyValuePair<string, Func<Task<ScenarioResult>>>> selected =
            TrySelectScenario(args, out string? selectionError);
        if (selectionError is not null)
        {
            Console.Error.WriteLine(selectionError);
            return 2;
        }

        int passed = 0;
        List<(string Key, Exception Exception)> failures = [];
        foreach ((string key, Func<Task<ScenarioResult>> execute) in selected)
        {
            try
            {
                ScenarioResult result = await execute();
                WriteScenario(result);
                passed++;
            }
            catch (Exception exception)
            {
                failures.Add((key, exception));
                WriteFailure(key, exception);
            }
        }

        Console.WriteLine($"Scenarios: {selected.Count} run, {passed} passed, {failures.Count} failed");
        Console.WriteLine($"Conclusion: {(failures.Count == 0 ? "PASS" : "FAIL")}");
        return failures.Count == 0 ? 0 : 1;
    }

    private static IReadOnlyList<KeyValuePair<string, Func<Task<ScenarioResult>>>> TrySelectScenario(
        string[] args,
        out string? error)
    {
        error = null;
        if (args.Length == 0)
        {
            return Scenarios.ToArray();
        }

        if (args.Length == 2 && string.Equals(args[0], "--scenario", StringComparison.OrdinalIgnoreCase))
        {
            if (!Scenarios.TryGetValue(args[1], out Func<Task<ScenarioResult>>? scenario))
            {
                error = $"Unknown scenario '{args[1]}'. Use --list to see supported names.";
                return [];
            }

            return
            [
                new KeyValuePair<string, Func<Task<ScenarioResult>>>(args[1], scenario),
            ];
        }

        error = "Usage: dotnet run --project .\\T17QaHarness.csproj -- [--list | --scenario <name>]";
        return [];
    }

    private static void WriteScenario(ScenarioResult result)
    {
        Console.WriteLine($"Scenario: {result.Name}");
        Console.WriteLine("Status: PASS");
        Console.WriteLine($"Steps: {result.Steps}");
        Console.WriteLine($"Expected: {result.Expected}");
        Console.WriteLine($"Actual: {result.Actual}");
        Console.WriteLine($"Evidence: {result.Evidence}");
        Console.WriteLine();
    }

    private static void WriteFailure(string key, Exception exception)
    {
        Console.WriteLine($"Scenario: {key}");
        Console.WriteLine("Status: FAIL");
        Console.WriteLine($"Actual: {exception.GetType().Name}: {exception.Message}");
        Console.WriteLine("Evidence:");
        Console.WriteLine(exception);
        Console.WriteLine();
    }

    private static async Task<ScenarioResult> RunEditIfMatchScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage editedResponse = await SendDatePatchAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 2,
            title: "Edited title",
            instructions: "Prepare food.",
            startsAt: seed.Seed.Now.AddDays(1),
            endsAt: seed.Seed.Now.AddDays(1).AddHours(4),
            cancellationDeadlineAt: seed.Seed.Now.AddHours(22));
        Assert.Equal(HttpStatusCode.OK, editedResponse.StatusCode);
        Assert.Equal("\"3\"", editedResponse.Headers.ETag?.Tag);
        DateResponseContract edited =
            await editedResponse.Content.ReadFromJsonAsync<DateResponseContract>()
            ?? throw new InvalidOperationException("Missing edited date response.");

        await using (TabrukDbContext context = database.CreateContext())
        {
            ServiceDateEntity date = await context.ServiceDates.SingleAsync();
            HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
            Assert.Equal("Edited title", date.Title);
            Assert.Equal("Prepare food.", date.Instructions);
            Assert.Equal(seed.Seed.Now.AddHours(22), date.CancellationDeadlineAt);
            Assert.Equal(3, date.Version);
            Assert.Equal("Chop vegetables.", need.Instructions);
            Assert.Equal(10, need.Capacity);
            Assert.Equal(0, need.Version);
        }

        using HttpResponseMessage staleResponse = await SendDatePatchAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            manager.AccessToken,
            expectedVersion: 2,
            title: "Should fail",
            instructions: "Changed",
            startsAt: seed.Seed.Now.AddDays(1),
            endsAt: seed.Seed.Now.AddDays(1).AddHours(5),
            cancellationDeadlineAt: seed.Seed.Now.AddHours(21));
        ProblemContract staleProblem = await SignupDecisionTestSupport.AssertProblemAsync(
            staleResponse,
            HttpStatusCode.PreconditionFailed,
            ErrorCodes.StaleVersion);
        Assert.Equal("application/problem+json", staleResponse.Content.Headers.ContentType?.MediaType);

        using HttpRequestMessage malformedRequest = new(
            HttpMethod.Patch,
            $"{ApiDefaults.BasePath}/dates/{seed.Seed.ServiceDateId}")
        {
            Content = JsonContent.Create(
                new
                {
                    title = "Malformed",
                    instructions = "Malformed",
                    startsAt = seed.Seed.Now.AddDays(1),
                    endsAt = seed.Seed.Now.AddDays(1).AddHours(4),
                    cancellationDeadlineAt = seed.Seed.Now.AddHours(22),
                }),
        };
        malformedRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", manager.AccessToken);
        malformedRequest.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, "W/\"3\"");

        using HttpResponseMessage malformedResponse = await host.Client.SendAsync(malformedRequest);
        ProblemContract malformedProblem = await SignupDecisionTestSupport.AssertProblemAsync(
            malformedResponse,
            HttpStatusCode.BadRequest,
            DateApplicationErrorCodes.InvalidDateRequest);
        Assert.Equal("application/problem+json", malformedResponse.Content.Headers.ContentType?.MediaType);

        await using (TabrukDbContext context = database.CreateContext())
        {
            ServiceDateEntity date = await context.ServiceDates.SingleAsync();
            Assert.Equal("Edited title", date.Title);
            Assert.Equal(3, date.Version);
        }

        return new ScenarioResult(
            "Authorized edit honors ETag and rejects stale/malformed If-Match",
            "login manager -> PATCH /api/v1/dates/{dateId} with If-Match \"2\" -> repeat with stale \"2\" -> repeat with malformed W/\"3\"",
            "First edit returns 200 with ETag \"3\" and persists the change; stale replay returns 412 stale_version; malformed If-Match returns 400 invalid_date_request; no later write changes the stored row.",
            $"edit={(int)HttpStatusCode.OK} etag={editedResponse.Headers.ETag?.Tag} version={edited.Version} stale={(int)staleResponse.StatusCode}:{staleProblem.Code} malformed={(int)malformedResponse.StatusCode}:{malformedProblem.Code}",
            $"dateTitle={edited.Title} dbVersion=3 helpNeedInstructionsUnchanged=true");
    }

    private static async Task<ScenarioResult> RunCapacityReductionScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        MembershipId secondPrimary = await SignupDecisionTestSupport.AddMemberAsync(database, seed.Seed);
        await AddApprovedSignupAsync(database, seed.Seed, secondPrimary);
        ManagementSnapshot before = await CaptureSnapshotAsync(database);
        EffectCounts beforeEffects = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage response = await SendNeedPatchAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            manager.AccessToken,
            expectedVersion: 0,
            instructions: "Chop vegetables.",
            capacity: 1,
            status: "open");
        ProblemContract problem = await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ErrorCodes.CapacityUnavailable);

        ManagementSnapshot after = await CaptureSnapshotAsync(database);
        AssertSnapshotEqual(before, after);
        await using TabrukDbContext context = database.CreateContext();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        Assert.Equal(
            2,
            await context.Signups.CountAsync(signup => signup.Status == (short)SignupStatus.Approved));
        Assert.Equal(beforeEffects, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));

        return new ScenarioResult(
            "Need capacity reduction rejects over-allocation without writes",
            "approve existing signup -> seed second approved signup -> PATCH /api/v1/needs/{needId} If-Match \"0\" with capacity 1",
            "API returns 409 capacity_unavailable and leaves the help need, signup versions, and effect tables unchanged.",
            $"patch={(int)response.StatusCode}:{problem.Code}",
            $"needCapacity={need.Capacity} needVersion={need.Version} signupVersion={need.SignupVersion} approvedSignups=2 fullStateUnchanged=true");
    }

    private static async Task<ScenarioResult> RunClosePreservesThreadScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);

        using HttpResponseMessage closeResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        Assert.Equal("\"3\"", closeResponse.Headers.ETag?.Tag);
        DateResponseContract closed =
            await closeResponse.Content.ReadFromJsonAsync<DateResponseContract>()
            ?? throw new InvalidOperationException("Missing close response.");

        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        SignupEntity signup = await context.Signups.SingleAsync();
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        Assert.Equal((short)ServiceDateStatus.Closed, date.Status);
        Assert.Equal((short)HelpNeedStatus.Closed, need.Status);
        Assert.Equal((short)SignupStatus.Approved, signup.Status);
        Assert.Equal((short)ThreadStatus.Locked, thread.Status);

        return new ScenarioResult(
            "Close preserves approved signup history and locks the existing thread",
            "approve pending signup -> POST /api/v1/dates/{dateId}/close with If-Match \"2\" and Idempotency-Key",
            "Close returns 200 with ETag \"3\"; the date and help need become closed, the approved signup remains approved, and the existing thread is locked rather than deleted.",
            $"close={(int)closeResponse.StatusCode} etag={closeResponse.Headers.ETag?.Tag} status={closed.Status} version={closed.Version}",
            $"dateStatus=closed needStatus=closed signupStatus=approved threadStatus=locked threadVersion={thread.Version}");
    }

    private static async Task<ScenarioResult> RunCloseNoThreadBlocksSignupScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        TestActor newPrimary = await SignupDecisionTestSupport.AddAuthenticatedMemberAsync(database, seed.Seed);
        await DeleteThreadAsync(database, seed.Seed.ThreadId);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);
        TokenSetResponse member = await SignupDecisionTestSupport.LoginAsync(host, newPrimary.Email);

        await ApproveAsync(database, host, seed, manager);
        using HttpResponseMessage closeResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        EffectCounts beforeSubmit = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage submitResponse = await SendSignupAsync(
            host.Client,
            seed.Seed.HelpNeedId,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract submitProblem = await SignupDecisionTestSupport.AssertProblemAsync(
            submitResponse,
            HttpStatusCode.Conflict,
            ErrorCodes.CategoryClosed);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.DateThreads.ToListAsync());
        Assert.Equal(1, await context.Signups.CountAsync());
        Assert.Equal(beforeSubmit, await SignupDecisionTestSupport.ReadEffectCountsAsync(database));

        return new ScenarioResult(
            "Close without an existing thread does not backfill and blocks new ordinary signup",
            "delete date thread -> approve pending signup -> POST /api/v1/dates/{dateId}/close -> login a different member -> POST /api/v1/needs/{needId}/signups",
            "Close succeeds without creating a thread row; a new member is denied with 409 category_closed and the failed submit adds no signup or side effects.",
            $"close={(int)closeResponse.StatusCode} threadRows=0 signupSubmit={(int)submitResponse.StatusCode}:{submitProblem.Code}",
            "signupsPersisted=1 effectsUnchangedAfterFailedSubmit=true");
    }

    private static async Task<ScenarioResult> RunCloseThenCancelScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
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

        using HttpResponseMessage closeResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);
        EffectCounts beforeCancel = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);

        using HttpResponseMessage cancelResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 3,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        EffectCounts afterCancel = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        DateThreadEntity thread = await context.DateThreads.SingleAsync();
        Dictionary<Guid, SignupEntity> signups = await context.Signups
            .ToDictionaryAsync(signup => signup.Id);

        Assert.Equal((short)ServiceDateStatus.Cancelled, date.Status);
        Assert.Equal((short)HelpNeedStatus.Closed, need.Status);
        Assert.Equal(8, need.SignupVersion);
        Assert.Equal((short)ThreadStatus.Locked, thread.Status);
        Assert.Equal((short)SignupStatus.Cancelled, signups[seed.SignupId.Value].Status);
        Assert.Equal((short)SignupStatus.Cancelled, signups[pendingSignupId.Value].Status);
        Assert.Equal((short)SignupStatus.Cancelled, signups[waitlistedSignupId.Value].Status);
        Assert.Null(signups[waitlistedSignupId.Value].WaitlistOrder);
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

        return new ScenarioResult(
            "Close then cancel preserves history until cancellation and emits distinct-contact effects",
            "approve one signup -> add pending and waitlisted signups with different primary contacts -> close date -> cancel date",
            "Cancel after close returns 200, keeps the help need closed, converts approved/pending/waitlisted signups to cancelled, emits one cancellation notification per distinct affected contact, and writes one thread.access_changed outbox record.",
            $"close={(int)closeResponse.StatusCode} cancel={(int)cancelResponse.StatusCode} cancelledSignups=3",
            $"dateStatus=cancelled needStatus=closed signupVersion={need.SignupVersion} notificationsDelta=3 threadAccessChanged=1 waitlistCleared=true");
    }

    private static async Task<ScenarioResult> RunCancelIdempotencyScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        IdempotencyKey key = IdempotencyKey.New();

        using HttpResponseMessage firstResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            key,
            expectedVersion: 2,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        DateResponseContract first =
            await firstResponse.Content.ReadFromJsonAsync<DateResponseContract>()
            ?? throw new InvalidOperationException("Missing first cancel response.");
        ManagementSnapshot afterFirst = await CaptureSnapshotAsync(database);

        using HttpResponseMessage retryResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            key,
            expectedVersion: 2,
            reason: "Weather");
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
        DateResponseContract retry =
            await retryResponse.Content.ReadFromJsonAsync<DateResponseContract>()
            ?? throw new InvalidOperationException("Missing retry cancel response.");
        ManagementSnapshot afterRetry = await CaptureSnapshotAsync(database);
        AssertSnapshotEqual(afterFirst, afterRetry);
        Assert.Equal(first.Status, retry.Status);
        Assert.Equal(first.Version, retry.Version);

        using HttpResponseMessage mismatchedIfMatchResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            key,
            expectedVersion: 3,
            reason: "Weather");
        ProblemContract mismatch = await SignupDecisionTestSupport.AssertProblemAsync(
            mismatchedIfMatchResponse,
            HttpStatusCode.Conflict,
            DateApplicationErrorCodes.IdempotencyMismatch);
        AssertSnapshotEqual(afterFirst, await CaptureSnapshotAsync(database));

        return new ScenarioResult(
            "Cancel same-key retry is idempotent and changed If-Match with same key is rejected",
            "approve signup -> POST /api/v1/dates/{dateId}/cancel with key K and If-Match \"2\" -> retry same key/version -> retry same key with If-Match \"3\"",
            "The same-key retry returns 200 with the current cancelled projection and no duplicate effects; the same key with a changed If-Match returns 409 idempotency_mismatch and does not replay the mutation.",
            $"first={(int)firstResponse.StatusCode}:{first.Status}/{first.Version} retry={(int)retryResponse.StatusCode}:{retry.Status}/{retry.Version} changedIfMatch={(int)mismatchedIfMatchResponse.StatusCode}:{mismatch.Code}",
            "stateAfterRetryEqualsFirst=true effectsUnchangedAfterMismatch=true");
    }

    private static async Task<ScenarioResult> RunAuthorityRevocationRaceScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        ManagementSnapshot before = await CaptureSnapshotAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartWithDateRepositoryProxyAsync(
            database,
            clock,
            beforeRevalidate: () => SignupDecisionTestSupport.RevokeFoodInchargeAsync(database, seed.Seed));
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        using HttpResponseMessage response = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        ProblemContract problem = await SignupDecisionTestSupport.AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "forbidden");

        ManagementSnapshot after = await CaptureSnapshotAsync(database);
        AssertSnapshotEqual(before, after);

        return new ScenarioResult(
            "Food Incharge authority loss between authorization and write fails closed",
            "login manager -> revoke Food Incharge role during write-time revalidation -> POST /api/v1/dates/{dateId}/close",
            "The request returns 403 forbidden and the date, help need, signup, thread, and effect tables remain unchanged.",
            $"close={(int)response.StatusCode}:{problem.Code}",
            $"dateStatus={(ServiceDateStatus)after.DateStatus} dateVersion={after.DateVersion} effectsUnchanged=true");
    }

    private static async Task<ScenarioResult> RunCancelRollbackScenarioAsync()
    {
        await using PostgresTestDatabase database = await PostgresTestDatabase.CreateAsync(true);
        DecisionSeed seed = await SignupDecisionTestSupport.CreateSeedAsync(database);
        MutableClock clock = new(seed.Seed.Now.AddMinutes(1));
        await using AuthApiHost host = await StartQuietHostAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await SignupDecisionTestSupport.LoginAsync(
            host,
            SignupDecisionTestSupport.ManagerEmail);

        await ApproveAsync(database, host, seed, manager);
        using HttpResponseMessage closeResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "close",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 2,
            reason: "Coordination complete");
        Assert.Equal(HttpStatusCode.OK, closeResponse.StatusCode);

        await CreateFailureTriggerAsync(database, "notifications", "INSERT");
        ManagementSnapshot before = await CaptureSnapshotAsync(database);

        using HttpResponseMessage cancelResponse = await SendDateMutationAsync(
            host.Client,
            seed.Seed.ServiceDateId,
            "cancel",
            manager.AccessToken,
            IdempotencyKey.New(),
            expectedVersion: 3,
            reason: "Weather");
        ProblemContract problem = await SignupDecisionTestSupport.AssertProblemAsync(
            cancelResponse,
            HttpStatusCode.ServiceUnavailable,
            ErrorCodes.DependencyUnavailable);

        ManagementSnapshot after = await CaptureSnapshotAsync(database);
        AssertSnapshotEqual(before, after);
        Assert.Equal((short)ServiceDateStatus.Closed, after.DateStatus);
        Assert.Equal((short)SignupStatus.Approved, after.Signups.Single().Status);
        Assert.Equal((short)ThreadStatus.Locked, after.ThreadStatus);

        return new ScenarioResult(
            "Cancel rolls back atomically when notification persistence fails",
            "approve signup -> close date -> inject PostgreSQL notification INSERT failure trigger -> POST /api/v1/dates/{dateId}/cancel",
            "The API returns 503 dependency_unavailable and leaves the already-closed date, approved signup, locked thread, and effect tables unchanged.",
            $"cancel={(int)cancelResponse.StatusCode}:{problem.Code}",
            $"dateStatus={(ServiceDateStatus)after.DateStatus} signupStatus={(SignupStatus)after.Signups.Single().Status} threadStatus={(ThreadStatus)after.ThreadStatus} effectsUnchanged=true");
    }

    private static void AssertSnapshotEqual(ManagementSnapshot expected, ManagementSnapshot actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected),
            JsonSerializer.Serialize(actual));

    private static async Task<ManagementSnapshot> CaptureSnapshotAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync();
        HelpNeedEntity need = await context.HelpNeeds.SingleAsync();
        DateThreadEntity? thread = await context.DateThreads.SingleOrDefaultAsync();
        EffectCounts effects = await SignupDecisionTestSupport.ReadEffectCountsAsync(database);
        List<SignupSnapshot> signups = await context.Signups
            .OrderBy(signup => signup.Id)
            .Select(signup => new SignupSnapshot(
                signup.Id,
                signup.Status,
                signup.Version,
                signup.WaitlistOrder))
            .ToListAsync();

        return new ManagementSnapshot(
            date.Status,
            date.Version,
            need.Capacity,
            need.Status,
            need.Version,
            need.SignupVersion,
            signups,
            thread?.Status ?? -1,
            thread?.Version ?? -1,
            effects);
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

    private static async Task<HttpResponseMessage> SendSignupAsync(
        HttpClient client,
        HelpNeedId helpNeedId,
        string accessToken,
        string idempotencyKey)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{ApiDefaults.BasePath}/needs/{helpNeedId}/signups")
        {
            Content = JsonContent.Create(
                new
                {
                    kind = "individual",
                    memberParticipantIds = Array.Empty<string>(),
                    unnamedParticipantCount = 0,
                }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation(ApiDefaults.IdempotencyHeaderName, idempotencyKey);
        return await client.SendAsync(request);
    }

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

    private static async Task<AuthApiHost> StartWithDateRepositoryProxyAsync(
        PostgresTestDatabase database,
        MutableClock clock,
        Func<Task>? beforeRevalidate = null,
        Func<Task>? beforeSaveDate = null,
        Func<Task>? beforeSaveNeed = null) =>
        await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configurationOverrides: CreateLoggingOverrides(),
            configureServices: services =>
            {
                services.RemoveAll<IServiceDateRepository>();
                services.AddScoped<IServiceDateRepository>(
                    provider =>
                        DateManagementIntegrationTests.DateRepositoryProxy.Create(
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
        string functionName = $"fail_t17_qa_{table}_{triggerEvent}".ToLowerInvariant();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            $"""
            CREATE FUNCTION {functionName}() RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'injected t17 qa dependency write failure'
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

    private static Task<AuthApiHost> StartQuietHostAsync(
        string connectionString,
        MutableClock clock) =>
        AuthApiHost.StartAsync(
            connectionString,
            clock,
            configurationOverrides: CreateLoggingOverrides());

    private static IReadOnlyList<KeyValuePair<string, string?>> CreateLoggingOverrides() =>
    [
        new("Logging:LogLevel:Default", "Warning"),
        new("Logging:LogLevel:Microsoft", "Warning"),
        new("Logging:LogLevel:Microsoft.AspNetCore", "Warning"),
        new("Logging:LogLevel:Microsoft.EntityFrameworkCore", "Warning"),
        new("Logging:LogLevel:Microsoft.EntityFrameworkCore.Query", "Error"),
    ];

    private sealed record ScenarioResult(
        string Name,
        string Steps,
        string Expected,
        string Actual,
        string Evidence);

    private sealed record DateResponseContract(
        string Id,
        string Title,
        string Instructions,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateTimeOffset CancellationDeadlineAt,
        string ManagerMembershipId,
        string Status,
        long Version,
        IReadOnlyList<HelpNeedResponseContract> HelpNeeds);

    private sealed record HelpNeedResponseContract(
        string Id,
        string Category,
        string Instructions,
        int? Availability,
        string Status,
        long Version);

    private sealed record SignupSnapshot(
        Guid Id,
        short Status,
        long Version,
        long? WaitlistOrder);

    private sealed record ManagementSnapshot(
        short DateStatus,
        long DateVersion,
        int? NeedCapacity,
        short NeedStatus,
        long NeedVersion,
        long NeedSignupVersion,
        IReadOnlyList<SignupSnapshot> Signups,
        short ThreadStatus,
        long ThreadVersion,
        EffectCounts Effects);
}
