using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.IntegrationTests.Identity;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IdentityAnonymousRateLimitTestGroup
{
    public const string Name = "Identity anonymous rate limiting";
}

[Collection(IdentityAnonymousRateLimitTestGroup.Name)]
public sealed class IdentityAnonymousRateLimitingTests
{
    private static readonly byte[] FingerprintKey =
        Enumerable.Range(65, 32).Select(value => (byte)value).ToArray();
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AntiforgeryPrecedesAdmissionAndMfaEnrollmentRoutesRemainUnthrottled()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AntiforgeryPrecedesAdmissionAndMfaEnrollmentRoutesRemainUnthrottled));
        var limiter = new RecordingRateLimiter();
        await using var factory = CreateFactory(database, 5, services =>
        {
            services.RemoveAll<IIdentityAnonymousRateLimiter>();
            services.AddSingleton<IIdentityAnonymousRateLimiter>(limiter);
        });
        var ordinary = await factory.SeedUserAsync(
            "below-threshold@example.test",
            "BelowThreshold!234",
            [],
            enableMfa: false);

        using var anonymousClient = factory.CreateIdentityClient();
        using (var missingAntiforgery = await anonymousClient.PostAsJsonAsync(
                   "/admin/identity/login",
                   new { email = ordinary.Email, password = ordinary.Password }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);
            Assert.Equal(0, limiter.AttemptCount);
        }

        var login = await anonymousClient.LoginAsync(ordinary.Email, ordinary.Password);
        Assert.Equal("signed_in", login.Status);
        Assert.Equal(1, limiter.AttemptCount);
        Assert.Equal(["login"], limiter.EndpointFamilies);

        var administrator = await factory.SeedUserAsync(
            "unthrottled-mfa@example.test",
            "UnthrottledMfa!234",
            [RoleNames.ContentEditor],
            enableMfa: false);
        using var privilegedClient = await factory.CreateAuthenticatedClientAsync(
            administrator,
            [RoleNames.ContentEditor],
            mfaSatisfied: false);
        var setupToken = await privilegedClient.GetAntiforgeryTokenAsync();
        using var setupResponse = await privilegedClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/setup",
            new { },
            setupToken);
        setupResponse.EnsureSuccessStatusCode();
        var setup = await setupResponse.Content.ReadFromJsonAsync<MfaSetupResponseDto>();
        Assert.NotNull(setup);

        var enableToken = await privilegedClient.GetAntiforgeryTokenAsync();
        using var enableResponse = await privilegedClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/mfa/enable",
            new
            {
                oneTimeCode = IdentityHttpClientExtensions.CreateTotpCode(setup!.SharedKey),
            },
            enableToken);
        enableResponse.EnsureSuccessStatusCode();
        Assert.Equal(1, limiter.AttemptCount);
    }

    [Fact]
    public async Task LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(LoginAndMfaChallengeShareUniformThrottleBeforeJsonOrLockoutMutation));
        var passwordHasher = new CountingPasswordHasher();
        await using var factory = CreateFactory(database, 1, services =>
        {
            services.RemoveAll<IPasswordHasher<HusayniaIdentityUser>>();
            services.AddSingleton<IPasswordHasher<HusayniaIdentityUser>>(passwordHasher);
        });
        var administrator = await factory.SeedUserAsync(
            "shared-login-family@example.test",
            "SharedLoginFamily!234",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        using var client = factory.CreateIdentityClient();
        var firstToken = await client.GetAntiforgeryTokenAsync();
        using (var allowedMfaFailure = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/login",
                   new
                   {
                       email = administrator.Email,
                       password = administrator.Password,
                       oneTimeCode = "000000",
                   },
                   firstToken))
        {
            Assert.Equal(HttpStatusCode.BadRequest, allowedMfaFailure.StatusCode);
        }

        var stateAfterAllowedAttempt = await factory.FindUserAsync(administrator.Email);
        Assert.NotNull(stateAfterAllowedAttempt);
        Assert.Equal(1, stateAfterAllowedAttempt!.AccessFailedCount);
        var originalLockoutEnd = stateAfterAllowedAttempt.LockoutEnd;
        var verifierCallsAfterAllowedAttempt = passwordHasher.VerifyCount;
        Assert.True(verifierCallsAfterAllowedAttempt > 0);

        const string correlationId = "corr-uniform-login-throttle";
        var responses = new List<(HttpStatusCode Status, string Body, string RetryAfter)>();
        var validCode = IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!);
        foreach (var payload in new object[]
                 {
                     new
                     {
                         email = administrator.Email,
                         password = administrator.Password,
                         oneTimeCode = validCode,
                     },
                     new
                     {
                         email = "missing-shared-login@example.test",
                         password = "WrongPassword!234",
                         oneTimeCode = "111111",
                     },
                 })
        {
            var token = await client.GetAntiforgeryTokenAsync();
            using var response = await client.PostJsonWithAntiforgeryAsync(
                "/admin/identity/login",
                payload,
                token,
                correlationId);
            responses.Add((
                response.StatusCode,
                await response.Content.ReadAsStringAsync(),
                response.Headers.GetValues("Retry-After").Single()));
        }

        var malformedToken = await client.GetAntiforgeryTokenAsync();
        using (var malformedRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/login")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        })
        {
            malformedRequest.Headers.Add("RequestVerificationToken", malformedToken);
            malformedRequest.Headers.Add("X-Correlation-ID", correlationId);
            using var malformedResponse = await client.SendAsync(malformedRequest);
            responses.Add((
                malformedResponse.StatusCode,
                await malformedResponse.Content.ReadAsStringAsync(),
                malformedResponse.Headers.GetValues("Retry-After").Single()));
        }

        Assert.All(responses, response => Assert.Equal((HttpStatusCode)429, response.Status));
        Assert.All(responses, response => Assert.Equal(responses[0].Body, response.Body));
        Assert.All(responses, response => Assert.Equal("300", response.RetryAfter));
        using (var payload = System.Text.Json.JsonDocument.Parse(responses[0].Body))
        {
            Assert.Equal("rate_limited", payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "Too many attempts. Try again later.",
                payload.RootElement.GetProperty("message").GetString());
        }

        var stateAfterThrottle = await factory.FindUserAsync(administrator.Email);
        Assert.NotNull(stateAfterThrottle);
        Assert.Equal(1, stateAfterThrottle!.AccessFailedCount);
        Assert.Equal(originalLockoutEnd, stateAfterThrottle.LockoutEnd);
        Assert.Equal(verifierCallsAfterAllowedAttempt, passwordHasher.VerifyCount);
    }

    [Fact]
    public async Task InvitationHasASeparatePartitionAndThrottlePreservesInvitationState()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(InvitationHasASeparatePartitionAndThrottlePreservesInvitationState));
        await using var factory = CreateFactory(database, 1);
        const string acceptedEmail = "accepted-partition@example.test";
        const string acceptedToken = "ACCEPTED-PARTITION-TOKEN";
        const string throttledEmail = "throttled-partition@example.test";
        const string throttledToken = "THROTTLED-PARTITION-TOKEN";
        await factory.SeedInvitationAsync(
            acceptedEmail,
            acceptedToken,
            DateTimeOffset.UtcNow.AddDays(1));
        await factory.SeedInvitationAsync(
            throttledEmail,
            throttledToken,
            DateTimeOffset.UtcNow.AddDays(1));
        using var client = factory.CreateIdentityClient();

        var loginToken = await client.GetAntiforgeryTokenAsync();
        using (var login = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/login",
                   new
                   {
                       email = "missing-partition@example.test",
                       password = "WrongPassword!234",
                   },
                   loginToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }

        var exhaustedLoginToken = await client.GetAntiforgeryTokenAsync();
        using (var exhaustedLogin = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/login",
                   new
                   {
                       email = "another-missing-partition@example.test",
                       password = "WrongPassword!234",
                   },
                   exhaustedLoginToken))
        {
            Assert.Equal((HttpStatusCode)429, exhaustedLogin.StatusCode);
        }

        var invitationToken = await client.GetAntiforgeryTokenAsync();
        using (var accepted = await client.PostJsonWithAntiforgeryAsync(
                   "/admin/identity/invitations/accept",
                   new
                   {
                       email = acceptedEmail,
                       token = acceptedToken,
                       password = "AcceptedPartition!234",
                   },
                   invitationToken))
        {
            accepted.EnsureSuccessStatusCode();
            var receipt = await accepted.Content.ReadFromJsonAsync<InvitationAcceptedResponseDto>();
            Assert.NotNull(receipt);
            Assert.Equal(acceptedEmail, receipt!.Email);
        }

        var beforeThrottle = await factory.FindUserAsync(throttledEmail);
        Assert.NotNull(beforeThrottle);
        var invitationHash = beforeThrottle!.InvitationTokenHash;
        Assert.False(beforeThrottle.HasPassword);

        var throttledTokenHeader = await client.GetAntiforgeryTokenAsync();
        using var throttled = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/invitations/accept",
            new
            {
                email = throttledEmail,
                token = throttledToken,
                password = "ThrottledPartition!234",
            },
            throttledTokenHeader);
        Assert.Equal((HttpStatusCode)429, throttled.StatusCode);
        Assert.Equal("300", throttled.Headers.GetValues("Retry-After").Single());

        var afterThrottle = await factory.FindUserAsync(throttledEmail);
        Assert.NotNull(afterThrottle);
        Assert.False(afterThrottle!.HasPassword);
        Assert.Equal(invitationHash, afterThrottle.InvitationTokenHash);
    }

    [Fact]
    public async Task LimiterDependencyFailureReturnsGeneric503WithoutIdentityMutation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(LimiterDependencyFailureReturnsGeneric503WithoutIdentityMutation));
        var limiter = new ThrowingRateLimiter();
        await using var factory = CreateFactory(database, 5, services =>
        {
            services.RemoveAll<IIdentityAnonymousRateLimiter>();
            services.AddSingleton<IIdentityAnonymousRateLimiter>(limiter);
        });
        var user = await factory.SeedUserAsync(
            "dependency-login@example.test",
            "DependencyLogin!234",
            [],
            enableMfa: false);
        const string invitationEmail = "dependency-invitation@example.test";
        const string invitationToken = "DEPENDENCY-INVITATION-TOKEN";
        await factory.SeedInvitationAsync(
            invitationEmail,
            invitationToken,
            DateTimeOffset.UtcNow.AddDays(1));
        var invitationBefore = await factory.FindUserAsync(invitationEmail);
        Assert.NotNull(invitationBefore);
        const string correlationId = "corr-rate-limit-dependency";
        using var client = factory.CreateIdentityClient();

        var loginToken = await client.GetAntiforgeryTokenAsync();
        using var loginResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new { email = user.Email, password = "WrongPassword!234" },
            loginToken,
            correlationId);
        var acceptToken = await client.GetAntiforgeryTokenAsync();
        using var invitationResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/invitations/accept",
            new
            {
                email = invitationEmail,
                token = invitationToken,
                password = "DependencyAccepted!234",
            },
            acceptToken,
            correlationId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, loginResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, invitationResponse.StatusCode);
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.Equal(loginBody, await invitationResponse.Content.ReadAsStringAsync());
        Assert.DoesNotContain("SQL-SENTINEL", loginBody, StringComparison.Ordinal);
        using (var payload = System.Text.Json.JsonDocument.Parse(loginBody))
        {
            Assert.Equal(
                "identity_rate_limit_unavailable",
                payload.RootElement.GetProperty("code").GetString());
            Assert.Equal(
                "The identity service is temporarily unavailable.",
                payload.RootElement.GetProperty("message").GetString());
        }

        var userAfter = await factory.FindUserAsync(user.Email);
        var invitationAfter = await factory.FindUserAsync(invitationEmail);
        Assert.NotNull(userAfter);
        Assert.NotNull(invitationAfter);
        Assert.Equal(0, userAfter!.AccessFailedCount);
        Assert.False(invitationAfter!.HasPassword);
        Assert.Equal(invitationBefore!.InvitationTokenHash, invitationAfter.InvitationTokenHash);
        Assert.Equal(
            ["login", "invitation-accept"],
            limiter.EndpointFamilies.Order(StringComparer.Ordinal).Reverse().ToArray());
    }

    [Fact]
    public async Task TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(TwoHostsEnforceOneGlobalEndpointThresholdWithoutRaceOvershoot));
        await using var firstFactory = CreateFactory(database, 4);
        await using var secondFactory = CreateFactory(database, 4);
        var clients = Enumerable.Range(0, 5)
            .Select(index => (index % 2 == 0 ? firstFactory : secondFactory).CreateIdentityClient())
            .ToArray();
        try
        {
            var antiforgeryTokens = await Task.WhenAll(
                clients.Select(client => client.GetAntiforgeryTokenAsync()));
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requests = clients.Select((client, index) => SendLoginAfterSignalAsync(
                    client,
                    antiforgeryTokens[index],
                    index,
                    start.Task))
                .ToArray();

            start.SetResult();
            using var responses = new ResponseCollection(await Task.WhenAll(requests));

            Assert.Equal(
                4,
                responses.Items.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Single(
                responses.Items,
                response => response.StatusCode == (HttpStatusCode)429);

            await using var context = database.CreateContext();
            var row = await context.Set<IdentityAnonymousRateLimit>()
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal("login", row.EndpointFamily);
            Assert.Equal(4, row.RequestCount);
            Assert.Equal(32, row.ClientFingerprint.Length);
            Assert.DoesNotContain(
                Encoding.UTF8.GetBytes("127.0.0.1"),
                row.ClientFingerprint);
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static IdentityWebApplicationFactory CreateFactory(
        IdentitySqlServerTestDatabase database,
        int permitLimit,
        Action<IServiceCollection>? configureServices = null)
    {
        var timeProvider = new FixedTimeProvider(FixedNow);
        return new IdentityWebApplicationFactory(
            database,
            ValidConfiguration(permitLimit),
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(timeProvider);
                configureServices?.Invoke(services);
            });
    }

    private static Dictionary<string, string?> ValidConfiguration(int permitLimit) =>
        new(StringComparer.Ordinal)
        {
            ["Identity:AnonymousRateLimit:PermitLimit"] =
                permitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Identity:AnonymousRateLimit:Window"] = "00:05:00",
            ["Identity:AnonymousRateLimit:Retention"] = "1.00:00:00",
            ["Identity:AnonymousRateLimit:FingerprintKey"] =
                Convert.ToBase64String(FingerprintKey),
        };

    private static async Task<HttpResponseMessage> SendLoginAfterSignalAsync(
        HttpClient client,
        string antiforgeryToken,
        int index,
        Task start)
    {
        await start;
        return await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new
            {
                email = $"missing-race-{index}@example.test",
                password = "WrongPassword!234",
            },
            antiforgeryToken,
            $"corr-rate-race-{index}");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRateLimiter : IIdentityAnonymousRateLimiter
    {
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal ConcurrentQueue<string> EndpointFamilies { get; } = new();

        public Task<IdentityRateLimitDecision> AttemptAsync(
            string endpointFamily,
            ReadOnlyMemory<byte> clientFingerprint,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            EndpointFamilies.Enqueue(endpointFamily);
            return Task.FromResult(
                new IdentityRateLimitDecision(true, FixedNow.AddMinutes(5), AttemptCount, 5));
        }
    }

    private sealed class ThrowingRateLimiter : IIdentityAnonymousRateLimiter
    {
        internal ConcurrentQueue<string> EndpointFamilies { get; } = new();

        public Task<IdentityRateLimitDecision> AttemptAsync(
            string endpointFamily,
            ReadOnlyMemory<byte> clientFingerprint,
            CancellationToken cancellationToken)
        {
            EndpointFamilies.Enqueue(endpointFamily);
            throw new IdentityAnonymousRateLimitDependencyException(
                new InvalidOperationException("SQL-SENTINEL"));
        }
    }

    private sealed class CountingPasswordHasher : IPasswordHasher<HusayniaIdentityUser>
    {
        private readonly PasswordHasher<HusayniaIdentityUser> inner = new();
        private int verifyCount;

        internal int VerifyCount => Volatile.Read(ref verifyCount);

        public string HashPassword(HusayniaIdentityUser user, string password) =>
            inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            HusayniaIdentityUser user,
            string hashedPassword,
            string providedPassword)
        {
            Interlocked.Increment(ref verifyCount);
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class ResponseCollection(HttpResponseMessage[] items) : IDisposable
    {
        internal HttpResponseMessage[] Items { get; } = items;

        public void Dispose()
        {
            foreach (var item in Items)
            {
                item.Dispose();
            }
        }
    }
}
