using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityCompatibilityRegressionTests
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
    public async Task BelowThresholdAnonymousRoutesPreserveStatusBodyAntiforgeryAndRedactionContracts()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(BelowThresholdAnonymousRoutesPreserveStatusBodyAntiforgeryAndRedactionContracts));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        using var client = factory.CreateIdentityClient();

        using (var missingAntiforgery = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/login")
        {
            Content = JsonContent.Create(new
            {
                email = "missing-compatibility@example.test",
                password = "CompatibilityPassword!234",
            }),
        })
        {
            missingAntiforgery.Headers.Add("X-Correlation-ID", "corr-compat-antiforgery");
            using var response = await client.SendAsync(missingAntiforgery);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.False(response.Headers.Contains("Retry-After"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("antiforgery_required", body.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "A valid antiforgery token is required.",
                body.RootElement.GetProperty("message").GetString());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        }

        var loginToken = await client.GetAntiforgeryTokenAsync();
        using (var loginResponse = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/login",
                   new
                   {
                       email = "missing-compatibility@example.test",
                       password = "CompatibilityPassword!234",
                   },
                   loginToken,
                   correlationId: "corr-compat-login"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
            Assert.False(loginResponse.Headers.Contains("Retry-After"));
            var failure = await loginResponse.ReadFailureAsync();
            Assert.NotNull(failure);
            Assert.Equal("invalid_credentials", failure!.Code);
            Assert.Equal("The supplied credentials are invalid.", failure.Message);
            Assert.Equal("corr-compat-login", failure.CorrelationId);
        }

        var invitationToken = await client.GetAntiforgeryTokenAsync();
        using (var invitationResponse = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/invitations/accept",
                   new
                   {
                       email = "missing-compatibility@example.test",
                       token = "COMPATIBILITY-INVITATION-TOKEN",
                       password = "CompatibilityPassword!234",
                   },
                   invitationToken,
                   correlationId: "corr-compat-invitation"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invitationResponse.StatusCode);
            Assert.False(invitationResponse.Headers.Contains("Retry-After"));
            var failure = await invitationResponse.ReadFailureAsync();
            Assert.NotNull(failure);
            Assert.Equal("invalid_invitation", failure!.Code);
            Assert.Equal("The invitation is invalid.", failure.Message);
            Assert.Equal("corr-compat-invitation", failure.CorrelationId);
        }

        var audits = await factory.ReadAuditEventsAsync();
        Assert.Equal(2, audits.Count);
        Assert.Single(audits, audit => audit.Action == "identity.login");
        Assert.Single(audits, audit => audit.Action == "identity.invitation.accept");
        Assert.All(
            audits,
            audit =>
            {
                AssertAuditDoesNotContain(audit, "missing-compatibility@example.test");
                AssertAuditDoesNotContain(audit, "CompatibilityPassword!234");
                AssertAuditDoesNotContain(audit, "COMPATIBILITY-INVITATION-TOKEN");
            });
    }

    [Fact]
    public async Task BelowThresholdPrivilegedRoutePreservesAuthorizationAntiforgeryAndAuditContracts()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(BelowThresholdPrivilegedRoutePreservesAuthorizationAntiforgeryAndAuditContracts));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var ordinary = await factory.SeedUserAsync(
            "ordinary-compatibility@example.test",
            "OrdinaryCompatibility!234",
            [],
            enableMfa: false);
        var administrator = await factory.SeedUserAsync(
            "administrator-compatibility@example.test",
            "AdministratorCompatibility!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var anonymousClient = factory.CreateIdentityClient();
        using (var anonymousResponse = await anonymousClient.GetAsync(
                   $"/admin/identity/capabilities/{AdministrativeCapability.UsersRolesIntegrationsSettings}"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        }

        using var ordinaryClient = factory.CreateIdentityClient();
        await ordinaryClient.LoginAsync(ordinary.Email, ordinary.Password);
        using (var ordinaryResponse = await ordinaryClient.GetAsync(
                   $"/admin/identity/capabilities/{AdministrativeCapability.UsersRolesIntegrationsSettings}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, ordinaryResponse.StatusCode);
        }

        using var administratorClient = factory.CreateIdentityClient();
        await administratorClient.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/users/invite")
        {
            Content = JsonContent.Create(new
            {
                email = "invite-compatibility@example.test",
                roles = new[] { RoleNames.ContentEditor },
            }),
        };
        request.Headers.Add("X-Correlation-ID", "corr-compat-privileged-antiforgery");
        using var response = await administratorClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("antiforgery_required", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "A valid antiforgery token is required.",
            body.RootElement.GetProperty("message").GetString());
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());

        var audits = await factory.ReadAuditEventsAsync();
        Assert.Equal(3, audits.Count);
        Assert.Equal(2, audits.Count(audit => audit.Action == "identity.capability.read"));
        var antiforgeryAudit = Assert.Single(
            audits,
            audit => audit.Action == "identity.user.invite");
        Assert.Equal("corr-compat-privileged-antiforgery", antiforgeryAudit.CorrelationId);
        Assert.Equal("denied", antiforgeryAudit.Outcome);
        Assert.Contains("antiforgery_required", antiforgeryAudit.DetailJson, StringComparison.Ordinal);
        AssertAuditDoesNotContain(antiforgeryAudit, "invite-compatibility@example.test");
    }

    private static void AssertAuditDoesNotContain(AuditEvent audit, string sentinel)
    {
        var serializedFields = string.Join(
            "|",
            audit.ActorId,
            audit.RolesJson,
            audit.Action,
            audit.TargetType,
            audit.TargetId,
            audit.Outcome,
            audit.CorrelationId,
            audit.DetailJson);
        Assert.DoesNotContain(sentinel, serializedFields, StringComparison.OrdinalIgnoreCase);
    }
}
