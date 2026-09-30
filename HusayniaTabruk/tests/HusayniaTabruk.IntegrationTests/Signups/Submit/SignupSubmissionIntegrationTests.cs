using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Application.Admin.Members;
using HusayniaTabruk.Application.Roster;
using HusayniaTabruk.Application.Signups.Queries;
using HusayniaTabruk.Application.Signups.Submit;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
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

namespace HusayniaTabruk.IntegrationTests.Signups.Submit;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Signups")]
[Trait("Category", "FirstSlice")]
public sealed class SignupSubmissionIntegrationTests : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task MemberSubmitsPendingSignupAndQueriesRemainPrivacySafe()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");

        using HttpResponseMessage submittedResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "household",
                label = "Food Preparation Group 2",
                memberParticipantIds = new[] { seed.SecondAdministratorMembershipId.ToString() },
                unnamedParticipantCount = 1,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        string submittedJson = await submittedResponse.Content.ReadAsStringAsync();
        SignupContract submitted = Deserialize<SignupContract>(submittedJson);

        Assert.Equal(HttpStatusCode.Created, submittedResponse.StatusCode);
        Assert.Equal("pending", submitted.Status, ignoreCase: true);
        Assert.Equal(3, submitted.TotalParticipantCount);
        Assert.Equal("Member", submitted.PrimaryContact.DisplayName);
        Assert.Equal(
            "Second administrator",
            Assert.Single(submitted.MemberParticipants).DisplayName);
        Assert.Equal("Food Preparation Group 2", submitted.Label);
        AssertPrivacySafe(submittedJson);

        using HttpResponseMessage mineResponse = await GetAsync(
            host.Client,
            "/api/v1/signups/mine",
            member.AccessToken);
        string mineJson = await mineResponse.Content.ReadAsStringAsync();
        SignupPageContract mine = Deserialize<SignupPageContract>(mineJson);
        Assert.Equal(HttpStatusCode.OK, mineResponse.StatusCode);
        SignupContract mineSignup = Assert.Single(mine.Items);
        Assert.Equal(submitted.Id, mineSignup.Id);
        Assert.Equal("Food Preparation Group 2", mineSignup.Label);
        AssertPrivacySafe(mineJson);

        using HttpResponseMessage rosterResponse = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        string rosterJson = await rosterResponse.Content.ReadAsStringAsync();
        RosterContract roster = Deserialize<RosterContract>(rosterJson);
        Assert.Equal(HttpStatusCode.OK, rosterResponse.StatusCode);
        Assert.Equal(seed.ServiceDateId.ToString(), roster.ServiceDateId);
        SignupContract rosterSignup = Assert.Single(roster.Items);
        Assert.Equal(submitted.Id, rosterSignup.Id);
        Assert.Equal("Food Preparation Group 2", rosterSignup.Label);
        AssertPrivacySafe(rosterJson);

        using HttpResponseMessage concealed = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            member.AccessToken);
        ProblemContract concealedProblem = await ReadRequiredAsync<ProblemContract>(
            concealed,
            HttpStatusCode.NotFound);
        Assert.Equal("roster_not_found", concealedProblem.Code);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(1, await context.Signups.CountAsync());
        Assert.Equal(1, await context.SignupMemberParticipants.CountAsync());
        Assert.Equal(
            1,
            await context.AuditEvents.CountAsync(
                audit => audit.Action == "signup.submitted"));
        Assert.Equal(
            1,
            await context.Notifications.CountAsync(
                notification => notification.RecipientMembershipId
                    == seed.ManagerMembershipId.Value));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "notification.push_requested"));
        Assert.Equal(1, await context.IdempotencyRecords.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task SubmissionRejectsParticipantNamesBoundsAndOversizedContentWithoutEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        using HttpResponseMessage participantName = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "individual",
                participantName = "Must not be accepted",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "invalid_signup_input",
            (await ReadRequiredAsync<ProblemContract>(
                participantName,
                HttpStatusCode.BadRequest)).Code);

        using HttpResponseMessage tooManyReferences = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "team",
                memberParticipantIds = Enumerable.Range(0, 21)
                    .Select(_ => Guid.CreateVersion7().ToString("D"))
                    .ToArray(),
                unnamedParticipantCount = 0,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "invalid_signup_input",
            (await ReadRequiredAsync<ProblemContract>(
                tooManyReferences,
                HttpStatusCode.BadRequest)).Code);

        using HttpResponseMessage tooManyUnnamed = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "team",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 21,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "invalid_signup_input",
            (await ReadRequiredAsync<ProblemContract>(
                tooManyUnnamed,
                HttpStatusCode.BadRequest)).Code);

        using HttpResponseMessage labelTooLarge = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "individual",
                label = new string('x', 81),
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            ErrorCodes.PayloadTooLarge,
            (await ReadRequiredAsync<ProblemContract>(
                labelTooLarge,
                HttpStatusCode.RequestEntityTooLarge)).Code);

        string[] identifyingLabels =
        [
            "Fatima Ali",
            "fatima@example.test",
            "+1 (555) 010-0200",
            "WhatsApp 5550100200",
        ];
        foreach (string identifyingLabel in identifyingLabels)
        {
            using HttpResponseMessage identifyingResponse = await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/needs/{seed.HelpNeedId}/signups",
                new
                {
                    kind = "team",
                    label = identifyingLabel,
                    memberParticipantIds = Array.Empty<string>(),
                    unnamedParticipantCount = 1,
                },
                member.AccessToken,
                Guid.CreateVersion7().ToString("D"));
            string body = await identifyingResponse.Content.ReadAsStringAsync();
            ProblemContract problem = Deserialize<ProblemContract>(body);
            using JsonDocument problemDocument = JsonDocument.Parse(body);
            JsonElement problemRoot = problemDocument.RootElement;

            Assert.Equal(HttpStatusCode.BadRequest, identifyingResponse.StatusCode);
            Assert.Equal(
                "application/problem+json",
                identifyingResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(SignupApplicationErrorCodes.InvalidSignupRequest, problem.Code);
            Assert.Equal((int)HttpStatusCode.BadRequest, problem.Status);
            Assert.Equal(
                "https://httpstatuses.com/400",
                problemRoot.GetProperty("type").GetString());
            Assert.Equal("Bad request", problemRoot.GetProperty("title").GetString());
            Assert.Matches(
                "^[0-9a-f]{32}$",
                problemRoot.GetProperty("traceId").GetString());
            Assert.DoesNotContain(
                identifyingLabel,
                body,
                StringComparison.Ordinal);
        }

        using HttpRequestMessage oversized = new(
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups");
        oversized.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", member.AccessToken);
        oversized.Headers.TryAddWithoutValidation(
            ApiDefaults.IdempotencyHeaderName,
            Guid.CreateVersion7().ToString("D"));
        oversized.Content = new ByteArrayContent(
            new byte[ApplicationLimits.MaximumSignupRequestBytes + 1]);
        oversized.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage oversizedResponse = await host.Client.SendAsync(oversized);
        Assert.Equal(
            ErrorCodes.PayloadTooLarge,
            (await ReadRequiredAsync<ProblemContract>(
                oversizedResponse,
                HttpStatusCode.RequestEntityTooLarge)).Code);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.Signups.ToListAsync());
        Assert.Empty(await context.AuditEvents.ToListAsync());
        Assert.Empty(await context.Notifications.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
        Assert.Empty(await context.IdempotencyRecords.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task DeserializedLabelBypassesAreRejectedAndNeverReachMineOrRoster()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");

        string[] rawLabels =
        [
            "",
            " ",
            "   ",
            " Food",
            "Food ",
            "food",
            "Fatima\\u0040example.test",
            "Food\\u00a0Team",
            "Food-Team",
            "Food  2",
            "Food 010",
            "Food \\uff11",
            new string(' ', ApplicationLimits.MaximumSignupLabelUnicodeScalars + 1) + "Food",
        ];
        for (int index = 0; index < rawLabels.Length; index++)
        {
            string rawLabel = rawLabels[index];
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"/api/v1/needs/{seed.HelpNeedId}/signups");
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    index < 8 ? member.AccessToken : manager.AccessToken);
            request.Headers.TryAddWithoutValidation(
                ApiDefaults.IdempotencyHeaderName,
                Guid.CreateVersion7().ToString("D"));
            request.Content = new StringContent(
                $$"""
                {"kind":"team","label":"{{rawLabel}}","memberParticipantIds":[],"unnamedParticipantCount":1}
                """,
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await host.Client.SendAsync(request);
            bool isOversized =
                rawLabel.EnumerateRunes().Count()
                > ApplicationLimits.MaximumSignupLabelUnicodeScalars;
            ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
                response,
                isOversized
                    ? HttpStatusCode.RequestEntityTooLarge
                    : HttpStatusCode.BadRequest);

            Assert.Equal(
                isOversized
                    ? ErrorCodes.PayloadTooLarge
                    : SignupApplicationErrorCodes.InvalidSignupRequest,
                problem.Code);
        }

        using HttpResponseMessage mineResponse = await GetAsync(
            host.Client,
            "/api/v1/signups/mine",
            member.AccessToken);
        SignupPageContract mine = await ReadRequiredAsync<SignupPageContract>(
            mineResponse,
            HttpStatusCode.OK);
        Assert.Empty(mine.Items);

        using HttpResponseMessage rosterResponse = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        RosterContract roster = await ReadRequiredAsync<RosterContract>(
            rosterResponse,
            HttpStatusCode.OK);
        Assert.Empty(roster.Items);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.Signups.ToListAsync());
        Assert.Empty(await context.AuditEvents.ToListAsync());
        Assert.Empty(await context.Notifications.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
        Assert.Empty(await context.IdempotencyRecords.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task SubmissionAcceptsExactCompositionAndLabelBoundsAndRejectsLowerAndTotalBounds()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        AddedMembers participants = await AddActiveMembersAsync(
            database,
            seed.OrganizationId,
            count: 5,
            "boundary",
            includePasswords: false);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        object[] invalidBodies =
        [
            new
            {
                kind = "team",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = -1,
            },
            new
            {
                kind = "individual",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 1,
            },
            new
            {
                kind = "household",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            },
            new
            {
                kind = "team",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            },
            new
            {
                kind = "team",
                memberParticipantIds = participants.MembershipIds
                    .Select(id => id.ToString())
                    .ToArray(),
                unnamedParticipantCount = ApplicationLimits.MaximumUnnamedParticipants,
            },
        ];

        foreach (object invalidBody in invalidBodies)
        {
            using HttpResponseMessage response = await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/needs/{seed.HelpNeedId}/signups",
                invalidBody,
                member.AccessToken,
                Guid.CreateVersion7().ToString("D"));
            ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
                response,
                HttpStatusCode.BadRequest);
            Assert.Equal(SignupErrorCodes.InvalidSignupInput, problem.Code);
        }

        string maximumLabel = string.Join(
            ' ',
            Enumerable.Repeat("Food", 15).Append("Group"));
        Assert.Equal(
            ApplicationLimits.MaximumSignupLabelUnicodeScalars,
            maximumLabel.EnumerateRunes().Count());
        Assert.True(
            Encoding.UTF8.GetByteCount(maximumLabel)
                <= ApplicationLimits.MaximumSignupLabelUtf8Bytes);
        using HttpResponseMessage acceptedResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "team",
                label = maximumLabel,
                memberParticipantIds = participants.MembershipIds
                    .Take(4)
                    .Select(id => id.ToString())
                    .ToArray(),
                unnamedParticipantCount = ApplicationLimits.MaximumUnnamedParticipants,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        SignupContract accepted = await ReadRequiredAsync<SignupContract>(
            acceptedResponse,
            HttpStatusCode.Created);

        Assert.Equal(maximumLabel, accepted.Label);
        Assert.Equal(ApplicationLimits.MaximumTotalParticipants, accepted.TotalParticipantCount);
        Assert.Equal(4, accepted.MemberParticipants.Count);
        Assert.Equal(
            ApplicationLimits.MaximumUnnamedParticipants,
            accepted.UnnamedParticipantCount);
        Assert.Equal("pending", accepted.Status, ignoreCase: true);

        await using TabrukDbContext context = database.CreateContext();
        SignupEntity persisted = Assert.Single(await context.Signups.ToListAsync());
        Assert.Equal(maximumLabel, persisted.Label);
        Assert.Equal(
            4,
            await context.SignupMemberParticipants.CountAsync(
                participant => participant.SignupId == persisted.Id));
        Assert.Equal(1, await context.IdempotencyRecords.CountAsync());
        Assert.Equal(1, await context.AuditEvents.CountAsync());
        Assert.Equal(1, await context.Notifications.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task SameKeyConcurrentSubmissionHasOneEffectAndRetryShowsFinalState()
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

        Task<HttpResponseMessage> firstRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        Task<HttpResponseMessage> secondRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            key);
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using HttpResponseMessage first = responses[0];
        using HttpResponseMessage second = responses[1];

        Assert.All(
            responses,
            response => Assert.True(
                response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict,
                $"Unexpected concurrent submission status {(int)response.StatusCode}."));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Created);

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
        Assert.Equal("pending", final.Status, ignoreCase: true);

        await using TabrukDbContext context = database.CreateContext();
        SignupEntity persisted = Assert.Single(await context.Signups.ToListAsync());
        Assert.Equal(persisted.Id.ToString(), final.Id);
        Assert.Equal(1, await context.IdempotencyRecords.CountAsync());
        Assert.Equal(1, await context.AuditEvents.CountAsync());
        Assert.Equal(1, await context.Notifications.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task DifferentKeyConcurrentDuplicateCreatesOneSignupAndMineShowsFinalState()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        Task<HttpResponseMessage> firstRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Task<HttpResponseMessage> secondRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using HttpResponseMessage first = responses[0];
        using HttpResponseMessage second = responses[1];

        HttpResponseMessage created = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.Created);
        HttpResponseMessage rejected = Assert.Single(
            responses,
            response => response.StatusCode != HttpStatusCode.Created);
        ProblemContract rejectedProblem = Deserialize<ProblemContract>(
            await rejected.Content.ReadAsStringAsync());
        Assert.True(
            (rejected.StatusCode == HttpStatusCode.Conflict
                && rejectedProblem.Code == ErrorCodes.SignupDuplicate)
            || (rejected.StatusCode == HttpStatusCode.PreconditionFailed
                && rejectedProblem.Code == ErrorCodes.StaleVersion),
            $"Unexpected duplicate response {(int)rejected.StatusCode} {rejectedProblem.Code}.");

        SignupContract createdSignup = Deserialize<SignupContract>(
            await created.Content.ReadAsStringAsync());
        using HttpResponseMessage mineResponse = await GetAsync(
            host.Client,
            "/api/v1/signups/mine",
            member.AccessToken);
        SignupPageContract mine = await ReadRequiredAsync<SignupPageContract>(
            mineResponse,
            HttpStatusCode.OK);
        SignupContract final = Assert.Single(mine.Items);
        Assert.Equal(createdSignup.Id, final.Id);
        Assert.Equal("pending", final.Status, ignoreCase: true);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(1, await context.Signups.CountAsync());
        Assert.Equal(1, await context.IdempotencyRecords.CountAsync());
        Assert.Equal(1, await context.AuditEvents.CountAsync());
        Assert.Equal(1, await context.Notifications.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task AccountSubmissionQuotaReturns429WithoutAdditionalEffects()
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

        for (int attempt = 0;
             attempt < ApplicationLimits.SignupSubmissionsPerMinutePerAccount;
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
        Assert.True(limited.Headers.TryGetValues(
            ApiDefaults.RetryAfterHeaderName,
            out IEnumerable<string>? retryAfter));
        Assert.True(
            int.Parse(
                Assert.Single(retryAfter),
                System.Globalization.CultureInfo.InvariantCulture) > 0);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(1, await context.Signups.CountAsync());
        Assert.Equal(1, await context.AuditEvents.CountAsync());
        Assert.Equal(1, await context.Notifications.CountAsync());
        Assert.Equal(1, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task OrganizationSubmissionQuotaReturns429AcrossDistinctAccountsWithoutAdditionalEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        AddedMembers added = await AddActiveMembersAsync(
            database,
            seed.OrganizationId,
            count: 8,
            "organization-quota",
            includePasswords: true);
        string[] emails =
        [
            "manager@example.test",
            "admin@example.test",
            "member@example.test",
            .. added.Emails,
        ];
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        List<TokenSetResponse> accounts = [];
        foreach (string email in emails)
        {
            accounts.Add(await LoginAsync(host, email));
        }

        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };
        for (int accountIndex = 0; accountIndex < 10; accountIndex++)
        {
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
                    accounts[accountIndex].AccessToken,
                    key);
                Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
            }
        }

        using HttpResponseMessage limited = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            accounts[10].AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            limited,
            HttpStatusCode.TooManyRequests);
        Assert.Equal(ErrorCodes.RateLimited, problem.Code);
        Assert.True(limited.Headers.TryGetValues(
            ApiDefaults.RetryAfterHeaderName,
            out IEnumerable<string>? retryAfter));
        Assert.True(
            int.Parse(
                Assert.Single(retryAfter),
                System.Globalization.CultureInfo.InvariantCulture) > 0);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(10, await context.Signups.CountAsync());
        Assert.Equal(10, await context.IdempotencyRecords.CountAsync());
        Assert.Equal(10, await context.AuditEvents.CountAsync());
        Assert.Equal(10, await context.Notifications.CountAsync());
        Assert.Equal(10, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task StaleSignupVersionReturns412AndRollsBackIdempotency()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using TabrukDbContext loadingContext = database.CreateContext();
        Result<HelpNeedSignups> loaded = await new PostgresSignupRepository(
            loadingContext).GetAsync(seed.OrganizationId, seed.HelpNeedId);
        Assert.True(loaded.IsSuccess);
        StaleSignupRepository staleRepository = new(seed, loaded.Value);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
                services.AddSingleton<ISignupRepository>(staleRepository));
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            new
            {
                kind = "individual",
                memberParticipantIds = Array.Empty<string>(),
                unnamedParticipantCount = 0,
            },
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            response,
            HttpStatusCode.PreconditionFailed);

        Assert.Equal(ErrorCodes.StaleVersion, problem.Code);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.IdempotencyRecords.ToListAsync());
        Assert.Empty(await context.Signups.ToListAsync());
        Assert.Empty(await context.AuditEvents.ToListAsync());
        Assert.Empty(await context.Notifications.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task DisabledAndRevokedActorsAreDeniedWithoutProtectedDisclosure()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        object body = new
        {
            kind = "individual",
            memberParticipantIds = Array.Empty<string>(),
            unnamedParticipantCount = 0,
        };

        using HttpResponseMessage anonymous = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            idempotencyKey: Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "unauthorized",
            (await ReadRequiredAsync<ProblemContract>(
                anonymous,
                HttpStatusCode.Unauthorized)).Code);

        clock.Set(member.AccessTokenExpiresAt);
        using HttpResponseMessage expired = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "unauthorized",
            (await ReadRequiredAsync<ProblemContract>(
                expired,
                HttpStatusCode.Unauthorized)).Code);
        clock.Set(seed.Now);

        await using (TabrukDbContext update = database.CreateContext())
        {
            MembershipEntity membership = await update.Memberships.SingleAsync(
                candidate => candidate.Id == seed.MemberMembershipId.Value);
            membership.Status = (short)MembershipStatus.Disabled;
            RoleAssignmentEntity managerRole = await update.RoleAssignments.SingleAsync(
                candidate => candidate.MembershipId == seed.ManagerMembershipId.Value
                    && candidate.Role == (short)OrganizationRole.FoodIncharge
                    && candidate.RevokedAt == null);
            managerRole.RevokedAt = seed.Now;
            managerRole.RevokedByMembershipId =
                seed.SecondAdministratorMembershipId.Value;
            await update.SaveChangesAsync();
        }

        using HttpResponseMessage disabled = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/needs/{seed.HelpNeedId}/signups",
            body,
            member.AccessToken,
            Guid.CreateVersion7().ToString("D"));
        Assert.Equal(
            "unauthorized",
            (await ReadRequiredAsync<ProblemContract>(
                disabled,
                HttpStatusCode.Unauthorized)).Code);

        using HttpResponseMessage revoked = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        Assert.Equal(
            "roster_not_found",
            (await ReadRequiredAsync<ProblemContract>(
                revoked,
                HttpStatusCode.NotFound)).Code);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(await context.Signups.ToListAsync());
        Assert.Empty(await context.IdempotencyRecords.ToListAsync());
        Assert.Empty(await context.AuditEvents.ToListAsync());
        Assert.Empty(await context.Notifications.ToListAsync());
        Assert.Empty(await context.OutboxMessages.ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task RosterRevalidatesRoleAfterRequestAuthorityWasResolved()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        TokenSetResponse manager;
        await using (AuthApiHost loginHost =
                     await AuthApiHost.StartAsync(database.ConnectionString, clock))
        {
            manager = await LoginAsync(loginHost, "manager@example.test");
        }

        RevokingMembershipRepository membershipRepository = new(
            database.ConnectionString,
            seed);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
                services.AddSingleton<IMembershipRepository>(membershipRepository));

        using HttpResponseMessage response = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}/roster",
            manager.AccessToken);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            response,
            HttpStatusCode.NotFound);

        Assert.Equal("roster_not_found", problem.Code);
        Assert.Equal(2, membershipRepository.ResolveCount);
        await using TabrukDbContext context = database.CreateContext();
        RoleAssignmentEntity role = await context.RoleAssignments.SingleAsync(
            candidate => candidate.MembershipId == seed.ManagerMembershipId.Value
                && candidate.Role == (short)OrganizationRole.FoodIncharge);
        Assert.NotNull(role.RevokedAt);
    }

    [RequiresPostgresFact]
    public async Task SubmissionSaveRejectsPartialSetStaleWriterAndCloseWithAtomicRollback()
    {
        await AssertPartialSetRejectedAsync();
        await AssertStaleWriterRejectedAsync();
        await AssertCloseDuringSubmitRejectedAsync();
    }

    private static async Task AssertPartialSetRejectedAsync()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        SignupId persistedSignupId = SignupId.New();
        await using (TabrukDbContext setup = database.CreateContext())
        {
            setup.Signups.Add(
                new SignupEntity
                {
                    Id = persistedSignupId.Value,
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.MemberMembershipId.Value,
                    Kind = (short)SignupKind.Individual,
                    UnnamedParticipantCount = 0,
                    Status = (short)SignupStatus.Pending,
                    SubmittedAt = seed.Now,
                    Version = 0,
                });
            await setup.SaveChangesAsync();
        }

        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<HelpNeedSignups> loaded = await repository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(loaded.IsSuccess, loaded.IsFailure ? loaded.Error.Message : null);
        FieldInfo signupsField = typeof(HelpNeedSignups).GetField(
            "signups",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        List<Signup> signups = (List<Signup>)signupsField.GetValue(loaded.Value)!;
        signups.Clear();
        SignupId newSignupId = SignupId.New();
        Result<SignupSubmitted> submitted = loaded.Value.Submit(
            newSignupId,
            PersistenceSeed.ActiveMembership(
                seed.ManagerMembershipId,
                seed.OrganizationId,
                "Manager"),
            SignupKind.Individual,
            [],
            0,
            seed.Now);
        Assert.True(submitted.IsSuccess, submitted.IsFailure ? submitted.Error.Message : null);

        Result saved = await repository.SaveSubmissionAsync(
            loaded.Value,
            new SignupSubmissionWrite(
                newSignupId,
                null,
                CreateEffects(seed, newSignupId, seed.ManagerMembershipId)));

        Assert.True(saved.IsFailure);
        Assert.Equal(SignupErrorCodes.InvalidSignupAggregateState, saved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(
            persistedSignupId.Value,
            (await verification.Signups.SingleAsync()).Id);
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.Notifications.ToListAsync());
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
    }

    private static async Task AssertStaleWriterRejectedAsync()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext firstContext = database.CreateContext();
        await using TabrukDbContext secondContext = database.CreateContext();
        PostgresSignupRepository firstRepository = new(firstContext);
        PostgresSignupRepository secondRepository = new(secondContext);
        Result<HelpNeedSignups> first = await firstRepository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Result<HelpNeedSignups> second = await secondRepository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        SignupId firstId = SignupId.New();
        SignupId secondId = SignupId.New();
        Assert.True(first.Value.Submit(
            firstId,
            PersistenceSeed.ActiveMembership(
                seed.MemberMembershipId,
                seed.OrganizationId,
                "Member"),
            SignupKind.Individual,
            [],
            0,
            seed.Now).IsSuccess);
        Assert.True(second.Value.Submit(
            secondId,
            PersistenceSeed.ActiveMembership(
                seed.ManagerMembershipId,
                seed.OrganizationId,
                "Manager"),
            SignupKind.Individual,
            [],
            0,
            seed.Now).IsSuccess);

        Result firstSaved = await firstRepository.SaveSubmissionAsync(
            first.Value,
            new SignupSubmissionWrite(
                firstId,
                null,
                CreateEffects(seed, firstId, seed.MemberMembershipId)));
        Result secondSaved = await secondRepository.SaveSubmissionAsync(
            second.Value,
            new SignupSubmissionWrite(
                secondId,
                null,
                CreateEffects(seed, secondId, seed.ManagerMembershipId)));

        Assert.True(firstSaved.IsSuccess);
        Assert.True(secondSaved.IsFailure);
        Assert.Equal(ErrorCodes.StaleVersion, secondSaved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        Assert.Equal(1, await verification.Signups.CountAsync());
        Assert.Equal(1, await verification.AuditEvents.CountAsync());
        Assert.Equal(1, await verification.Notifications.CountAsync());
        Assert.Equal(1, await verification.OutboxMessages.CountAsync());
    }

    private static async Task AssertCloseDuringSubmitRejectedAsync()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext context = database.CreateContext();
        PostgresSignupRepository repository = new(context);
        Result<HelpNeedSignups> loaded = await repository.GetAsync(
            seed.OrganizationId,
            seed.HelpNeedId);
        Assert.True(loaded.IsSuccess);
        SignupId signupId = SignupId.New();
        Assert.True(loaded.Value.Submit(
            signupId,
            PersistenceSeed.ActiveMembership(
                seed.MemberMembershipId,
                seed.OrganizationId,
                "Member"),
            SignupKind.Individual,
            [],
            0,
            seed.Now).IsSuccess);

        await using (TabrukDbContext closing = database.CreateContext())
        {
            ServiceDateEntity date = await closing.ServiceDates.SingleAsync();
            HelpNeedEntity need = await closing.HelpNeeds.SingleAsync();
            date.Status = (short)ServiceDateStatus.Closed;
            need.Status = (short)HelpNeedStatus.Closed;
            await closing.SaveChangesAsync();
        }

        Result saved = await repository.SaveSubmissionAsync(
            loaded.Value,
            new SignupSubmissionWrite(
                signupId,
                null,
                CreateEffects(seed, signupId, seed.MemberMembershipId)));

        Assert.True(saved.IsFailure);
        Assert.Equal(ErrorCodes.CategoryClosed, saved.Error.Code);
        await using TabrukDbContext verification = database.CreateContext();
        Assert.Empty(await verification.Signups.ToListAsync());
        Assert.Empty(await verification.AuditEvents.ToListAsync());
        Assert.Empty(await verification.Notifications.ToListAsync());
        Assert.Empty(await verification.OutboxMessages.ToListAsync());
    }

    private static SignupPersistenceEffects CreateEffects(
        PersistenceSeed seed,
        SignupId signupId,
        MembershipId actorMembershipId)
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
        Assert.True(notification.IsSuccess);
        return new SignupPersistenceEffects(
            [notification.Value.Notification],
            [
                new AuditEntry(
                    AuditEventId.New(),
                    seed.OrganizationId,
                    actorMembershipId,
                    "signup.submitted",
                    "signup",
                    signupId.ToString(),
                    "integration test",
                    "signup_submission",
                    signupId.ToString(),
                    null,
                    """{"status":"pending"}""",
                    seed.Now),
            ],
            [
                new OutboxMessage(
                    OutboxMessageId.New(),
                    seed.OrganizationId,
                    "notification.push_requested",
                    "{}",
                    seed.Now),
            ]);
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

    private static async Task<AddedMembers> AddActiveMembersAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        int count,
        string emailPrefix,
        bool includePasswords)
    {
        await using TabrukDbContext context = database.CreateContext();
        PasswordHasher<TabrukIdentityUser> hasher = new();
        List<string> emails = [];
        List<MembershipId> membershipIds = [];
        for (int index = 0; index < count; index++)
        {
            string email = $"{emailPrefix}-{index}@example.test";
            Guid userId = UserId.New().Value;
            MembershipId membershipId = MembershipId.New();
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
            if (includePasswords)
            {
                user.PasswordHash = hasher.HashPassword(user, SharedPassword);
            }

            context.Users.Add(user);
            context.Memberships.Add(
                new MembershipEntity
                {
                    Id = membershipId.Value,
                    OrganizationId = organizationId.Value,
                    UserId = userId,
                    DisplayName = $"{emailPrefix} member {index}",
                    Status = (short)MembershipStatus.Active,
                    EligibleAsNamedParticipant = true,
                });
            emails.Add(email);
            membershipIds.Add(membershipId);
        }

        await context.SaveChangesAsync();
        return new AddedMembers(emails, membershipIds);
    }

    private static async Task<TokenSetResponse> LoginAsync(
        AuthApiHost host,
        string email)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/login",
            new { email, password = SharedPassword },
            installationId: Guid.CreateVersion7());
        return await ReadRequiredAsync<TokenSetResponse>(
            response,
            HttpStatusCode.OK);
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
            ?? throw new InvalidOperationException(
                $"Missing {typeof(T).Name} response body.");
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException(
            $"Missing {typeof(T).Name} JSON payload.");

    private static void AssertPrivacySafe(string json)
    {
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("login", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("participantName", json, StringComparison.OrdinalIgnoreCase);
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

    private sealed record ParticipantContract(
        string MembershipId,
        string DisplayName);

    private sealed record AddedMembers(
        IReadOnlyList<string> Emails,
        IReadOnlyList<MembershipId> MembershipIds);

    private sealed record ProblemContract(
        string Code,
        int Status,
        string Detail);

    private sealed class StaleSignupRepository : ISignupRepository
    {
        private readonly SignupSubmissionContext context;

        public StaleSignupRepository(
            PersistenceSeed seed,
            HelpNeedSignups aggregate)
        {
            context = new SignupSubmissionContext(
                aggregate,
                PersistenceSeed.ActiveMembership(
                    seed.MemberMembershipId,
                    seed.OrganizationId,
                    "Member"),
                [],
                seed.ManagerMembershipId,
                HelpCategory.FoodPreparation);
        }

        public ValueTask<Result<HelpNeedSignups>> GetAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(context.Aggregate));

        public ValueTask<Result<SignupSubmissionContext>> GetSubmissionContextAsync(
            OrganizationId organizationId,
            HelpNeedId helpNeedId,
            MembershipId primaryMembershipId,
            IReadOnlyCollection<MembershipId> memberParticipantIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result.Success(context));

        public ValueTask<Result> SaveSubmissionAsync(
            HelpNeedSignups aggregate,
            SignupSubmissionWrite write,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                Result.Failure(
                    DomainError.PreconditionFailed(
                        ErrorCodes.StaleVersion,
                        "The signup aggregate changed. Refresh and retry.")));

        public ValueTask<Result<SignupSummary>> GetOwnedAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            SignupId signupId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result<SignupPage>> ListMineAsync(
            OrganizationId organizationId,
            MembershipId primaryMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result<RosterPage>> GetManagedRosterAsync(
            OrganizationId organizationId,
            ServiceDateId serviceDateId,
            MembershipId managerMembershipId,
            string? cursor,
            int? pageSize,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }

    private sealed class RevokingMembershipRepository(
        string connectionString,
        PersistenceSeed seed)
        : IMembershipRepository
    {
        private int resolveCount;

        public int ResolveCount => Volatile.Read(ref resolveCount);

        public ValueTask<Result<ActiveMembershipContext>> ResolveActiveActorAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ResolveAsync(userId, membershipId, organizationId, cancellationToken);

        private async ValueTask<Result<ActiveMembershipContext>> ResolveAsync(
            UserId userId,
            MembershipId membershipId,
            OrganizationId organizationId,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref resolveCount);
            if (call == 2)
            {
                DbContextOptions<TabrukDbContext> options =
                    new DbContextOptionsBuilder<TabrukDbContext>()
                        .UseNpgsql(connectionString)
                        .Options;
                await using TabrukDbContext context = new(options);
                RoleAssignmentEntity role = await context.RoleAssignments.SingleAsync(
                    candidate => candidate.MembershipId
                            == seed.ManagerMembershipId.Value
                        && candidate.Role == (short)OrganizationRole.FoodIncharge
                        && candidate.RevokedAt == null,
                    cancellationToken);
                role.RevokedAt = seed.Now;
                role.RevokedByMembershipId =
                    seed.SecondAdministratorMembershipId.Value;
                await context.SaveChangesAsync(cancellationToken);
            }

            return Result.Success(
                new ActiveMembershipContext(
                    userId,
                    membershipId,
                    organizationId,
                    "Manager",
                    true,
                    [OrganizationRole.Admin, OrganizationRole.FoodIncharge],
                    "Test organization",
                    "America/Los_Angeles"));
        }

        public ValueTask<Result<InvitationAcceptanceContext>>
            GetInvitationAcceptanceContextAsync(
                string invitationToken,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result> AcceptInvitationAsync(
            string invitationToken,
            MembershipId membershipId,
            string displayName,
            DateTimeOffset acceptedAt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result<ActiveMembershipContext>> GetActiveMembershipAsync(
            UserId userId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result<OrganizationAccountGovernance>> GetGovernanceAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result> SaveGovernanceAsync(
            OrganizationAccountGovernance aggregate,
            MembershipId actorMembershipId,
            DateTimeOffset occurredAt,
            MembershipAdministrationPersistenceEffects effects,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();

        public ValueTask<Result> IssueInvitationAsync(
            IssueMembershipInvitationPersistenceRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException();
    }
}
