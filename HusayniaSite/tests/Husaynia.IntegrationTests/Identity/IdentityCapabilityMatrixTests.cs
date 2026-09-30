using System.Net;
using System.Net.Http.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityCapabilityMatrixTests
{
    private static readonly IReadOnlyDictionary<string, string?> TestConfiguration =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:AnonymousRateLimit:PermitLimit"] = "100",
            ["Identity:AnonymousRateLimit:Window"] = "00:01:00",
            ["Identity:AnonymousRateLimit:Retention"] = "00:05:00",
            ["Identity:AnonymousRateLimit:FingerprintKey"] =
                Convert.ToBase64String(new byte[32]),
        };

    [Fact]
    public async Task CapabilityProbeEnforcesFrozenMatrixForAnonymousOrdinaryAndAllPrivilegedRoles()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(CapabilityProbeEnforcesFrozenMatrixForAnonymousOrdinaryAndAllPrivilegedRoles));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);

        var ordinary = await factory.SeedUserAsync(
            "ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var contentEditor = await factory.SeedUserAsync(
            "content@example.test",
            "ContentEditor!234",
            [RoleNames.ContentEditor],
            enableMfa: true);
        var eventEditor = await factory.SeedUserAsync(
            "event@example.test",
            "EventEditor!2345",
            [RoleNames.EventEditor],
            enableMfa: true);
        var mediaEditor = await factory.SeedUserAsync(
            "media@example.test",
            "MediaEditor!2345",
            [RoleNames.MediaEditor],
            enableMfa: true);
        var donationOperator = await factory.SeedUserAsync(
            "donation@example.test",
            "DonationOperator!2",
            [RoleNames.DonationOperator],
            enableMfa: true);
        var auditor = await factory.SeedUserAsync(
            "auditor@example.test",
            "ReadOnlyAuditor!2",
            [RoleNames.ReadOnlyAuditor],
            enableMfa: true);

        using var anonymousClient = factory.CreateIdentityClient();
        using var ordinaryClient = factory.CreateIdentityClient();
        using var siteAdminClient = factory.CreateIdentityClient();
        using var contentClient = factory.CreateIdentityClient();
        using var eventClient = factory.CreateIdentityClient();
        using var mediaClient = factory.CreateIdentityClient();
        using var donationClient = factory.CreateIdentityClient();
        using var auditorClient = factory.CreateIdentityClient();

        await ordinaryClient.LoginAsync(ordinary.Email, ordinary.Password);
        await siteAdminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));
        await contentClient.LoginAsync(
            contentEditor.Email,
            contentEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(contentEditor.AuthenticatorKey!));
        await eventClient.LoginAsync(
            eventEditor.Email,
            eventEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(eventEditor.AuthenticatorKey!));
        await mediaClient.LoginAsync(
            mediaEditor.Email,
            mediaEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(mediaEditor.AuthenticatorKey!));
        await donationClient.LoginAsync(
            donationOperator.Email,
            donationOperator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(donationOperator.AuthenticatorKey!));
        await auditorClient.LoginAsync(
            auditor.Email,
            auditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(auditor.AuthenticatorKey!));

        var clients = new Dictionary<string, HttpClient>(StringComparer.Ordinal)
        {
            ["anonymous"] = anonymousClient,
            ["ordinary"] = ordinaryClient,
            [RoleNames.SiteAdministrator] = siteAdminClient,
            [RoleNames.ContentEditor] = contentClient,
            [RoleNames.EventEditor] = eventClient,
            [RoleNames.MediaEditor] = mediaClient,
            [RoleNames.DonationOperator] = donationClient,
            [RoleNames.ReadOnlyAuditor] = auditorClient,
        };

        foreach (var capability in Enum.GetValues<AdministrativeCapability>())
        {
            var anonymousResponse = await anonymousClient.GetAsync(
                $"/admin/identity/capabilities/{capability}");
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

            var ordinaryResponse = await ordinaryClient.GetAsync(
                $"/admin/identity/capabilities/{capability}");
            Assert.Equal(HttpStatusCode.Forbidden, ordinaryResponse.StatusCode);

            foreach (var role in RoleNames.All.Order(StringComparer.Ordinal))
            {
                var response = await clients[role].GetAsync(
                    $"/admin/identity/capabilities/{capability}");
                var expectedAccess = AuthorizationContract.CapabilityMatrix[capability]
                    .GetValueOrDefault(role, CapabilityAccess.None);

                if (expectedAccess == CapabilityAccess.None)
                {
                    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<CapabilityProbeView>();
                Assert.NotNull(payload);
                Assert.Equal(capability, payload!.Capability);
                Assert.Equal(expectedAccess, payload.GrantedAccess);
            }
        }

        var auditEvents = await factory.ReadAuditEventsAsync();
        Assert.Equal(48, auditEvents.Count);
        Assert.All(auditEvents, audit => Assert.Equal("identity.capability.read", audit.Action));
        Assert.All(auditEvents, audit => Assert.False(string.IsNullOrWhiteSpace(audit.CorrelationId)));
    }
}
