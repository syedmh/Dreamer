using System.Net;
using System.Net.Http.Json;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAnonymousEnumerationTests
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
    public async Task LoginFailuresAreExternallyEquivalentAcrossAccountStates()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(LoginFailuresAreExternallyEquivalentAcrossAccountStates));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var disabled = await factory.SeedUserAsync(
            "disabled-enumeration@example.test",
            "EnumerationUser!234",
            [],
            enableMfa: false,
            disabled: true);
        var unconfirmed = await factory.SeedUserAsync(
            "unconfirmed-enumeration@example.test",
            "EnumerationUser!234",
            [],
            enableMfa: false,
            confirmed: false);
        var locked = await factory.SeedUserAsync(
            "locked-enumeration@example.test",
            "EnumerationUser!234",
            [],
            enableMfa: false);
        var active = await factory.SeedUserAsync(
            "active-enumeration@example.test",
            "EnumerationUser!234",
            [],
            enableMfa: false);
        await factory.LockOutUserAsync(locked.UserId);

        var responses = new List<(HttpStatusCode Status, string Body)>();
        foreach (var email in new[]
                 {
                     "missing-enumeration@example.test",
                     disabled.Email,
                     unconfirmed.Email,
                     locked.Email,
                     active.Email,
                 })
        {
            using var client = factory.CreateIdentityClient();
            var token = await client.GetAntiforgeryTokenAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/login")
            {
                Content = JsonContent.Create(new
                {
                    email,
                    password = "WrongEnumeration!234",
                }),
            };
            request.Headers.Add("RequestVerificationToken", token);
            request.Headers.Add("X-Correlation-ID", "corr-login-enumeration");
            using var response = await client.SendAsync(request);
            responses.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.Status));
        Assert.All(responses, response => Assert.Equal(responses[0].Body, response.Body));
        using var failure = System.Text.Json.JsonDocument.Parse(responses[0].Body);
        Assert.Equal(
            "invalid_credentials",
            failure.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The supplied credentials are invalid.",
            failure.RootElement.GetProperty("message").GetString());
        Assert.Equal(
            "corr-login-enumeration",
            failure.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(3, failure.RootElement.EnumerateObject().Count());

        var audits = (await factory.ReadAuditEventsAsync())
            .Where(entry => entry.Action == "identity.login")
            .ToArray();
        Assert.Equal(5, audits.Length);
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_not_found", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_disabled", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_not_confirmed", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("locked_out", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("invalid_credentials", StringComparison.Ordinal));
        Assert.All(
            audits,
            entry => AssertAuditDoesNotContain(entry, "enumeration@example.test"));
    }

    [Fact]
    public async Task InvitationFailuresAreExternallyEquivalentAcrossAccountAndTokenStates()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(InvitationFailuresAreExternallyEquivalentAcrossAccountAndTokenStates));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        const string validToken = "VALID-INVITATION-TOKEN";
        await factory.SeedInvitationAsync(
            "disabled-invitation@example.test",
            validToken,
            DateTimeOffset.UtcNow.AddDays(1),
            disabled: true);
        _ = await factory.SeedUserAsync(
            "active-invitation@example.test",
            "EnumerationUser!234",
            [],
            enableMfa: false);
        await factory.SeedInvitationAsync(
            "expired-invitation@example.test",
            validToken,
            DateTimeOffset.UtcNow.AddHours(-1));
        await factory.SeedInvitationAsync(
            "invalid-token-invitation@example.test",
            validToken,
            DateTimeOffset.UtcNow.AddDays(1));

        var scenarios = new[]
        {
            ("missing-invitation@example.test", validToken),
            ("disabled-invitation@example.test", validToken),
            ("active-invitation@example.test", validToken),
            ("expired-invitation@example.test", validToken),
            ("invalid-token-invitation@example.test", "WRONG-INVITATION-TOKEN"),
        };
        var responses = new List<(HttpStatusCode Status, string Body)>();
        foreach (var (email, tokenValue) in scenarios)
        {
            using var client = factory.CreateIdentityClient();
            var antiforgery = await client.GetAntiforgeryTokenAsync();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/admin/identity/invitations/accept")
            {
                Content = JsonContent.Create(new
                {
                    email,
                    token = tokenValue,
                    password = "AcceptedPassword!234",
                }),
            };
            request.Headers.Add("RequestVerificationToken", antiforgery);
            request.Headers.Add("X-Correlation-ID", "corr-invitation-enumeration");
            using var response = await client.SendAsync(request);
            responses.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.BadRequest, response.Status));
        Assert.All(responses, response => Assert.Equal(responses[0].Body, response.Body));
        using var failure = System.Text.Json.JsonDocument.Parse(responses[0].Body);
        Assert.Equal(
            "invalid_invitation",
            failure.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "The invitation is invalid.",
            failure.RootElement.GetProperty("message").GetString());
        Assert.Equal(
            "corr-invitation-enumeration",
            failure.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(3, failure.RootElement.EnumerateObject().Count());
        var audits = (await factory.ReadAuditEventsAsync())
            .Where(entry => entry.Action == "identity.invitation.accept")
            .ToArray();
        Assert.Equal(5, audits.Length);
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_not_found", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_disabled", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("account_already_active", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("invitation_expired", StringComparison.Ordinal));
        Assert.Contains(audits, entry => entry.DetailJson.Contains("invalid_invitation_token", StringComparison.Ordinal));
        Assert.All(
            audits,
            entry =>
            {
                AssertAuditDoesNotContain(entry, "@example.test");
                AssertAuditDoesNotContain(entry, "INVITATION-TOKEN");
                AssertAuditDoesNotContain(entry, "AcceptedPassword!234");
            });
    }

    [Fact]
    public async Task ConcurrentInvitationAcceptanceHasOneSuccessAndOneUniformFailure()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentInvitationAcceptanceHasOneSuccessAndOneUniformFailure));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        const string email = "concurrent-invitation@example.test";
        const string invitationToken = "CONCURRENT-INVITATION-TOKEN";
        await factory.SeedInvitationAsync(
            email,
            invitationToken,
            DateTimeOffset.UtcNow.AddDays(1));
        using var firstClient = factory.CreateIdentityClient();
        using var secondClient = factory.CreateIdentityClient();
        var firstAntiforgery = await firstClient.GetAntiforgeryTokenAsync();
        var secondAntiforgery = await secondClient.GetAntiforgeryTokenAsync();

        var firstRequest = firstClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/invitations/accept",
            new
            {
                email,
                token = invitationToken,
                password = "AcceptedPassword!234",
            },
            firstAntiforgery,
            correlationId: "corr-concurrent-invitation");
        var secondRequest = secondClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/invitations/accept",
            new
            {
                email,
                token = invitationToken,
                password = "AcceptedPassword!234",
            },
            secondAntiforgery,
            correlationId: "corr-concurrent-invitation");
        using var firstResponse = await firstRequest;
        using var secondResponse = await secondRequest;
        var responses = new[] { firstResponse, secondResponse };

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var failureResponse = Assert.Single(
            responses,
            response => response.StatusCode == HttpStatusCode.BadRequest);
        var failure = await failureResponse.ReadFailureAsync();
        Assert.Equal("invalid_invitation", failure!.Code);
        var user = await factory.FindUserAsync(email);
        Assert.NotNull(user);
        Assert.True(user!.HasPassword);
        Assert.Null(user.InvitationTokenHash);
        Assert.Single(
            await factory.ReadAuditEventsAsync(),
            entry => entry.Action == "identity.invitation.accept");
    }

    private static void AssertAuditDoesNotContain(
        Husaynia.Domain.Identity.AuditEvent audit,
        string sentinel)
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
