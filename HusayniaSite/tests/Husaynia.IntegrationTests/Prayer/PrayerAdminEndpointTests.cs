using System.Net;
using System.Net.Http.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Prayer;
using Husaynia.IntegrationTests.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.IntegrationTests.Prayer;

public sealed class PrayerAdminEndpointTests
{
    [Fact]
    public async Task OnlyMfaSatisfiedSiteAdministratorMayCreateProfile()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(OnlyMfaSatisfiedSiteAdministratorMayCreateProfile));
        var fake = new StubPrayerAdministration();
        using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IPrayerAdministration>();
                services.AddSingleton<IPrayerAdministration>(fake);
            });
        var roles = new[]
        {
            RoleNames.ContentEditor,
            RoleNames.EventEditor,
            RoleNames.MediaEditor,
            RoleNames.DonationOperator,
            RoleNames.ReadOnlyAuditor,
        };
        foreach (var role in roles)
        {
            var user = await factory.SeedUserAsync(
                $"{role.ToLowerInvariant()}@example.test",
                "ValidPassword!234",
                [role],
                enableMfa: true);
            using var client = await factory.CreateAuthenticatedClientAsync(user, [role], true);
            var response = await PostProfileAsync(client);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var ordinary = await factory.SeedUserAsync(
            "ordinary@example.test",
            "ValidPassword!234",
            [],
            enableMfa: false);
        using (var client = await factory.CreateAuthenticatedClientAsync(ordinary, [], false))
        {
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await PostProfileAsync(client)).StatusCode);
        }

        var administrator = await factory.SeedUserAsync(
            "administrator@example.test",
            "ValidPassword!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using (var client = await factory.CreateAuthenticatedClientAsync(
                   administrator,
                   [RoleNames.SiteAdministrator],
                   false))
        {
            var response = await PostProfileAsync(client);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("mfa_required", (await response.ReadFailureAsync())!.Code);
        }

        using (var client = await factory.CreateAuthenticatedClientAsync(
                   administrator,
                   [RoleNames.SiteAdministrator],
                   true))
        {
            var response = await PostProfileAsync(client);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var anonymous = factory.CreateIdentityClient())
        {
            var response = await PostProfileAsync(anonymous);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Equal(1, fake.CreateCalls);
        var audits = await factory.ReadAuditEventsAsync();
        Assert.Equal(
            roles.Length + 3,
            audits.Count(audit => audit.Action == "prayer.profile.create"));
        Assert.All(
            audits.Where(audit => audit.Action == "prayer.profile.create"),
            audit => Assert.DoesNotContain(
                "methodJson",
                audit.DetailJson,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationRequiresAntiforgeryAndReturnsCorrelation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(MutationRequiresAntiforgeryAndReturnsCorrelation));
        var fake = new StubPrayerAdministration();
        using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IPrayerAdministration>();
                services.AddSingleton<IPrayerAdministration>(fake);
            });
        var administrator = await factory.SeedUserAsync(
            "administrator@example.test",
            "ValidPassword!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.SiteAdministrator],
            true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/prayer/profiles")
        {
            Content = JsonContent.Create(ProfileBody()),
        };
        request.Headers.Add("X-Correlation-ID", "prayer-correlation");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("prayer-correlation", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(
            "prayer-correlation",
            (await response.ReadFailureAsync())!.CorrelationId);
        Assert.Equal(0, fake.CreateCalls);
        var audits = await factory.ReadAuditEventsAsync();
        var audit = Assert.Single(
            audits,
            entry => entry.Action == "prayer.profile.create");
        Assert.Contains("antiforgery_required", audit.DetailJson, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostProfileAsync(HttpClient client)
    {
        var token = await client.GetAntiforgeryTokenAsync();
        return await client.PostJsonWithAntiforgeryAsync(
            "/admin/prayer/profiles",
            ProfileBody(),
            token);
    }

    private static object ProfileBody() =>
        new
        {
            providerKind = "local",
            latitude = 47.9129m,
            longitude = -122.0982m,
            methodJson =
                """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
            algorithmVersion = DeterministicPrayerCalculator.AlgorithmVersion,
            effectiveFrom = new DateOnly(2026, 1, 1),
        };

    private sealed class StubPrayerAdministration : IPrayerAdministration
    {
        public int CreateCalls { get; private set; }

        public Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
            CreatePrayerProfileCommand command,
            UserContext actor,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            var profile = PrayerProfile.Create(
                command.ProviderKind,
                command.Latitude,
                command.Longitude,
                command.MethodJson,
                command.AlgorithmVersion,
                PrayerTimeZone.IanaId,
                command.EffectiveFrom,
                actor.UserId);
            return Task.FromResult(
                Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                    PrayerProfileView.From(profile)));
        }

        public Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
            ActivatePrayerProfileCommand command,
            UserContext actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
            SavePrayerOverrideCommand command,
            UserContext actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
            DeactivatePrayerOverrideCommand command,
            UserContext actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PrayerRefreshReceipt, PrayerAdministrationError>> RefreshAsync(
            RefreshPrayerScheduleCommand command,
            UserContext actor,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
