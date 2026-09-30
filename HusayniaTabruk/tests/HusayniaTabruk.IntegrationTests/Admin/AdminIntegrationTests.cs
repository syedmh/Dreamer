using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.IntegrationTests.Auth;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HusayniaTabruk.IntegrationTests.Admin;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Admin")]
public sealed class AdminIntegrationTests : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private const string InvitationStepUpPurpose = "membership.invite";
    private const string DisableMembershipStepUpPurpose = "membership.disable";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [RequiresPostgresFact]
    public async Task AdminMemberListRequiresAuthenticationAndForbidsActiveNonAdministrators()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage anonymous = await GetAsync(host.Client, "/api/v1/admin/members");
        ProblemContract anonymousProblem = await ReadRequiredAsync<ProblemContract>(
            anonymous,
            HttpStatusCode.Unauthorized);

        TokenSetResponse memberLogin = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        using HttpResponseMessage forbidden = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            memberLogin.AccessToken);
        ProblemContract forbiddenProblem = await ReadRequiredAsync<ProblemContract>(
            forbidden,
            HttpStatusCode.Forbidden);

        Assert.Equal("unauthorized", anonymousProblem.Code);
        Assert.Equal("forbidden", forbiddenProblem.Code);
    }

    [RequiresPostgresFact]
    public async Task AdminMemberListReturnsPendingRoleRequestsAndDoesNotExposePrivateIdentifiers()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse approver = await LoginAsync(
            host,
            "admin@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage initialList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long initialVersion = GetEtagVersion(initialList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(initialList, HttpStatusCode.OK);

        StepUpResponse proposerStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage proposalResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/admin-role-requests",
            new
            {
                targetMembershipId = seed.MemberMembershipId.ToString(),
                action = "grant",
                reason = "Promote the target.",
            },
            manager.AccessToken,
            ifMatch: initialVersion,
            stepUpToken: proposerStepUp.StepUpToken);
        AdminRoleRequestContract proposed = await ReadRequiredAsync<AdminRoleRequestContract>(
            proposalResponse,
            HttpStatusCode.OK);

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            approver.AccessToken);
        string json = await listResponse.Content.ReadAsStringAsync();
        AdminMemberPageContract page = Deserialize<AdminMemberPageContract>(json);

        Assert.Equal(1, GetEtagVersion(listResponse));
        AdminMemberContract target = Assert.Single(page.Items, item => item.Id == seed.MemberMembershipId.ToString());
        AdminRoleRequestContract pending = Assert.Single(target.PendingAdministratorRoleRequests);
        Assert.Equal(proposed.Id, pending.Id);
        Assert.Equal("grant", pending.Action);
        Assert.Equal(seed.ManagerMembershipId.ToString(), pending.ProposerMembershipId);
        Assert.DoesNotContain("\"email\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"userId\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"organizationId\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"normalizedEmail\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuanceCreatesInvitedMembershipAndReturnsOnlyAHashedStoredToken()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        StepUpResponse invitationStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "invitee@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: invitationStepUp.StepUpToken);
        IssueInvitationContract issued = await ReadRequiredAsync<IssueInvitationContract>(
            response,
            HttpStatusCode.OK);
        string token = GetQueryParameter(issued.InviteUrl, "token");

        Assert.Null(response.Headers.Location);
        Assert.Equal(clock.UtcNow.AddHours(24), issued.ExpiresAt);
        Assert.Matches("^[A-Za-z0-9_-]{64}$", token);

        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Email == "invitee@example.test");
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.UserId == user.Id);
        InvitationEntity invitation = await context.Invitations.SingleAsync(
            candidate => candidate.NormalizedEmail == "INVITEE@EXAMPLE.TEST");

        Assert.Equal((short)Domain.Common.Enums.MembershipStatus.Invited, membership.Status);
        Assert.Equal(Hash(token), invitation.TokenHash);
        Assert.DoesNotContain(token, invitation.TokenHash, StringComparison.Ordinal);
        Assert.NotEqual("INVITEE@EXAMPLE.TEST", user.NormalizedUserName);
        Assert.True(string.IsNullOrWhiteSpace(user.NormalizedEmail));
        Assert.False(user.EmailConfirmed);
        Assert.True(string.IsNullOrWhiteSpace(user.PasswordHash));
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuanceIsSingleUseAndReturnsStableConflictCode()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        StepUpResponse firstStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);

        using HttpResponseMessage firstResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "invite-once@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: firstStepUp.StepUpToken);
        _ = await ReadRequiredAsync<IssueInvitationContract>(firstResponse, HttpStatusCode.OK);

        StepUpResponse secondStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage secondResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "invite-once@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: secondStepUp.StepUpToken);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            secondResponse,
            HttpStatusCode.Conflict);

        Assert.Equal("invitation_conflict", problem.Code);

        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Email == "invite-once@example.test");
        Assert.Equal(1, await context.Memberships.CountAsync(candidate => candidate.UserId == user.Id));
        Assert.Equal(
            1,
            await context.Invitations.CountAsync(
                candidate => candidate.NormalizedEmail == "INVITE-ONCE@EXAMPLE.TEST"));
    }

    [RequiresPostgresFact]
    public async Task ExpiredInvitationCanBeReissuedForTheSameOrganizationAndEmail()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        StepUpResponse firstStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage firstResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "reissued@example.test",
                expiresInHours = 1,
            },
            manager.AccessToken,
            stepUpToken: firstStepUp.StepUpToken);
        _ = await ReadRequiredAsync<IssueInvitationContract>(firstResponse, HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromHours(1));
        manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        StepUpResponse secondStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage secondResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "reissued@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: secondStepUp.StepUpToken);
        _ = await ReadRequiredAsync<IssueInvitationContract>(secondResponse, HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            2,
            await context.Invitations.CountAsync(
                candidate => candidate.NormalizedEmail == "REISSUED@EXAMPLE.TEST"));
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuedByAdministratorsCanBeAcceptedAndFinalizesThePlaceholderIdentity()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        StepUpResponse invitationStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);

        using HttpResponseMessage inviteResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "accepted-invite@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: invitationStepUp.StepUpToken);
        IssueInvitationContract issued = await ReadRequiredAsync<IssueInvitationContract>(
            inviteResponse,
            HttpStatusCode.OK);
        string token = GetQueryParameter(issued.InviteUrl, "token");
        Guid installationId = Guid.CreateVersion7();

        using HttpResponseMessage acceptResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/invitations/accept",
            new
            {
                token,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: installationId);
        _ = await ReadRequiredAsync<TokenSetResponse>(acceptResponse, HttpStatusCode.OK);

        using HttpResponseMessage loginResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/login",
            new
            {
                email = "accepted-invite@example.test",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        _ = await ReadRequiredAsync<TokenSetResponse>(loginResponse, HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Email == "accepted-invite@example.test");
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.UserId == user.Id);

        Assert.Equal("accepted-invite@example.test", user.UserName);
        Assert.Equal("ACCEPTED-INVITE@EXAMPLE.TEST", user.NormalizedUserName);
        Assert.Equal("ACCEPTED-INVITE@EXAMPLE.TEST", user.NormalizedEmail);
        Assert.Equal((short)Domain.Common.Enums.MembershipStatus.Active, membership.Status);
        Assert.Equal("Accepted Member", membership.DisplayName);
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuanceRequiresPurposeBoundSingleUseStepUpAndUsesTrustedConfiguredBaseUrl()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configurationOverrides:
            [
                new KeyValuePair<string, string?>(
                    "TabrukAuth:InvitationBaseUrl",
                    "https://trusted.example/admin"),
            ]);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage missingStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "missing-step-up@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            hostHeader: "evil.example");
        ProblemContract missingStepUpProblem = await ReadRequiredAsync<ProblemContract>(
            missingStepUp,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_invalid", missingStepUpProblem.Code);

        StepUpResponse wrongPurpose = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage wrongPurposeResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "wrong-purpose@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: wrongPurpose.StepUpToken,
            hostHeader: "evil.example");
        ProblemContract wrongPurposeProblem = await ReadRequiredAsync<ProblemContract>(
            wrongPurposeResponse,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_purpose_mismatch", wrongPurposeProblem.Code);

        StepUpResponse invitationStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage issuedResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "trusted-url@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: invitationStepUp.StepUpToken,
            hostHeader: "evil.example");
        IssueInvitationContract issued = await ReadRequiredAsync<IssueInvitationContract>(
            issuedResponse,
            HttpStatusCode.OK);

        Assert.StartsWith(
            "https://trusted.example/admin/api/v1/auth/invitations/accept?token=",
            issued.InviteUrl,
            StringComparison.Ordinal);

        using HttpResponseMessage replayedToken = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "replayed-token@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: invitationStepUp.StepUpToken,
            hostHeader: "evil.example");
        ProblemContract replayedTokenProblem = await ReadRequiredAsync<ProblemContract>(
            replayedToken,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_invalid", replayedTokenProblem.Code);
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuanceScopesConflictsToOrganizationAndDoesNotReserveCrossTenantIdentity()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        SecondOrganizationSeed secondOrganization = await AddSecondOrganizationAsync(
            database,
            seed.Now,
            "second-manager@example.test",
            "second-admin@example.test",
            "shared-active@example.test");
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse firstOrganizationManager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse secondOrganizationManager = await LoginAsync(
            host,
            secondOrganization.PrimaryAdminEmail,
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse existingCrossTenantMember = await LoginAsync(
            host,
            secondOrganization.SharedMemberEmail!,
            SharedPassword,
            Guid.CreateVersion7());

        StepUpResponse existingAccountInviteStepUp = await IssueStepUpAsync(
            host,
            firstOrganizationManager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage existingAccountInviteResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = secondOrganization.SharedMemberEmail,
                expiresInHours = 24,
            },
            firstOrganizationManager.AccessToken,
            stepUpToken: existingAccountInviteStepUp.StepUpToken);
        IssueInvitationContract existingAccountInvite = await ReadRequiredAsync<IssueInvitationContract>(
            existingAccountInviteResponse,
            HttpStatusCode.OK);

        StepUpResponse firstOrganizationSharedInviteStepUp = await IssueStepUpAsync(
            host,
            firstOrganizationManager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage firstOrganizationSharedInviteResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "shared-pending@example.test",
                expiresInHours = 24,
            },
            firstOrganizationManager.AccessToken,
            stepUpToken: firstOrganizationSharedInviteStepUp.StepUpToken);
        IssueInvitationContract firstOrganizationSharedInvite = await ReadRequiredAsync<IssueInvitationContract>(
            firstOrganizationSharedInviteResponse,
            HttpStatusCode.OK);

        StepUpResponse secondOrganizationSharedInviteStepUp = await IssueStepUpAsync(
            host,
            secondOrganizationManager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage secondOrganizationSharedInviteResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "shared-pending@example.test",
                expiresInHours = 24,
            },
            secondOrganizationManager.AccessToken,
            stepUpToken: secondOrganizationSharedInviteStepUp.StepUpToken);
        IssueInvitationContract secondOrganizationSharedInvite = await ReadRequiredAsync<IssueInvitationContract>(
            secondOrganizationSharedInviteResponse,
            HttpStatusCode.OK);

        using HttpResponseMessage existingMemberMe = await GetAsync(
            host.Client,
            "/api/v1/me",
            existingCrossTenantMember.AccessToken);
        _ = await ReadRequiredAsync<MeResponse>(existingMemberMe, HttpStatusCode.OK);

        Assert.NotEqual(
            GetQueryParameter(firstOrganizationSharedInvite.InviteUrl, "token"),
            GetQueryParameter(secondOrganizationSharedInvite.InviteUrl, "token"));
        Assert.Matches(
            "^https://",
            existingAccountInvite.InviteUrl);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            1,
            await context.Invitations.CountAsync(
                invitation =>
                    invitation.OrganizationId == seed.OrganizationId.Value
                    && invitation.NormalizedEmail == "SHARED-ACTIVE@EXAMPLE.TEST"));
        Assert.Equal(
            1,
            await context.Invitations.CountAsync(
                invitation =>
                    invitation.OrganizationId == seed.OrganizationId.Value
                    && invitation.NormalizedEmail == "SHARED-PENDING@EXAMPLE.TEST"));
        Assert.Equal(
            1,
            await context.Invitations.CountAsync(
                invitation =>
                    invitation.OrganizationId == secondOrganization.OrganizationId.Value
                    && invitation.NormalizedEmail == "SHARED-PENDING@EXAMPLE.TEST"));
    }

    [RequiresPostgresFact]
    public async Task InvitationIssuanceAdvancesAdminMembersEtag()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        _ = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 19, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage initialListResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long initialVersion = GetEtagVersion(initialListResponse);
        AdminMemberPageContract initialPage = await ReadRequiredAsync<AdminMemberPageContract>(
            initialListResponse,
            HttpStatusCode.OK);

        StepUpResponse invitationStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage inviteResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/invitations",
            new
            {
                email = "etag-change@example.test",
                expiresInHours = 24,
            },
            manager.AccessToken,
            stepUpToken: invitationStepUp.StepUpToken);
        _ = await ReadRequiredAsync<IssueInvitationContract>(inviteResponse, HttpStatusCode.OK);

        using HttpResponseMessage updatedListResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long updatedVersion = GetEtagVersion(updatedListResponse);
        AdminMemberPageContract updatedPage = await ReadRequiredAsync<AdminMemberPageContract>(
            updatedListResponse,
            HttpStatusCode.OK);

        Assert.Equal(initialVersion + 1, updatedVersion);
        Assert.Equal(initialPage.Items.Count + 1, updatedPage.Items.Count);
    }

    [RequiresPostgresFact]
    public async Task FoodInchargeAssignmentAndRevocationRequireStepUpSingleUseAndImmediatelyAffectMe()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse member = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage initialList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long initialVersion = GetEtagVersion(initialList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(initialList, HttpStatusCode.OK);

        using HttpResponseMessage missingStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Grant Food Incharge." },
            manager.AccessToken,
            ifMatch: initialVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, missingStepUp.StatusCode);

        StepUpResponse firstStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage assignResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Grant Food Incharge." },
            manager.AccessToken,
            ifMatch: initialVersion,
            stepUpToken: firstStepUp.StepUpToken);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);
        long assignedVersion = GetEtagVersion(assignResponse);

        using HttpResponseMessage meAfterAssign = await GetAsync(
            host.Client,
            "/api/v1/me",
            member.AccessToken);
        MeResponse assignedMe = await ReadRequiredAsync<MeResponse>(meAfterAssign, HttpStatusCode.OK);
        Assert.Contains("FoodIncharge", assignedMe.Roles);

        using HttpResponseMessage replayedStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Delete,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Revoke Food Incharge." },
            manager.AccessToken,
            ifMatch: assignedVersion,
            stepUpToken: firstStepUp.StepUpToken);
        ProblemContract replayProblem = await ReadRequiredAsync<ProblemContract>(
            replayedStepUp,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_invalid", replayProblem.Code);

        StepUpResponse secondStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage revokeResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Delete,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Revoke Food Incharge." },
            manager.AccessToken,
            ifMatch: assignedVersion,
            stepUpToken: secondStepUp.StepUpToken);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        using HttpResponseMessage meAfterRevoke = await GetAsync(
            host.Client,
            "/api/v1/me",
            member.AccessToken);
        MeResponse revokedMe = await ReadRequiredAsync<MeResponse>(meAfterRevoke, HttpStatusCode.OK);
        Assert.DoesNotContain("FoodIncharge", revokedMe.Roles);
    }

    [RequiresPostgresFact]
    public async Task FoodInchargeAssignmentEnforcesReasonBoundaryBeforeConsumingStepUp()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long version = GetEtagVersion(listResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(listResponse, HttpStatusCode.OK);

        StepUpResponse stepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        string oversizedReason = new('r', ApplicationLimits.MaximumReasonUnicodeScalars + 1);
        using HttpResponseMessage invalidResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = oversizedReason },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: stepUp.StepUpToken);
        ProblemContract invalidProblem = await ReadRequiredAsync<ProblemContract>(
            invalidResponse,
            HttpStatusCode.BadRequest);

        Assert.Equal("invalid_admin_input", invalidProblem.Code);

        using HttpResponseMessage validResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = new string('r', ApplicationLimits.MaximumReasonUnicodeScalars) },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: stepUp.StepUpToken);

        Assert.Equal(HttpStatusCode.NoContent, validResponse.StatusCode);
        Assert.Equal(version + 1, GetEtagVersion(validResponse));

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            1,
            await context.RoleAssignments.CountAsync(
                assignment => assignment.MembershipId == seed.MemberMembershipId.Value
                    && assignment.Role == (short)Domain.Common.Enums.OrganizationRole.FoodIncharge
                    && assignment.RevokedAt == null));
    }

    [RequiresPostgresFact]
    public async Task FoodInchargeAssignmentRejectsExpiredStepUpWithoutPersistingChanges()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long version = GetEtagVersion(listResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(listResponse, HttpStatusCode.OK);

        StepUpResponse stepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        clock.Advance(TimeSpan.FromMinutes(5));

        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Grant Food Incharge." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: stepUp.StepUpToken);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            response,
            HttpStatusCode.Unauthorized);

        Assert.Equal("step_up_invalid", problem.Code);

        await using TabrukDbContext context = database.CreateContext();
        Assert.Equal(
            0,
            await context.RoleAssignments.CountAsync(
                assignment => assignment.MembershipId == seed.MemberMembershipId.Value
                    && assignment.Role == (short)Domain.Common.Enums.OrganizationRole.FoodIncharge
                    && assignment.RevokedAt == null));
        Assert.Equal(0, await context.Notifications.CountAsync());
        Assert.Equal(0, await context.AuditEvents.CountAsync());
        Assert.Equal(0, await context.OutboxMessages.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task AdministratorRoleApprovalRevalidatesThatTheStoredProposerRemainsActive()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        AdditionalAdministratorSeed thirdAdmin = await AddThirdAdministratorAsync(database, seed);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse approver = await LoginAsync(
            host,
            "admin@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse disabler = await LoginAsync(
            host,
            thirdAdmin.Email,
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse member = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage initialList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long initialVersion = GetEtagVersion(initialList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(initialList, HttpStatusCode.OK);

        StepUpResponse proposerStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage proposalResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/admin/admin-role-requests",
            new
            {
                targetMembershipId = seed.MemberMembershipId.ToString(),
                action = "grant",
                reason = "Promote the target.",
            },
            manager.AccessToken,
            ifMatch: initialVersion,
            stepUpToken: proposerStepUp.StepUpToken);
        AdminRoleRequestContract request = await ReadRequiredAsync<AdminRoleRequestContract>(
            proposalResponse,
            HttpStatusCode.OK);

        using HttpResponseMessage disablerList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            disabler.AccessToken);
        long disableVersion = GetEtagVersion(disablerList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(disablerList, HttpStatusCode.OK);
        StepUpResponse disableStepUp = await IssueStepUpAsync(
            host,
            disabler.AccessToken,
            DisableMembershipStepUpPurpose);
        using HttpResponseMessage disableResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{seed.ManagerMembershipId}/disable",
            new { reason = "Disable the proposer." },
            disabler.AccessToken,
            ifMatch: disableVersion,
            stepUpToken: disableStepUp.StepUpToken);
        _ = await ReadRequiredAsync<AdminMemberContract>(disableResponse, HttpStatusCode.OK);

        using HttpResponseMessage approverList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            approver.AccessToken);
        long approvalVersion = GetEtagVersion(approverList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(approverList, HttpStatusCode.OK);
        StepUpResponse approvalStepUp = await IssueStepUpAsync(
            host,
            approver.AccessToken,
            "governance.approve");
        using HttpResponseMessage approvalResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/admin-role-requests/{request.Id}/approve",
            new { reason = "Second approval." },
            approver.AccessToken,
            ifMatch: approvalVersion,
            stepUpToken: approvalStepUp.StepUpToken);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            approvalResponse,
            HttpStatusCode.Conflict);

        Assert.Equal("active_membership_required", problem.Code);

        using HttpResponseMessage memberMe = await GetAsync(host.Client, "/api/v1/me", member.AccessToken);
        MeResponse me = await ReadRequiredAsync<MeResponse>(memberMe, HttpStatusCode.OK);
        Assert.DoesNotContain("Admin", me.Roles);
    }

    [RequiresPostgresFact]
    public async Task AdminMutationEndpointsReturnStableNotFoundProblemCodes()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long version = GetEtagVersion(listResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(listResponse, HttpStatusCode.OK);

        StepUpResponse assignStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage missingMemberResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{MembershipId.New()}/food-incharge",
            new { reason = "Grant Food Incharge." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: assignStepUp.StepUpToken);
        ProblemContract missingMember = await ReadRequiredAsync<ProblemContract>(
            missingMemberResponse,
            HttpStatusCode.NotFound);

        StepUpResponse approveStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.approve");
        using HttpResponseMessage missingRequestResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/admin-role-requests/{RoleChangeRequestId.New()}/approve",
            new { reason = "Approve request." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: approveStepUp.StepUpToken);
        ProblemContract missingRequest = await ReadRequiredAsync<ProblemContract>(
            missingRequestResponse,
            HttpStatusCode.NotFound);

        Assert.Equal("member_not_found", missingMember.Code);
        Assert.Equal("role_change_request_not_found", missingRequest.Code);
    }

    [RequiresPostgresFact]
    public async Task StaleIfMatchReturns412WithoutExtraSideEffectsAndLeavesStepUpReusable()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse secondAdmin = await LoginAsync(
            host,
            "admin@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage managerList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long staleVersion = GetEtagVersion(managerList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(managerList, HttpStatusCode.OK);

        using HttpResponseMessage secondAdminList = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            secondAdmin.AccessToken);
        long currentVersion = GetEtagVersion(secondAdminList);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(secondAdminList, HttpStatusCode.OK);

        StepUpResponse secondAdminStepUp = await IssueStepUpAsync(
            host,
            secondAdmin.AccessToken,
            "governance.assign");
        using HttpResponseMessage assignResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Grant Food Incharge." },
            secondAdmin.AccessToken,
            ifMatch: currentVersion,
            stepUpToken: secondAdminStepUp.StepUpToken);
        Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);
        long updatedVersion = GetEtagVersion(assignResponse);

        StepUpResponse managerStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            "governance.assign");
        using HttpResponseMessage staleResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Put,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Grant Food Incharge." },
            manager.AccessToken,
            ifMatch: staleVersion,
            stepUpToken: managerStepUp.StepUpToken);
        ProblemContract staleProblem = await ReadRequiredAsync<ProblemContract>(
            staleResponse,
            HttpStatusCode.PreconditionFailed);
        Assert.Equal("stale_version", staleProblem.Code);

        await using (TabrukDbContext context = database.CreateContext())
        {
            Assert.Equal(
                1,
                await context.RoleAssignments.CountAsync(
                    assignment => assignment.MembershipId == seed.MemberMembershipId.Value
                        && assignment.Role == (short)Domain.Common.Enums.OrganizationRole.FoodIncharge
                        && assignment.RevokedAt == null));
            Assert.Equal(1, await context.Notifications.CountAsync());
            Assert.Equal(1, await context.AuditEvents.CountAsync());
            Assert.Equal(1, await context.OutboxMessages.CountAsync());
        }

        using HttpResponseMessage reusableStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Delete,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/food-incharge",
            new { reason = "Revoke Food Incharge." },
            manager.AccessToken,
            ifMatch: updatedVersion,
            stepUpToken: managerStepUp.StepUpToken);
        Assert.Equal(HttpStatusCode.NoContent, reusableStepUp.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task DisableMembershipImmediatelyRevokesExistingAccessToken()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse member = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long version = GetEtagVersion(listResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(listResponse, HttpStatusCode.OK);

        StepUpResponse disableStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            DisableMembershipStepUpPurpose);
        using HttpResponseMessage disableResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/disable",
            new { reason = "Disable the member." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: disableStepUp.StepUpToken);
        AdminMemberContract disabled = await ReadRequiredAsync<AdminMemberContract>(
            disableResponse,
            HttpStatusCode.OK);
        Assert.Equal("disabled", disabled.Status);

        using HttpResponseMessage revoked = await GetAsync(host.Client, "/api/v1/me", member.AccessToken);
        ProblemContract problem = await ReadRequiredAsync<ProblemContract>(
            revoked,
            HttpStatusCode.Unauthorized);
        Assert.Equal("unauthorized", problem.Code);
    }

    [RequiresPostgresFact]
    public async Task DisableMembershipRequiresPurposeBoundSingleUseStepUp()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        AdditionalMemberSeed additionalMember = await AddMemberAsync(
            database,
            seed.OrganizationId,
            "other-member@example.test",
            "Other member");
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse manager = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());

        using HttpResponseMessage listResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long version = GetEtagVersion(listResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(listResponse, HttpStatusCode.OK);

        using HttpResponseMessage missingStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/disable",
            new { reason = "Disable the member." },
            manager.AccessToken,
            ifMatch: version);
        ProblemContract missingStepUpProblem = await ReadRequiredAsync<ProblemContract>(
            missingStepUp,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_invalid", missingStepUpProblem.Code);

        StepUpResponse wrongPurpose = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            InvitationStepUpPurpose);
        using HttpResponseMessage wrongPurposeResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/disable",
            new { reason = "Disable the member." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: wrongPurpose.StepUpToken);
        ProblemContract wrongPurposeProblem = await ReadRequiredAsync<ProblemContract>(
            wrongPurposeResponse,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_purpose_mismatch", wrongPurposeProblem.Code);

        StepUpResponse disableStepUp = await IssueStepUpAsync(
            host,
            manager.AccessToken,
            DisableMembershipStepUpPurpose);
        using HttpResponseMessage disableResponse = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{seed.MemberMembershipId}/disable",
            new { reason = "Disable the member." },
            manager.AccessToken,
            ifMatch: version,
            stepUpToken: disableStepUp.StepUpToken);
        AdminMemberContract disabled = await ReadRequiredAsync<AdminMemberContract>(
            disableResponse,
            HttpStatusCode.OK);
        Assert.Equal("disabled", disabled.Status);

        using HttpResponseMessage updatedListResponse = await GetAsync(
            host.Client,
            "/api/v1/admin/members",
            manager.AccessToken);
        long updatedVersion = GetEtagVersion(updatedListResponse);
        _ = await ReadRequiredAsync<AdminMemberPageContract>(updatedListResponse, HttpStatusCode.OK);

        using HttpResponseMessage replayedStepUp = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            $"/api/v1/admin/members/{additionalMember.MembershipId}/disable",
            new { reason = "Disable the other member." },
            manager.AccessToken,
            ifMatch: updatedVersion,
            stepUpToken: disableStepUp.StepUpToken);
        ProblemContract replayedStepUpProblem = await ReadRequiredAsync<ProblemContract>(
            replayedStepUp,
            HttpStatusCode.Unauthorized);
        Assert.Equal("step_up_invalid", replayedStepUpProblem.Code);
    }

    private static async Task<PersistenceSeed> CreateSeedWithPasswordsAsync(PostgresTestDatabase database)
    {
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        await SeedPasswordAsync(database, "manager@example.test", SharedPassword);
        await SeedPasswordAsync(database, "admin@example.test", SharedPassword);
        await SeedPasswordAsync(database, "member@example.test", SharedPassword);
        return seed;
    }

    private static async Task SeedPasswordAsync(
        PostgresTestDatabase database,
        string email,
        string password)
    {
        string normalizedEmail = email.ToUpperInvariant();
        await using TabrukDbContext context = database.CreateContext();
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.NormalizedEmail == normalizedEmail);
        PasswordHasher<TabrukIdentityUser> hasher = new();
        user.PasswordHash = hasher.HashPassword(user, password);
        user.EmailConfirmed = true;
        await context.SaveChangesAsync();
    }

    private static async Task<AdditionalAdministratorSeed> AddThirdAdministratorAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed)
    {
        UserId userId = UserId.New();
        MembershipId membershipId = MembershipId.New();
        const string email = "third-admin@example.test";
        await using TabrukDbContext context = database.CreateContext();
        context.Users.Add(
            new TabrukIdentityUser
            {
                Id = userId.Value,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            });
        context.Memberships.Add(
            new MembershipEntity
            {
                Id = membershipId.Value,
                OrganizationId = seed.OrganizationId.Value,
                UserId = userId.Value,
                DisplayName = "Third administrator",
                Status = (short)Domain.Common.Enums.MembershipStatus.Active,
                EligibleAsNamedParticipant = true,
            });
        context.RoleAssignments.Add(
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = seed.OrganizationId.Value,
                MembershipId = membershipId.Value,
                Role = (short)Domain.Common.Enums.OrganizationRole.Admin,
                AssignedByMembershipId = seed.ManagerMembershipId.Value,
                AssignedAt = seed.Now,
            });
        await context.SaveChangesAsync();
        await SeedPasswordAsync(database, email, SharedPassword);
        return new AdditionalAdministratorSeed(userId, membershipId, email);
    }

    private static async Task<SecondOrganizationSeed> AddSecondOrganizationAsync(
        PostgresTestDatabase database,
        DateTimeOffset now,
        string primaryAdminEmail,
        string secondAdminEmail,
        string? sharedMemberEmail = null)
    {
        OrganizationId organizationId = OrganizationId.New();
        UserId primaryAdminUserId = UserId.New();
        UserId secondAdminUserId = UserId.New();
        MembershipId primaryAdminMembershipId = MembershipId.New();
        MembershipId secondAdminMembershipId = MembershipId.New();
        UserId? sharedMemberUserId = sharedMemberEmail is null ? null : UserId.New();
        MembershipId? sharedMemberMembershipId = sharedMemberEmail is null ? null : MembershipId.New();

        await using TabrukDbContext context = database.CreateContext();
        context.Organizations.Add(
            new OrganizationEntity
            {
                Id = organizationId.Value,
                Name = "Second organization",
                TimeZone = "America/Los_Angeles",
                DefaultCancellationLeadMinutes = 60,
                Status = 0,
                BootstrapStatus = (short)AdministratorBootstrapStatus.Sealed,
                BootstrapSealedAt = now,
                Version = 0,
            });
        context.Users.AddRange(
            CreateUser(primaryAdminUserId.Value, primaryAdminEmail),
            CreateUser(secondAdminUserId.Value, secondAdminEmail));
        context.Memberships.AddRange(
            CreateMembershipEntity(
                primaryAdminMembershipId,
                organizationId,
                primaryAdminUserId,
                "Second organization manager"),
            CreateMembershipEntity(
                secondAdminMembershipId,
                organizationId,
                secondAdminUserId,
                "Second organization administrator"));
        context.RoleAssignments.AddRange(
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId.Value,
                MembershipId = primaryAdminMembershipId.Value,
                Role = (short)Domain.Common.Enums.OrganizationRole.Admin,
                AssignedByMembershipId = secondAdminMembershipId.Value,
                AssignedAt = now,
            },
            new RoleAssignmentEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = organizationId.Value,
                MembershipId = secondAdminMembershipId.Value,
                Role = (short)Domain.Common.Enums.OrganizationRole.Admin,
                AssignedByMembershipId = primaryAdminMembershipId.Value,
                AssignedAt = now,
            });

        if (sharedMemberEmail is not null
            && sharedMemberUserId is not null
            && sharedMemberMembershipId is not null)
        {
            context.Users.Add(CreateUser(sharedMemberUserId.Value.Value, sharedMemberEmail));
            context.Memberships.Add(
                CreateMembershipEntity(
                    sharedMemberMembershipId.Value,
                    organizationId,
                    sharedMemberUserId.Value,
                    "Shared active member"));
        }

        await context.SaveChangesAsync();
        await SeedPasswordAsync(database, primaryAdminEmail, SharedPassword);
        await SeedPasswordAsync(database, secondAdminEmail, SharedPassword);
        if (sharedMemberEmail is not null)
        {
            await SeedPasswordAsync(database, sharedMemberEmail, SharedPassword);
        }

        return new SecondOrganizationSeed(
            organizationId,
            primaryAdminMembershipId,
            secondAdminMembershipId,
            primaryAdminEmail,
            secondAdminEmail,
            sharedMemberMembershipId,
            sharedMemberEmail);
    }

    private static async Task<AdditionalMemberSeed> AddMemberAsync(
        PostgresTestDatabase database,
        OrganizationId organizationId,
        string email,
        string displayName)
    {
        UserId userId = UserId.New();
        MembershipId membershipId = MembershipId.New();
        await using TabrukDbContext context = database.CreateContext();
        context.Users.Add(CreateUser(userId.Value, email));
        context.Memberships.Add(
            CreateMembershipEntity(
                membershipId,
                organizationId,
                userId,
                displayName));
        await context.SaveChangesAsync();
        return new AdditionalMemberSeed(userId, membershipId, email);
    }

    private static async Task<TokenSetResponse> LoginAsync(
        AuthApiHost host,
        string email,
        string password,
        Guid installationId)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/login",
            new { email, password },
            installationId: installationId);
        return await ReadRequiredAsync<TokenSetResponse>(response, HttpStatusCode.OK);
    }

    private static async Task<StepUpResponse> IssueStepUpAsync(
        AuthApiHost host,
        string accessToken,
        string purpose)
    {
        using HttpResponseMessage response = await SendJsonAsync(
            host.Client,
            HttpMethod.Post,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose,
            },
            accessToken);
        return await ReadRequiredAsync<StepUpResponse>(response, HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string? bearerToken = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null,
        string? bearerToken = null,
        long? ifMatch = null,
        string? stepUpToken = null,
        Guid? installationId = null,
        string? hostHeader = null)
    {
        HttpRequestMessage request = new(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (ifMatch.HasValue)
        {
            request.Headers.TryAddWithoutValidation(ApiDefaults.IfMatchHeaderName, $"\"{ifMatch.Value}\"");
        }

        if (!string.IsNullOrWhiteSpace(stepUpToken))
        {
            request.Headers.TryAddWithoutValidation(AuthHeaders.StepUpToken, stepUpToken);
        }

        if (installationId.HasValue)
        {
            request.Headers.TryAddWithoutValidation(
                AuthHeaders.InstallationId,
                installationId.Value.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(hostHeader))
        {
            request.Headers.Host = hostHeader;
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException($"The response did not contain a {typeof(T).Name} body.");
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"The JSON did not contain a {typeof(T).Name} payload.");

    private static long GetEtagVersion(HttpResponseMessage response)
    {
        string tag = response.Headers.ETag?.Tag
            ?? throw new InvalidOperationException("The response did not include an ETag header.");
        Assert.StartsWith("\"", tag, StringComparison.Ordinal);
        Assert.EndsWith("\"", tag, StringComparison.Ordinal);
        return long.Parse(tag[1..^1], CultureInfo.InvariantCulture);
    }

    private static string GetQueryParameter(string url, string parameterName)
    {
        Uri uri = new(url, UriKind.Absolute);
        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], parameterName, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parts[1]);
            }
        }

        throw new InvalidOperationException($"The URL did not contain the '{parameterName}' query parameter.");
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record AdditionalAdministratorSeed(
        UserId UserId,
        MembershipId MembershipId,
        string Email);

    private sealed record AdditionalMemberSeed(
        UserId UserId,
        MembershipId MembershipId,
        string Email);

    private sealed record SecondOrganizationSeed(
        OrganizationId OrganizationId,
        MembershipId PrimaryAdminMembershipId,
        MembershipId SecondAdminMembershipId,
        string PrimaryAdminEmail,
        string SecondAdminEmail,
        MembershipId? SharedMemberMembershipId,
        string? SharedMemberEmail);

    private sealed record AdminMemberPageContract(
        IReadOnlyList<AdminMemberContract> Items,
        string? NextCursor);

    private sealed record AdminMemberContract(
        string Id,
        string DisplayName,
        string Status,
        bool EligibleAsNamedParticipant,
        IReadOnlyList<string> Roles,
        IReadOnlyList<AdminRoleRequestContract> PendingAdministratorRoleRequests);

    private sealed record AdminRoleRequestContract(
        string Id,
        string TargetMembershipId,
        string Action,
        string ProposerMembershipId,
        string? ApproverMembershipId,
        string Reason,
        DateTimeOffset ProposedAt,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ApprovedAt,
        string Status);

    private sealed record IssueInvitationContract(
        string InviteUrl,
        DateTimeOffset ExpiresAt);

    private sealed record ProblemContract(
        string Code,
        int Status,
        string Detail);

    private static TabrukIdentityUser CreateUser(Guid userId, string email) =>
        new()
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        };

    private static MembershipEntity CreateMembershipEntity(
        MembershipId membershipId,
        OrganizationId organizationId,
        UserId userId,
        string displayName) =>
        new()
        {
            Id = membershipId.Value,
            OrganizationId = organizationId.Value,
            UserId = userId.Value,
            DisplayName = displayName,
            Status = (short)Domain.Common.Enums.MembershipStatus.Active,
            EligibleAsNamedParticipant = true,
        };
}
