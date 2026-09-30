using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityMfaAuditTests
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
    public async Task SetupFrameworkAndAntiforgeryDenialsAreAuditedExactlyOnce()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SetupFrameworkAndAntiforgeryDenialsAreAuditedExactlyOnce));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var user = await factory.SeedUserAsync(
            "mfa-setup-denial@example.test",
            "MfaSetupDenial!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var anonymousClient = factory.CreateIdentityClient();
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/setup")
        {
            Content = JsonContent.Create(new { }),
        };
        anonymousRequest.Headers.Add("X-Correlation-ID", "corr-mfa-setup-framework");
        using var anonymousResponse = await anonymousClient.SendAsync(anonymousRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        using var antiforgeryRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/setup")
        {
            Content = JsonContent.Create(new { }),
        };
        antiforgeryRequest.Headers.Add("X-Correlation-ID", "corr-mfa-setup-antiforgery");
        using var antiforgeryResponse = await client.SendAsync(antiforgeryRequest);
        Assert.Equal(HttpStatusCode.BadRequest, antiforgeryResponse.StatusCode);

        var audits = await factory.ReadAuditEventsAsync();
        AssertAudit(audits, "identity.mfa.setup", "corr-mfa-setup-framework", "denied");
        AssertAudit(audits, "identity.mfa.setup", "corr-mfa-setup-antiforgery", "denied");
        Assert.Null(await factory.ReadAuthenticatorKeyAsync(user.UserId));
    }

    [Fact]
    public async Task SetupAndEnableForbiddenFrameworkDenialsAreAuditedExactlyOnce()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SetupAndEnableForbiddenFrameworkDenialsAreAuditedExactlyOnce));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var ordinary = await factory.SeedUserAsync(
            "mfa-forbidden@example.test",
            "MfaForbidden!234",
            [],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(ordinary.Email, ordinary.Password);
        using var setupRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/setup")
        {
            Content = JsonContent.Create(new { }),
        };
        setupRequest.Headers.Add("X-Correlation-ID", "corr-mfa-setup-forbidden");
        using var setupResponse = await client.SendAsync(setupRequest);
        Assert.Equal(HttpStatusCode.Forbidden, setupResponse.StatusCode);

        using var enableRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/enable")
        {
            Content = JsonContent.Create(new { oneTimeCode = "123456" }),
        };
        enableRequest.Headers.Add("X-Correlation-ID", "corr-mfa-enable-forbidden");
        using var enableResponse = await client.SendAsync(enableRequest);
        Assert.Equal(HttpStatusCode.Forbidden, enableResponse.StatusCode);

        var audits = await factory.ReadAuditEventsAsync();
        AssertAudit(audits, "identity.mfa.setup", "corr-mfa-setup-forbidden", "denied");
        AssertAudit(audits, "identity.mfa.enable", "corr-mfa-enable-forbidden", "denied");
        Assert.Null(await factory.ReadAuthenticatorKeyAsync(ordinary.UserId));
    }

    [Fact]
    public async Task SetupSuccessAuditIsSanitizedAndIdentifiesTheAuthenticatedUser()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SetupSuccessAuditIsSanitizedAndIdentifiesTheAuthenticatedUser));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var user = await factory.SeedUserAsync(
            "mfa-setup-success@example.test",
            "MfaSetupSuccess!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var token = await client.GetAntiforgeryTokenAsync();
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: "corr-mfa-setup-success");
        response.EnsureSuccessStatusCode();
        var setup = await response.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setup);

        var audit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.setup",
            "corr-mfa-setup-success",
            "allowed");
        Assert.Equal(user.UserId, audit.ActorId);
        Assert.Equal(user.UserId, audit.TargetId);
        Assert.Equal("IdentityUser", audit.TargetType);
        AssertDetail(audit, "result", "key_ready");
        AssertDetail(audit, "keyCreated", "true");
        AssertSanitized(audit, setup!.SharedKey, user.Email, user.Password);
    }

    [Fact]
    public async Task InvalidEnableCodesAuditEveryAttemptAndLockTheAccount()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(InvalidEnableCodesAuditEveryAttemptAndLockTheAccount));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var user = await factory.SeedUserAsync(
            "mfa-enable-lockout@example.test",
            "MfaEnableLockout!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var setupToken = await client.GetAntiforgeryTokenAsync();
        using var setupResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            setupToken);
        setupResponse.EnsureSuccessStatusCode();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var token = await client.GetAntiforgeryTokenAsync();
            var correlationId = $"corr-mfa-enable-invalid-{attempt}";
            using var response = await client.PostJsonWithAntiforgeryAsync(
                "/admin/identity/mfa/enable",
                new { oneTimeCode = "000000" },
                token,
                correlationId: correlationId);
            Assert.Equal(
                attempt < 5 ? HttpStatusCode.BadRequest : (HttpStatusCode)423,
                response.StatusCode);

            var audit = AssertAudit(
                await factory.ReadAuditEventsAsync(),
                "identity.mfa.enable",
                correlationId,
                "allowed");
            AssertDetail(audit, "result", attempt < 5 ? "invalid_code" : "locked_out");
            AssertSanitized(audit, "000000", user.Email, user.Password);
        }
    }

    [Fact]
    public async Task EnableDenialsAndValidationFailuresAreAuditedExactlyOnce()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EnableDenialsAndValidationFailuresAreAuditedExactlyOnce));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var user = await factory.SeedUserAsync(
            "mfa-enable-validation@example.test",
            "MfaEnableValidation!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var anonymousClient = factory.CreateIdentityClient();
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/enable")
        {
            Content = JsonContent.Create(new { oneTimeCode = "123456" }),
        };
        anonymousRequest.Headers.Add("X-Correlation-ID", "corr-mfa-enable-framework");
        using var anonymousResponse = await anonymousClient.SendAsync(anonymousRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        using var antiforgeryRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/mfa/enable")
        {
            Content = JsonContent.Create(new { oneTimeCode = "123456" }),
        };
        antiforgeryRequest.Headers.Add("X-Correlation-ID", "corr-mfa-enable-antiforgery");
        using var antiforgeryResponse = await client.SendAsync(antiforgeryRequest);
        Assert.Equal(HttpStatusCode.BadRequest, antiforgeryResponse.StatusCode);

        var token = await client.GetAntiforgeryTokenAsync();
        using var missingResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = "" },
            token,
            correlationId: "corr-mfa-enable-missing");
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);

        using var malformedResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = "12-secret-xyz" },
            token,
            correlationId: "corr-mfa-enable-malformed");
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        using var malformedJsonRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/identity/mfa/enable")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        };
        malformedJsonRequest.Headers.Add("RequestVerificationToken", token);
        malformedJsonRequest.Headers.Add("X-Correlation-ID", "corr-mfa-enable-malformed-json");
        using var malformedJsonResponse = await client.SendAsync(malformedJsonRequest);
        Assert.Equal(HttpStatusCode.BadRequest, malformedJsonResponse.StatusCode);

        var audits = await factory.ReadAuditEventsAsync();
        AssertAudit(audits, "identity.mfa.enable", "corr-mfa-enable-framework", "denied");
        AssertAudit(audits, "identity.mfa.enable", "corr-mfa-enable-antiforgery", "denied");
        AssertDetail(
            AssertAudit(audits, "identity.mfa.enable", "corr-mfa-enable-missing", "allowed"),
            "errorCode",
            "missing_two_factor_code");
        var malformedAudit = AssertAudit(
            audits,
            "identity.mfa.enable",
            "corr-mfa-enable-malformed",
            "allowed");
        AssertDetail(malformedAudit, "errorCode", "malformed_two_factor_code");
        AssertSanitized(malformedAudit, "12-secret-xyz", user.Email, user.Password);
        AssertDetail(
            AssertAudit(
                audits,
                "identity.mfa.enable",
                "corr-mfa-enable-malformed-json",
                "allowed"),
            "errorCode",
            "malformed_two_factor_code");
    }

    [Fact]
    public async Task SetupConflictAndExistingKeySuccessPreserveKeyAndAuditState()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SetupConflictAndExistingKeySuccessPreserveKeyAndAuditState));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var pending = await factory.SeedUserAsync(
            "mfa-existing-key@example.test",
            "MfaExistingKey!234",
            [RoleNames.ContentEditor],
            enableMfa: false);
        var enabled = await factory.SeedUserAsync(
            "mfa-conflict@example.test",
            "MfaConflict!234",
            [RoleNames.ContentEditor],
            enableMfa: true);

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
            var user = await userManager.FindByIdAsync(pending.UserId);
            Assert.NotNull(user);
            Assert.True((await userManager.ResetAuthenticatorKeyAsync(user!)).Succeeded);
        }

        var existingKey = await factory.ReadAuthenticatorKeyAsync(pending.UserId);
        using var pendingClient = factory.CreateIdentityClient();
        await pendingClient.LoginAsync(pending.Email, pending.Password);
        var pendingToken = await pendingClient.GetAntiforgeryTokenAsync();
        using var pendingResponse = await pendingClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            pendingToken,
            correlationId: "corr-mfa-setup-existing-key");
        pendingResponse.EnsureSuccessStatusCode();
        var pendingAudit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.setup",
            "corr-mfa-setup-existing-key",
            "allowed");
        AssertDetail(pendingAudit, "keyCreated", "false");
        Assert.Equal(existingKey, await factory.ReadAuthenticatorKeyAsync(pending.UserId));

        using var enabledClient = factory.CreateIdentityClient();
        await enabledClient.LoginAsync(
            enabled.Email,
            enabled.Password,
            IdentityHttpClientExtensions.CreateTotpCode(enabled.AuthenticatorKey!));
        var enabledToken = await enabledClient.GetAntiforgeryTokenAsync();
        using var enabledResponse = await enabledClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            enabledToken,
            correlationId: "corr-mfa-setup-conflict");
        Assert.Equal(HttpStatusCode.Conflict, enabledResponse.StatusCode);
        var conflictAudit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.setup",
            "corr-mfa-setup-conflict",
            "allowed");
        AssertDetail(conflictAudit, "result", "conflict");
        Assert.Equal(enabled.AuthenticatorKey, await factory.ReadAuthenticatorKeyAsync(enabled.UserId));
        AssertSanitized(conflictAudit, enabled.AuthenticatorKey!, enabled.Email, enabled.Password);
    }

    [Theory]
    [InlineData(UserManagerFailureMode.SetupFailure, "failed", "authenticator_key_generation_failed")]
    [InlineData(UserManagerFailureMode.SetupException, "exception", "unexpected_failure")]
    public async Task SetupGenerationFailureAndUnexpectedExceptionAreAudited(
        UserManagerFailureMode failureMode,
        string result,
        string errorCode)
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            $"{nameof(SetupGenerationFailureAndUnexpectedExceptionAreAudited)}_{failureMode}");
        await using var factory = CreateFactory(database, failureMode);
        var user = await factory.SeedUserAsync(
            $"{failureMode}@example.test",
            "MfaSetupFailure!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var token = await client.GetAntiforgeryTokenAsync();
        var correlationId = $"corr-mfa-setup-{failureMode}";
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: correlationId);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(TestUserManager.Secret, await response.Content.ReadAsStringAsync());

        var audit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.setup",
            correlationId,
            "allowed");
        AssertDetail(audit, "result", result);
        AssertDetail(audit, "errorCode", errorCode);
        Assert.Null(await factory.ReadAuthenticatorKeyAsync(user.UserId));
        AssertSanitized(audit, TestUserManager.Secret, user.Email, user.Password);
    }

    [Fact]
    public async Task EnableSuccessAuditsOnceResetsFailuresAndIssuesSatisfiedSessionAfterCommit()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EnableSuccessAuditsOnceResetsFailuresAndIssuesSatisfiedSessionAfterCommit));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var user = await factory.SeedUserAsync(
            "mfa-enable-success@example.test",
            "MfaEnableSuccess!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var setupToken = await client.GetAntiforgeryTokenAsync();
        using var setupResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            setupToken);
        var setup = await setupResponse.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setup);

        var invalidToken = await client.GetAntiforgeryTokenAsync();
        using var invalidResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = "000000" },
            invalidToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        var token = await client.GetAntiforgeryTokenAsync();
        var otp = IdentityHttpClientExtensions.CreateTotpCode(setup!.SharedKey);
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = otp },
            token,
            correlationId: "corr-mfa-enable-success");
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponseDto>();
        Assert.NotNull(login);
        Assert.True(login!.Session.MfaSatisfied);

        var persisted = await factory.FindUserAsync(user.Email);
        Assert.NotNull(persisted);
        Assert.True(persisted!.TwoFactorEnabled);
        Assert.Equal(0, persisted.AccessFailedCount);
        var audit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.enable",
            "corr-mfa-enable-success",
            "allowed");
        AssertDetail(audit, "result", "enabled");
        AssertSanitized(audit, otp, setup.SharedKey, user.Email, user.Password);
    }

    [Theory]
    [InlineData(UserManagerFailureMode.EnableFailure, "failed", "mfa_enable_failed")]
    [InlineData(UserManagerFailureMode.EnableException, "exception", "unexpected_failure")]
    public async Task EnableFrameworkFailureAndUnexpectedExceptionAreAuditedWithoutMutation(
        UserManagerFailureMode failureMode,
        string result,
        string errorCode)
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            $"{nameof(EnableFrameworkFailureAndUnexpectedExceptionAreAuditedWithoutMutation)}_{failureMode}");
        await using var factory = CreateFactory(database, failureMode);
        var user = await factory.SeedUserAsync(
            $"{failureMode}@example.test",
            "MfaEnableFailure!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var setupToken = await client.GetAntiforgeryTokenAsync();
        using var setupResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            setupToken);
        var setup = await setupResponse.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setup);

        var token = await client.GetAntiforgeryTokenAsync();
        var correlationId = $"corr-mfa-enable-{failureMode}";
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = IdentityHttpClientExtensions.CreateTotpCode(setup!.SharedKey) },
            token,
            correlationId: correlationId);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(TestUserManager.Secret, await response.Content.ReadAsStringAsync());

        var persisted = await factory.FindUserAsync(user.Email);
        Assert.NotNull(persisted);
        Assert.False(persisted!.TwoFactorEnabled);
        var audit = AssertAudit(
            await factory.ReadAuditEventsAsync(),
            "identity.mfa.enable",
            correlationId,
            "allowed");
        AssertDetail(audit, "result", result);
        AssertDetail(audit, "errorCode", errorCode);
        AssertSanitized(audit, TestUserManager.Secret, setup.SharedKey, user.Email, user.Password);
    }

    [Fact]
    public async Task AuditFailureRollsBackSetupKeyMutation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AuditFailureRollsBackSetupKeyMutation));
        var writer = new ThrowingAuditWriter();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            TestConfiguration,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton<IAuditWriter>(writer);
            });
        var user = await factory.SeedUserAsync(
            "mfa-setup-audit-failure@example.test",
            "MfaSetupAuditFailure!234",
            [RoleNames.ContentEditor],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(user.Email, user.Password);
        var token = await client.GetAntiforgeryTokenAsync();
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            token,
            correlationId: "corr-mfa-setup-audit-failure");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, writer.AttemptCount);
        Assert.Null(await factory.ReadAuthenticatorKeyAsync(user.UserId));
        Assert.DoesNotContain(ThrowingAuditWriter.Secret, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuditFailureRollsBackEnableMutationAndDoesNotIssueMfaCookie()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AuditFailureRollsBackEnableMutationAndDoesNotIssueMfaCookie));
        var writer = new ThrowingAuditWriter();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            TestConfiguration,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton<IAuditWriter>(writer);
            });
        var seeded = await factory.SeedUserAsync(
            "mfa-enable-audit-failure@example.test",
            "MfaEnableAuditFailure!234",
            [RoleNames.ContentEditor],
            enableMfa: false);
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<HusayniaIdentityUser>>();
            var user = await userManager.FindByIdAsync(seeded.UserId);
            Assert.NotNull(user);
            Assert.True((await userManager.ResetAuthenticatorKeyAsync(user!)).Succeeded);
        }

        var key = await factory.ReadAuthenticatorKeyAsync(seeded.UserId);
        Assert.False(string.IsNullOrWhiteSpace(key));
        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(seeded.Email, seeded.Password);
        var token = await client.GetAntiforgeryTokenAsync();
        using var response = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new { oneTimeCode = IdentityHttpClientExtensions.CreateTotpCode(key!) },
            token,
            correlationId: "corr-mfa-enable-audit-failure");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, writer.AttemptCount);
        var persisted = await factory.FindUserAsync(seeded.Email);
        Assert.NotNull(persisted);
        Assert.False(persisted!.TwoFactorEnabled);
        Assert.DoesNotContain(ThrowingAuditWriter.Secret, await response.Content.ReadAsStringAsync());

        using var selfResponse = await client.GetAsync("/admin/identity/self");
        selfResponse.EnsureSuccessStatusCode();
        var session = await selfResponse.Content.ReadFromJsonAsync<SessionViewDto>();
        Assert.NotNull(session);
        Assert.False(session!.MfaSatisfied);
    }

    private static IdentityWebApplicationFactory CreateFactory(
        IdentitySqlServerTestDatabase database,
        UserManagerFailureMode failureMode) =>
        new(
            database,
            TestConfiguration,
            configureServices: services =>
            {
                services.RemoveAll<UserManager<HusayniaIdentityUser>>();
                services.AddScoped<UserManager<HusayniaIdentityUser>>(provider =>
                    new TestUserManager(
                        provider.GetRequiredService<IUserStore<HusayniaIdentityUser>>(),
                        provider.GetRequiredService<IOptions<IdentityOptions>>(),
                        provider.GetRequiredService<IPasswordHasher<HusayniaIdentityUser>>(),
                        provider.GetServices<IUserValidator<HusayniaIdentityUser>>(),
                        provider.GetServices<IPasswordValidator<HusayniaIdentityUser>>(),
                        provider.GetRequiredService<ILookupNormalizer>(),
                        provider.GetRequiredService<IdentityErrorDescriber>(),
                        provider,
                        provider.GetRequiredService<ILogger<UserManager<HusayniaIdentityUser>>>(),
                        failureMode));
            });

    private static AuditEvent AssertAudit(
        IReadOnlyCollection<AuditEvent> audits,
        string action,
        string correlationId,
        string outcome) =>
        Assert.Single(
            audits,
            audit =>
                audit.Action == action &&
                audit.CorrelationId == correlationId &&
                audit.Outcome == outcome);

    private static void AssertDetail(AuditEvent audit, string name, string expected)
    {
        using var details = JsonDocument.Parse(audit.DetailJson);
        Assert.Equal(expected, details.RootElement.GetProperty(name).GetString());
    }

    private static void AssertSanitized(AuditEvent audit, params string[] secrets)
    {
        var serialized = JsonSerializer.Serialize(audit);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, serialized, StringComparison.OrdinalIgnoreCase);
        }
    }

    public enum UserManagerFailureMode
    {
        SetupFailure,
        SetupException,
        EnableFailure,
        EnableException,
    }

    private sealed class TestUserManager(
        IUserStore<HusayniaIdentityUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<HusayniaIdentityUser> passwordHasher,
        IEnumerable<IUserValidator<HusayniaIdentityUser>> userValidators,
        IEnumerable<IPasswordValidator<HusayniaIdentityUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<HusayniaIdentityUser>> logger,
        UserManagerFailureMode failureMode)
        : UserManager<HusayniaIdentityUser>(
            store,
            optionsAccessor,
            passwordHasher,
            userValidators,
            passwordValidators,
            keyNormalizer,
            errors,
            services,
            logger)
    {
        internal const string Secret = "sentinel-user-manager-secret";

        public override Task<IdentityResult> ResetAuthenticatorKeyAsync(HusayniaIdentityUser user) =>
            failureMode switch
            {
                UserManagerFailureMode.SetupFailure =>
                    Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "setup_failed" })),
                UserManagerFailureMode.SetupException => throw new InvalidOperationException(Secret),
                _ => base.ResetAuthenticatorKeyAsync(user),
            };

        public override Task<bool> VerifyTwoFactorTokenAsync(
            HusayniaIdentityUser user,
            string tokenProvider,
            string token) =>
            failureMode == UserManagerFailureMode.EnableException
                ? throw new InvalidOperationException(Secret)
                : base.VerifyTwoFactorTokenAsync(user, tokenProvider, token);

        public override Task<IdentityResult> SetTwoFactorEnabledAsync(
            HusayniaIdentityUser user,
            bool enabled) =>
            failureMode == UserManagerFailureMode.EnableFailure
                ? Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "enable_failed" }))
                : base.SetTwoFactorEnabledAsync(user, enabled);
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        internal const string Secret = "sentinel-audit-writer-secret";

        internal int AttemptCount { get; private set; }

        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            throw new InvalidOperationException(Secret);
        }
    }
}
