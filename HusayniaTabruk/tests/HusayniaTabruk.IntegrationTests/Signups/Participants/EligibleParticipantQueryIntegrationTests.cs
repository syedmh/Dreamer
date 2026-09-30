using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Application.Abstractions;
using HusayniaTabruk.Application.Signups.Participants;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HusayniaTabruk.IntegrationTests.Signups.Participants;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "FirstSlice")]
[Trait("Category", "Privacy")]
public sealed class EligibleParticipantQueryIntegrationTests
    : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task ActiveMemberGetsOnlyOtherEligibleSameOrganizationAdultsInStablePages()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithMemberPasswordAsync(database);
        ParticipantSeed participants = await AddParticipantCasesAsync(database, seed);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host);

        EligibleParticipantPageContract first = await ReadRequiredAsync<EligibleParticipantPageContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants?pageSize=2",
                member.AccessToken),
            HttpStatusCode.OK);
        EligibleParticipantPageContract second = await ReadRequiredAsync<EligibleParticipantPageContract>(
            await GetAsync(
                host.Client,
                $"/api/v1/members/eligible-participants?pageSize=2&cursor={Uri.EscapeDataString(first.NextCursor!)}",
                member.AccessToken),
            HttpStatusCode.OK);
        using HttpResponseMessage finalResponse = await GetAsync(
            host.Client,
            $"/api/v1/members/eligible-participants?pageSize=2&cursor={Uri.EscapeDataString(second.NextCursor!)}",
            member.AccessToken);
        string finalJson = await finalResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, finalResponse.StatusCode);
        EligibleParticipantPageContract final =
            Deserialize<EligibleParticipantPageContract>(finalJson);

        EligibleParticipantContract[] all =
            [.. first.Items, .. second.Items, .. final.Items];
        Assert.Equal(
            new[]
            {
                participants.AlphaMembershipId,
                seed.ManagerMembershipId.Value,
                seed.SecondAdministratorMembershipId.Value,
                participants.FirstTwinMembershipId,
                participants.SecondTwinMembershipId,
            },
            all.Select(item => item.MembershipId).ToArray());
        Assert.Equal(
            ["Alpha", "Manager", "Second administrator", "Twin", "Twin"],
            all.Select(item => item.DisplayName).ToArray());
        Assert.NotNull(first.NextCursor);
        Assert.NotNull(second.NextCursor);
        Assert.Null(final.NextCursor);
        Assert.DoesNotContain(
            all,
            item => item.MembershipId == seed.MemberMembershipId.Value
                || item.MembershipId == participants.InvitedMembershipId
                || item.MembershipId == participants.DisabledMembershipId
                || item.MembershipId == participants.IneligibleMembershipId
                || item.MembershipId == participants.CrossOrganizationMembershipId);
        Assert.DoesNotContain("email", finalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", finalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("userId", finalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", finalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("status", finalJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eligible", finalJson, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresPostgresFact]
    public async Task AnonymousInvalidPaginationAndDisabledActorFailClosed()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithMemberPasswordAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host =
            await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse member = await LoginAsync(host);

        ProblemContract anonymous = await ReadRequiredAsync<ProblemContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants"),
            HttpStatusCode.Unauthorized);
        ProblemContract invalidCursor = await ReadRequiredAsync<ProblemContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants?cursor=not-base64",
                member.AccessToken),
            HttpStatusCode.BadRequest);
        ProblemContract invalidPageSize = await ReadRequiredAsync<ProblemContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants?pageSize=0",
                member.AccessToken),
            HttpStatusCode.BadRequest);
        ProblemContract excessivePageSize = await ReadRequiredAsync<ProblemContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants?pageSize=101",
                member.AccessToken),
            HttpStatusCode.BadRequest);

        await using (TabrukDbContext context = database.CreateContext())
        {
            MembershipEntity actor = await context.Memberships.SingleAsync(
                candidate => candidate.Id == seed.MemberMembershipId.Value);
            actor.Status = (short)MembershipStatus.Disabled;
            await context.SaveChangesAsync();
        }

        ProblemContract disabled = await ReadRequiredAsync<ProblemContract>(
            await GetAsync(
                host.Client,
                "/api/v1/members/eligible-participants?cursor=not-base64&pageSize=0",
                member.AccessToken),
            HttpStatusCode.Unauthorized);

        Assert.Equal("unauthorized", anonymous.Code);
        Assert.Equal(
            EligibleParticipantErrorCodes.InvalidParticipantQuery,
            invalidCursor.Code);
        Assert.Equal(
            EligibleParticipantErrorCodes.InvalidParticipantQuery,
            invalidPageSize.Code);
        Assert.Equal(
            EligibleParticipantErrorCodes.InvalidParticipantQuery,
            excessivePageSize.Code);
        Assert.Equal("unauthorized", disabled.Code);
    }

    [RequiresPostgresFact]
    public async Task ParticipantProjectionDependencyFailureReturnsSanitized503()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithMemberPasswordAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
                services.AddSingleton<IEligibleSignupParticipantRepository>(
                    new UnavailableParticipantRepository()));
        TokenSetResponse member = await LoginAsync(host);

        using HttpResponseMessage response = await GetAsync(
            host.Client,
            "/api/v1/members/eligible-participants",
            member.AccessToken);
        string body = await response.Content.ReadAsStringAsync();
        ProblemContract problem = Deserialize<ProblemContract>(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependency_unavailable", problem.Code);
        Assert.DoesNotContain(
            nameof(DependencyUnavailableException),
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Postgres", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<PersistenceSeed> CreateSeedWithMemberPasswordAsync(
        PostgresTestDatabase database)
    {
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.NormalizedEmail == "MEMBER@EXAMPLE.TEST");
        PasswordHasher<TabrukIdentityUser> hasher = new();
        user.PasswordHash = hasher.HashPassword(user, SharedPassword);
        user.EmailConfirmed = true;
        await context.SaveChangesAsync();
        return seed;
    }

    private static async Task<ParticipantSeed> AddParticipantCasesAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        Guid alphaMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000010");
        Guid firstTwinMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000020");
        Guid secondTwinMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000021");
        Guid invitedMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000030");
        Guid disabledMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000040");
        Guid ineligibleMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000050");
        Guid crossOrganizationMembershipId =
            Guid.Parse("00000000-0000-7000-8000-000000000060");
        Guid otherOrganizationId = Guid.CreateVersion7();

        await using TabrukDbContext context = database.CreateContext();
        context.Organizations.Add(
            new OrganizationEntity
            {
                Id = otherOrganizationId,
                Name = "Other organization",
                TimeZone = "America/Los_Angeles",
                DefaultCancellationLeadMinutes = 60,
                Status = 0,
                BootstrapStatus = 0,
                Version = 0,
            });
        AddMembership(
            context,
            alphaMembershipId,
            seed.OrganizationId.Value,
            "alpha@example.test",
            "Alpha",
            MembershipStatus.Active,
            eligible: true);
        AddMembership(
            context,
            firstTwinMembershipId,
            seed.OrganizationId.Value,
            "twin-one@example.test",
            "Twin",
            MembershipStatus.Active,
            eligible: true);
        AddMembership(
            context,
            secondTwinMembershipId,
            seed.OrganizationId.Value,
            "twin-two@example.test",
            "Twin",
            MembershipStatus.Active,
            eligible: true);
        AddMembership(
            context,
            invitedMembershipId,
            seed.OrganizationId.Value,
            "invited@example.test",
            "A invited",
            MembershipStatus.Invited,
            eligible: true);
        AddMembership(
            context,
            disabledMembershipId,
            seed.OrganizationId.Value,
            "disabled@example.test",
            "B disabled",
            MembershipStatus.Disabled,
            eligible: true);
        AddMembership(
            context,
            ineligibleMembershipId,
            seed.OrganizationId.Value,
            "ineligible@example.test",
            "C ineligible",
            MembershipStatus.Active,
            eligible: false);
        AddMembership(
            context,
            crossOrganizationMembershipId,
            otherOrganizationId,
            "cross@example.test",
            "A cross organization",
            MembershipStatus.Active,
            eligible: true);
        await context.SaveChangesAsync();

        return new ParticipantSeed(
            alphaMembershipId,
            firstTwinMembershipId,
            secondTwinMembershipId,
            invitedMembershipId,
            disabledMembershipId,
            ineligibleMembershipId,
            crossOrganizationMembershipId);
    }

    private static void AddMembership(
        TabrukDbContext context,
        Guid membershipId,
        Guid organizationId,
        string email,
        string displayName,
        MembershipStatus status,
        bool eligible)
    {
        Guid userId = Guid.CreateVersion7();
        context.Users.Add(
            new TabrukIdentityUser
            {
                Id = userId,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            });
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId,
                OrganizationId = organizationId,
                UserId = userId,
                DisplayName = displayName,
                Status = (short)status,
                EligibleAsNamedParticipant = eligible,
            });
    }

    private static async Task<TokenSetResponse> LoginAsync(AuthApiHost host)
    {
        using HttpResponseMessage response = await SendAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/login",
            new
            {
                email = "member@example.test",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        return await ReadRequiredAsync<TokenSetResponse>(
            response,
            HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? bearerToken = null) =>
        SendAsync(
            client,
            HttpMethod.Get,
            path,
            bearerToken: bearerToken);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null,
        string? bearerToken = null,
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
        using (response)
        {
            Assert.Equal(expectedStatus, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Missing {typeof(T).Name} response body.");
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException(
            $"Missing {typeof(T).Name} JSON payload.");

    private sealed class UnavailableParticipantRepository
        : IEligibleSignupParticipantRepository
    {
        public ValueTask<Result<EligibleSignupParticipantSlice>> ListEligibleAsync(
            OrganizationId organizationId,
            MembershipId excludedMembershipId,
            int startIndex,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new DependencyUnavailableException();
    }

    private sealed record EligibleParticipantPageContract(
        IReadOnlyList<EligibleParticipantContract> Items,
        string? NextCursor);

    private sealed record EligibleParticipantContract(
        Guid MembershipId,
        string DisplayName);

    private sealed record ProblemContract(
        string Code,
        int Status,
        string Detail);

    private sealed record ParticipantSeed(
        Guid AlphaMembershipId,
        Guid FirstTwinMembershipId,
        Guid SecondTwinMembershipId,
        Guid InvitedMembershipId,
        Guid DisabledMembershipId,
        Guid IneligibleMembershipId,
        Guid CrossOrganizationMembershipId);
}
