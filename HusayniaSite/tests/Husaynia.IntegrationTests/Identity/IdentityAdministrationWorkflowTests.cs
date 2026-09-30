using System.Net;
using System.Net.Http.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAdministrationWorkflowTests
{
    private static readonly string[] InvalidRoles = ["BadRole"];
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
    public async Task SiteAdministratorCanInviteAcceptManageRolesAndDisableWithOptimisticConcurrency()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(SiteAdministratorCanInviteAcceptManageRolesAndDisableWithOptimisticConcurrency));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
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

        var inviteToken = await adminClient.GetAntiforgeryTokenAsync();
        var inviteResponse = await adminClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/users/invite",
            new
            {
                email = "content@example.test",
                roles = new[] { RoleNames.ContentEditor },
            },
            inviteToken,
            correlationId: "corr-invite");
        inviteResponse.EnsureSuccessStatusCode();
        var inviteReceipt = await inviteResponse.Content.ReadFromJsonAsync<InviteIdentityUserReceipt>();
        Assert.NotNull(inviteReceipt);
        Assert.True(inviteReceipt!.WasCreated);
        Assert.Equal([RoleNames.ContentEditor], inviteReceipt.User.Roles);

        using var anonymousClient = factory.CreateIdentityClient();
        var acceptToken = await anonymousClient.GetAntiforgeryTokenAsync();
        var acceptResponse = await anonymousClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/invitations/accept",
            new
            {
                email = "content@example.test",
                token = inviteReceipt.InvitationToken,
                password = "InviteePassword!234",
            },
            acceptToken);
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<InvitationAcceptedResponseDto>();
        Assert.NotNull(accepted);
        Assert.Equal(inviteReceipt.User.UserId, accepted!.UserId);
        var acceptedView = await factory.ReadUserViewAsync(inviteReceipt.User.UserId);

        using var invitedClient = factory.CreateIdentityClient();
        var invitedLogin = await invitedClient.LoginAsync(
            "content@example.test",
            "InviteePassword!234");
        Assert.Equal("mfa_enrollment_required", invitedLogin.Status);
        Assert.True(invitedLogin.Session.RequiresMfaEnrollment);

        var rolesToken = await adminClient.GetAntiforgeryTokenAsync();
        var rolesResponse = await adminClient.PutJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{inviteReceipt.User.UserId}/roles",
            new
            {
                expectedConcurrencyStamp = acceptedView.ConcurrencyStamp,
                roles = new[] { RoleNames.ContentEditor, RoleNames.EventEditor },
            },
            rolesToken);
        rolesResponse.EnsureSuccessStatusCode();
        var rolesReceipt = await rolesResponse.Content.ReadFromJsonAsync<IdentityAccountMutationReceipt>();
        Assert.NotNull(rolesReceipt);
        Assert.Equal(
            [RoleNames.ContentEditor, RoleNames.EventEditor],
            rolesReceipt!.User.Roles);

        var staleRoleToken = await adminClient.GetAntiforgeryTokenAsync();
        var staleRoleResponse = await adminClient.PutJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{inviteReceipt.User.UserId}/roles",
            new
            {
                expectedConcurrencyStamp = inviteReceipt.User.ConcurrencyStamp,
                roles = new[] { RoleNames.MediaEditor },
            },
            staleRoleToken);
        Assert.Equal(HttpStatusCode.Conflict, staleRoleResponse.StatusCode);

        var staleDisableToken = await adminClient.GetAntiforgeryTokenAsync();
        var staleDisableResponse = await adminClient.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{inviteReceipt.User.UserId}/disable",
            new
            {
                expectedConcurrencyStamp = inviteReceipt.User.ConcurrencyStamp,
                reason = "stale disable attempt",
            },
            staleDisableToken);
        Assert.Equal(HttpStatusCode.Conflict, staleDisableResponse.StatusCode);

        var disableToken = await adminClient.GetAntiforgeryTokenAsync();
        var disableResponse = await adminClient.PostJsonWithAntiforgeryAsync(
            $"/admin/identity/users/{inviteReceipt.User.UserId}/disable",
            new
            {
                expectedConcurrencyStamp = rolesReceipt.User.ConcurrencyStamp,
                reason = "disable for test",
            },
            disableToken);
        disableResponse.EnsureSuccessStatusCode();
        var disableReceipt = await disableResponse.Content.ReadFromJsonAsync<IdentityAccountMutationReceipt>();
        Assert.NotNull(disableReceipt);
        Assert.True(disableReceipt!.User.IsDisabled);

        using var disabledClient = factory.CreateIdentityClient();
        var disabledToken = await disabledClient.GetAntiforgeryTokenAsync();
        var disabledLoginResponse = await disabledClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/login",
            new
            {
                email = "content@example.test",
                password = "InviteePassword!234",
            },
            disabledToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledLoginResponse.StatusCode);
        var disabledFailure = await disabledLoginResponse.ReadFailureAsync();
        Assert.Equal("invalid_credentials", disabledFailure!.Code);

        var workflowAudits = await factory.ReadAuditEventsAsync();
        Assert.Single(workflowAudits, entry => entry.Action == "identity.user.invite");
        Assert.Equal(2, workflowAudits.Count(entry => entry.Action == "identity.user.roles.set"));
        Assert.Equal(2, workflowAudits.Count(entry => entry.Action == "identity.user.disable"));
        Assert.Single(workflowAudits, entry => entry.Action == "identity.login");
    }

    [Fact]
    public async Task OrdinaryUserCannotReachAdministrationAndExactRoleValidationRejectsUnknownRoles()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(OrdinaryUserCannotReachAdministrationAndExactRoleValidationRejectsUnknownRoles));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);
        var ordinary = await factory.SeedUserAsync(
            "ordinary@example.test",
            "OrdinaryUser!234",
            [],
            enableMfa: false);

        using var ordinaryClient = factory.CreateIdentityClient();
        await ordinaryClient.LoginAsync(ordinary.Email, ordinary.Password);
        var ordinaryAdminResponse = await ordinaryClient.GetAsync("/admin/identity/audit/summary");
        Assert.Equal(HttpStatusCode.Forbidden, ordinaryAdminResponse.StatusCode);

        using var adminClient = factory.CreateIdentityClient();
        await adminClient.LoginAsync(
            siteAdministrator.Email,
            siteAdministrator.Password,
            IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!));
        var token = await adminClient.GetAntiforgeryTokenAsync();
        var response = await adminClient.PostJsonWithAntiforgeryAsync(
            "/admin/identity/users/invite",
            new
            {
                email = "badrole@example.test",
                roles = InvalidRoles,
            },
            token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var failure = await response.ReadFailureAsync();
        Assert.Equal("invalid_role", failure!.Code);

        var audits = await factory.ReadAuditEventsAsync();
        Assert.Single(audits, entry => entry.Action == "identity.audit.summary.read");
        Assert.Single(audits, entry => entry.Action == "identity.user.invite");
    }

    [Fact]
    public async Task ConcurrentCreateRoleAndDisableRequestsDoNotDuplicateOrLoseAccountState()
    {
        await using var database = await IdentitySqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentCreateRoleAndDisableRequestsDoNotDuplicateOrLoseAccountState));
        await using var factory = new IdentityWebApplicationFactory(database, TestConfiguration);
        var siteAdministrator = await factory.SeedUserAsync(
            "siteadmin@example.test",
            "SiteAdmin!23456",
            [RoleNames.SiteAdministrator],
            enableMfa: true);

        using var firstClient = factory.CreateIdentityClient();
        using var secondClient = factory.CreateIdentityClient();
        var mfaCode = IdentityHttpClientExtensions.CreateTotpCode(siteAdministrator.AuthenticatorKey!);
        await firstClient.LoginAsync(siteAdministrator.Email, siteAdministrator.Password, mfaCode);
        await secondClient.LoginAsync(siteAdministrator.Email, siteAdministrator.Password, mfaCode);

        const int inviteAttempts = 5;
        for (var attempt = 0; attempt < inviteAttempts; attempt++)
        {
            var firstInviteToken = await firstClient.GetAntiforgeryTokenAsync();
            var secondInviteToken = await secondClient.GetAntiforgeryTokenAsync();
            var email = $"concurrent-{attempt}@example.test";
            var inviteResponses = await Task.WhenAll(
                firstClient.PostJsonWithAntiforgeryAsync(
                    "/admin/identity/users/invite",
                    new
                    {
                        email,
                        roles = new[] { RoleNames.ContentEditor },
                    },
                    firstInviteToken,
                    correlationId: $"corr-invite-{attempt}-first"),
                secondClient.PostJsonWithAntiforgeryAsync(
                    "/admin/identity/users/invite",
                    new
                    {
                        email = email.ToUpperInvariant(),
                        roles = new[] { RoleNames.ContentEditor },
                    },
                    secondInviteToken,
                    correlationId: $"corr-invite-{attempt}-second"));

            Assert.Equal(1, inviteResponses.Count(response => response.IsSuccessStatusCode));
            var conflict = Assert.Single(
                inviteResponses,
                response => response.StatusCode == HttpStatusCode.Conflict);
            var failure = await conflict.ReadFailureAsync();
            Assert.NotNull(failure);
            Assert.Equal("duplicate_account", failure!.Code);

            await using var context = database.CreateContext();
            var normalizedEmail = email.ToUpperInvariant();
            Assert.Equal(
                1,
                await context.Set<HusayniaIdentityUser>()
                    .CountAsync(user => user.NormalizedEmail == normalizedEmail));
        }

        var inviteAudits = (await factory.ReadAuditEventsAsync())
            .Where(entry => entry.Action == "identity.user.invite")
            .ToArray();
        Assert.Equal(inviteAttempts * 2, inviteAudits.Length);
        Assert.Equal(
            inviteAttempts,
            inviteAudits.Count(entry => entry.DetailJson.Contains(
                "\"errorCode\":\"duplicate_account\"",
                StringComparison.Ordinal)));

        var invited = await factory.FindUserAsync("concurrent-0@example.test");
        Assert.NotNull(invited);
        var initialStamp = invited!.ConcurrencyStamp!;
        var firstRolesToken = await firstClient.GetAntiforgeryTokenAsync();
        var secondRolesToken = await secondClient.GetAntiforgeryTokenAsync();
        var roleResponses = await Task.WhenAll(
            firstClient.PutJsonWithAntiforgeryAsync(
                $"/admin/identity/users/{invited.Id}/roles",
                new
                {
                    expectedConcurrencyStamp = initialStamp,
                    roles = new[] { RoleNames.EventEditor },
                },
                firstRolesToken),
            secondClient.PutJsonWithAntiforgeryAsync(
                $"/admin/identity/users/{invited.Id}/roles",
                new
                {
                    expectedConcurrencyStamp = initialStamp,
                    roles = new[] { RoleNames.MediaEditor },
                },
                secondRolesToken));
        Assert.Equal(1, roleResponses.Count(response => response.IsSuccessStatusCode));
        Assert.Equal(1, roleResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict));

        var afterRoles = await factory.ReadUserViewAsync(invited.Id.ToString());
        Assert.True(
            afterRoles.Roles.SequenceEqual([RoleNames.EventEditor], StringComparer.Ordinal) ||
            afterRoles.Roles.SequenceEqual([RoleNames.MediaEditor], StringComparer.Ordinal));

        var firstDisableToken = await firstClient.GetAntiforgeryTokenAsync();
        var secondDisableToken = await secondClient.GetAntiforgeryTokenAsync();
        var disableResponses = await Task.WhenAll(
            firstClient.PostJsonWithAntiforgeryAsync(
                $"/admin/identity/users/{invited.Id}/disable",
                new
                {
                    expectedConcurrencyStamp = afterRoles.ConcurrencyStamp,
                    reason = "concurrent disable",
                },
                firstDisableToken),
            secondClient.PostJsonWithAntiforgeryAsync(
                $"/admin/identity/users/{invited.Id}/disable",
                new
                {
                    expectedConcurrencyStamp = afterRoles.ConcurrencyStamp,
                    reason = "concurrent disable",
                },
                secondDisableToken));
        Assert.Contains(disableResponses, response => response.IsSuccessStatusCode);
        Assert.All(
            disableResponses,
            response => Assert.Contains(
                response.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        var disabled = await factory.ReadUserViewAsync(invited.Id.ToString());
        Assert.True(disabled.IsDisabled);
    }

}
