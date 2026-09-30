using System.Net;
using System.Net.Http.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityMfaAndLockoutTests
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
    public async Task PrivilegedUserMustEnrollMfaThenSubsequentLoginsRequireChallengeCode()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(PrivilegedUserMustEnrollMfaThenSubsequentLoginsRequireChallengeCode));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var contentEditor = await factory.SeedUserAsync(
            "content@example.test",
            "ContentEditor!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        var initialLogin = await client.LoginAsync(contentEditor.Email, contentEditor.Password);
        Assert.Equal("mfa_enrollment_required", initialLogin.Status);
        Assert.True(initialLogin.Session.RequiresMfaEnrollment);

        var deniedResponse = await client.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.PagesAnnouncementsReligiousContent}");
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        var setupToken = await client.GetAntiforgeryTokenAsync();
        var setupHttpResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            setupToken);
        setupHttpResponse.EnsureSuccessStatusCode();
        var setupResponse = await setupHttpResponse.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setupResponse);
        Assert.False(setupResponse!.AlreadyEnabled);
        Assert.False(string.IsNullOrWhiteSpace(setupResponse.SharedKey));

        var enableToken = await client.GetAntiforgeryTokenAsync();
        var enableResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = IdentityHttpClientExtensions.CreateTotpCode(setupResponse.SharedKey) },
            enableToken);
        enableResponse.EnsureSuccessStatusCode();
        var enabledLogin = await enableResponse.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(enabledLogin);
        Assert.Equal("mfa_enabled", enabledLogin!.Status);
        Assert.True(enabledLogin.Session.MfaSatisfied);

        var capabilityResponse = await client.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.PagesAnnouncementsReligiousContent}");
        capabilityResponse.EnsureSuccessStatusCode();
        var capability = await capabilityResponse.Content.ReadFromJsonAsync<CapabilityProbeView>();
        Assert.NotNull(capability);
        Assert.Equal(
            CapabilityAccess.Read | CapabilityAccess.Write,
            capability!.GrantedAccess);

        using var freshClient = factory.CreateIdentityClient();
        var challengeToken = await freshClient.GetAntiforgeryTokenAsync();
        var challengeResponse = await freshClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new
            {
                email = contentEditor.Email,
                password = contentEditor.Password,
            },
            challengeToken);
        Assert.Equal(HttpStatusCode.Unauthorized, challengeResponse.StatusCode);
        var challengeFailure = await challengeResponse.ReadFailureAsync();
        Assert.Equal("mfa_challenge_required", challengeFailure!.Code);

        var completedLogin = await freshClient.LoginAsync(
            contentEditor.Email,
            contentEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(setupResponse.SharedKey));
        Assert.Equal("signed_in", completedLogin.Status);
        Assert.True(completedLogin.Session.MfaSatisfied);
    }

    [Fact]
    public async Task LockoutTriggersAfterRepeatedPasswordFailuresAndRegistrationRouteRemainsAbsent()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(LockoutTriggersAfterRepeatedPasswordFailuresAndRegistrationRouteRemainsAbsent));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var ordinary = await factory.SeedUserAsync(
            "ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var token = await client.GetAntiforgeryTokenAsync();
            var response = await client.PostJsonWithAntiforgeryAsync(
                "/admin/identity/login",
                new
                {
                    email = ordinary.Email,
                    password = "WrongPassword!234",
                },
                token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var lockedToken = await client.GetAntiforgeryTokenAsync();
        var lockedResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new
            {
                email = ordinary.Email,
                password = ordinary.Password,
            },
            lockedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedResponse.StatusCode);

        var missingRegisterGet = await client.GetAsync("/admin/identity/register");
        var missingRegisterPost = await client.PostAsync("/admin/identity/register", null);
        Assert.Equal(HttpStatusCode.NotFound, missingRegisterGet.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingRegisterPost.StatusCode);
    }

    [Fact]
    public async Task RepeatedInvalidMfaCodesLockPrivilegedAccount()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(RepeatedInvalidMfaCodesLockPrivilegedAccount));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var token = await client.GetAntiforgeryTokenAsync();
            var response = await client.PostJsonWithAntiforgeryAsync(
                "/admin/identity/login",
                new
                {
                    email = siteAdministrator.Email,
                    password = siteAdministrator.Password,
                    oneTimeCode = "000000",
                },
                token);

            Assert.Equal(
                attempt < 5 ? HttpStatusCode.BadRequest : (HttpStatusCode)423,
                response.StatusCode);
        }

        var lockedToken = await client.GetAntiforgeryTokenAsync();
        var lockedResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new
            {
                email = siteAdministrator.Email,
                password = siteAdministrator.Password,
                oneTimeCode = IdentityHttpClientExtensions.CreateTotpCode(
                    siteAdministrator.AuthenticatorKey!),
            },
            lockedToken);
        Assert.Equal(HttpStatusCode.Unauthorized, lockedResponse.StatusCode);
    }

    [Fact]
    public async Task MfaSetupRejectsGetAndMissingAntiforgeryWithoutResettingKey()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(MfaSetupRejectsGetAndMissingAntiforgeryWithoutResettingKey));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var contentEditor = await factory.SeedUserAsync(
            "content@example.test",
            "ContentEditor!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(contentEditor.Email, contentEditor.Password);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync(
            "/admin/identity/mfa/setup")).StatusCode);
        using var missingAntiforgeryRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/identity/mfa/setup")
        {
            Content = JsonContent.Create(new { }),
        };
        missingAntiforgeryRequest.Headers.Add(
            "X-Correlation-ID",
            "corr-mfa-setup-missing-antiforgery");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.SendAsync(missingAntiforgeryRequest)).StatusCode);

        Assert.Null(await factory.ReadAuthenticatorKeyAsync(contentEditor.UserId));

        var token = await client.GetAntiforgeryTokenAsync();
        var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: "corr-mfa-setup-created");
        response.EnsureSuccessStatusCode();
        var setup = await response.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setup);
        Assert.False(string.IsNullOrWhiteSpace(setup!.SharedKey));
        AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "corr-mfa-setup-missing-antiforgery",
            "denied",
            "antiforgery_required");
        AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "corr-mfa-setup-created",
            "allowed",
            errorCode: null);
    }

    [Fact]
    public async Task EnabledMfaSetupNeverRevealsEstablishedKeyOrDowngradesSatisfiedSession()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EnabledMfaSetupNeverRevealsEstablishedKeyOrDowngradesSatisfiedSession));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var contentEditor = await factory.SeedUserAsync(
            "enabled-content@example.test",
            "ContentEditor!234",
            [RoleNames.ContentEditor],
            enableMfa: true);

        using var satisfiedClient = factory.CreateIdentityClient();
        await satisfiedClient.LoginAsync(
            contentEditor.Email,
            contentEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(contentEditor.AuthenticatorKey!));
        var token = await satisfiedClient.GetAntiforgeryTokenAsync();
        var response = await satisfiedClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: "corr-mfa-setup-enabled-conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var failure = await response.ReadFailureAsync();
        Assert.Equal("mfa_already_enabled", failure!.Code);
        Assert.DoesNotContain(contentEditor.AuthenticatorKey!, await response.Content.ReadAsStringAsync());
        Assert.Equal(
            contentEditor.AuthenticatorKey,
            await factory.ReadAuthenticatorKeyAsync(contentEditor.UserId));
        AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "corr-mfa-setup-enabled-conflict",
            "allowed",
            "mfa_already_enabled");

        var capability = await satisfiedClient.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.PagesAnnouncementsReligiousContent}");
        capability.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task EnabledMfaSetupRejectsUnsatisfiedAdministrativeSession()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EnabledMfaSetupRejectsUnsatisfiedAdministrativeSession));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var enabledUser = await factory.SeedUserAsync(
            "enabled-unsatisfied@example.test",
            "ContentEditor!234",
            [RoleNames.ContentEditor],
            enableMfa: true);

        using var client = await factory.CreateAuthenticatedClientAsync(
            enabledUser,
            [RoleNames.ContentEditor],
            mfaSatisfied: false);
        var token = await client.GetAntiforgeryTokenAsync();
        var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: "corr-mfa-setup-unsatisfied-conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(enabledUser.AuthenticatorKey!, await response.Content.ReadAsStringAsync());
        Assert.Equal(
            enabledUser.AuthenticatorKey,
            await factory.ReadAuthenticatorKeyAsync(enabledUser.UserId));
        AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "corr-mfa-setup-unsatisfied-conflict",
            "allowed",
            "mfa_already_enabled");
    }

    private static void AssertAudit(
        IReadOnlyCollection<AuditEvent> audits,
        string correlationId,
        string outcome,
        string? errorCode)
    {
        var audit = Assert.Single(
            audits,
            entry =>
                entry.Action == "identity.mfa.setup" &&
                entry.CorrelationId == correlationId &&
                entry.Outcome == outcome);
        if (errorCode is not null)
        {
            Assert.Contains($"\"errorCode\":\"{errorCode}\"", audit.DetailJson, StringComparison.Ordinal);
        }
    }
}
