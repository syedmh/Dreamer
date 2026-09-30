using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.Infrastructure.Persistence.Repositories;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace HusayniaTabruk.IntegrationTests.Signups.Submit;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Signups")]
[Trait("Category", "T12QA")]
public sealed class T12ApiConsumerQaTests(ITestOutputHelper output) : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task PrimaryCompositionsFlowThroughHttpIntoMineRosterAndPostgres()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        HelpNeedId servingNeed = await AddHelpNeedAsync(
            database,
            seed,
            HelpCategory.Serving,
            "Serve the meal.");
        HelpNeedId cleanupNeed = await AddHelpNeedAsync(
            database,
            seed,
            HelpCategory.Cleanup,
            "Clean the kitchen.");
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");

        SignupContract individual = await SubmitAndReadAsync(
            host.Client,
            seed.HelpNeedId,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"),
            new
            {
                kind = "individual",
                label = (string?)null,
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            });
        SignupContract household = await SubmitAndReadAsync(
            host.Client,
            servingNeed,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"),
            new
            {
                kind = "household",
                label = "Serving Household",
                memberParticipantIds = new[]
                {
                    seed.SecondAdministratorMembershipId.ToString(),
                },
                unnamedParticipantCount = 1,
            });
        SignupContract team = await SubmitAndReadAsync(
            host.Client,
            cleanupNeed,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"),
            new
            {
                kind = "team",
                label = "Cleanup Team",
                memberParticipantIds = new[]
                {
                    seed.ManagerMembershipId.ToString(),
                    seed.SecondAdministratorMembershipId.ToString(),
                },
                unnamedParticipantCount = 2,
            });

        Assert.Equal("pending", individual.Status, ignoreCase: true);
        Assert.Equal(1, individual.TotalParticipantCount);
        Assert.Equal("pending", household.Status, ignoreCase: true);
        Assert.Equal(3, household.TotalParticipantCount);
        Assert.Equal("pending", team.Status, ignoreCase: true);
        Assert.Equal(5, team.TotalParticipantCount);

        using HttpResponseMessage mineResponse = await GetAsync(
            host.Client,
            "/api/v1/signups/mine",
            member.AccessToken);
        string mineJson = await mineResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);
        SignupPageContract mine = Deserialize<SignupPageContract>(mineJson);
        Assert.Equal(3, mine.Items.Count);
        Assert.Equal(
            new[] { individual.Id, household.Id, team.Id }.Order(StringComparer.Ordinal),
            mine.Items.Select(item => item.Id).Order(StringComparer.Ordinal));
        Assert.Null(mine.Items.Single(item => item.Id == individual.Id).Label);
        Assert.Equal(
            "Serving Household",
            mine.Items.Single(item => item.Id == household.Id).Label);
        Assert.Equal(
            "Cleanup Team",
            mine.Items.Single(item => item.Id == team.Id).Label);
        AssertPrivacySafe(mineJson);

        using HttpResponseMessage rosterResponse = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        string rosterJson = await rosterResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, rosterResponse.StatusCode);
        RosterContract roster = Deserialize<RosterContract>(rosterJson);
        Assert.Equal(3, roster.Items.Count);
        Assert.Equal(seed.ServiceDateId.ToString(), roster.ServiceDateId);
        Assert.Null(roster.Items.Single(item => item.Id == individual.Id).Label);
        Assert.Equal(
            "Serving Household",
            roster.Items.Single(item => item.Id == household.Id).Label);
        Assert.Equal(
            "Cleanup Team",
            roster.Items.Single(item => item.Id == team.Id).Label);
        AssertPrivacySafe(rosterJson);

        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(3, 3, 3, 3, 3, 3), counts);
        await using TabrukDbContext context = database.CreateContext();
        Assert.All(
            await context.HelpNeeds.OrderBy(need => need.Category).ToListAsync(),
            need => Assert.Equal(1, need.SignupVersion));

        output.WriteLine(
            $"PRIMARY status=201/201/201 kinds={individual.Kind},{household.Kind},{team.Kind} "
            + $"mine=3 roster=3 db={counts}");
    }

    [RequiresPostgresFact]
    public async Task DenialsConcealAnonymousDisabledRevokedCrossOrgAndUnrelatedAccess()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        string outsiderEmail = "outsider@example.test";
        await AddCrossOrganizationUserAsync(database, outsiderEmail);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        TokenSetResponse outsider = await LoginAsync(host, outsiderEmail);
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        using HttpResponseMessage anonymousSubmit = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            idempotencyKey: Guid.CreateVersion7().ToString("D"));
        ProblemContract anonymousProblem = await ReadRequiredAsync<ProblemContract>(
            anonymousSubmit,
            HttpStatusCode.Unauthorized);
        Assert.Equal("unauthorized", anonymousProblem.Code);

        using HttpResponseMessage anonymousMine = await GetAsync(
            host.Client,
            "/api/v1/signups/mine");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousMine.StatusCode);
        AssertPrivacySafe(await anonymousMine.Content.ReadAsStringAsync());

        using HttpResponseMessage unrelatedRoster = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            member.AccessToken);
        ProblemContract unrelatedProblem = await ReadRequiredAsync<ProblemContract>(
            unrelatedRoster,
            HttpStatusCode.NotFound);
        Assert.Equal("roster_not_found", unrelatedProblem.Code);

        using HttpResponseMessage crossOrgSubmit = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            outsider.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract crossOrgSubmitProblem = await ReadRequiredAsync<ProblemContract>(
            crossOrgSubmit,
            HttpStatusCode.NotFound);
        AssertPrivacySafe(await crossOrgSubmit.Content.ReadAsStringAsync());

        using HttpResponseMessage crossOrgRoster = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            outsider.AccessToken);
        ProblemContract crossOrgRosterProblem = await ReadRequiredAsync<ProblemContract>(
            crossOrgRoster,
            HttpStatusCode.NotFound);
        Assert.Equal("roster_not_found", crossOrgRosterProblem.Code);
        AssertPrivacySafe(await crossOrgRoster.Content.ReadAsStringAsync());

        await using (TabrukDbContext update = database.CreateContext())
        {
            MembershipEntity membership = await update.Memberships.SingleAsync(
                candidate => candidate.Id == seed.MemberMembershipId.Value);
            membership.Status = (short)MembershipStatus.Disabled;
            RoleAssignmentEntity role = await update.RoleAssignments.SingleAsync(
                candidate => candidate.MembershipId == seed.ManagerMembershipId.Value
                    && candidate.Role == (short)OrganizationRole.FoodIncharge
                    && candidate.RevokedAt == null);
            role.RevokedAt = seed.Now;
            role.RevokedByMembershipId = seed.SecondAdministratorMembershipId.Value;
            await update.SaveChangesAsync();
        }

        using HttpResponseMessage disabledSubmit = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract disabledProblem = await ReadRequiredAsync<ProblemContract>(
            disabledSubmit,
            HttpStatusCode.Unauthorized);
        Assert.Equal("unauthorized", disabledProblem.Code);

        using HttpResponseMessage disabledMine = await GetAsync(
            host.Client,
            "/api/v1/signups/mine",
            member.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledMine.StatusCode);
        AssertPrivacySafe(await disabledMine.Content.ReadAsStringAsync());

        using HttpResponseMessage revokedRoster = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        ProblemContract revokedProblem = await ReadRequiredAsync<ProblemContract>(
            revokedRoster,
            HttpStatusCode.NotFound);
        Assert.Equal("roster_not_found", revokedProblem.Code);

        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(DbCounts.Empty, counts);
        output.WriteLine(
            $"DENIALS anonymous=401 disabled=401 revoked=404 unrelated=404 "
            + $"crossOrgSubmit=404:{crossOrgSubmitProblem.Code} crossOrgRoster=404 db={counts}");
    }

    [RequiresPostgresFact]
    public async Task DuplicateConcurrencyAndRetryHaveExactlyOneEffect()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        string key = Guid.CreateVersion7().ToString("D");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        Task<HttpResponseMessage> firstTask = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        Task<HttpResponseMessage> secondTask = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        HttpResponseMessage[] concurrent = await Task.WhenAll(firstTask, secondTask);
        using HttpResponseMessage first = concurrent[0];
        using HttpResponseMessage second = concurrent[1];
        Assert.All(
            concurrent,
            response => Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected status {(int)response.StatusCode}."));
        Assert.Contains(concurrent, response => response.StatusCode == HttpStatusCode.Created);

        using HttpResponseMessage retry = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        SignupContract final = await ReadRequiredAsync<SignupContract>(
            retry,
            HttpStatusCode.Created);

        foreach (HttpResponseMessage response in concurrent.Where(
                     response => response.StatusCode == HttpStatusCode.Created))
        {
            SignupContract concurrentResult = Deserialize<SignupContract>(
                await response.Content.ReadAsStringAsync());
            Assert.Equal(final.Id, concurrentResult.Id);
        }

        using HttpResponseMessage differentKeyDuplicate = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract duplicateProblem = await ReadRequiredAsync<ProblemContract>(
            differentKeyDuplicate,
            HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.SignupDuplicate, duplicateProblem.Code);

        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(1, 0, 1, 1, 1, 1), counts);
        output.WriteLine(
            $"IDEMPOTENCY concurrent={(int)first.StatusCode}/{(int)second.StatusCode} "
            + $"retry=201 duplicateDifferentKey=409:{duplicateProblem.Code} signup={final.Id} db={counts}");
    }

    [RequiresPostgresFact]
    public async Task RealStaleCasReturns412RollsBackAndRetryRecovers()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        SaveRaceCoordinator coordinator = new();
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
            {
                services.AddSingleton(coordinator);
                services.AddScoped<ISignupRepository>(
                    provider => new BlockingSignupRepository(
                        provider.GetRequiredService<TabrukDbContext>(),
                        provider.GetRequiredService<SaveRaceCoordinator>()));
            });
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        string staleKey = Guid.CreateVersion7().ToString("D");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        Task<HttpResponseMessage> staleTask = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            staleKey);
        await coordinator.WaitUntilBlockedAsync();

        using HttpResponseMessage winning = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            manager.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        SignupContract winningSignup = await ReadRequiredAsync<SignupContract>(
            winning,
            HttpStatusCode.Created);
        coordinator.Release();

        using HttpResponseMessage stale = await staleTask;
        ProblemContract staleProblem = await ReadRequiredAsync<ProblemContract>(
            stale,
            HttpStatusCode.PreconditionFailed);
        Assert.Equal(ErrorCodes.StaleVersion, staleProblem.Code);
        Assert.Equal(new DbCounts(1, 0, 1, 1, 1, 1), await ReadCountsAsync(database));

        using HttpResponseMessage retry = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            staleKey);
        SignupContract recovered = await ReadRequiredAsync<SignupContract>(
            retry,
            HttpStatusCode.Created);
        Assert.NotEqual(winningSignup.Id, recovered.Id);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(2, 0, 2, 2, 2, 2), counts);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            2,
            (await context.HelpNeeds.SingleAsync(need => need.Id == seed.HelpNeedId.Value))
                .SignupVersion);
        output.WriteLine(
            $"STALE winner=201 stale=412:{staleProblem.Code} retry=201 "
            + $"winnerId={winningSignup.Id} retryId={recovered.Id} db={counts}");
    }

    [RequiresPostgresFact]
    public async Task CategoryCloseRaceReturns409AndRollsBackAtomically()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        SaveRaceCoordinator coordinator = new();
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
            {
                services.AddSingleton(coordinator);
                services.AddScoped<ISignupRepository>(
                    provider => new BlockingSignupRepository(
                        provider.GetRequiredService<TabrukDbContext>(),
                        provider.GetRequiredService<SaveRaceCoordinator>()));
            });
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        string key = Guid.CreateVersion7().ToString("D");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        Task<HttpResponseMessage> submitTask = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        await coordinator.WaitUntilBlockedAsync();
        await using (TabrukDbContext closing = database.CreateContext())
        {
            ServiceDateEntity date = await closing.ServiceDates.SingleAsync(
                candidate => candidate.Id == seed.ServiceDateId.Value);
            HelpNeedEntity need = await closing.HelpNeeds.SingleAsync(
                candidate => candidate.Id == seed.HelpNeedId.Value);
            date.Status = (short)ServiceDateStatus.Closed;
            date.Version++;
            need.Status = (short)HelpNeedStatus.Closed;
            need.Version++;
            await closing.SaveChangesAsync();
        }
        coordinator.Release();

        using HttpResponseMessage raced = await submitTask;
        ProblemContract racedProblem = await ReadRequiredAsync<ProblemContract>(
            raced,
            HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.CategoryClosed, racedProblem.Code);
        Assert.Equal(DbCounts.Empty, await ReadCountsAsync(database));

        using HttpResponseMessage retry = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        ProblemContract retryProblem = await ReadRequiredAsync<ProblemContract>(
            retry,
            HttpStatusCode.Conflict);
        Assert.Equal(ErrorCodes.CategoryClosed, retryProblem.Code);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(DbCounts.Empty, counts);
        await using TabrukDbContext verification = database.CreateContext();
        HelpNeedEntity persistedNeed = await verification.HelpNeeds.SingleAsync(
            need => need.Id == seed.HelpNeedId.Value);
        Assert.Equal((short)HelpNeedStatus.Closed, persistedNeed.Status);
        Assert.Equal(0, persistedNeed.SignupVersion);

        output.WriteLine(
            $"CLOSE_RACE first=409:{racedProblem.Code} retry=409:{retryProblem.Code} "
            + $"needStatus=closed signupVersion=0 db={counts}");
    }

    [RequiresPostgresFact]
    public async Task OversizedPayloadReturns413WithoutAnyDatabaseEffect()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            member.AccessToken);
        request.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            Guid.CreateVersion7().ToString("D"));
        request.Content = new ByteArrayContent(
            new byte[ApplicationLimits.MaximumSignupRequestBytes + 1]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await host.Client.SendAsync(request);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            response,
            HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(ErrorCodes.PayloadTooLarge, problem.Code);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(DbCounts.Empty, counts);
        output.WriteLine(
            $"PAYLOAD bytes={ApplicationLimits.MaximumSignupRequestBytes + 1} "
            + $"status=413:{problem.Code} db={counts}");
    }

    [RequiresPostgresFact]
    public async Task AccountQuotaReturns429WithRetryAfterAndNoAdditionalEffect()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        string key = Guid.CreateVersion7().ToString("D");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        for (int attempt = 1;
             attempt <= ApplicationLimits.SignupSubmissionsPerMinutePerAccount;
             attempt++)
        {
            using HttpResponseMessage accepted = await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/needs/{seed.HelpNeedId}/signups",
                body,
                member.AccessToken,
                key);
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using HttpResponseMessage limited = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            limited,
            HttpStatusCode.TooManyRequests);
        Assert.Equal(ErrorCodes.RateLimited, problem.Code);
        int retryAfter = ReadRetryAfter(limited);
        Assert.True(retryAfter > 0);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(1, 0, 1, 1, 1, 1), counts);

        output.WriteLine(
            $"ACCOUNT_QUOTA accepted={ApplicationLimits.SignupSubmissionsPerMinutePerAccount} "
            + $"next=429:{problem.Code} retryAfter={retryAfter} db={counts}");
    }

    [RequiresPostgresFact]
    public async Task OrganizationQuotaReturns429OnRequest101WithoutExtraEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        IReadOnlyList<string> emails = await AddOrganizationUsersAsync(
            database,
            seed.OrganizationId,
            count: 11);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        for (int account = 0; account < 10; account++)
        {
            TokenSetResponse token = await LoginAsync(host, emails[account]);
            string key = Guid.CreateVersion7().ToString("D");
            for (int attempt = 0;
                 attempt < ApplicationLimits.SignupSubmissionsPerMinutePerAccount;
                 attempt++)
            {
                using HttpResponseMessage accepted = await SendJsonAsync(
                    host.Client,
                    HttpMethod.Post,
                    $"/api/v1/needs/{seed.HelpNeedId}/signups",
                    body,
                    token.AccessToken,
                    key);
                Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            }
        }

        TokenSetResponse eleventh = await LoginAsync(host, emails[10]);
        using HttpResponseMessage limited = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            eleventh.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            limited,
            HttpStatusCode.TooManyRequests);
        Assert.Equal(ErrorCodes.RateLimited, problem.Code);
        int retryAfter = ReadRetryAfter(limited);
        Assert.True(retryAfter > 0);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(10, 0, 10, 10, 10, 10), counts);

        output.WriteLine(
            $"ORG_QUOTA accepted={ApplicationLimits.SignupSubmissionsPerHourPerOrganization} "
            + $"request101=429:{problem.Code} retryAfter={retryAfter} db={counts}");
    }

    [RequiresPostgresFact]
    public async Task DatabaseOutageReturns503AndSameRequestRecoversExactlyOnce()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        TokenSetResponse member;
        await using (AuthApiHost loginHost =
                     await AuthApiHost.StartAsync(database.ConnectionString, clock))
        {
            member = await LoginAsync(loginHost, "member@example.test");
        }

        string key = Guid.CreateVersion7().ToString("D");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };
        const string unavailableConnection =
            "Host=127.0.0.1;Port=1;Database=tabruk;Username=tabruk;"
            + "Password=tabruk_local_development;Timeout=1;Command Timeout=1;Pooling=false";
        await using (AuthApiHost unavailableHost =
                     await AuthApiHost.StartAsync(unavailableConnection, clock))
        {
            using HttpResponseMessage unavailable = await SendJsonAsync(
                unavailableHost.Client,
                HttpMethod.Post,
                $"/api/v1/needs/{seed.HelpNeedId}/signups",
                body,
                member.AccessToken,
                key);
            ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
                unavailable,
                HttpStatusCode.ServiceUnavailable);
            Assert.Equal(ErrorCodes.DependencyUnavailable, problem.Code);
            Assert.DoesNotContain("127.0.0.1", problem.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", problem.Detail, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(DbCounts.Empty, await ReadCountsAsync(database));

        await using AuthApiHost recoveredHost =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        using HttpResponseMessage recovered = await SendJsonAsync(
            recoveredHost.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        SignupContract signup = await ReadRequiredAsync<SignupContract>(
            recovered,
            HttpStatusCode.Created);
        DbCounts counts = await ReadCountsAsync(database);
        Assert.Equal(new DbCounts(1, 0, 1, 1, 1, 1), counts);

        output.WriteLine(
            $"DB_RECOVERY unavailable=503:{ErrorCodes.DependencyUnavailable} "
            + $"recovered=201 signup={signup.Id} db={counts}");
    }

    private static async Task<SignupContract> SubmitAndReadAsync(
        HttpClient client,
        HelpNeedId needId,
        string accessToken,
        string key,
        object body)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"/api/v1/needs/{needId}/signups",
            body,
            accessToken,
            key);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertPrivacySafe(json);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.NotNull(response.Headers.Location);
        return Deserialize<SignupContract>(json);
    }

    private static async Task<PersistenceSeed> CreateSeedWithPasswordsAsync(
        PostgresTestDatabase database)
    {
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await SeedPasswordAsync(database, "manager@example.test");
        await SeedPasswordAsync(database, "admin@example.test");
        await SeedPasswordAsync(database, "member@example.test");
        return seed;
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

    private static async Task<HelpNeedId> AddHelpNeedAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        HelpCategory category,
        string instructions)
    {
        HelpNeedId id = HelpNeedId.New();
        await using TabrukDbContext context = database.CreateContext();
        ServiceDateEntity date = await context.ServiceDates.SingleAsync(
            candidate => candidate.Id == seed.ServiceDateId.Value);
        context.HelpNeeds.Add(
            new HelpNeedEntity
            {
                Id = id.Value,
                OrganizationId = seed.OrganizationId.Value,
                ServiceDateId = seed.ServiceDateId.Value,
                Category = (short)category,
                Instructions = instructions,
                Capacity = 10,
                Status = (short)HelpNeedStatus.Open,
                Version = 0,
                SignupVersion = 0,
                WaitlistOrderHighWater = 0,
            });
        date.Version++;
        await context.SaveChangesAsync();
        return id;
    }

    private static async Task AddCrossOrganizationUserAsync(
        PostgresTestDatabase database,
        string email)
    {
        OrganizationId organizationId = OrganizationId.New();
        MembershipId membershipId = MembershipId.New();
        Guid userId = UserId.New().Value;
        DateTimeOffset now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        await using TabrukDbContext context = database.CreateContext();
        context.Organizations.Add(
            new OrganizationEntity
            {
                Id = organizationId.Value,
                Name = "Other test organization",
                TimeZone = "America/Los_Angeles",
                DefaultCancellationLeadMinutes = 60,
                Status = 0,
                BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                BootstrapSealedAt = now,
                Version = 0,
            });
        TabrukIdentityUser user = CreateUser(userId, email);
        context.Users.Add(user);
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId.Value,
                OrganizationId = organizationId.Value,
                UserId = userId,
                DisplayName = "Cross organization member",
                Status = (short)MembershipStatus.Active,
                EligibleAsNamedParticipant = true,
            });
        await context.SaveChangesAsync();
    }

    private static async Task<IReadOnlyList<string>> AddOrganizationUsersAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        int count)
    {
        List<string> emails = [];
        await using TabrukDbContext context = database.CreateContext();
        for (int index = 1; index <= count; index++)
        {
            string email = $"quota{index:D2}@example.test";
            Guid userId = UserId.New().Value;
            MembershipId membershipId = MembershipId.New();
            context.Users.Add(CreateUser(userId, email));
            context.Memberships.Add(
                new MembershipEntity
                {
                    Id = membershipId.Value,
                    OrganizationId = organizationId.Value,
                    UserId = userId,
                    DisplayName = $"Quota member {index:D2}",
                    Status = (short)MembershipStatus.Active,
                    EligibleAsNamedParticipant = true,
                });
            emails.Add(email);
        }
        await context.SaveChangesAsync();
        return emails;
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

    private static async Task<TokenSetResponse> LoginAsync(AuthApiHost host, string email)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/login",
            new { email, password = SharedPassword },
            installationId: Guid.CreateVersion7());
        return await ReadRequiredAsync<TokenSetResponse>(response, HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? bearerToken = null) =>
        SendJsonAsync(client, HttpMethod.Get, path, bearerToken: bearerToken);

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null,
        string? bearerToken = null,
        string? idempotencyKey = null,
        Guid? installationId = null)
    {
        HttpRequestMessage request = new(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(
                ApiDefaults.IdempotencyHeaderName,
                idempotencyKey);
        }
        if (installationId.HasValue)
        {
            request.Headers.TryAddWithoutValidation(
                AuthHeaders.InstallationId,
                installationId.Value.ToString("D"));
        }
        return await client.SendAsync(request);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException($"Missing {typeof(T).Name} response body.");
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name} JSON payload.");

    private static void AssertPrivacySafe(string json)
    {
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("login", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("participantName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SharedPassword, json, StringComparison.Ordinal);
    }

    private static int ReadRetryAfter(HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.TryGetValues(
                ApiDefaults.RetryAfterHeaderName,
                out IEnumerable<string>? values));
        return int.Parse(
            Assert.Single(values),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<DbCounts> ReadCountsAsync(PostgresTestDatabase database)
    {
        await using TabrukDbContext context = database.CreateContext();
        return new DbCounts(
            await context.Signups.CountAsync(),
            await context.SignupMemberParticipants.CountAsync(),
            await context.AuditEvents.CountAsync(),
            await context.Notifications.CountAsync(),
            await context.OutboxMessages.CountAsync(),
            await context.IdempotencyRecords.CountAsync());
    }

    private sealed record SignupPageContract(
        IReadOnlyList<SignupContract> Items,
        string? NextCursor);

    private sealed record RosterContract(
        string ServiceDateId,
        IReadOnlyList<SignupContract> Items,
        string? NextCursor);

    private sealed record SignupContract(
        string Id,
        string ServiceDateId,
        string HelpNeedId,
        string Category,
        ParticipantContract PrimaryContact,
        string Kind,
        string? Label,
        IReadOnlyList<ParticipantContract> MemberParticipants,
        int UnnamedParticipantCount,
        int TotalParticipantCount,
        string Status,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? LastTransitionAt,
        long? WaitlistOrder,
        long Version,
        long SignupVersion);

    private sealed record ParticipantContract(string MembershipId, string DisplayName);

    private sealed record ProblemContract(string Code, int Status, string Detail);

    private sealed record DbCounts(
        int Signups,
        int Participants,
        int Audits,
        int Notifications,
        int Outbox,
        int Idempotency)
    {
        public static DbCounts Empty { get; } = new(0, 0, 0, 0, 0, 0);

        public override string ToString() =>
            $"signups={Signups},participants={Participants},audits={Audits},"
            + $"notifications={Notifications},outbox={Outbox},idempotency={Idempotency}";
    }

    private sealed class SaveRaceCoordinator
    {
        private readonly TaskCompletionSource<bool> blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int saveCalls;

        public async Task BlockFirstSaveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref saveCalls) != 1)
            {
                return;
            }
            blocked.TrySetResult(true);
            await released.Task.WaitAsync(cancellationToken);
        }

        public Task<bool> WaitUntilBlockedAsync() =>
            blocked.Task.WaitAsync(TimeSpan.FromSeconds(15));

        public void Release() => released.TrySetResult(true);
    }

    private sealed class BlockingSignupRepository(
        TabrukDbContext context,
        SaveRaceCoordinator coordinator) : ISignupRepository
    {
        private readonly PostgresSignupRepository inner = new(context);

        public ValueTask<Result<HelpNeedSignups>> GetAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(organizationId, helpNeedId, cancellationToken);

        public ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            MembershipId primaryMembershipId,
            IReadOnlyCollection<MembershipId> memberParticipantIds,
            CancellationToken cancellationToken = default) =>
            inner.GetSubmissionContextAsync(
                organizationId,
                helpNeedId,
                primaryMembershipId,
                memberParticipantIds,
                cancellationToken);

        public async ValueTask<Result> SaveSubmissionAsync(
            HelpNeedSignups aggregate,
            SignupSubmissionWrite write,
            CancellationToken cancellationToken = default)
        {
            await coordinator.BlockFirstSaveAsync(cancellationToken);
            return await inner.SaveSubmissionAsync(aggregate, write, cancellationToken);
        }

        public ValueTask<Result<SignupSummary>> GetOwnedAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            SignupId signupId,
            CancellationToken cancellationToken = default) =>
            inner.GetOwnedAsync(
                organizationId,
                primaryMembershipId,
                signupId,
                cancellationToken);

        public ValueTask<Result<SignupPage>> ListMineAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            inner.ListMineAsync(
                organizationId,
                primaryMembershipId,
                cursor,
                pageSize,
                cancellationToken);

        public ValueTask<Result<RosterPage>> GetManagedRosterAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            MembershipId managerMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            inner.GetManagedRosterAsync(
                organizationId,
                serviceDateId,
                managerMembershipId,
                cursor,
                pageSize,
                cancellationToken);
    }
}
