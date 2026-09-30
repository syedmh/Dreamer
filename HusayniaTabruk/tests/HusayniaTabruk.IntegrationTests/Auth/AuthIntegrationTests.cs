using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Auth;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Api.Middleware;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Application.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Infrastructure.Identity.Entities;
using HusayniaTabruk.Infrastructure.Identity.Services;
using HusayniaTabruk.Infrastructure.Persistence;
using HusayniaTabruk.Infrastructure.Persistence.Entities;
using HusayniaTabruk.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HusayniaTabruk.IntegrationTests.Auth;

[Collection(nameof(PostgresPersistenceCollectionDefinition))]
[Trait("Category", "Auth")]
public sealed class AuthIntegrationTests : PostgresPersistenceTest
{
    private const string SharedPassword = "Passw0rd!Passw0rd!";
    private const string RefreshLoginProvider = "tabruk.refresh.v1";
    private const string RefreshConsumedLoginProvider = "tabruk.refresh.consumed.v1";
    private const string StepUpLoginProvider = "tabruk.stepup.v1";

    [RequiresPostgresFact]
    public async Task InvitationAcceptanceActivatesMembershipIssuesTokensAndReturnsMe()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow,
            clock.UtcNow.AddHours(1));
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage acceptResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: installationId);
        TokenSetResponse tokens = await ReadRequiredAsync<TokenSetResponse>(acceptResponse, HttpStatusCode.OK);

        Assert.Equal(clock.UtcNow.AddMinutes(10), tokens.AccessTokenExpiresAt);
        Assert.Equal(clock.UtcNow.AddDays(30), tokens.RefreshTokenExpiresAt);

        using HttpResponseMessage meResponse = await GetAsync(
            host.Client,
            "/api/v1/me",
            tokens.AccessToken);
        string meJson = await meResponse.Content.ReadAsStringAsync();
        using JsonDocument meDocument = JsonDocument.Parse(meJson);
        MeResponse me = await ReadRequiredAsync<MeResponse>(meResponse, HttpStatusCode.OK);

        Assert.Equal("Accepted Member", me.Membership.DisplayName);
        Assert.Equal(invited.MembershipId.ToString(), me.Membership.Id);
        Assert.Equal(seed.OrganizationId.ToString(), me.Organization.Id);
        Assert.Equal(
            ["membership", "organization", "roles"],
            meDocument.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["displayName", "eligibleAsNamedParticipant", "id"],
            meDocument.RootElement.GetProperty("membership")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            ["id", "name", "timeZone"],
            meDocument.RootElement.GetProperty("organization")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain("email", meJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", meJson, StringComparison.OrdinalIgnoreCase);

        await using TabrukDbContext context = database.CreateContext();
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.Id == invited.MembershipId.Value);
        InvitationEntity invitation = await context.Invitations.SingleAsync(
            candidate => candidate.TokenHash == Hash(invited.RawToken));
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Id == invited.UserId.Value);
        IdentityUserToken<Guid> refreshRow = await context.Set<IdentityUserToken<Guid>>()
            .SingleAsync(candidate => candidate.UserId == invited.UserId.Value
                && candidate.LoginProvider == RefreshLoginProvider);

        Assert.Equal((short)MembershipStatus.Active, membership.Status);
        Assert.Equal("Accepted Member", membership.DisplayName);
        Assert.Equal(clock.UtcNow, invitation.AcceptedAt);
        Assert.True(user.EmailConfirmed);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        AssertOpaqueBearer(tokens.RefreshToken);
        Assert.DoesNotContain(invited.UserId.Value.ToString("N"), tokens.RefreshToken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(tokens.RefreshToken, refreshRow.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(tokens.RefreshToken, refreshRow.Name, StringComparison.Ordinal);
        Assert.Contains($"device:{installationId:N}:family:", refreshRow.Name, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task InvitationAcceptanceDeniesExpiredInvitationsWithoutSideEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow.AddHours(-2),
            clock.UtcNow.AddMinutes(-1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage acceptResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string body = await acceptResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, acceptResponse.StatusCode);
        Assert.Contains(AuthenticationErrorCodes.InvitationInvalid, body, StringComparison.Ordinal);

        await using TabrukDbContext context = database.CreateContext();
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.Id == invited.MembershipId.Value);
        InvitationEntity invitation = await context.Invitations.SingleAsync(
            candidate => candidate.TokenHash == Hash(invited.RawToken));
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Id == invited.UserId.Value);

        Assert.Equal((short)MembershipStatus.Invited, membership.Status);
        Assert.Null(invitation.AcceptedAt);
        Assert.True(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.Empty(await context.Set<IdentityUserToken<Guid>>().ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task InvitationAcceptanceDeniesRevokedInvitationsWithoutSideEffects()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow,
            clock.UtcNow.AddHours(1));
        await using (TabrukDbContext context = database.CreateContext())
        {
            InvitationEntity revokedInvitation = await context.Invitations.SingleAsync(
                candidate => candidate.TokenHash == Hash(invited.RawToken));
            revokedInvitation.RevokedAt = clock.UtcNow;
            await context.SaveChangesAsync();
        }

        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage acceptResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string body = await acceptResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, acceptResponse.StatusCode);
        Assert.Contains(AuthenticationErrorCodes.InvitationInvalid, body, StringComparison.Ordinal);

        await using TabrukDbContext verification = database.CreateContext();
        MembershipEntity membership = await verification.Memberships.SingleAsync(
            candidate => candidate.Id == invited.MembershipId.Value);
        InvitationEntity invitation = await verification.Invitations.SingleAsync(
            candidate => candidate.TokenHash == Hash(invited.RawToken));
        TabrukIdentityUser user = await verification.Users.SingleAsync(
            candidate => candidate.Id == invited.UserId.Value);

        Assert.Equal((short)MembershipStatus.Invited, membership.Status);
        Assert.Null(invitation.AcceptedAt);
        Assert.Equal(clock.UtcNow, invitation.RevokedAt);
        Assert.True(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.Empty(await verification.Set<IdentityUserToken<Guid>>().ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task InvitationAcceptanceIsSingleUse()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow,
            clock.UtcNow.AddHours(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage firstResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        _ = await ReadRequiredAsync<TokenSetResponse>(firstResponse, HttpStatusCode.OK);

        using HttpResponseMessage secondResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Second Attempt",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string secondBody = await secondResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, secondResponse.StatusCode);
        Assert.Contains(AuthenticationErrorCodes.InvitationInvalid, secondBody, StringComparison.Ordinal);

        await using TabrukDbContext context = database.CreateContext();
        InvitationEntity invitation = await context.Invitations.SingleAsync(
            candidate => candidate.TokenHash == Hash(invited.RawToken));
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.Id == invited.MembershipId.Value);

        Assert.Equal(clock.UtcNow, invitation.AcceptedAt);
        Assert.Equal((short)MembershipStatus.Active, membership.Status);
        Assert.Equal(
            1,
            await context.Set<IdentityUserToken<Guid>>().CountAsync(
                candidate => candidate.UserId == invited.UserId.Value
                    && candidate.LoginProvider == RefreshLoginProvider));
    }

    [RequiresPostgresFact]
    public async Task ConcurrentInvitationAcceptanceAllowsOneWinnerAndRollsBackTheLoser()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow,
            clock.UtcNow.AddHours(1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        const string firstPassword = "FirstPassw0rd!First";
        const string secondPassword = "SecondPassw0rd!Second";
        Task<HttpResponseMessage> firstTask = PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "First Winner",
                password = firstPassword,
            },
            installationId: Guid.CreateVersion7());
        Task<HttpResponseMessage> secondTask = PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Second Winner",
                password = secondPassword,
            },
            installationId: Guid.CreateVersion7());

        HttpResponseMessage[] responses = await Task.WhenAll(firstTask, secondTask);
        using HttpResponseMessage firstResponse = responses[0];
        using HttpResponseMessage secondResponse = responses[1];
        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Unauthorized],
            responses.Select(response => response.StatusCode).Order().ToArray());

        HttpResponseMessage deniedResponse =
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        string deniedBody = await deniedResponse.Content.ReadAsStringAsync();
        Assert.Contains(AuthenticationErrorCodes.InvitationInvalid, deniedBody, StringComparison.Ordinal);

        await using TabrukDbContext context = database.CreateContext();
        InvitationEntity invitation = await context.Invitations.SingleAsync(
            candidate => candidate.TokenHash == Hash(invited.RawToken));
        MembershipEntity membership = await context.Memberships.SingleAsync(
            candidate => candidate.Id == invited.MembershipId.Value);
        TabrukIdentityUser user = await context.Users.SingleAsync(
            candidate => candidate.Id == invited.UserId.Value);
        IPasswordHasher<TabrukIdentityUser> passwordHasher =
            host.Services.GetRequiredService<IPasswordHasher<TabrukIdentityUser>>();

        string winningPassword = membership.DisplayName == "First Winner" ? firstPassword : secondPassword;
        string losingPassword = membership.DisplayName == "First Winner" ? secondPassword : firstPassword;

        Assert.Equal(clock.UtcNow, invitation.AcceptedAt);
        Assert.Equal((short)MembershipStatus.Active, membership.Status);
        Assert.True(user.EmailConfirmed);
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.NotEqual(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, winningPassword));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, losingPassword));
        Assert.Equal(
            1,
            await context.Set<IdentityUserToken<Guid>>().CountAsync(
                candidate => candidate.UserId == invited.UserId.Value
                    && candidate.LoginProvider == RefreshLoginProvider));
    }

    [RequiresPostgresFact]
    public async Task InvitationFailuresDoNotLeakTheRawInvitationTokenInResponsesOrLogs()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            clock.UtcNow.AddHours(-2),
            clock.UtcNow.AddMinutes(-1));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/invitations/accept",
            new
            {
                token = invited.RawToken,
                displayName = "Accepted Member",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(invited.RawToken, body, StringComparison.Ordinal);
        Assert.All(
            host.Logs.Records,
            record =>
            {
                Assert.DoesNotContain(invited.RawToken, record.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(invited.RawToken, record.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
            });
    }

    [RequiresPostgresFact]
    public async Task LoginDeniesDisabledMembershipAndCreatesNoSession()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using (TabrukDbContext context = database.CreateContext())
        {
            MembershipEntity membership = await context.Memberships.SingleAsync(
                candidate => candidate.Id == seed.MemberMembershipId.Value);
            membership.Status = (short)MembershipStatus.Disabled;
            await context.SaveChangesAsync();
        }

        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email = "member@example.test",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Empty(await verification.Set<IdentityUserToken<Guid>>().ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task LoginDeniesUnknownUsersAndCreatesNoSession()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await PersistenceSeed.CreateAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        const string unknownEmail = "unknown-member@example.test";
        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email = unknownEmail,
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(AuthenticationErrorCodes.AuthenticationFailed, body, StringComparison.Ordinal);
        Assert.DoesNotContain(unknownEmail, body, StringComparison.OrdinalIgnoreCase);

        await using TabrukDbContext verification = database.CreateContext();
        Assert.Empty(await verification.Set<IdentityUserToken<Guid>>().ToListAsync());
    }

    [RequiresPostgresFact]
    public async Task IdentityServiceUsesTheRealOrDummyPasswordHashForEveryCredentialFailurePath()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        InvitedMemberSeed invited = await AddInvitedMemberAsync(
            database,
            seed,
            seed.Now,
            seed.Now.AddHours(1));
        await using TabrukDbContext context = database.CreateContext();
        ObservingPasswordHasher passwordHasher = new();
        AspNetIdentityService service = new(context, passwordHasher);

        Result<UserId> knownWrongPassword = await service.VerifyCredentialsAsync(
            "member@example.test",
            "WrongPassw0rd!");
        Result<UserId> unknownUser = await service.VerifyCredentialsAsync(
            "unknown-member@example.test",
            SharedPassword);
        Result<UserId> invitedUserWithoutPassword = await service.VerifyCredentialsAsync(
            invited.Email,
            SharedPassword);
        Result missingUserStepUp = await service.VerifyPasswordAsync(UserId.New(), SharedPassword);
        Result invitedUserStepUp = await service.VerifyPasswordAsync(invited.UserId, SharedPassword);

        Assert.True(knownWrongPassword.IsFailure);
        Assert.True(unknownUser.IsFailure);
        Assert.True(invitedUserWithoutPassword.IsFailure);
        Assert.True(missingUserStepUp.IsFailure);
        Assert.True(invitedUserStepUp.IsFailure);
        Assert.Equal(5, passwordHasher.VerifyAttempts.Count);

        string realHash = passwordHasher.VerifyAttempts[0].HashedPassword;
        string dummyHash = passwordHasher.VerifyAttempts[1].HashedPassword;
        Assert.False(string.IsNullOrWhiteSpace(realHash));
        Assert.False(string.IsNullOrWhiteSpace(dummyHash));
        Assert.NotEqual(realHash, dummyHash);
        Assert.All(
            passwordHasher.VerifyAttempts.Skip(1),
            attempt => Assert.Equal(dummyHash, attempt.HashedPassword));
    }

    [RequiresPostgresFact]
    public async Task AccessTokenHasAnExactTenMinuteLifetimeIsIdentityOnlyAndExpiresOnSchedule()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now.AddMilliseconds(789));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse tokens = await LoginAsync(host, "member@example.test", SharedPassword, Guid.CreateVersion7());
        IAccessTokenCodec codec = host.Services.GetRequiredService<IAccessTokenCodec>();
        DateTimeOffset expectedIssuedAt = TruncateToSecond(clock.UtcNow);

        Assert.True(codec.TryRead(tokens.AccessToken, clock.UtcNow, out AccessTokenPayload? payload));
        Assert.NotNull(payload);
        Assert.Equal(seed.MemberMembershipId, payload!.MembershipId);
        Assert.Equal(seed.OrganizationId, payload.OrganizationId);
        Assert.Equal(expectedIssuedAt, payload.IssuedAt);
        Assert.Equal(expectedIssuedAt.AddMinutes(10), payload.ExpiresAt);
        Assert.Equal(payload.ExpiresAt, tokens.AccessTokenExpiresAt);
        Assert.Equal(TimeSpan.FromMinutes(10), payload.ExpiresAt - payload.IssuedAt);

        string payloadJson = DecodeJwtPayload(tokens.AccessToken);
        Assert.DoesNotContain("role", payloadJson, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage firstMe = await GetAsync(host.Client, "/api/v1/me", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, firstMe.StatusCode);

        clock.Set(tokens.AccessTokenExpiresAt.AddMilliseconds(-1));

        using HttpResponseMessage almostExpiredMe = await GetAsync(host.Client, "/api/v1/me", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, almostExpiredMe.StatusCode);

        clock.Set(tokens.AccessTokenExpiresAt);

        using HttpResponseMessage expiredMe = await GetAsync(host.Client, "/api/v1/me", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredMe.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task ProtectedRequestUsesCurrentDatabaseRoleAssignmentsAndMembershipState()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureApi: group => group.MapGet(
                    "/_auth/admin-probe",
                    (ClaimsPrincipal user) => user.IsInRole(nameof(OrganizationRole.Admin))
                        ? Results.NoContent()
                        : Results.Forbid())
                .WithName("AdminProbe"));

        TokenSetResponse adminTokens = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        TokenSetResponse memberTokens = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        using HttpResponseMessage probeAllowed = await GetAsync(
            host.Client,
            "/api/v1/_auth/admin-probe",
            adminTokens.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, probeAllowed.StatusCode);

        await using (TabrukDbContext context = database.CreateContext())
        {
            RoleAssignmentEntity adminRole = await context.RoleAssignments.SingleAsync(
                candidate => candidate.OrganizationId == seed.OrganizationId.Value
                    && candidate.MembershipId == seed.ManagerMembershipId.Value
                    && candidate.Role == (short)OrganizationRole.Admin
                    && candidate.RevokedAt == null);
            adminRole.RevokedAt = clock.UtcNow.AddMinutes(1);
            adminRole.RevokedByMembershipId = seed.SecondAdministratorMembershipId.Value;

            MembershipEntity membership = await context.Memberships.SingleAsync(
                candidate => candidate.Id == seed.MemberMembershipId.Value);
            membership.Status = (short)MembershipStatus.Disabled;
            await context.SaveChangesAsync();
        }

        using HttpResponseMessage probeDenied = await GetAsync(
            host.Client,
            "/api/v1/_auth/admin-probe",
            adminTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, probeDenied.StatusCode);

        using HttpResponseMessage disabledMe = await GetAsync(
            host.Client,
            "/api/v1/me",
            memberTokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledMe.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task RefreshRotatesHashedOpaquePerInstallationTokens()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);
        using HttpResponseMessage refreshResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new
            {
                refreshToken = login.RefreshToken,
            },
            installationId: installationId);
        TokenSetResponse rotated = await ReadRequiredAsync<TokenSetResponse>(refreshResponse, HttpStatusCode.OK);

        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        AssertOpaqueBearer(login.RefreshToken);
        AssertOpaqueBearer(rotated.RefreshToken);
        Assert.Equal(clock.UtcNow.AddMinutes(10), rotated.AccessTokenExpiresAt);
        Assert.Equal(clock.UtcNow.AddDays(30), rotated.RefreshTokenExpiresAt);

        Guid memberUserId = await GetUserIdByEmailAsync(database, "member@example.test");
        Assert.DoesNotContain(memberUserId.ToString("N"), login.RefreshToken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(memberUserId.ToString("N"), rotated.RefreshToken, StringComparison.OrdinalIgnoreCase);
        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid> row = await context.Set<IdentityUserToken<Guid>>().SingleAsync(
            candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshLoginProvider);

        Assert.DoesNotContain(login.RefreshToken, row.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.RefreshToken, row.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, ReadConsumedTokenCount(row.Value));
    }

    [RequiresPostgresFact]
    public async Task RefreshRateLimitFollowsTheFamilyAcrossRotationsAndKeepsFamiliesIndependent()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid firstInstallation = Guid.CreateVersion7();
        Guid secondInstallation = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse first = await LoginAsync(host, "member@example.test", SharedPassword, firstInstallation);
        TokenSetResponse second = await LoginAsync(host, "member@example.test", SharedPassword, secondInstallation);

        for (int rotation = 0; rotation < 30; rotation++)
        {
            first = await RefreshAsync(host, first.RefreshToken, firstInstallation, HttpStatusCode.OK);
        }

        using HttpResponseMessage limited = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = first.RefreshToken },
            installationId: firstInstallation);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

        second = await RefreshAsync(host, second.RefreshToken, secondInstallation, HttpStatusCode.OK);
        AssertOpaqueBearer(second.RefreshToken);
    }

    [RequiresPostgresFact]
    public async Task LoginPurgesOnlyRefreshLifecycleRowsPastRetention()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid userId = await GetUserIdByEmailAsync(database, "member@example.test");
        string expiredFamily = $"device:{Guid.CreateVersion7():N}:family:expired:state";
        string retainedFamily = $"device:{Guid.CreateVersion7():N}:family:retained:state";
        DateTimeOffset expiredAt = clock.UtcNow
            .Subtract(AuthenticationLifecycleRetention.RefreshFamily)
            .AddSeconds(-1);
        DateTimeOffset retainedAt = clock.UtcNow
            .Subtract(AuthenticationLifecycleRetention.RefreshFamily)
            .AddSeconds(1);

        await AddRefreshLifecycleRowsAsync(
            database,
            userId,
            expiredFamily,
            expiredAt,
            retainedFamily,
            retainedAt);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        _ = await LoginAsync(host, "member@example.test", SharedPassword, Guid.CreateVersion7());

        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid>[] rows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == userId)
            .ToArrayAsync();
        Assert.DoesNotContain(rows, row => row.Name.Contains(":expired:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name == retainedFamily);
        Assert.Contains(rows, row => row.Name.Contains(":retained:consumed:", StringComparison.Ordinal));
    }

    [RequiresPostgresFact]
    public async Task RefreshPurgesExpiredFamilyAndConsumedHistoryTransactionally()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);
        Guid userId = await GetUserIdByEmailAsync(database, "member@example.test");
        string expiredFamily = $"device:{Guid.CreateVersion7():N}:family:expired:state";
        string retainedFamily = $"device:{Guid.CreateVersion7():N}:family:retained:state";

        await AddRefreshLifecycleRowsAsync(
            database,
            userId,
            expiredFamily,
            clock.UtcNow.Subtract(AuthenticationLifecycleRetention.RefreshFamily).AddSeconds(-1),
            retainedFamily,
            clock.UtcNow.Subtract(AuthenticationLifecycleRetention.RefreshFamily).AddSeconds(1));

        _ = await RefreshAsync(host, login.RefreshToken, installationId, HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid>[] rows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == userId)
            .ToArrayAsync();
        Assert.DoesNotContain(rows, row => row.Name.Contains(":expired:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name == retainedFamily);
    }

    [RequiresPostgresFact]
    public async Task RefreshTokensRemainPerInstallationAndLogoutRevokesOnlyTheMatchingInstallation()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationA = Guid.CreateVersion7();
        Guid installationB = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse first = await LoginAsync(host, "member@example.test", SharedPassword, installationA);
        TokenSetResponse second = await LoginAsync(host, "member@example.test", SharedPassword, installationB);

        using HttpResponseMessage logout = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/logout",
            new { refreshToken = first.RefreshToken },
            installationId: installationA);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using HttpResponseMessage revokedRefresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = first.RefreshToken },
            installationId: installationA);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRefresh.StatusCode);

        using HttpResponseMessage survivingRefresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = second.RefreshToken },
            installationId: installationB);
        _ = await ReadRequiredAsync<TokenSetResponse>(survivingRefresh, HttpStatusCode.OK);

        Guid memberUserId = await GetUserIdByEmailAsync(database, "member@example.test");
        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid>[] rows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshLoginProvider)
            .OrderBy(candidate => candidate.Name)
            .ToArrayAsync();

        Assert.Equal(2, rows.Length);
        IdentityUserToken<Guid> firstRow = Assert.Single(
            rows,
            candidate => candidate.Name.Contains($"device:{installationA:N}:family:", StringComparison.Ordinal));
        IdentityUserToken<Guid> secondRow = Assert.Single(
            rows,
            candidate => candidate.Name.Contains($"device:{installationB:N}:family:", StringComparison.Ordinal));
        Assert.Contains(@"""revocationReason"":""logout""", firstRow.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(@"""revocationReason"":""logout""", secondRow.Value ?? string.Empty, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task RefreshTokensExpireExactlyAfterThirtyDays()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid beforeBoundaryInstallation = Guid.CreateVersion7();
        Guid boundaryInstallation = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse beforeBoundary = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            beforeBoundaryInstallation);
        TokenSetResponse atBoundary = await LoginAsync(
            host,
            "member@example.test",
            SharedPassword,
            boundaryInstallation);

        clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));

        using HttpResponseMessage almostExpiredRefresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = beforeBoundary.RefreshToken },
            installationId: beforeBoundaryInstallation);
        TokenSetResponse rotated = await ReadRequiredAsync<TokenSetResponse>(
            almostExpiredRefresh,
            HttpStatusCode.OK);

        Assert.Equal(clock.UtcNow.AddMinutes(10), rotated.AccessTokenExpiresAt);
        Assert.Equal(clock.UtcNow.AddDays(30), rotated.RefreshTokenExpiresAt);

        clock.Advance(TimeSpan.FromSeconds(1));

        using HttpResponseMessage expiredRefresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = atBoundary.RefreshToken },
            installationId: boundaryInstallation);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredRefresh.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task RefreshFamilyCapRevokesTheFamilyAndKeepsStorageBounded()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configurationOverrides:
            [
                new KeyValuePair<string, string?>("TabrukAuth:RefreshFamilyConsumedTokenLimit", "2"),
            ]);

        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);
        TokenSetResponse firstRotation = await RefreshAsync(host, login.RefreshToken, installationId, HttpStatusCode.OK);
        TokenSetResponse secondRotation = await RefreshAsync(host, firstRotation.RefreshToken, installationId, HttpStatusCode.OK);
        using HttpResponseMessage cappedRefresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = secondRotation.RefreshToken },
            installationId: installationId);

        Assert.Equal(HttpStatusCode.Unauthorized, cappedRefresh.StatusCode);

        Guid memberUserId = await GetUserIdByEmailAsync(database, "member@example.test");
        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid> stateRow = await context.Set<IdentityUserToken<Guid>>().SingleAsync(
            candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshLoginProvider);
        IdentityUserToken<Guid>[] consumedRows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshConsumedLoginProvider)
            .OrderBy(candidate => candidate.Name)
            .ToArrayAsync();

        Assert.Equal(2, ReadConsumedTokenCount(stateRow.Value));
        Assert.Contains(@"""revocationReason"":""history_limit""", stateRow.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, consumedRows.Length);
        Assert.All(
            consumedRows,
            row => Assert.True((row.Value?.Length ?? 0) < 256, $"Consumed token row grew unexpectedly: {row.Value}"));
        Assert.True((stateRow.Value?.Length ?? 0) < 512, $"Refresh family state grew unexpectedly: {stateRow.Value}");
    }

    [RequiresPostgresFact]
    public async Task RefreshFamilyCapPrunesExpiredConsumedTokensBeforeRevoking()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configurationOverrides:
            [
                new KeyValuePair<string, string?>("TabrukAuth:RefreshFamilyConsumedTokenLimit", "1"),
            ]);

        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);
        clock.Advance(TimeSpan.FromSeconds(1));
        TokenSetResponse firstRotation = await RefreshAsync(host, login.RefreshToken, installationId, HttpStatusCode.OK);

        clock.Set(login.RefreshTokenExpiresAt);

        TokenSetResponse secondRotation = await RefreshAsync(
            host,
            firstRotation.RefreshToken,
            installationId,
            HttpStatusCode.OK);

        Assert.NotEqual(firstRotation.RefreshToken, secondRotation.RefreshToken);

        Guid memberUserId = await GetUserIdByEmailAsync(database, "member@example.test");
        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid> stateRow = await context.Set<IdentityUserToken<Guid>>().SingleAsync(
            candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshLoginProvider);
        IdentityUserToken<Guid>[] consumedRows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshConsumedLoginProvider)
            .OrderBy(candidate => candidate.Name)
            .ToArrayAsync();
        IdentityUserToken<Guid> survivingConsumedRow = Assert.Single(consumedRows);

        Assert.Equal(1, ReadConsumedTokenCount(stateRow.Value));
        Assert.DoesNotContain(
            consumedRows,
            row => row.Name.Contains(Hash(login.RefreshToken), StringComparison.Ordinal));
        Assert.Contains(Hash(firstRotation.RefreshToken), survivingConsumedRow.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(@"""revocationReason"":""history_limit""", stateRow.Value ?? string.Empty, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task ConcurrentRefreshReuseRevokesTheEntireFamily()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);

        Task<HttpResponseMessage> first = PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken },
            installationId);
        Task<HttpResponseMessage> second = PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken },
            installationId);

        HttpResponseMessage[] responses = await Task.WhenAll(first, second);
        HttpResponseMessage success = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        HttpResponseMessage failure = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
        string failureBody = await failure.Content.ReadAsStringAsync();
        TokenSetResponse rotated = await ReadRequiredAsync<TokenSetResponse>(success, HttpStatusCode.OK);

        Assert.Contains(AuthenticationErrorCodes.RefreshTokenReused, failureBody, StringComparison.Ordinal);

        using HttpResponseMessage familyRevoked = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = rotated.RefreshToken },
            installationId);
        Assert.Equal(HttpStatusCode.Unauthorized, familyRevoked.StatusCode);

        Guid memberUserId = await GetUserIdByEmailAsync(database, "member@example.test");
        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid> row = await context.Set<IdentityUserToken<Guid>>().SingleAsync(
            candidate => candidate.UserId == memberUserId
                && candidate.LoginProvider == RefreshLoginProvider);
        Assert.Contains("reuse", row.Value ?? string.Empty, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task LogoutRevokesTheSessionFamily()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid installationId = Guid.CreateVersion7();
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "member@example.test", SharedPassword, installationId);

        using HttpResponseMessage logout = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/logout",
            new { refreshToken = login.RefreshToken },
            installationId: installationId);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using HttpResponseMessage refresh = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = login.RefreshToken },
            installationId: installationId);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task StepUpIsPurposeBoundSingleUseAndExpiresExactlyAfterFiveMinutes()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "manager@example.test", SharedPassword, Guid.CreateVersion7());
        UserId managerUserId = UserId.From(await GetUserIdByEmailAsync(database, "manager@example.test"));

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: login.AccessToken);
        StepUpResponse grant = await ReadRequiredAsync<StepUpResponse>(response, HttpStatusCode.OK);

        Assert.Equal(clock.UtcNow.AddMinutes(5), grant.ExpiresAt);

        using IServiceScope scope = host.Services.CreateScope();
        IStepUpVerifier verifier = scope.ServiceProvider.GetRequiredService<IStepUpVerifier>();
        Result wrongPurpose = await verifier.ConsumeAsync(
            managerUserId,
            new StepUpToken(grant.StepUpToken),
            new StepUpPurpose("governance.approve"));
        Result consumed = await verifier.ConsumeAsync(
            managerUserId,
            new StepUpToken(grant.StepUpToken),
            new StepUpPurpose("governance.assign"));
        Result replay = await verifier.ConsumeAsync(
            managerUserId,
            new StepUpToken(grant.StepUpToken),
            new StepUpPurpose("governance.assign"));

        Assert.True(wrongPurpose.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.StepUpPurposeMismatch, wrongPurpose.Error.Code);
        Assert.True(consumed.IsSuccess);
        Assert.True(replay.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.StepUpInvalid, replay.Error.Code);

        using HttpResponseMessage secondResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: login.AccessToken);
        StepUpResponse expiringGrant = await ReadRequiredAsync<StepUpResponse>(secondResponse, HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromMinutes(5));

        Result expired = await verifier.ConsumeAsync(
            managerUserId,
            new StepUpToken(expiringGrant.StepUpToken),
            new StepUpPurpose("governance.assign"));
        Assert.True(expired.IsFailure);
        Assert.Equal(AuthenticationErrorCodes.StepUpInvalid, expired.Error.Code);
    }

    [RequiresPostgresFact]
    public async Task StepUpTokensAreOpaqueAndStoredOnlyAsHashes()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "manager@example.test", SharedPassword, Guid.CreateVersion7());

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: login.AccessToken);
        StepUpResponse grant = await ReadRequiredAsync<StepUpResponse>(response, HttpStatusCode.OK);
        Guid managerUserId = await GetUserIdByEmailAsync(database, "manager@example.test");

        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid> row = await context.Set<IdentityUserToken<Guid>>().SingleAsync(
            candidate => candidate.UserId == managerUserId
                && candidate.LoginProvider == StepUpLoginProvider);

        AssertOpaqueBearer(grant.StepUpToken);
        Assert.DoesNotContain(managerUserId.ToString("N"), grant.StepUpToken, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(grant.StepUpToken, row.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(grant.StepUpToken, row.Name, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task StepUpIssuanceAndVerificationPurgeOnlyAuditRetentionExpiredGrants()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        TokenSetResponse login = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            Guid.CreateVersion7());
        Guid managerUserId = await GetUserIdByEmailAsync(database, "manager@example.test");
        DateTimeOffset cutoff = clock.UtcNow.Subtract(AuthenticationLifecycleRetention.StepUpGrant);

        await AddStepUpLifecycleRowAsync(database, managerUserId, "expired", cutoff.AddSeconds(-1));
        await AddStepUpLifecycleRowAsync(database, managerUserId, "retained", cutoff.AddSeconds(1));

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: login.AccessToken);
        StepUpResponse grant = await ReadRequiredAsync<StepUpResponse>(response, HttpStatusCode.OK);

        await using (TabrukDbContext afterIssue = database.CreateContext())
        {
            IdentityUserToken<Guid>[] rows = await afterIssue.Set<IdentityUserToken<Guid>>()
                .Where(candidate => candidate.UserId == managerUserId
                    && candidate.LoginProvider == StepUpLoginProvider)
                .ToArrayAsync();
            Assert.DoesNotContain(rows, row => row.Name == "grant:expired");
            Assert.Contains(rows, row => row.Name == "grant:retained");
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        using IServiceScope scope = host.Services.CreateScope();
        IStepUpVerifier verifier = scope.ServiceProvider.GetRequiredService<IStepUpVerifier>();
        Result consumed = await verifier.ConsumeAsync(
            UserId.From(managerUserId),
            new StepUpToken(grant.StepUpToken),
            new StepUpPurpose("governance.assign"));
        Assert.True(consumed.IsSuccess);

        await using TabrukDbContext afterVerification = database.CreateContext();
        IdentityUserToken<Guid>[] remaining = await afterVerification.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == managerUserId
                && candidate.LoginProvider == StepUpLoginProvider)
            .ToArrayAsync();
        Assert.DoesNotContain(remaining, row => row.Name == "grant:retained");
        Assert.Single(remaining);
    }

    [RequiresPostgresFact]
    public async Task LifecycleCleanupDeletesTheInclusiveCutoffAndRetainsTheNextInstant()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid managerUserId = await GetUserIdByEmailAsync(database, "manager@example.test");
        DateTimeOffset refreshCutoff = clock.UtcNow.Subtract(AuthenticationLifecycleRetention.RefreshFamily);
        DateTimeOffset stepUpCutoff = clock.UtcNow.Subtract(AuthenticationLifecycleRetention.StepUpGrant);

        await AddRefreshLifecycleRowsAsync(
            database,
            managerUserId,
            $"device:{Guid.CreateVersion7():N}:family:before-cutoff:state",
            refreshCutoff.AddTicks(-10),
            $"device:{Guid.CreateVersion7():N}:family:at-cutoff:state",
            refreshCutoff);
        await AddRefreshLifecycleRowsAsync(
            database,
            managerUserId,
            $"device:{Guid.CreateVersion7():N}:family:after-cutoff:state",
            refreshCutoff.AddTicks(10),
            $"device:{Guid.CreateVersion7():N}:family:active:state",
            clock.UtcNow.AddDays(1));
        await AddStepUpLifecycleRowAsync(database, managerUserId, "before-cutoff", stepUpCutoff.AddTicks(-10));
        await AddStepUpLifecycleRowAsync(database, managerUserId, "at-cutoff", stepUpCutoff);
        await AddStepUpLifecycleRowAsync(database, managerUserId, "after-cutoff", stepUpCutoff.AddTicks(10));

        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        Guid installationId = Guid.CreateVersion7();
        TokenSetResponse login = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            installationId);
        _ = await RefreshAsync(host, login.RefreshToken, installationId, HttpStatusCode.OK);

        using HttpResponseMessage stepUpResponse = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: login.AccessToken);
        _ = await ReadRequiredAsync<StepUpResponse>(stepUpResponse, HttpStatusCode.OK);

        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid>[] rows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == managerUserId)
            .ToArrayAsync();

        Assert.DoesNotContain(rows, row => row.Name.Contains(":before-cutoff:", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Name.Contains(":at-cutoff:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name.Contains(":after-cutoff:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name.Contains(":active:", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Name == "grant:before-cutoff");
        Assert.DoesNotContain(rows, row => row.Name == "grant:at-cutoff");
        Assert.Contains(rows, row => row.Name == "grant:after-cutoff");
    }

    [RequiresPostgresFact]
    public async Task LifecycleCleanupDeletesExactlyTheHundredOldestEligibleRowsPerInvocation()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        Guid userId = await GetUserIdByEmailAsync(database, "member@example.test");
        DateTimeOffset oldestExpiration = clock.UtcNow
            .Subtract(AuthenticationLifecycleRetention.RefreshFamily)
            .AddDays(-2);
        const string eligibleMarker = ":t9-batch:";

        await AddRefreshLifecycleBatchAsync(
            database,
            userId,
            eligibleMarker,
            oldestExpiration,
            AuthenticationLifecycleRetention.CleanupBatchSize + 1);
        string activeFamily = $"device:{Guid.CreateVersion7():N}:family:t9-active:state";
        await AddRefreshLifecycleRowsAsync(
            database,
            userId,
            activeFamily,
            clock.UtcNow.AddDays(1),
            $"device:{Guid.CreateVersion7():N}:family:t9-reuse-history:state",
            clock.UtcNow.AddDays(1));

        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        _ = await LoginAsync(host, "member@example.test", SharedPassword, Guid.CreateVersion7());
        string[] remainingAfterFirst;
        await using (TabrukDbContext afterFirst = database.CreateContext())
        {
            remainingAfterFirst = await afterFirst.Set<IdentityUserToken<Guid>>()
                .Where(candidate => candidate.UserId == userId
                    && candidate.Name.Contains(eligibleMarker))
                .OrderBy(candidate => candidate.Name)
                .Select(candidate => candidate.Name)
                .ToArrayAsync();
        }

        _ = await LoginAsync(host, "member@example.test", SharedPassword, Guid.CreateVersion7());

        await using TabrukDbContext afterSecond = database.CreateContext();
        IdentityUserToken<Guid>[] finalRows = await afterSecond.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == userId)
            .ToArrayAsync();

        Assert.Equal(2, remainingAfterFirst.Length);
        Assert.True(
            remainingAfterFirst.All(name => name.Contains(":age-100:", StringComparison.Ordinal)),
            $"Expected only the newest eligible age-100 family and consumed rows to remain, but found: {string.Join(", ", remainingAfterFirst)}");
        Assert.DoesNotContain(finalRows, row => row.Name.Contains(eligibleMarker, StringComparison.Ordinal));
        Assert.Contains(finalRows, row => row.Name == activeFamily);
        Assert.Contains(finalRows, row => row.Name.Contains(":t9-reuse-history:", StringComparison.Ordinal));
    }

    [RequiresPostgresFact]
    public async Task ConcurrentLifecycleCleanupAcrossRequestContextsIsSafe()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);
        Guid managerUserId = await GetUserIdByEmailAsync(database, "manager@example.test");
        Guid firstInstallation = Guid.CreateVersion7();
        Guid secondInstallation = Guid.CreateVersion7();
        TokenSetResponse firstLogin = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            firstInstallation);
        TokenSetResponse secondLogin = await LoginAsync(
            host,
            "manager@example.test",
            SharedPassword,
            secondInstallation);
        DateTimeOffset expiredAt = clock.UtcNow
            .Subtract(AuthenticationLifecycleRetention.RefreshFamily)
            .AddDays(-2);

        await AddRefreshLifecycleBatchAsync(
            database,
            managerUserId,
            ":t9-concurrent-expired:",
            expiredAt,
            AuthenticationLifecycleRetention.CleanupBatchSize);
        await AddStepUpLifecycleBatchAsync(
            database,
            managerUserId,
            "t9-concurrent-expired",
            clock.UtcNow.Subtract(AuthenticationLifecycleRetention.StepUpGrant).AddDays(-2),
            AuthenticationLifecycleRetention.CleanupBatchSize);
        string activeFamily = $"device:{Guid.CreateVersion7():N}:family:t9-concurrent-active:state";
        await AddRefreshLifecycleRowsAsync(
            database,
            managerUserId,
            activeFamily,
            clock.UtcNow.AddDays(1),
            $"device:{Guid.CreateVersion7():N}:family:t9-concurrent-reuse:state",
            clock.UtcNow.AddDays(1));
        await AddStepUpLifecycleRowAsync(
            database,
            managerUserId,
            "t9-concurrent-active",
            clock.UtcNow);

        Task<HttpResponseMessage> firstRefresh = PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = firstLogin.RefreshToken },
            firstInstallation);
        Task<HttpResponseMessage> secondRefresh = PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken = secondLogin.RefreshToken },
            secondInstallation);
        Task<HttpResponseMessage> firstStepUp = PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: firstLogin.AccessToken);
        Task<HttpResponseMessage> secondStepUp = PostJsonAsync(
            host.Client,
            "/api/v1/auth/step-up",
            new
            {
                password = SharedPassword,
                purpose = "governance.assign",
            },
            bearerToken: secondLogin.AccessToken);

        HttpResponseMessage[] responses = await Task.WhenAll(
                firstRefresh,
                secondRefresh,
                firstStepUp,
                secondStepUp)
            .WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }
        }

        await using TabrukDbContext context = database.CreateContext();
        IdentityUserToken<Guid>[] rows = await context.Set<IdentityUserToken<Guid>>()
            .Where(candidate => candidate.UserId == managerUserId)
            .ToArrayAsync();
        Assert.DoesNotContain(
            rows,
            row => row.Name.Contains(":t9-concurrent-expired:", StringComparison.Ordinal));
        Assert.DoesNotContain(
            rows,
            row => row.Name.StartsWith("grant:t9-concurrent-expired-", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name == activeFamily);
        Assert.Contains(rows, row => row.Name.Contains(":t9-concurrent-reuse:", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name == "grant:t9-concurrent-active");
    }

    [RequiresPostgresFact]
    public async Task StepUpRateLimitRejectsTheExtraRequestWithoutCreatingAnotherGrant()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        TokenSetResponse login = await LoginAsync(host, "manager@example.test", SharedPassword, Guid.CreateVersion7());

        List<HttpStatusCode> statuses = [];
        for (int attempt = 0; attempt < 6; attempt++)
        {
            using HttpResponseMessage response = await PostJsonAsync(
                host.Client,
                "/api/v1/auth/step-up",
                new
                {
                    password = SharedPassword,
                    purpose = $"governance.assign.{attempt}",
                },
                bearerToken: login.AccessToken);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            statuses);

        await using TabrukDbContext context = database.CreateContext();
        int grants = await context.Set<IdentityUserToken<Guid>>().CountAsync(
            candidate => candidate.LoginProvider == StepUpLoginProvider);
        Assert.Equal(5, grants);
    }

    [RequiresPostgresFact]
    public async Task LoginRateLimitThrottlesOneAccountAcrossDistributedAddresses()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
                services.AddSingleton<IApiClientAddressProvider, TestClientAddressProvider>());

        Guid installationId = Guid.CreateVersion7();
        List<HttpStatusCode> statuses = [];
        HttpResponseMessage? limitedResponse = null;

        for (int attempt = 0; attempt < 11; attempt++)
        {
            HttpResponseMessage response = await PostJsonAsync(
                host.Client,
                "/api/v1/auth/login",
                new
                {
                    Email = "member@example.test",
                    password = "WrongPassw0rd!",
                },
                installationId: installationId,
                clientAddress: $"203.0.113.{attempt + 1}");
            statuses.Add(response.StatusCode);

            if (attempt == 10)
            {
                limitedResponse = response;
            }
            else
            {
                response.Dispose();
            }
        }

        using (limitedResponse)
        {
            string body = await limitedResponse!.Content.ReadAsStringAsync();
            using JsonDocument problem = JsonDocument.Parse(body);

            Assert.Equal(
                [
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.Unauthorized,
                    HttpStatusCode.TooManyRequests,
                ],
                statuses);
            Assert.True(
                limitedResponse.Headers.TryGetValues(ApiDefaults.RetryAfterHeaderName, out IEnumerable<string>? values));
            string retryAfterValue = Assert.Single(values ?? []);
            Assert.True(int.TryParse(retryAfterValue, out int retryAfter));
            Assert.True(retryAfter > 0);
            Assert.Equal("rate_limited", problem.RootElement.GetProperty("code").GetString());
        }
    }

    [RequiresPostgresFact]
    public async Task LoginRateLimitKeepsTwoAccountsBehindOneAddressIndependent()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(
            database.ConnectionString,
            clock,
            configureServices: services =>
                services.AddSingleton<IApiClientAddressProvider, TestClientAddressProvider>());

        Guid installationId = Guid.CreateVersion7();
        const string sharedAddress = "198.51.100.42";
        for (int attempt = 0; attempt < 10; attempt++)
        {
            using HttpResponseMessage memberResponse = await PostJsonAsync(
                host.Client,
                "/api/v1/auth/login",
                new
                {
                    email = "member@example.test",
                    password = "WrongPassw0rd!",
                },
                installationId: installationId,
                clientAddress: sharedAddress);
            using HttpResponseMessage managerResponse = await PostJsonAsync(
                host.Client,
                "/api/v1/auth/login",
                new
                {
                    email = "manager@example.test",
                    password = "WrongPassw0rd!",
                },
                installationId: installationId,
                clientAddress: sharedAddress);

            Assert.Equal(HttpStatusCode.Unauthorized, memberResponse.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, managerResponse.StatusCode);
        }

        using HttpResponseMessage limitedMember = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email = "member@example.test",
                password = "WrongPassw0rd!",
            },
            installationId: installationId,
            clientAddress: sharedAddress);
        using HttpResponseMessage limitedManager = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email = "manager@example.test",
                password = "WrongPassw0rd!",
            },
            installationId: installationId,
            clientAddress: sharedAddress);

        Assert.Equal(HttpStatusCode.TooManyRequests, limitedMember.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedManager.StatusCode);
    }

    [RequiresPostgresFact]
    public async Task LoginRequestBodyOverLimitReturnsPayloadTooLarge()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero));
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        using ByteArrayContent content =
            new(new byte[ApplicationLimits.MaximumAdministrativeRequestBytes + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(AuthHeaders.InstallationId, Guid.CreateVersion7().ToString("D"));

        using HttpResponseMessage response = await host.Client.SendAsync(request);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", problem.RootElement.GetProperty("code").GetString());
    }

    [RequiresPostgresFact]
    public async Task LoginReturnsDependencyUnavailableProblemWhenPostgresIsUnavailable()
    {
        MutableClock clock = new(new DateTimeOffset(2026, 8, 16, 17, 0, 0, TimeSpan.Zero));
        const string unavailableConnectionString =
            "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Search Path=public;Options=-c tabruk.target_schema=public -c tabruk.disposable_ef=on;Pooling=false;Timeout=1;Command Timeout=1";
        await using AuthApiHost host = await AuthApiHost.StartAsync(unavailableConnectionString, clock);

        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email = "member@example.test",
                password = SharedPassword,
            },
            installationId: Guid.CreateVersion7());
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument problem = JsonDocument.Parse(body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("dependency_unavailable", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=127.0.0.1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLSTATE", body, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresPostgresFact]
    public async Task AuthFailuresDoNotLeakSecretsInResponsesOrLogs()
    {
        await using PostgresTestDatabase database = await CreateDatabaseAsync();
        PersistenceSeed seed = await CreateSeedWithPasswordsAsync(database);
        MutableClock clock = new(seed.Now);
        await using AuthApiHost host = await AuthApiHost.StartAsync(database.ConnectionString, clock);

        const string email = "member@example.test";
        const string password = "WrongPassw0rd!";
        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new
            {
                email,
                password,
            },
            installationId: Guid.CreateVersion7());
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.All(
            host.Logs.Records,
            record =>
            {
                Assert.DoesNotContain(email, record.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(password, record.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(email, record.Exception?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(password, record.Exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
            });
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

    private static async Task<InvitedMemberSeed> AddInvitedMemberAsync(
        PostgresTestDatabase database,
        PersistenceSeed seed,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt)
    {
        UserId userId = UserId.New();
        MembershipId membershipId = MembershipId.New();
        string email = $"invitee-{Guid.CreateVersion7():N}@example.test";
        string rawToken = $"invite-{Guid.CreateVersion7():N}";
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
                DisplayName = "Pending Invite",
                Status = (short)MembershipStatus.Invited,
                EligibleAsNamedParticipant = true,
            });
        context.Invitations.Add(
            new InvitationEntity
            {
                Id = Guid.CreateVersion7(),
                OrganizationId = seed.OrganizationId.Value,
                NormalizedEmail = email.ToUpperInvariant(),
                TokenHash = Hash(rawToken),
                ExpiresAt = expiresAt,
                IssuedByMembershipId = seed.ManagerMembershipId.Value,
                IssuedAt = issuedAt,
            });
        await context.SaveChangesAsync();

        return new InvitedMemberSeed(userId, membershipId, email, rawToken);
    }

    private static async Task AddRefreshLifecycleRowsAsync(
        PostgresTestDatabase database,
        Guid userId,
        string expiredFamilyName,
        DateTimeOffset expiredAt,
        string retainedFamilyName,
        DateTimeOffset retainedAt)
    {
        await using TabrukDbContext context = database.CreateContext();
        AddRefreshLifecycleRows(context, userId, expiredFamilyName, expiredAt);
        AddRefreshLifecycleRows(context, userId, retainedFamilyName, retainedAt);
        await context.SaveChangesAsync();
    }

    private static void AddRefreshLifecycleRows(
        TabrukDbContext context,
        Guid userId,
        string familyName,
        DateTimeOffset expiresAt)
    {
        string prefix = familyName[..^":state".Length];
        context.Set<IdentityUserToken<Guid>>().AddRange(
            new IdentityUserToken<Guid>
            {
                UserId = userId,
                LoginProvider = RefreshLoginProvider,
                Name = familyName,
                Value = JsonSerializer.Serialize(
                    new
                    {
                        membershipId = Guid.CreateVersion7(),
                        organizationId = Guid.CreateVersion7(),
                        currentTokenHash = new string('a', 64),
                        currentExpiresAt = expiresAt,
                        createdAt = expiresAt.AddDays(-1),
                        updatedAt = expiresAt,
                        revokedAt = (DateTimeOffset?)null,
                        revocationReason = (string?)null,
                        consumedTokenCount = 1,
                    }),
            },
            new IdentityUserToken<Guid>
            {
                UserId = userId,
                LoginProvider = RefreshConsumedLoginProvider,
                Name = $"{prefix}:consumed:{new string('b', 64)}",
                Value = JsonSerializer.Serialize(
                    new
                    {
                        expiresAt,
                        consumedAt = expiresAt.AddDays(-1),
                    }),
            });
    }

    private static async Task AddRefreshLifecycleBatchAsync(
        PostgresTestDatabase database,
        Guid userId,
        string marker,
        DateTimeOffset oldestExpiration,
        int count)
    {
        await using TabrukDbContext context = database.CreateContext();
        Guid deviceId = Guid.CreateVersion7();
        for (int age = 0; age < count; age++)
        {
            int reverseOrder = count - age - 1;
            string familyName =
                $"device:{deviceId:N}:family{marker}order-{reverseOrder:D3}:age-{age:D3}:state";
            AddRefreshLifecycleRows(
                context,
                userId,
                familyName,
                oldestExpiration.AddMinutes(age));
        }

        await context.SaveChangesAsync();
    }

    private static async Task AddStepUpLifecycleRowAsync(
        PostgresTestDatabase database,
        Guid userId,
        string locator,
        DateTimeOffset consumedAt)
    {
        await using TabrukDbContext context = database.CreateContext();
        context.Set<IdentityUserToken<Guid>>().Add(
            new IdentityUserToken<Guid>
            {
                UserId = userId,
                LoginProvider = StepUpLoginProvider,
                Name = $"grant:{locator}",
                Value = JsonSerializer.Serialize(
                    new
                    {
                        purpose = "governance.assign",
                        tokenHash = new string('c', 64),
                        issuedAt = consumedAt.AddMinutes(-1),
                        expiresAt = consumedAt.AddMinutes(5),
                        consumedAt = (DateTimeOffset?)consumedAt,
                    }),
            });
        await context.SaveChangesAsync();
    }

    private static async Task AddStepUpLifecycleBatchAsync(
        PostgresTestDatabase database,
        Guid userId,
        string locatorPrefix,
        DateTimeOffset oldestConsumedAt,
        int count)
    {
        await using TabrukDbContext context = database.CreateContext();
        for (int index = 0; index < count; index++)
        {
            DateTimeOffset consumedAt = oldestConsumedAt.AddMinutes(index);
            context.Set<IdentityUserToken<Guid>>().Add(
                new IdentityUserToken<Guid>
                {
                    UserId = userId,
                    LoginProvider = StepUpLoginProvider,
                    Name = $"grant:{locatorPrefix}-{index:D3}",
                    Value = JsonSerializer.Serialize(
                        new
                        {
                            purpose = "governance.assign",
                            tokenHash = new string('d', 64),
                            issuedAt = consumedAt.AddMinutes(-1),
                            expiresAt = consumedAt.AddMinutes(5),
                            consumedAt = (DateTimeOffset?)consumedAt,
                        }),
                });
        }

        await context.SaveChangesAsync();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<TokenSetResponse> LoginAsync(
        AuthApiHost host,
        string email,
        string password,
        Guid installationId)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/login",
            new { email, password },
            installationId: installationId);
        return await ReadRequiredAsync<TokenSetResponse>(response, HttpStatusCode.OK);
    }

    private static async Task<TokenSetResponse> RefreshAsync(
        AuthApiHost host,
        string refreshToken,
        Guid installationId,
        HttpStatusCode expectedStatus)
    {
        using HttpResponseMessage response = await PostJsonAsync(
            host.Client,
            "/api/v1/auth/refresh",
            new { refreshToken },
            installationId: installationId);
        return await ReadRequiredAsync<TokenSetResponse>(response, expectedStatus);
    }

    private static async Task<Guid> GetUserIdByEmailAsync(PostgresTestDatabase database, string email)
    {
        string normalizedEmail = email.ToUpperInvariant();
        await using TabrukDbContext context = database.CreateContext();
        return await context.Users
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Select(candidate => candidate.Id)
            .SingleAsync();
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    private static int ReadConsumedTokenCount(string? json)
    {
        using JsonDocument document = JsonDocument.Parse(json ?? throw new InvalidOperationException("Missing refresh family state JSON."));
        return document.RootElement.GetProperty("consumedTokenCount").GetInt32();
    }

    private static void AssertOpaqueBearer(string token)
    {
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.DoesNotContain('.', token);
        Assert.Matches("^[A-Za-z0-9_-]{64}$", token);
    }

    private static string DecodeJwtPayload(string token)
    {
        string payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        int remainder = payload.Length % 4;
        if (remainder > 0)
        {
            payload = payload.PadRight(payload.Length + (4 - remainder), '=');
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        object body,
        Guid? installationId = null,
        string? bearerToken = null,
        string? clientAddress = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        if (installationId.HasValue)
        {
            request.Headers.TryAddWithoutValidation(AuthHeaders.InstallationId, installationId.Value.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        if (!string.IsNullOrWhiteSpace(clientAddress))
        {
            request.Headers.TryAddWithoutValidation(TestClientAddressProvider.HeaderName, clientAddress);
        }

        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string bearerToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await client.SendAsync(request);
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<T>()
            ?? throw new InvalidOperationException($"The response did not contain a {typeof(T).Name} body.");
    }

    private sealed record PasswordVerificationAttempt(
        Guid? UserId,
        string HashedPassword,
        string ProvidedPassword);

    private sealed class ObservingPasswordHasher : IPasswordHasher<TabrukIdentityUser>
    {
        private readonly PasswordHasher<TabrukIdentityUser> inner = new();

        public List<PasswordVerificationAttempt> VerifyAttempts { get; } = [];

        public string HashPassword(TabrukIdentityUser user, string password) =>
            inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            TabrukIdentityUser user,
            string hashedPassword,
            string providedPassword)
        {
            VerifyAttempts.Add(new PasswordVerificationAttempt(user.Id, hashedPassword, providedPassword));
            return inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed record InvitedMemberSeed(
        UserId UserId,
        MembershipId MembershipId,
        string Email,
        string RawToken);

    private sealed class TestClientAddressProvider : IApiClientAddressProvider
    {
        public const string HeaderName = "X-Test-Client-Address";

        public string GetAddress(HttpContext context) =>
            context.Request.Headers[HeaderName].FirstOrDefault()
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
    }
}
