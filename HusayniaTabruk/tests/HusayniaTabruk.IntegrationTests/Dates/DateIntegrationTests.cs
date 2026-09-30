using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.IntegrationTests.Dates;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Dates")]
[Trait("Category", "FirstSlice")]
public sealed class DateIntegrationTests : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task FoodInchargeCanCreateNeedAndOpenDateForMemberSafeQueries()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        DateTimeOffset startsAt = clock.UtcNow.AddDays(7);

        using HttpResponseMessage createResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/dates",
            new
            {
                title = "Muharram service",
                instructions = "Arrive through the east entrance.",
                startsAt,
                endsAt = startsAt.AddHours(5),
                cancellationDeadlineAt = startsAt.AddDays(-1),
                managerMembershipId = seed.ManagerMembershipId.ToString(),
            },
            manager.AccessToken);
        DateContract draft = await ReadRequiredAsync<DateContract>(
            createResponse,
            HttpStatusCode.Created);

        using HttpResponseMessage needResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/needs",
            new
            {
                category = "serving",
                instructions = "Serve the evening meal.",
                capacity = 12,
            },
            manager.AccessToken);
        HelpNeedContract createdNeed = await ReadRequiredAsync<HelpNeedContract>(
            needResponse,
            HttpStatusCode.Created);
        string idempotencyKey = Guid.CreateVersion7().ToString("D");

        using HttpResponseMessage openResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/open",
            bearerToken: manager.AccessToken,
            idempotencyKey: idempotencyKey);
        DateContract opened = await ReadRequiredAsync<DateContract>(
            openResponse,
            HttpStatusCode.OK);

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/dates?scope=open",
            member.AccessToken);
        string listJson = await listResponse.Content.ReadAsStringAsync();
        DatePageContract page = Deserialize<DatePageContract>(listJson);

        using HttpResponseMessage detailResponse = await GetAsync(
            host.Client,
            $"/api/v1/dates/{draft.Id}",
            member.AccessToken);
        string detailJson = await detailResponse.Content.ReadAsStringAsync();
        DateContract detail = Deserialize<DateContract>(detailJson);

        Assert.Equal(ServiceDateStatus.Draft.ToString(), draft.Status, ignoreCase: true);
        Assert.Equal(0, createdNeed.Version);
        Assert.Equal(12, createdNeed.Availability);
        Assert.Equal(ServiceDateStatus.Open.ToString(), opened.Status, ignoreCase: true);
        Assert.Equal(2, opened.Version);
        HelpNeedContract need = Assert.Single(opened.HelpNeeds);
        Assert.Equal("serving", need.Category, ignoreCase: true);
        Assert.Equal(12, need.Availability);
        Assert.Contains(page.Items, item => item.Id == draft.Id);
        Assert.Equal("Arrive through the east entrance.", detail.Instructions);
        Assert.Equal(startsAt.AddDays(-1), detail.CancellationDeadlineAt);
        Assert.DoesNotContain("roster", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signup", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", listJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", detailJson, StringComparison.OrdinalIgnoreCase);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            3,
            await context.AuditEvents.CountAsync(
                audit => audit.ResourceId == draft.Id));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "service_date.opened"
                    && message.Payload.Contains(draft.Id)));
        Assert.Equal(
            1,
            await context.IdempotencyRecords.CountAsync(
                record => record.Key == Guid.Parse(idempotencyKey)));
    }

    [RequiresPostgresFact]
    public async Task NonManagerCannotMutateDatesAndAnonymousCannotReadThem()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        using HttpResponseMessage anonymous = await GetAsync(host.Client, "/api/v1/dates?scope=open");
        ProblemContract anonymousProblem = await ReadRequiredAsync<ProblemContract>(
            anonymous,
            HttpStatusCode.Unauthorized);

        using HttpResponseMessage forbidden = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/dates",
            new
            {
                title = "Forbidden date",
                instructions = "Must not persist.",
                startsAt = clock.UtcNow.AddDays(2),
                endsAt = clock.UtcNow.AddDays(2).AddHours(1),
                cancellationDeadlineAt = clock.UtcNow.AddDays(1),
                managerMembershipId = seed.MemberMembershipId.ToString(),
            },
            member.AccessToken);
        ProblemContract forbiddenProblem = await ReadRequiredAsync<ProblemContract>(
            forbidden,
            HttpStatusCode.Forbidden);

        Assert.Equal("unauthorized", anonymousProblem.Code);
        Assert.Equal("forbidden", forbiddenProblem.Code);

        await using TabrukDbContext context = database.CreateContext();
        Assert.DoesNotContain(
            await context.ServiceDates.AsNoTracking().ToListAsync(),
            date => date.Title == "Forbidden date");
    }

    [RequiresPostgresFact]
    public async Task OpenRetryWithSameIdempotencyKeyDoesNotDuplicateEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        DateTimeOffset startsAt = clock.UtcNow.AddDays(3);

        DateContract draft = await ReadRequiredAsync<DateContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                "/api/v1/dates",
                new
                {
                    title = "Retry date",
                    instructions = "Retry-safe opening.",
                    startsAt,
                    endsAt = startsAt.AddHours(2),
                    cancellationDeadlineAt = startsAt.AddHours(-2),
                    managerMembershipId = seed.ManagerMembershipId.ToString(),
                },
                manager.AccessToken),
            HttpStatusCode.Created);
        _ = await ReadRequiredAsync<HelpNeedContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/dates/{draft.Id}/needs",
                new
                {
                    category = "cleanup",
                    instructions = "Clean the hall.",
                    capacity = 4,
                },
                manager.AccessToken),
            HttpStatusCode.Created);

        string key = Guid.CreateVersion7().ToString("D");
        DateContract first = await ReadRequiredAsync<DateContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/dates/{draft.Id}/open",
                bearerToken: manager.AccessToken,
                idempotencyKey: key),
            HttpStatusCode.OK);
        DateContract retry = await ReadRequiredAsync<DateContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/dates/{draft.Id}/open",
                bearerToken: manager.AccessToken,
                idempotencyKey: key),
            HttpStatusCode.OK);

        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(first.Status, retry.Status);
        Assert.Equal(first.Version, retry.Version);
        Assert.Equal(first.HelpNeeds, retry.HelpNeeds);
        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            1,
            await context.AuditEvents.CountAsync(
                audit => audit.ResourceId == draft.Id
                    && audit.Action == "service_date.opened"));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "service_date.opened"
                    && message.Payload.Contains(draft.Id)));
    }

    [RequiresPostgresFact]
    public async Task MemberCannotDiscoverDraftOrMutateNeedAndOpenSurfaces()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        TokenSetResponse member = await LoginAsync(host, "member@example.test");
        DateTimeOffset startsAt = clock.UtcNow.AddDays(4);

        DateContract draft = await ReadRequiredAsync<DateContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                "/api/v1/dates",
                new
                {
                    title = "Concealed draft",
                    instructions = "Managers only until open.",
                    startsAt,
                    endsAt = startsAt.AddHours(2),
                    cancellationDeadlineAt = startsAt.AddHours(-2),
                    managerMembershipId = seed.ManagerMembershipId.ToString(),
                },
                manager.AccessToken),
            HttpStatusCode.Created);

        using HttpResponseMessage discover = await GetAsync(
            host.Client,
            $"/api/v1/dates/{draft.Id}",
            member.AccessToken);
        _ = await ReadRequiredAsync<ProblemContract>(discover, HttpStatusCode.NotFound);

        using HttpResponseMessage addNeed = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/needs",
            new
            {
                category = "serving",
                instructions = "Must not persist.",
                capacity = 8,
            },
            member.AccessToken);
        _ = await ReadRequiredAsync<ProblemContract>(addNeed, HttpStatusCode.Forbidden);

        using HttpResponseMessage open = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/open",
            bearerToken: member.AccessToken,
            idempotencyKey: Guid.CreateVersion7().ToString("D"));
        _ = await ReadRequiredAsync<ProblemContract>(open, HttpStatusCode.Forbidden);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Empty(
            await context.HelpNeeds
                .AsNoTracking()
                .Where(need => need.ServiceDateId == Guid.Parse(draft.Id))
                .ToListAsync());
        Assert.Equal(
            (short)ServiceDateStatus.Draft,
            await context.ServiceDates
                .Where(date => date.Id == Guid.Parse(draft.Id))
                .Select(date => date.Status)
                .SingleAsync());
    }

    [RequiresPostgresFact]
    public async Task ConcurrentOpenWithSameKeyHasOneDurableEffectAndNoServerError()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse manager = await LoginAsync(host, "manager@example.test");
        DateTimeOffset startsAt = clock.UtcNow.AddDays(5);

        DateContract draft = await ReadRequiredAsync<DateContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                "/api/v1/dates",
                new
                {
                    title = "Concurrent open",
                    instructions = "Same request may race.",
                    startsAt,
                    endsAt = startsAt.AddHours(2),
                    cancellationDeadlineAt = startsAt.AddHours(-2),
                    managerMembershipId = seed.ManagerMembershipId.ToString(),
                },
                manager.AccessToken),
            HttpStatusCode.Created);
        _ = await ReadRequiredAsync<HelpNeedContract>(
            await SendJsonAsync(
                host.Client,
                HttpMethod.Post,
                $"/api/v1/dates/{draft.Id}/needs",
                new
                {
                    category = "cleanup",
                    instructions = "Clean after service.",
                    capacity = 4,
                },
                manager.AccessToken),
            HttpStatusCode.Created);

        string key = Guid.CreateVersion7().ToString("D");
        Task<HttpResponseMessage> firstRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/open",
            bearerToken: manager.AccessToken,
            idempotencyKey: key);
        Task<HttpResponseMessage> secondRequest = SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/dates/{draft.Id}/open",
            bearerToken: manager.AccessToken,
            idempotencyKey: key);
        HttpResponseMessage[] responses = await Task.WhenAll(firstRequest, secondRequest);
        using HttpResponseMessage first = responses[0];
        using HttpResponseMessage second = responses[1];

        Assert.All(
            responses,
            response => Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Unexpected concurrent-open status {(int)response.StatusCode}."));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            1,
            await context.AuditEvents.CountAsync(
                audit => audit.ResourceId == draft.Id
                    && audit.Action == "service_date.opened"));
        Assert.Equal(
            1,
            await context.OutboxMessages.CountAsync(
                message => message.Type == "service_date.opened"
                    && message.Payload.Contains(draft.Id)));
        Assert.Equal(
            1,
            await context.IdempotencyRecords.CountAsync(
                record => record.Key == Guid.Parse(key)));
    }

    [RequiresPostgresFact]
    public async Task OpenDateAvailabilitySubtractsApprovedParticipantsAndClosedNeedsStayConcealed()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host, "member@example.test");

        await using (TabrukDbContext context = database.CreateContext())
        {
            ServiceDateEntity date = await context.ServiceDates.SingleAsync(
                candidate => candidate.Id == seed.ServiceDateId.Value);
            date.Version = 3;
            context.HelpNeeds.Add(
                new HelpNeedEntity
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    Category = (short)HelpCategory.Serving,
                    Instructions = "Closed category instructions.",
                    Capacity = 5,
                    Status = (short)HelpNeedStatus.Closed,
                    Version = 1,
                    SignupVersion = 0,
                    WaitlistOrderHighWater = 0,
                });
            context.Signups.Add(
                new SignupEntity
                {
                    Id = Guid.CreateVersion7(),
                    OrganizationId = seed.OrganizationId.Value,
                    ServiceDateId = seed.ServiceDateId.Value,
                    HelpNeedId = seed.HelpNeedId.Value,
                    PrimaryMembershipId = seed.MemberMembershipId.Value,
                    Kind = (short)SignupKind.Household,
                    UnnamedParticipantCount = 2,
                    Status = (short)SignupStatus.Approved,
                    SubmittedAt = seed.Now.AddMinutes(-10),
                    LastTransitionAt = seed.Now.AddMinutes(-5),
                    Version = 1,
                });
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage response = await GetAsync(
            host.Client,
            $"/api/v1/dates/{seed.ServiceDateId}",
            member.AccessToken);
        string json = await response.Content.ReadAsStringAsync();
        DateContract dateResponse = Deserialize<DateContract>(json);

        HelpNeedContract visible = Assert.Single(dateResponse.HelpNeeds);
        Assert.Equal("foodPreparation", visible.Category, ignoreCase: true);
        Assert.Equal(7, visible.Availability);
        Assert.DoesNotContain("Closed category instructions.", json, StringComparison.Ordinal);
    }

    private static async Task<PersistenceSeed> CreateSeedWithPasswordsAsync(PostgresTestDatabase database)
    {
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await SeedPasswordAsync(database, "manager@example.test");
        await SeedPasswordAsync(database, "admin@example.test");
        await SeedPasswordAsync(database, "member@example.test");
        return seed;
    }

    private static async Task SeedPasswordAsync(PostgresTestDatabase database, string email)
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
        Guid? installationId = null,
        string? idempotencyKey = null)
    {
        HttpRequestMessage request = new(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (installationId.HasValue)
        {
            request.Headers.TryAddWithoutValidation(
                AuthHeaders.InstallationId,
                installationId.Value.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(
                ApiDefaults.IdempotencyHeaderName,
                idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        using (response)
        {
            Assert.Equal(expectedStatus, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
                ?? throw new InvalidOperationException($"Missing {typeof(T).Name} response body.");
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Missing {typeof(T).Name} JSON payload.");

    private sealed record DatePageContract(
        IReadOnlyList<DateContract> Items,
        string? NextCursor);

    private sealed record DateContract(
        string Id,
        string Title,
        string Instructions,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        DateTimeOffset CancellationDeadlineAt,
        string ManagerMembershipId,
        string Status,
        long Version,
        IReadOnlyList<HelpNeedContract> HelpNeeds);

    private sealed record HelpNeedContract(
        string Id,
        string Category,
        string Instructions,
        int? Availability,
        string Status,
        long Version);

    private sealed record ProblemContract(
        string Code,
        int Status,
        string Detail);
}
