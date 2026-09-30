using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityPrivilegedAuditOutcomeMatrixTests
{
    [Fact]
    public async Task PrivilegedRouteOutcomesEmitExactlyOneAuditPerActionAndCorrelation()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(PrivilegedRouteOutcomesEmitExactlyOneAuditPerActionAndCorrelation));
        await using var factory = new IdentityWebApplicationFactory(database);
        var administrator = await factory.SeedUserAsync(
            "matrix-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var ordinary = await factory.SeedUserAsync(
            "matrix-ordinary@example.test",
            "OrdinaryUser!23456",
            [],
            enableMfa: false);
        var target = await factory.SeedUserAsync(
            "matrix-target@example.test",
            "TargetUser!23456",
            [RoleNames.MediaEditor],
            enableMfa: true);

        using (var ordinaryClient = factory.CreateIdentityClient())
        {
            await ordinaryClient.LoginAsync(ordinary.Email, ordinary.Password);
            using var denialRequest = new HttpRequestMessage(
                HttpMethod.Get,
                $"/admin/identity/capabilities/{AdministrativeCapability.UsersRolesIntegrationsSettings}");
            denialRequest.Headers.Add("X-Correlation-ID", "corr-matrix-denial");
            using var denialResponse = await ordinaryClient.SendAsync(denialRequest);
            Assert.Equal(HttpStatusCode.Forbidden, denialResponse.StatusCode);
        }

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));

        using (var antiforgeryRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/admin/identity/users/invite")
        {
            Content = JsonContent.Create(new
            {
                email = "matrix-invitee@example.test",
                roles = Array.Empty<string>(),
            }),
        })
        {
            antiforgeryRequest.Headers.Add("X-Correlation-ID", "corr-matrix-antiforgery");
            using var antiforgeryResponse = await client.SendAsync(antiforgeryRequest);
            Assert.Equal(HttpStatusCode.BadRequest, antiforgeryResponse.StatusCode);
        }

        var token = await client.GetAntiforgeryTokenAsync();
        using (var malformedRequest = new HttpRequestMessage(
                   HttpMethod.Post,
                   "/admin/identity/users/invite")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json"),
        })
        {
            malformedRequest.Headers.Add("RequestVerificationToken", token);
            malformedRequest.Headers.Add("X-Correlation-ID", "corr-matrix-malformed");
            using var malformedResponse = await client.SendAsync(malformedRequest);
            Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        }

        using var validationResponse = await client.PostJsonWithAntiforgeryAsync(
            "/admin/identity/users/not-a-guid/disable",
            new { expectedConcurrencyStamp = "stamp", reason = "matrix" },
            token,
            correlationId: "corr-matrix-validation");
        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);

        using var conflictResponse = await client.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{target.UserId}/disable",
            new { expectedConcurrencyStamp = "stale-stamp", reason = "matrix" },
            token,
            correlationId: "corr-matrix-conflict");
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        using var successResponse = await client.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{target.UserId}/disable",
            new { expectedConcurrencyStamp = target.ConcurrencyStamp, reason = "matrix" },
            token,
            correlationId: "corr-matrix-success");
        Assert.Equal(HttpStatusCode.OK, successResponse.StatusCode);

        var audits = await factory.ReadAuditEventsAsync();
        AssertExactlyOne(audits, "identity.capability.read", "corr-matrix-denial");
        AssertExactlyOne(audits, "identity.user.invite", "corr-matrix-antiforgery");
        AssertExactlyOne(audits, "identity.user.invite", "corr-matrix-malformed");
        AssertExactlyOne(audits, "identity.user.disable", "corr-matrix-validation");
        AssertExactlyOne(audits, "identity.user.disable", "corr-matrix-conflict");
        AssertExactlyOne(audits, "identity.user.disable", "corr-matrix-success");
    }

    [Fact]
    public async Task EscapingEndpointExceptionIsAuditedOnceAndReturnsSanitizedFailure()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EscapingEndpointExceptionIsAuditedOnceAndReturnsSanitizedFailure));
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IUserAdministration>();
                services.AddScoped<IUserAdministration, ThrowingUserAdministration>();
            });
        var administrator = await factory.SeedUserAsync(
            "exception-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/admin/identity/audit/summary");
        request.Headers.Add("X-Correlation-ID", "corr-matrix-exception");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("unexpected_failure", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingUserAdministration.Secret, body, StringComparison.Ordinal);
        AssertExactlyOne(
            await factory.ReadAuditEventsAsync(),
            "identity.audit.summary.read",
            "corr-matrix-exception");
    }

    [Fact]
    public async Task ValidationConflictAndSuccessAuditsSurviveRequestAbort()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ValidationConflictAndSuccessAuditsSurviveRequestAbort));
        var coordinator = new CorrelationDelayCoordinator();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton(coordinator);
                services.AddScoped<IAuditWriter>(provider => new CorrelationDelayedAuditWriter(
                    new EfAuditWriter(
                        provider.GetRequiredService<HusayniaIdentityDbContext>(),
                        provider.GetRequiredService<ISensitiveDataRedactor>(),
                        provider.GetRequiredService<TimeProvider>()),
                    provider.GetRequiredService<CorrelationDelayCoordinator>()));
            });
        var administrator = await factory.SeedUserAsync(
            "abort-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var conflictTarget = await factory.SeedUserAsync(
            "abort-conflict@example.test",
            "TargetUser!23456",
            [RoleNames.MediaEditor],
            enableMfa: true);
        var successTarget = await factory.SeedUserAsync(
            "abort-success@example.test",
            "TargetUser!23456",
            [RoleNames.MediaEditor],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));
        var token = await client.GetAntiforgeryTokenAsync();

        await SendAndAbortAsync(
            coordinator,
            "corr-abort-validation",
            cancellationToken => PostJsonWithAntiforgeryAsync(
                client,
                "/admin/identity/users/not-a-guid/disable",
                new { expectedConcurrencyStamp = "stamp", reason = "abort" },
                token,
                "corr-abort-validation",
                cancellationToken));
        await SendAndAbortAsync(
            coordinator,
            "corr-abort-conflict",
            cancellationToken => PostJsonWithAntiforgeryAsync(
                client,
                $"/admin/identity/users/{conflictTarget.UserId}/disable",
                new { expectedConcurrencyStamp = "stale-stamp", reason = "abort" },
                token,
                "corr-abort-conflict",
                cancellationToken));
        await SendAndAbortAsync(
            coordinator,
            "corr-abort-success",
            cancellationToken => PostJsonWithAntiforgeryAsync(
                client,
                $"/admin/identity/users/{successTarget.UserId}/disable",
                new { expectedConcurrencyStamp = successTarget.ConcurrencyStamp, reason = "abort" },
                token,
                "corr-abort-success",
                cancellationToken));

        var audits = await factory.ReadAuditEventsAsync();
        AssertExactlyOne(audits, "identity.user.disable", "corr-abort-validation");
        AssertExactlyOne(audits, "identity.user.disable", "corr-abort-conflict");
        AssertExactlyOne(audits, "identity.user.disable", "corr-abort-success");
    }

    [Fact]
    public async Task EscapingExceptionAuditSurvivesRequestAbort()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(EscapingExceptionAuditSurvivesRequestAbort));
        var coordinator = new CorrelationDelayCoordinator();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton(coordinator);
                services.AddScoped<IAuditWriter>(provider => new CorrelationDelayedAuditWriter(
                    new EfAuditWriter(
                        provider.GetRequiredService<HusayniaIdentityDbContext>(),
                        provider.GetRequiredService<ISensitiveDataRedactor>(),
                        provider.GetRequiredService<TimeProvider>()),
                    provider.GetRequiredService<CorrelationDelayCoordinator>()));
                services.RemoveAll<IUserAdministration>();
                services.AddScoped<IUserAdministration, ThrowingUserAdministration>();
            });
        var administrator = await factory.SeedUserAsync(
            "abort-exception-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));

        await SendAndAbortAsync(
            coordinator,
            "corr-abort-exception",
            cancellationToken =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    "/admin/identity/audit/summary");
                request.Headers.Add("X-Correlation-ID", "corr-abort-exception");
                return client.SendAsync(request, cancellationToken);
            });

        AssertExactlyOne(
            await factory.ReadAuditEventsAsync(),
            "identity.audit.summary.read",
            "corr-abort-exception");
    }

    [Fact]
    public async Task ThrowingWriterReturnsSanitized503WithoutRetryOrTransactionalSuccess()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ThrowingWriterReturnsSanitized503WithoutRetryOrTransactionalSuccess));
        var writer = new ThrowingAuditWriter();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton<IAuditWriter>(writer);
            });
        var administrator = await factory.SeedUserAsync(
            "throwing-writer-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var target = await factory.SeedUserAsync(
            "throwing-writer-target@example.test",
            "TargetUser!23456",
            [RoleNames.MediaEditor],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            administrator.Email,
            administrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));
        var token = await client.GetAntiforgeryTokenAsync();

        using var response = await client.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{target.UserId}/disable",
            new { expectedConcurrencyStamp = target.ConcurrencyStamp, reason = "writer failure" },
            token,
            correlationId: "corr-throwing-writer");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("identity_audit_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ThrowingAuditWriter.Secret, body, StringComparison.Ordinal);
        Assert.Equal(1, writer.AttemptCount);

        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<HusayniaIdentityUser>>();
        var persisted = await userManager.FindByIdAsync(target.UserId);
        Assert.NotNull(persisted);
        Assert.False(persisted!.IsDisabled);
    }

    private static async Task SendAndAbortAsync(
        CorrelationDelayCoordinator coordinator,
        string correlationId,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync)
    {
        var probe = coordinator.Register(correlationId);
        using var cancellation = new CancellationTokenSource();
        var requestTask = sendAsync(cancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<HttpResponseMessage> PostJsonWithAntiforgeryAsync(
        HttpClient client,
        string requestUri,
        object body,
        string antiforgeryToken,
        string correlationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("RequestVerificationToken", antiforgeryToken);
        request.Headers.Add("X-Correlation-ID", correlationId);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AssertExactlyOne(
        IReadOnlyCollection<AuditEvent> audits,
        string action,
        string correlationId) =>
        Assert.Single(
            audits,
            audit => audit.Action == action && audit.CorrelationId == correlationId);

    private sealed class ThrowingUserAdministration : IUserAdministration
    {
        internal const string Secret = "secret-dependency-diagnostic";

        public Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
            InviteIdentityUserCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
            DisableIdentityUserCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
            SetIdentityUserRolesCommand command,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>>
            ReadAuditEventsAsync(
                ReadIdentityAuditEventsQuery query,
                AdministrativeRequestActor actor,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);

        public Task<Result<CapabilityProbeView, IdentityAdministrationError>> ProbeCapabilityAsync(
            AdministrativeCapability capability,
            AdministrativeRequestActor actor,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Secret);
    }

    private sealed class CorrelationDelayCoordinator
    {
        private readonly ConcurrentDictionary<string, CorrelationDelayProbe> probes =
            new(StringComparer.Ordinal);

        internal CorrelationDelayProbe Register(string correlationId)
        {
            var probe = new CorrelationDelayProbe();
            Assert.True(probes.TryAdd(correlationId, probe));
            return probe;
        }

        internal bool TryTake(string correlationId, out CorrelationDelayProbe probe) =>
            probes.TryRemove(correlationId, out probe!);
    }

    private sealed class CorrelationDelayProbe
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class CorrelationDelayedAuditWriter(
        IAuditWriter inner,
        CorrelationDelayCoordinator coordinator) : IAuditWriter
    {
        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            if (!coordinator.TryTake(descriptor.CorrelationId, out var probe))
            {
                await inner.AppendAsync(descriptor, outcome, details, cancellationToken);
                return;
            }

            probe.Entered.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await inner.AppendAsync(descriptor, outcome, details, cancellationToken);
            }
            finally
            {
                probe.Completed.TrySetResult();
            }
        }
    }

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        internal const string Secret = "secret-writer-diagnostic";
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        public Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            throw new InvalidOperationException(Secret);
        }
    }
}
