using System.Net.Mail;
using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Husaynia.Web.Areas.Admin.Identity;

internal static class IdentityAdminEndpoints
{
    private static readonly HusayniaIdentityUser DummyPasswordUser =
        new("anonymous-verification@invalid.test");
    private static readonly string DummyPasswordHash =
        new PasswordHasher<HusayniaIdentityUser>().HashPassword(
            DummyPasswordUser,
            "AnonymousVerificationOnly!234");
    private static readonly string DummyInvitationHash =
        HusayniaIdentityToken.Hash("ANONYMOUS-INVITATION-VERIFICATION");
    private static readonly TimeSpan DeniedAuditTimeout = TimeSpan.FromSeconds(5);
    private const string InviteAction = "identity.user.invite";
    private const string DisableAction = "identity.user.disable";
    private const string RolesAction = "identity.user.roles.set";
    private const string CapabilityReadAction = "identity.capability.read";
    private const string AuditEventsReadAction = "identity.audit.events.read";
    private const string AuditSummaryReadAction = "identity.audit.summary.read";
    private const string MfaSetupAction = "identity.mfa.setup";
    private const string MfaEnableAction = "identity.mfa.enable";
    private const string LoginEndpointFamily = "login";
    private const string InvitationAcceptEndpointFamily = "invitation-accept";
    private static readonly AuthorizationPolicy IdentityMfaEnrollment =
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireRole([.. RoleNames.All])
            .Build();

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/identity");
        group.MapGet("/antiforgery", GetAntiforgeryAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapPost("/invitations/accept", AcceptInvitationAsync);
        group.MapGet("/self", GetSelfAsync);
        group.MapPost("/mfa/setup", GetMfaSetupAsync)
            .RequireAuthorization(IdentityMfaEnrollment)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.UsersRolesIntegrationsSettings,
                MfaSetupAction,
                "IdentityUser",
                static context => context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        group.MapPost("/mfa/enable", EnableMfaAsync)
            .RequireAuthorization(IdentityMfaEnrollment)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.UsersRolesIntegrationsSettings,
                MfaEnableAction,
                "IdentityUser",
                static context => context.User.FindFirstValue(ClaimTypes.NameIdentifier)));
        foreach (var capability in Enum.GetValues<AdministrativeCapability>())
        {
            MapCapabilityEndpoint(group, capability);
        }

        group.MapGet("/audit/events", ReadAuditEventsAsync)
            .RequireAuthorization(PolicyNames.AuditRead)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.AuditAndOperationalReports,
                AuditEventsReadAction,
                "AuditEvent",
                static _ => "events"));
        group.MapGet("/audit/summary", ReadAuditSummaryAsync)
            .RequireAuthorization(PolicyNames.AuditRead)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.AuditAndOperationalReports,
                AuditSummaryReadAction,
                "AuditEvent",
                static _ => "summary"));
        group.MapPost("/users/invite", InviteUserAsync)
            .RequireAuthorization(PolicyNames.SiteAdministration)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.UsersRolesIntegrationsSettings,
                InviteAction,
                "IdentityUser",
                static _ => "request"));
        group.MapPost("/users/{userId}/disable", DisableUserAsync)
            .RequireAuthorization(PolicyNames.SiteAdministration)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.UsersRolesIntegrationsSettings,
                DisableAction,
                "IdentityUser",
                static context => context.Request.RouteValues["userId"]?.ToString()));
        group.MapPut("/users/{userId}/roles", SetUserRolesAsync)
            .RequireAuthorization(PolicyNames.SiteAdministration)
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                AdministrativeCapability.UsersRolesIntegrationsSettings,
                RolesAction,
                "IdentityUser",
                static context => context.Request.RouteValues["userId"]?.ToString()));
    }

    private static void MapCapabilityEndpoint(
        RouteGroupBuilder group,
        AdministrativeCapability capability)
    {
        group.MapGet(
                $"/capabilities/{capability}",
                (
                    HttpContext httpContext,
                    ICorrelationContext correlationContext,
                    IIdentityAuditFinalizer auditFinalizer,
                    IUserAdministration administration,
                    CancellationToken cancellationToken) =>
                    ProbeCapabilityAsync(
                        httpContext,
                        capability.ToString(),
                        correlationContext,
                        auditFinalizer,
                        administration,
                        cancellationToken))
            .RequireAuthorization(AdministrativeCapabilityAuthorizer.GetPolicyName(capability))
            .WithMetadata(new IdentityPrivilegedEndpointMetadata(
                capability,
                CapabilityReadAction,
                nameof(AdministrativeCapability),
                _ => capability.ToString()));
    }

    internal static bool HasSatisfiedMfa(ClaimsPrincipal principal) =>
        principal.HasClaim(claim =>
            (claim.Type == "amr" && claim.Value.Equals("mfa", StringComparison.OrdinalIgnoreCase)) ||
            (claim.Type == "husaynia.mfa" && claim.Value.Equals("true", StringComparison.OrdinalIgnoreCase)));

    internal static string? NormalizeAuditTargetId(string? targetId)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return null;
        }

        var normalized = targetId.Trim();
        return normalized.Length <= 256
            ? normalized
            : "sha256:" + Convert.ToHexString(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

    private static Ok<AntiforgeryTokenResponse> GetAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(httpContext);
        return TypedResults.Ok(new AntiforgeryTokenResponse(tokens.RequestToken ?? string.Empty));
    }

    private static async Task<IResult> LoginAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAnonymousAdmission anonymousAdmission,
        ICorrelationContext correlationContext,
        IAuditWriter auditWriter,
        UserManager<HusayniaIdentityUser> userManager,
        SignInManager<HusayniaIdentityUser> signInManager,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, "identity.login");
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var admission = await anonymousAdmission.AttemptAsync(
                httpContext,
                LoginEndpointFamily,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return AnonymousAdmissionFailure(httpContext, admission, correlationContext);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<LoginRequest>(cancellationToken)
            .ConfigureAwait(false);
        if (request is null ||
            !TryNormalizeEmail(request.Email, out var normalizedEmail) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("invalid_login", "A valid email address and password are required.", correlationContext);
        }

        var user = await userManager.FindByEmailAsync(normalizedEmail).ConfigureAwait(false);
        var passwordIsValid = user is null
            ? userManager.PasswordHasher.VerifyHashedPassword(
                DummyPasswordUser,
                DummyPasswordHash,
                request.Password) != PasswordVerificationResult.Failed
            : await userManager.CheckPasswordAsync(user, request.Password).ConfigureAwait(false);

        string? failureCause = user switch
        {
            null => "account_not_found",
            { IsDisabled: true } => "account_disabled",
            { EmailConfirmed: false } => "account_not_confirmed",
            _ when await userManager.IsLockedOutAsync(user).ConfigureAwait(false) => "locked_out",
            _ when !passwordIsValid => "invalid_credentials",
            _ => null,
        };
        if (failureCause is not null)
        {
            if (user is not null &&
                !user.IsDisabled &&
                user.EmailConfirmed &&
                !passwordIsValid &&
                !await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
            {
                await userManager.AccessFailedAsync(user).ConfigureAwait(false);
                if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
                {
                    failureCause = "locked_out";
                }
            }

            await AuditAnonymousFailureAsync(
                    httpContext,
                    auditWriter,
                    "identity.login",
                    normalizedEmail,
                    failureCause)
                .ConfigureAwait(false);
            return InvalidAnonymousCredential(correlationContext);
        }

        ArgumentNullException.ThrowIfNull(user);
        var roles = (await userManager.GetRolesAsync(user).ConfigureAwait(false))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var hasPrivilegedRole = roles.Any(RoleNames.All.Contains);

        if (hasPrivilegedRole && user.TwoFactorEnabled)
        {
            var normalizedCode = NormalizeCode(request.OneTimeCode);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return Unauthorized(
                    "mfa_challenge_required",
                    "A valid authenticator code is required for privileged administration.",
                    correlationContext);
            }

            if (!await userManager.VerifyTwoFactorTokenAsync(
                    user,
                    userManager.Options.Tokens.AuthenticatorTokenProvider,
                    normalizedCode).ConfigureAwait(false))
            {
                await userManager.AccessFailedAsync(user).ConfigureAwait(false);
                if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
                {
                    return Results.Json(
                        FailurePayload("locked_out", "The account is locked out.", correlationContext),
                        statusCode: StatusCodes.Status423Locked);
                }

                return BadRequest(
                    "invalid_two_factor_code",
                    "The authenticator code is invalid.",
                    correlationContext);
            }

            await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            await IssueSessionAsync(signInManager, user, mfaSatisfied: true).ConfigureAwait(false);
            return Results.Ok(new LoginResponse(
                ToSessionView(user, roles, mfaSatisfied: true, requiresMfaEnrollment: false),
                "signed_in"));
        }

        await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        await IssueSessionAsync(signInManager, user, mfaSatisfied: false).ConfigureAwait(false);

        return Results.Ok(new LoginResponse(
            ToSessionView(
                user,
                roles,
                mfaSatisfied: false,
                requiresMfaEnrollment: hasPrivilegedRole && !user.TwoFactorEnabled),
            hasPrivilegedRole && !user.TwoFactorEnabled
                ? "mfa_enrollment_required"
                : "signed_in"));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, "identity.logout");
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> AcceptInvitationAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IdentityAnonymousAdmission anonymousAdmission,
        ICorrelationContext correlationContext,
        IAuditWriter auditWriter,
        UserManager<HusayniaIdentityUser> userManager,
        HusayniaIdentityDbContext dbContext,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, "identity.invitation.accept");
        var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
            .ConfigureAwait(false);
        if (antiforgeryFailure is not null)
        {
            return antiforgeryFailure;
        }

        var admission = await anonymousAdmission.AttemptAsync(
                httpContext,
                InvitationAcceptEndpointFamily,
                cancellationToken)
            .ConfigureAwait(false);
        if (!admission.IsAllowed)
        {
            return AnonymousAdmissionFailure(httpContext, admission, correlationContext);
        }

        var request = await httpContext.Request.ReadFromJsonAsync<AcceptInvitationRequest>(cancellationToken)
            .ConfigureAwait(false);
        if (request is null ||
            !TryNormalizeEmail(request.Email, out var normalizedEmail) ||
            string.IsNullOrWhiteSpace(request.Token) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(
                "invalid_invitation",
                "A valid invitation payload is required.",
                correlationContext);
        }

        var now = DateTimeOffset.UtcNow;
        var tokenHash = HusayniaIdentityToken.Hash(request.Token);
        var user = await userManager.FindByEmailAsync(normalizedEmail).ConfigureAwait(false);
        var failureCause = GetInvitationFailureCause(user, tokenHash, now);
        if (failureCause is not null)
        {
            await AuditAnonymousFailureAsync(
                    httpContext,
                    auditWriter,
                    "identity.invitation.accept",
                    normalizedEmail,
                    failureCause)
                .ConfigureAwait(false);
            return InvalidInvitation(correlationContext);
        }

        ArgumentNullException.ThrowIfNull(user);
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
        var normalizedDatabaseEmail = userManager.NormalizeEmail(normalizedEmail);
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 SELECT [Id]
                 FROM [IdentityUsers] WITH (UPDLOCK, HOLDLOCK)
                 WHERE [NormalizedEmail] = {normalizedDatabaseEmail}
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        await dbContext.Entry(user).ReloadAsync(cancellationToken).ConfigureAwait(false);
        var acceptanceTime = DateTimeOffset.UtcNow;
        failureCause = GetInvitationFailureCause(user, tokenHash, acceptanceTime);
        if (failureCause is not null)
        {
            await AuditAnonymousFailureAsync(
                    httpContext,
                    auditWriter,
                    "identity.invitation.accept",
                    normalizedEmail,
                    failureCause)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return InvalidInvitation(correlationContext);
        }

        if (!user.AcceptInvitation(tokenHash, acceptanceTime))
        {
            await AuditAnonymousFailureAsync(
                    httpContext,
                    auditWriter,
                    "identity.invitation.accept",
                    normalizedEmail,
                    "invitation_state_changed")
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return InvalidInvitation(correlationContext);
        }

        var addPasswordResult = await userManager.AddPasswordAsync(user, request.Password)
            .ConfigureAwait(false);
        if (!addPasswordResult.Succeeded)
        {
            return BadRequest(
                "invalid_password",
                "The supplied password does not satisfy the configured policy.",
                correlationContext);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(new InvitationAcceptedResponse(user.Id.ToString(), normalizedEmail));
    }

    private static async Task<IResult> GetSelfAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext,
        UserManager<HusayniaIdentityUser> userManager)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, "identity.self");
        var actor = await AuthenticatePrivilegedSelfAsync(httpContext, correlationContext)
            .ConfigureAwait(false);
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        var user = await userManager.FindByIdAsync(actor.Actor!.UserId!).ConfigureAwait(false);
        if (user is null)
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
            return Unauthorized("unauthenticated", "Authentication is required.", correlationContext);
        }

        return Results.Ok(ToSessionView(
            user,
            actor.Actor.Roles.Order(StringComparer.Ordinal).ToArray(),
            actor.Actor.HasSatisfiedMfa,
            actor.Actor.Roles.Count > 0 && !user.TwoFactorEnabled));
    }

    private static async Task<IResult> GetMfaSetupAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        HusayniaIdentityDbContext dbContext,
        UserManager<HusayniaIdentityUser> userManager,
        SignInManager<HusayniaIdentityUser> signInManager)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, MfaSetupAction);
        AdministrativeRequestActor? actor = null;
        string? key = null;
        bool mfaSatisfied = false;
        try
        {
            var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
                .ConfigureAwait(false);
            if (antiforgeryFailure is not null)
            {
                actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaSetupAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        "antiforgery_required")
                    .ConfigureAwait(false);
                return antiforgeryFailure;
            }

            actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
            if (!actor.IsAuthenticated || !actor.Roles.Any(RoleNames.All.Contains))
            {
                var errorCode = actor.IsAuthenticated ? "forbidden" : "unauthenticated";
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaSetupAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        errorCode)
                    .ConfigureAwait(false);
                return actor.IsAuthenticated
                    ? Forbidden("forbidden", "Administrative access is denied.", correlationContext)
                    : Unauthorized("unauthenticated", "Authentication is required.", correlationContext);
            }

            var user = await userManager.FindByIdAsync(actor.UserId!).ConfigureAwait(false);
            if (user is null)
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaSetupAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        "unauthenticated")
                    .ConfigureAwait(false);
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return Unauthorized("unauthenticated", "Authentication is required.", correlationContext);
            }

            if (user.TwoFactorEnabled)
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaSetupAction,
                        PrivilegedAttemptOutcome.Allowed,
                        "conflict",
                        "mfa_already_enabled")
                    .ConfigureAwait(false);
                return Results.Json(
                    FailurePayload(
                        "mfa_already_enabled",
                        "Multi-factor authentication is already enabled.",
                        correlationContext),
                    statusCode: StatusCodes.Status409Conflict);
            }

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None)
                .ConfigureAwait(false);
            key = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            var keyCreated = string.IsNullOrWhiteSpace(key);
            if (keyCreated)
            {
                var resetResult = await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
                if (!resetResult.Succeeded)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    await FinalizeMfaOutcomeAsync(
                            auditFinalizer,
                            actor,
                            MfaSetupAction,
                            PrivilegedAttemptOutcome.Allowed,
                            "failed",
                            "authenticator_key_generation_failed")
                        .ConfigureAwait(false);
                    return Results.Json(
                        FailurePayload(
                            "identity_failure",
                            "The authenticator key could not be generated.",
                            correlationContext),
                        statusCode: StatusCodes.Status500InternalServerError);
                }

                key = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaSetupAction,
                        PrivilegedAttemptOutcome.Allowed,
                        "failed",
                        "authenticator_key_generation_failed")
                    .ConfigureAwait(false);
                return Results.Json(
                    FailurePayload(
                        "identity_failure",
                        "The authenticator key could not be generated.",
                        correlationContext),
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await FinalizeMfaOutcomeAsync(
                    auditFinalizer,
                    actor,
                    MfaSetupAction,
                    PrivilegedAttemptOutcome.Allowed,
                    "key_ready",
                    errorCode: null,
                    keyCreated,
                    token => transaction.CommitAsync(token))
                .ConfigureAwait(false);
            mfaSatisfied = actor.HasSatisfiedMfa;
        }
        catch (Exception exception)
        {
            if (exception is not IdentityAuditFinalizationException)
            {
                dbContext.ChangeTracker.Clear();
            }

            return await HandleMfaExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    MfaSetupAction,
                    actor,
                    exception)
                .ConfigureAwait(false);
        }

        try
        {
            var user = await userManager.FindByIdAsync(actor!.UserId!).ConfigureAwait(false);
            if (user is null)
            {
                return Results.Json(
                    FailurePayload(
                        "unexpected_failure",
                        "The identity operation failed unexpectedly.",
                        correlationContext),
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            await IssueSessionAsync(signInManager, user, mfaSatisfied).ConfigureAwait(false);
            return Results.Ok(new MfaSetupResponse(key!, AlreadyEnabled: false));
        }
        catch
        {
            return Results.Json(
                FailurePayload(
                    "unexpected_failure",
                    "The identity operation failed unexpectedly.",
                    correlationContext),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> EnableMfaAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        HusayniaIdentityDbContext dbContext,
        UserManager<HusayniaIdentityUser> userManager,
        SignInManager<HusayniaIdentityUser> signInManager,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, MfaEnableAction);
        AdministrativeRequestActor? actor = null;
        HusayniaIdentityUser? user = null;
        try
        {
            var antiforgeryFailure = await ValidateAntiforgeryAsync(httpContext, antiforgery)
                .ConfigureAwait(false);
            if (antiforgeryFailure is not null)
            {
                actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        "antiforgery_required")
                    .ConfigureAwait(false);
                return antiforgeryFailure;
            }

            actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
            if (!actor.IsAuthenticated || !actor.Roles.Any(RoleNames.All.Contains))
            {
                var errorCode = actor.IsAuthenticated ? "forbidden" : "unauthenticated";
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        errorCode)
                    .ConfigureAwait(false);
                return actor.IsAuthenticated
                    ? Forbidden("forbidden", "Administrative access is denied.", correlationContext)
                    : Unauthorized("unauthenticated", "Authentication is required.", correlationContext);
            }

            var request = await httpContext.Request.ReadFromJsonAsync<EnableMfaRequest>(cancellationToken)
                .ConfigureAwait(false);
            if (request is null || string.IsNullOrWhiteSpace(request.OneTimeCode))
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Allowed,
                        "validation_failed",
                        "missing_two_factor_code")
                    .ConfigureAwait(false);
                return BadRequest(
                    "invalid_two_factor_code",
                    "An authenticator code is required.",
                    correlationContext);
            }

            var code = NormalizeCode(request.OneTimeCode);
            if (code.Length != 6 || code.Any(character => !char.IsAsciiDigit(character)))
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Allowed,
                        "validation_failed",
                        "malformed_two_factor_code")
                    .ConfigureAwait(false);
                return BadRequest(
                    "invalid_two_factor_code",
                    "The authenticator code is invalid.",
                    correlationContext);
            }

            user = await userManager.FindByIdAsync(actor.UserId!).ConfigureAwait(false);
            if (user is null)
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Denied,
                        "denied",
                        "unauthenticated")
                    .ConfigureAwait(false);
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                return Unauthorized("unauthenticated", "Authentication is required.", correlationContext);
            }

            await using var transaction = await dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None)
                .ConfigureAwait(false);
            var valid = await userManager.VerifyTwoFactorTokenAsync(
                    user,
                    userManager.Options.Tokens.AuthenticatorTokenProvider,
                    code)
                .ConfigureAwait(false);
            if (!valid)
            {
                var accessFailure = await userManager.AccessFailedAsync(user).ConfigureAwait(false);
                if (!accessFailure.Succeeded)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    return await FinalizeMfaEnableFailureAsync(
                            auditFinalizer,
                            actor,
                            correlationContext)
                        .ConfigureAwait(false);
                }

                var lockedOut = await userManager.IsLockedOutAsync(user).ConfigureAwait(false);
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        MfaEnableAction,
                        PrivilegedAttemptOutcome.Allowed,
                        lockedOut ? "locked_out" : "invalid_code",
                        lockedOut ? "locked_out" : "invalid_two_factor_code",
                        completePersistenceAsync: token => transaction.CommitAsync(token))
                    .ConfigureAwait(false);
                return lockedOut
                    ? Results.Json(
                        FailurePayload("locked_out", "The account is locked out.", correlationContext),
                        statusCode: StatusCodes.Status423Locked)
                    : BadRequest(
                        "invalid_two_factor_code",
                        "The authenticator code is invalid.",
                        correlationContext);
            }

            var resetFailures = await userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
            if (!resetFailures.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return await FinalizeMfaEnableFailureAsync(
                        auditFinalizer,
                        actor,
                        correlationContext)
                    .ConfigureAwait(false);
            }

            var enableResult = await userManager.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
            if (!enableResult.Succeeded)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return await FinalizeMfaEnableFailureAsync(
                        auditFinalizer,
                        actor,
                        correlationContext)
                    .ConfigureAwait(false);
            }

            await FinalizeMfaOutcomeAsync(
                    auditFinalizer,
                    actor,
                    MfaEnableAction,
                    PrivilegedAttemptOutcome.Allowed,
                    "enabled",
                    errorCode: null,
                    completePersistenceAsync: token => transaction.CommitAsync(token))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not IdentityAuditFinalizationException)
            {
                dbContext.ChangeTracker.Clear();
            }

            return await HandleMfaExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    MfaEnableAction,
                    actor,
                    exception)
                .ConfigureAwait(false);
        }

        try
        {
            await IssueSessionAsync(signInManager, user!, mfaSatisfied: true).ConfigureAwait(false);
            return Results.Ok(new LoginResponse(
                ToSessionView(
                    user!,
                    actor!.Roles.Order(StringComparer.Ordinal).ToArray(),
                    mfaSatisfied: true,
                    requiresMfaEnrollment: false),
                "mfa_enabled"));
        }
        catch
        {
            return Results.Json(
                FailurePayload(
                    "unexpected_failure",
                    "The identity operation failed unexpectedly.",
                    correlationContext),
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> FinalizeMfaEnableFailureAsync(
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        ICorrelationContext correlationContext)
    {
        await FinalizeMfaOutcomeAsync(
                auditFinalizer,
                actor,
                MfaEnableAction,
                PrivilegedAttemptOutcome.Allowed,
                "failed",
                "mfa_enable_failed")
            .ConfigureAwait(false);
        return Results.Json(
            FailurePayload(
                "identity_failure",
                "Multi-factor authentication could not be enabled.",
                correlationContext),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> ProbeCapabilityAsync(
        HttpContext httpContext,
        [FromRoute] string capability,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, CapabilityReadAction);
        try
        {
            if (!Enum.TryParse<AdministrativeCapability>(capability, true, out var parsedCapability))
            {
                var invalidCapabilityActor = await AuthenticateActorAsync(httpContext, correlationContext)
                    .ConfigureAwait(false);
                await FinalizeEndpointFailureAsync(
                        auditFinalizer,
                        invalidCapabilityActor,
                        CapabilityReadAction,
                        nameof(AdministrativeCapability),
                        capability,
                        "invalid_capability",
                        "The capability name is invalid.")
                    .ConfigureAwait(false);
                return BadRequest(
                    "invalid_capability",
                    "The capability name is invalid.",
                    correlationContext);
            }

            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var result = await administration.ProbeCapabilityAsync(
                    parsedCapability,
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    CapabilityReadAction,
                    nameof(AdministrativeCapability),
                    capability,
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> ReadAuditEventsAsync(
        HttpContext httpContext,
        [FromQuery] int? take,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, AuditEventsReadAction);
        try
        {
            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var result = await administration.ReadAuditEventsAsync(
                    new ReadIdentityAuditEventsQuery(take ?? 50),
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    AuditEventsReadAction,
                    "AuditEvent",
                    "events",
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> ReadAuditSummaryAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, AuditSummaryReadAction);
        try
        {
            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var result = await administration.ReadAuditSummaryAsync(actor, cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    AuditSummaryReadAction,
                    "AuditEvent",
                    "summary",
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> InviteUserAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, InviteAction);
        try
        {
            var antiforgeryFailure = await ValidatePrivilegedAntiforgeryAsync(
                    httpContext,
                    antiforgery,
                    correlationContext,
                    auditFinalizer,
                    InviteAction,
                    "IdentityUser",
                    targetId: "request")
                .ConfigureAwait(false);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var request = await httpContext.Request
                .ReadFromJsonAsync<InviteUserRequest>(cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                await FinalizeEndpointFailureAsync(
                        auditFinalizer,
                        actor,
                        InviteAction,
                        "IdentityUser",
                        "request",
                        "invalid_request",
                        "A request body is required.")
                    .ConfigureAwait(false);
                return BadRequest("invalid_request", "A request body is required.", correlationContext);
            }

            var result = await administration.InviteAsync(
                    new InviteIdentityUserCommand(request.Email, request.Roles ?? []),
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    InviteAction,
                    "IdentityUser",
                    "request",
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> DisableUserAsync(
        HttpContext httpContext,
        [FromRoute] string userId,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, DisableAction);
        try
        {
            var antiforgeryFailure = await ValidatePrivilegedAntiforgeryAsync(
                    httpContext,
                    antiforgery,
                    correlationContext,
                    auditFinalizer,
                    DisableAction,
                    "IdentityUser",
                    userId)
                .ConfigureAwait(false);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var request = await httpContext.Request
                .ReadFromJsonAsync<DisableUserRequest>(cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                await FinalizeEndpointFailureAsync(
                        auditFinalizer,
                        actor,
                        DisableAction,
                        "IdentityUser",
                        userId,
                        "invalid_request",
                        "A request body is required.")
                    .ConfigureAwait(false);
                return BadRequest("invalid_request", "A request body is required.", correlationContext);
            }

            var result = await administration.DisableAsync(
                    new DisableIdentityUserCommand(
                        userId,
                        request.ExpectedConcurrencyStamp,
                        request.Reason),
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    DisableAction,
                    "IdentityUser",
                    userId,
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IResult> SetUserRolesAsync(
        HttpContext httpContext,
        [FromRoute] string userId,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        IUserAdministration administration,
        CancellationToken cancellationToken)
    {
        using var _ = BeginCorrelation(httpContext, correlationContext, RolesAction);
        try
        {
            var antiforgeryFailure = await ValidatePrivilegedAntiforgeryAsync(
                    httpContext,
                    antiforgery,
                    correlationContext,
                    auditFinalizer,
                    RolesAction,
                    "IdentityUser",
                    userId)
                .ConfigureAwait(false);
            if (antiforgeryFailure is not null)
            {
                return antiforgeryFailure;
            }

            var actor = await AuthenticateActorAsync(httpContext, correlationContext)
                .ConfigureAwait(false);
            var request = await httpContext.Request
                .ReadFromJsonAsync<SetRolesRequest>(cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                await FinalizeEndpointFailureAsync(
                        auditFinalizer,
                        actor,
                        RolesAction,
                        "IdentityUser",
                        userId,
                        "invalid_request",
                        "A request body is required.")
                    .ConfigureAwait(false);
                return BadRequest("invalid_request", "A request body is required.", correlationContext);
            }

            var result = await administration.SetRolesAsync(
                    new SetIdentityUserRolesCommand(
                        userId,
                        request.ExpectedConcurrencyStamp,
                        request.Roles ?? []),
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            return ToHttpResult(result, correlationContext);
        }
        catch (Exception exception)
        {
            return await HandlePrivilegedExceptionAsync(
                    httpContext,
                    correlationContext,
                    auditFinalizer,
                    RolesAction,
                    "IdentityUser",
                    userId,
                    exception)
                .ConfigureAwait(false);
        }
    }

    private static async Task<AdministrativeRequestActor> AuthenticateActorAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext)
    {
        var result = await httpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme)
            .ConfigureAwait(false);
        var principal = result.Succeeded && result.Principal is not null
            ? result.Principal
            : new ClaimsPrincipal(new ClaimsIdentity());

        if (principal.Identity?.IsAuthenticated == true)
        {
            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var securityStamp = principal.FindFirstValue("husaynia.security_stamp");
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(securityStamp))
            {
                await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                principal = new ClaimsPrincipal(new ClaimsIdentity());
            }
            else
            {
                var userManager = httpContext.RequestServices
                    .GetRequiredService<UserManager<HusayniaIdentityUser>>();
                var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);
                if (user is null ||
                    !string.Equals(user.SecurityStamp, securityStamp, StringComparison.Ordinal))
                {
                    await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
                    principal = new ClaimsPrincipal(new ClaimsIdentity());
                }
            }
        }

        httpContext.User = principal;
        return BuildActor(httpContext.User, correlationContext.Current.CorrelationId);
    }

    private static async Task<(AdministrativeRequestActor? Actor, IResult? Result)> AuthenticatePrivilegedSelfAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext)
    {
        var actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
        if (!actor.IsAuthenticated)
        {
            return (null, Unauthorized("unauthenticated", "Authentication is required.", correlationContext));
        }

        if (!actor.Roles.Any(RoleNames.All.Contains))
        {
            return (null, Forbidden("forbidden", "Administrative access is denied.", correlationContext));
        }

        return (actor, null);
    }

    private static AdministrativeRequestActor BuildActor(
        ClaimsPrincipal principal,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var roles = principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role && RoleNames.All.Contains(claim.Value))
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        return new AdministrativeRequestActor(
            principal.Identity?.IsAuthenticated == true,
            principal.FindFirstValue(ClaimTypes.NameIdentifier),
            roles,
            HasSatisfiedMfa(principal),
            correlationId);
    }

    private static IDisposable BeginCorrelation(
        HttpContext httpContext,
        ICorrelationContext correlationContext,
        string operationName)
    {
        var supplied = httpContext.Request.Headers["X-Correlation-ID"].ToString();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            supplied = httpContext.TraceIdentifier;
        }

        return correlationContext.Begin(supplied, operationName);
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext).ConfigureAwait(false);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new
            {
                code = "antiforgery_required",
                message = "A valid antiforgery token is required.",
            });
        }
    }

    private static async Task<IResult?> ValidatePrivilegedAntiforgeryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        string targetType,
        string? targetId)
    {
        var failure = await ValidateAntiforgeryAsync(httpContext, antiforgery).ConfigureAwait(false);
        if (failure is null)
        {
            return null;
        }

        var actor = await AuthenticateActorAsync(httpContext, correlationContext).ConfigureAwait(false);
        await auditFinalizer.FinalizeOnceAsync(
                new IdentityAuditDescriptor(
                    actor.UserId,
                    actor.Roles,
                    action,
                    targetType,
                    NormalizeAuditTargetId(targetId),
                    actor.CorrelationId),
                PrivilegedAttemptOutcome.Denied,
                new Dictionary<string, string?>
                {
                    ["errorCode"] = "antiforgery_required",
                    ["message"] = "A valid antiforgery token is required.",
                })
            .ConfigureAwait(false);
        return failure;
    }

    private static Task FinalizeEndpointFailureAsync(
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string? targetId,
        string errorCode,
        string message) =>
        auditFinalizer.FinalizeOnceAsync(
            new IdentityAuditDescriptor(
                actor.UserId,
                actor.Roles,
                action,
                targetType,
                NormalizeAuditTargetId(targetId),
                actor.CorrelationId),
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["result"] = "validation_failed",
                ["errorCode"] = errorCode,
                ["message"] = message,
            });

    private static Task FinalizeMfaOutcomeAsync(
        IIdentityAuditFinalizer auditFinalizer,
        AdministrativeRequestActor actor,
        string action,
        PrivilegedAttemptOutcome outcome,
        string result,
        string? errorCode,
        bool? keyCreated = null,
        Func<CancellationToken, Task>? completePersistenceAsync = null)
    {
        var details = new Dictionary<string, string?>
        {
            ["result"] = result,
        };
        if (errorCode is not null)
        {
            details["errorCode"] = errorCode;
        }

        if (keyCreated is not null)
        {
            details["keyCreated"] = keyCreated.Value ? "true" : "false";
        }

        return auditFinalizer.FinalizeOnceAsync(
            new IdentityAuditDescriptor(
                actor.UserId,
                actor.Roles,
                action,
                "IdentityUser",
                NormalizeAuditTargetId(actor.UserId),
                actor.CorrelationId),
            outcome,
            details,
            completePersistenceAsync);
    }

    private static async Task<IResult> HandleMfaExceptionAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        AdministrativeRequestActor? actor,
        Exception exception)
    {
        if (exception is IdentityAuditFinalizationException)
        {
            return AuditUnavailable(correlationContext);
        }

        actor ??= BuildActor(httpContext.User, correlationContext.Current.CorrelationId);
        try
        {
            if (exception is BadHttpRequestException or JsonException)
            {
                await FinalizeMfaOutcomeAsync(
                        auditFinalizer,
                        actor,
                        action,
                        PrivilegedAttemptOutcome.Allowed,
                        "validation_failed",
                        "malformed_two_factor_code")
                    .ConfigureAwait(false);
                return BadRequest(
                    "invalid_two_factor_code",
                    "The authenticator code is invalid.",
                    correlationContext);
            }

            await FinalizeMfaOutcomeAsync(
                    auditFinalizer,
                    actor,
                    action,
                    PrivilegedAttemptOutcome.Allowed,
                    "exception",
                    "unexpected_failure")
                .ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return AuditUnavailable(correlationContext);
        }

        return Results.Json(
            FailurePayload(
                "unexpected_failure",
                "The identity operation failed unexpectedly.",
                correlationContext),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> HandlePrivilegedExceptionAsync(
        HttpContext httpContext,
        ICorrelationContext correlationContext,
        IIdentityAuditFinalizer auditFinalizer,
        string action,
        string targetType,
        string? targetId,
        Exception exception)
    {
        if (exception is IdentityAuditFinalizationException)
        {
            return AuditUnavailable(correlationContext);
        }

        var actor = BuildActor(httpContext.User, correlationContext.Current.CorrelationId);
        if (exception is BadHttpRequestException or JsonException)
        {
            try
            {
                await FinalizeEndpointFailureAsync(
                        auditFinalizer,
                        actor,
                        action,
                        targetType,
                        targetId,
                        "invalid_request",
                        "The request body is malformed.")
                    .ConfigureAwait(false);
            }
            catch (IdentityAuditFinalizationException)
            {
                return AuditUnavailable(correlationContext);
            }

            return BadRequest(
                "invalid_request",
                "The request body is malformed.",
                correlationContext);
        }

        try
        {
            await auditFinalizer.FinalizeOnceAsync(
                    new IdentityAuditDescriptor(
                        actor.UserId,
                        actor.Roles,
                        action,
                        targetType,
                        NormalizeAuditTargetId(targetId),
                        actor.CorrelationId),
                    PrivilegedAttemptOutcome.Allowed,
                    new Dictionary<string, string?>
                    {
                        ["result"] = "exception",
                        ["errorCode"] = "unexpected_failure",
                        ["message"] = "The identity operation failed unexpectedly.",
                    })
                .ConfigureAwait(false);
        }
        catch (IdentityAuditFinalizationException)
        {
            return AuditUnavailable(correlationContext);
        }

        return Results.Json(
            FailurePayload(
                "unexpected_failure",
                "The identity operation failed unexpectedly.",
                correlationContext),
            statusCode: StatusCodes.Status500InternalServerError);
    }

    internal static CancellationTokenSource CreateDeniedAuditCancellation(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var applicationLifetime = httpContext.RequestServices.GetService<IHostApplicationLifetime>();
        var cancellation = applicationLifetime is null
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(applicationLifetime.ApplicationStopping);
        cancellation.CancelAfter(DeniedAuditTimeout);
        return cancellation;
    }

    private static async Task AuditAnonymousFailureAsync(
        HttpContext httpContext,
        IAuditWriter auditWriter,
        string action,
        string normalizedEmail,
        string failureCause)
    {
        using var auditCancellation = CreateDeniedAuditCancellation(httpContext);
        var targetId = "sha256:" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalizedEmail.ToUpperInvariant())));
        await auditWriter.AppendAsync(
                new IdentityAuditDescriptor(
                    ActorId: null,
                    Roles: new HashSet<string>(StringComparer.Ordinal),
                    action,
                    TargetType: "IdentityUser",
                    targetId,
                    httpContext.RequestServices
                        .GetRequiredService<ICorrelationContext>()
                        .Current.CorrelationId),
                PrivilegedAttemptOutcome.Denied,
                new Dictionary<string, string?>
                {
                    ["cause"] = failureCause,
                },
                auditCancellation.Token)
            .ConfigureAwait(false);
    }

    private static IResult InvalidAnonymousCredential(ICorrelationContext correlationContext) =>
        Unauthorized(
            "invalid_credentials",
            "The supplied credentials are invalid.",
            correlationContext);

    private static IResult InvalidInvitation(ICorrelationContext correlationContext) =>
        BadRequest(
            "invalid_invitation",
            "The invitation is invalid.",
            correlationContext);

    private static string? GetInvitationFailureCause(
        HusayniaIdentityUser? user,
        string tokenHash,
        DateTimeOffset now)
    {
        var tokenIsValid = user?.HasValidInvitationToken(tokenHash, now) ??
            CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(DummyInvitationHash),
                System.Text.Encoding.UTF8.GetBytes(tokenHash));
        return user switch
        {
            null => "account_not_found",
            { IsDisabled: true } => "account_disabled",
            { HasPassword: true } => "account_already_active",
            { InvitationExpiresAtUtc: { } expiresAtUtc } when expiresAtUtc <= now =>
                "invitation_expired",
            _ when !tokenIsValid => "invalid_invitation_token",
            _ => null,
        };
    }

    private static async Task IssueSessionAsync(
        SignInManager<HusayniaIdentityUser> signInManager,
        HusayniaIdentityUser user,
        bool mfaSatisfied)
    {
        var additionalClaims = mfaSatisfied
            ? new[]
            {
                new Claim("amr", "mfa"),
                new Claim("husaynia.mfa", "true"),
                new Claim("husaynia.security_stamp", user.SecurityStamp ?? string.Empty),
            }
            : [new Claim("husaynia.security_stamp", user.SecurityStamp ?? string.Empty)];
        await signInManager.SignInWithClaimsAsync(
            user,
            isPersistent: false,
            additionalClaims).ConfigureAwait(false);
    }

    private static string NormalizeCode(string? code) =>
        new([.. (code ?? string.Empty)
            .Where(character => !char.IsWhiteSpace(character) && character != '-')]);

    private static SessionView ToSessionView(
        HusayniaIdentityUser user,
        IReadOnlyCollection<string> roles,
        bool mfaSatisfied,
        bool requiresMfaEnrollment) =>
        new(
            user.Id.ToString(),
            user.Email ?? string.Empty,
            roles.Order(StringComparer.Ordinal).ToArray(),
            mfaSatisfied,
            user.TwoFactorEnabled,
            requiresMfaEnrollment);

    private static bool TryNormalizeEmail(string value, out string normalizedEmail)
    {
        normalizedEmail = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            normalizedEmail = new MailAddress(value.Trim()).Address;
            return normalizedEmail.Length <= 256;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static IResult ToHttpResult<T>(
        Result<T, IdentityAdministrationError> result,
        ICorrelationContext correlationContext) =>
        result.IsSuccess
            ? Results.Ok(result.Success)
            : result.Error.Code switch
            {
                "unauthenticated" => Unauthorized(result.Error.Code, result.Error.Message, correlationContext),
                "forbidden" or "mfa_required" or "limited_access" or "account_disabled" => Forbidden(
                    result.Error.Code,
                    result.Error.Message,
                    correlationContext),
                "account_not_found" => Results.NotFound(
                    FailurePayload(result.Error.Code, result.Error.Message, correlationContext)),
                "concurrency_conflict" or "duplicate_account" or "account_already_active" => Results.Json(
                    FailurePayload(result.Error.Code, result.Error.Message, correlationContext),
                    statusCode: StatusCodes.Status409Conflict),
                "audit_read_failed" or "identity_persistence_failure" or "identity_failure" => Results.Json(
                    FailurePayload(result.Error.Code, result.Error.Message, correlationContext),
                    statusCode: StatusCodes.Status500InternalServerError),
                _ => BadRequest(result.Error.Code, result.Error.Message, correlationContext),
            };

    private static IResult Unauthorized(
        string code,
        string message,
        ICorrelationContext correlationContext) =>
        Results.Json(
            FailurePayload(code, message, correlationContext),
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(
        string code,
        string message,
        ICorrelationContext correlationContext) =>
        Results.Json(
            FailurePayload(code, message, correlationContext),
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult BadRequest(
        string code,
        string message,
        ICorrelationContext correlationContext) =>
        Results.BadRequest(FailurePayload(code, message, correlationContext));

    private static IResult AuditUnavailable(ICorrelationContext correlationContext) =>
        Results.Json(
            FailurePayload(
                "identity_audit_unavailable",
                "The identity service is temporarily unavailable.",
                correlationContext),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult AnonymousAdmissionFailure(
        HttpContext httpContext,
        IdentityAnonymousAdmissionDecision admission,
        ICorrelationContext correlationContext)
    {
        if (admission.IsDependencyUnavailable)
        {
            return Results.Json(
                FailurePayload(
                    "identity_rate_limit_unavailable",
                    "The identity service is temporarily unavailable.",
                    correlationContext),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        httpContext.Response.Headers.RetryAfter =
            admission.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(
            FailurePayload(
                "rate_limited",
                "Too many attempts. Try again later.",
                correlationContext),
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static FailureResponse FailurePayload(
        string code,
        string message,
        ICorrelationContext correlationContext) =>
        new(code, message, correlationContext.Current.CorrelationId);

    private static FailureResponse FailurePayload(
        string code,
        string message,
        string correlationId) =>
        new(code, message, correlationId);

    private sealed record LoginRequest(
        string Email,
        string Password,
        string? OneTimeCode);

    private sealed record InviteUserRequest(
        string Email,
        string[] Roles);

    private sealed record DisableUserRequest(
        string ExpectedConcurrencyStamp,
        string? Reason);

    private sealed record SetRolesRequest(
        string ExpectedConcurrencyStamp,
        string[] Roles);

    private sealed record AcceptInvitationRequest(
        string Email,
        string Token,
        string Password);

    private sealed record EnableMfaRequest(string OneTimeCode);
}

internal sealed record AntiforgeryTokenResponse(string RequestToken);

internal sealed record FailureResponse(string Code, string Message, string CorrelationId);

internal sealed record SessionView(
    string UserId,
    string Email,
    IReadOnlyCollection<string> Roles,
    bool MfaSatisfied,
    bool TwoFactorEnabled,
    bool RequiresMfaEnrollment);

internal sealed record LoginResponse(SessionView Session, string Status);

internal sealed record InvitationAcceptedResponse(string UserId, string Email);

internal sealed record MfaSetupResponse(string SharedKey, bool AlreadyEnabled);
