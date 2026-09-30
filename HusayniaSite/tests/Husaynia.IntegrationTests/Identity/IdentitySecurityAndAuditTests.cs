using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentitySecurityAndAuditTests
{
    [Fact]
    public async Task PrivilegedEndpointsApplyTheFrozenAuthorizationPolicies()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(PrivilegedEndpointsApplyTheFrozenAuthorizationPolicies));
        await using var factory = new IdentityWebApplicationFactory(database);
        using var client = factory.CreateIdentityClient();
        _ = await client.GetAsync("/admin/identity/antiforgery");

        var endpoints = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertPolicy(endpoints, "/admin/identity/audit/events", PolicyNames.AuditRead);
        AssertPolicy(endpoints, "/admin/identity/audit/summary", PolicyNames.AuditRead);
        AssertPolicy(endpoints, "/admin/identity/users/invite", PolicyNames.SiteAdministration);
        AssertPolicy(endpoints, "/admin/identity/users/{userId}/disable", PolicyNames.SiteAdministration);
        AssertPolicy(endpoints, "/admin/identity/users/{userId}/roles", PolicyNames.SiteAdministration);
        foreach (var capability in Enum.GetValues<AdministrativeCapability>())
        {
            AssertPolicy(
                endpoints,
                $"/admin/identity/capabilities/{capability}",
                AdministrativeCapabilityAuthorizer.GetPolicyName(capability));
        }
    }

    [Fact]
    public async Task AntiforgeryDeniedPrivilegedMutationIsAudited()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AntiforgeryDeniedPrivilegedMutationIsAudited));
        await using var factory = new IdentityWebApplicationFactory(database);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var adminClient = factory.CreateIdentityClient();
        await adminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/identity/users/invite")
        {
            Content = JsonContent.Create(new
            {
                email = "secret-invitee@example.test",
                roles = new[] { RoleNames.MediaEditor },
                token = "body-secret-token",
            }),
        };
        request.Headers.Add("X-Correlation-ID", "corr-antiforgery-denied");
        var response = await adminClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var audit = Assert.Single(
            await factory.ReadAuditEventsAsync(),
            entry => entry.Action == "identity.user.invite");
        Assert.Equal("denied", audit.Outcome);
        Assert.Equal(siteAdministrator.UserId, audit.ActorId);
        Assert.Equal("IdentityUser", audit.TargetType);
        Assert.Equal("request", audit.TargetId);
        Assert.Equal("corr-antiforgery-denied", audit.CorrelationId);
        Assert.NotEqual(default, audit.OccurredAtUtc);
        Assert.DoesNotContain("secret-invitee", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body-secret-token", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizationDeniedAuditBoundsUntrustedCorrelationAndTargetIdentifiers()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AuthorizationDeniedAuditBoundsUntrustedCorrelationAndTargetIdentifiers));
        await using var factory = new IdentityWebApplicationFactory(database);
        var ordinary = await factory.SeedUserAsync(
            "ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(ordinary.Email, ordinary.Password);
        var oversizedTarget = new string('t', 512);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/identity/users/{oversizedTarget}/disable");
        request.Headers.Add("X-Correlation-ID", new string('c', 512));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var audit = Assert.Single(
            await factory.ReadAuditEventsAsync(),
            entry => entry.Action == "identity.user.disable");
        Assert.Equal("denied", audit.Outcome);
        Assert.InRange(audit.CorrelationId.Length, 1, 128);
        Assert.StartsWith("sha256:", audit.TargetId, StringComparison.Ordinal);
        Assert.DoesNotContain(oversizedTarget, audit.TargetId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AntiforgeryDeniedAuditBoundsOversizedRouteTarget()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AntiforgeryDeniedAuditBoundsOversizedRouteTarget));
        await using var factory = new IdentityWebApplicationFactory(database);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));
        var oversizedTarget = new string('t', 512);
        var response = await client.PostAsJsonAsync(
            $"/admin/identity/users/{oversizedTarget}/disable",
            new { expectedConcurrencyStamp = "secret", reason = "secret" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var audit = Assert.Single(
            await factory.ReadAuditEventsAsync(),
            entry => entry.Action == "identity.user.disable");
        Assert.Equal("denied", audit.Outcome);
        Assert.StartsWith("sha256:", audit.TargetId, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", audit.DetailJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseRejectsAuditUpdateAndDeleteOutsideTheEfChangeTracker()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(DatabaseRejectsAuditUpdateAndDeleteOutsideTheEfChangeTracker));
        await using var factory = new IdentityWebApplicationFactory(database);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var adminClient = factory.CreateIdentityClient();
        await adminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));
        var response = await adminClient.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.AuditAndOperationalReports}");
        response.EnsureSuccessStatusCode();

        await using var context = database.CreateContext();
        var directInsertId = Guid.NewGuid();
        var inserted = await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO [IdentityAuditEvents]
                 ([Id], [ActorId], [RolesJson], [Action], [TargetType], [TargetId],
                  [Outcome], [CorrelationId], [OccurredAtUtc], [DetailJson])
             VALUES
                 ({directInsertId}, {null}, {"[]"}, {"identity.audit.direct_insert"},
                  {"AuditEvent"}, {directInsertId.ToString()}, {"allowed"},
                  {"corr-direct-insert"}, {DateTimeOffset.UtcNow}, {"{}"})
             """);
        Assert.Equal(1, inserted);
        Assert.Equal(
            1,
            await context.Set<AuditEvent>()
                .AsNoTracking()
                .CountAsync(entry => entry.Id == directInsertId));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE [IdentityAuditEvents] SET [DetailJson] = N'{{\"tampered\":true}}' WHERE [Id] = {directInsertId}"));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM [IdentityAuditEvents] WHERE [Id] = {directInsertId}"));
        Assert.Equal(
            1,
            await context.Set<AuditEvent>()
                .AsNoTracking()
                .CountAsync(entry => entry.Id == directInsertId));
    }

    [Fact]
    public async Task BrowserMutationsRequireAntiforgeryAndRoleChangesInvalidateExistingSessions()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(BrowserMutationsRequireAntiforgeryAndRoleChangesInvalidateExistingSessions));
        await using var factory = new IdentityWebApplicationFactory(database);
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

        using var adminClient = factory.CreateIdentityClient();
        using var contentClient = factory.CreateIdentityClient();
        await adminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));
        await contentClient.LoginAsync(
            contentEditor.Email,
            contentEditor.Password,
            IdentityHttpClientExtensions.CreateTotpCode(contentEditor.AuthenticatorKey!));

        var beforeChange = await contentClient.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.PagesAnnouncementsReligiousContent}");
        beforeChange.EnsureSuccessStatusCode();

        var missingAntiforgery = await adminClient.PostAsJsonAsync(
            "/admin/identity/users/invite",
            new
            {
                email = "invitee@example.test",
                roles = new[] { RoleNames.MediaEditor },
            });
        Assert.Equal(HttpStatusCode.BadRequest, missingAntiforgery.StatusCode);

        var token = await adminClient.GetAntiforgeryTokenAsync();
        var roleResponse = await adminClient.PutJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{contentEditor.UserId}/roles",
            new
            {
                expectedConcurrencyStamp = contentEditor.ConcurrencyStamp,
                roles = Array.Empty<string>(),
            },
            token);
        roleResponse.EnsureSuccessStatusCode();

        var afterChange = await contentClient.GetAsync(
            $"/admin/identity/capabilities/{AdministrativeCapability.PagesAnnouncementsReligiousContent}");
        Assert.Equal(HttpStatusCode.Unauthorized, afterChange.StatusCode);
    }

    [Fact]
    public async Task AuditEventsCaptureCorrelationRedactSensitiveDetailsAndRemainAppendOnly()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(AuditEventsCaptureCorrelationRedactSensitiveDetailsAndRemainAppendOnly));
        await using var factory = new IdentityWebApplicationFactory(database);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var target = await factory.SeedUserAsync(
            "target@example.test",
            "TargetUser!23456",
            [RoleNames.MediaEditor],
            enableMfa: true);

        using var adminClient = factory.CreateIdentityClient();
        await adminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));

        var token = await adminClient.GetAntiforgeryTokenAsync();
        var response = await adminClient.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{target.UserId}/disable",
            new
            {
                expectedConcurrencyStamp = target.ConcurrencyStamp,
                reason = "Bearer secret-token user@example.test 4111111111111111 token=abc",
            },
            token,
            correlationId: "corr-redaction");
        response.EnsureSuccessStatusCode();

        var auditEvents = await factory.ReadAuditEventsAsync();
        var disableAudit = Assert.Single(auditEvents, entry => entry.Action == "identity.user.disable");
        Assert.Equal("corr-redaction", disableAudit.CorrelationId);
        Assert.Equal("allowed", disableAudit.Outcome);
        Assert.DoesNotContain("secret-token", disableAudit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@example.test", disableAudit.DetailJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4111111111111111", disableAudit.DetailJson, StringComparison.OrdinalIgnoreCase);

        await using var context = database.CreateContext();
        var persisted = await context.Set<AuditEvent>()
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .FirstAsync();
        context.Entry(persisted).Property(entry => entry.DetailJson).CurrentValue = "{\"tampered\":true}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        context.ChangeTracker.Clear();
        var toDelete = await context.Set<AuditEvent>().OrderByDescending(entry => entry.OccurredAtUtc).FirstAsync();
        context.Remove(toDelete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task DeniedAuditsSurviveClientDisconnectAndAreNotDuplicated()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(DeniedAuditsSurviveClientDisconnectAndAreNotDuplicated));
        var coordinator = new DelayedAuditCoordinator();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton(coordinator);
                services.AddScoped<IAuditWriter>(provider => new DelayedAuditWriter(
                    new EfAuditWriter(
                        provider.GetRequiredService<HusayniaIdentityDbContext>(),
                        provider.GetRequiredService<ISensitiveDataRedactor>(),
                        provider.GetRequiredService<TimeProvider>()),
                    provider.GetRequiredService<DelayedAuditCoordinator>()));
            });
        var ordinary = await factory.SeedUserAsync(
            "disconnect-ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);
        var administrator = await factory.SeedUserAsync(
            "disconnect-admin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var auditor = await factory.SeedUserAsync(
            "disconnect-auditor@example.test",
            "ReadOnlyAuditor!234",
            [RoleNames.ReadOnlyAuditor],
            enableMfa: true);

        using (var ordinaryClient = factory.CreateIdentityClient())
        {
            await ordinaryClient.LoginAsync(ordinary.Email, ordinary.Password);
            var probe = coordinator.Register("identity.user.disable");
            using var cancellation = new CancellationTokenSource();
            var requestTask = ordinaryClient.PostAsync(
                $"/admin/identity/users/{Guid.NewGuid()}/disable",
                content: null,
                cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
            await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using (var auditorClient = factory.CreateIdentityClient())
        {
            await auditorClient.LoginAsync(
                auditor.Email,
                auditor.Password,
                IdentityHttpClientExtensions.CreateTotpCode(auditor.AuthenticatorKey!));
            var antiforgery = await auditorClient.GetAntiforgeryTokenAsync();
            var probe = coordinator.Register("identity.user.roles.set");
            using var cancellation = new CancellationTokenSource();
            var requestTask = auditorClient.PutJsonWithAntiforgeryAsync(
                $"/admin/identity/users/{Guid.NewGuid()}/roles",
                new { expectedConcurrencyStamp = "unused", roles = Array.Empty<string>() },
                antiforgery,
                cancellationToken: cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
            await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        using (var adminClient = factory.CreateIdentityClient())
        {
            await adminClient.LoginAsync(
                administrator.Email,
                administrator.Password,
                IdentityHttpClientExtensions.CreateTotpCode(administrator.AuthenticatorKey!));
            var probe = coordinator.Register("identity.user.invite");
            using var cancellation = new CancellationTokenSource();
            var requestTask = adminClient.PostAsJsonAsync(
                "/admin/identity/users/invite",
                new { email = "disconnect-invitee@example.test", roles = Array.Empty<string>() },
                cancellation.Token);
            await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
            await probe.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var audits = await factory.ReadAuditEventsAsync();
        Assert.Single(audits, entry => entry.Action == "identity.user.disable");
        Assert.Single(audits, entry => entry.Action == "identity.user.roles.set");
        Assert.Single(audits, entry => entry.Action == "identity.user.invite");
    }

    [Fact]
    public async Task DeniedAuditIsBoundedIndependentlyOfRequestAborted()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(DeniedAuditIsBoundedIndependentlyOfRequestAborted));
        var auditWriter = new TimeoutObservingAuditWriter();
        await using var factory = new IdentityWebApplicationFactory(
            database,
            configureServices: services =>
            {
                services.RemoveAll<IAuditWriter>();
                services.AddSingleton<IAuditWriter>(auditWriter);
            });
        var ordinary = await factory.SeedUserAsync(
            "bounded-audit-ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);

        using var client = factory.CreateIdentityClient();
        await client.LoginAsync(ordinary.Email, ordinary.Password);
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.GetAsync(
                $"/admin/identity/capabilities/{AdministrativeCapability.UsersRolesIntegrationsSettings}")
            .WaitAsync(TimeSpan.FromSeconds(8));
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("identity_audit_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Identity audit finalization failed", body, StringComparison.Ordinal);
        Assert.Equal(1, auditWriter.AttemptCount);
        Assert.True(auditWriter.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8));
    }

    private static void AssertPolicy(
        IReadOnlyCollection<RouteEndpoint> endpoints,
        string routePattern,
        string expectedPolicy)
    {
        var endpoint = Assert.Single(
            endpoints,
            candidate => candidate.RoutePattern.RawText == routePattern);
        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(metadata => metadata.Policy)
            .Where(policy => policy is not null)
            .ToArray();
        Assert.Single(policies);
        Assert.Equal(expectedPolicy, policies[0]);
    }

    private sealed class DelayedAuditCoordinator
    {
        private readonly ConcurrentDictionary<string, DelayedAuditProbe> probes =
            new(StringComparer.Ordinal);

        internal DelayedAuditProbe Register(string action)
        {
            var probe = new DelayedAuditProbe();
            Assert.True(probes.TryAdd(action, probe));
            return probe;
        }

        internal bool TryTake(string action, out DelayedAuditProbe probe) =>
            probes.TryRemove(action, out probe!);
    }

    private sealed class DelayedAuditProbe
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class DelayedAuditWriter(
        IAuditWriter inner,
        DelayedAuditCoordinator coordinator) : IAuditWriter
    {
        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            if (!coordinator.TryTake(descriptor.Action, out var probe))
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

    private sealed class TimeoutObservingAuditWriter : IAuditWriter
    {
        private int attemptCount;

        internal int AttemptCount => Volatile.Read(ref attemptCount);

        internal TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task AppendAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attemptCount);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
            }
        }
    }
}
