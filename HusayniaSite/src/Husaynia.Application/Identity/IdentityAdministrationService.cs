using System.Net.Mail;
using System.Security.Cryptography;
using Husaynia.Application.Contracts;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Identity;

public sealed class IdentityAdministrationService(
    IIdentityAdministrationStore store,
    IIdentityAuditFinalizer auditFinalizer,
    AdministrativeCapabilityAuthorizer authorizer) : IUserAdministration
{
    internal const string InviteAction = "identity.user.invite";
    internal const string DisableAction = "identity.user.disable";
    internal const string RolesAction = "identity.user.roles.set";
    internal const string AuditEventsReadAction = "identity.audit.events.read";
    internal const string AuditSummaryReadAction = "identity.audit.summary.read";
    internal const string CapabilityReadAction = "identity.capability.read";

    private readonly IIdentityAdministrationStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IIdentityAuditFinalizer auditFinalizer =
        auditFinalizer ?? throw new ArgumentNullException(nameof(auditFinalizer));
    private readonly AdministrativeCapabilityAuthorizer authorizer =
        authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    public async Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
        InviteIdentityUserCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            InviteAction,
            targetType: "IdentityUser",
            targetId: HashEmailTarget(command.Email));
        var authorization = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            CapabilityAccess.Write,
            allowLimited: false);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<InviteIdentityUserReceipt>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        if (!TryNormalizeEmail(command.Email, out var normalizedEmail))
        {
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                descriptor,
                "invalid_email",
                "A valid email address is required.").ConfigureAwait(false);
        }

        if (!TryNormalizeRoles(command.Roles, out var normalizedRoles, out var roleError))
        {
            return await AuditAllowedFailureAsync<InviteIdentityUserReceipt>(
                descriptor,
                roleError.Code,
                roleError.Message).ConfigureAwait(false);
        }

        return await store.InviteAsync(
            command with { Email = normalizedEmail, Roles = normalizedRoles },
            descriptor,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
        DisableIdentityUserCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            DisableAction,
            targetType: "IdentityUser",
            targetId: command.UserId);
        var authorization = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            CapabilityAccess.Write,
            allowLimited: false);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<IdentityAccountMutationReceipt>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        if (!Guid.TryParse(command.UserId, out _))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                "invalid_user_id",
                "A valid user identifier is required.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(command.ExpectedConcurrencyStamp))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                "invalid_concurrency_token",
                "A concurrency token is required.").ConfigureAwait(false);
        }

        if (command.Reason is { Length: > 1_000 })
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                "invalid_reason",
                "The disable reason must be 1,000 characters or fewer.").ConfigureAwait(false);
        }

        return await store.DisableAsync(command, descriptor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
        SetIdentityUserRolesCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            RolesAction,
            targetType: "IdentityUser",
            targetId: command.UserId);
        var authorization = authorizer.Authorize(
            actor,
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            CapabilityAccess.Write,
            allowLimited: false);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<IdentityAccountMutationReceipt>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        if (!Guid.TryParse(command.UserId, out _))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                "invalid_user_id",
                "A valid user identifier is required.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(command.ExpectedConcurrencyStamp))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                "invalid_concurrency_token",
                "A concurrency token is required.").ConfigureAwait(false);
        }

        if (!TryNormalizeRoles(command.Roles, out var normalizedRoles, out var roleError))
        {
            return await AuditAllowedFailureAsync<IdentityAccountMutationReceipt>(
                descriptor,
                roleError.Code,
                roleError.Message).ConfigureAwait(false);
        }

        return await store.SetRolesAsync(
            command with { Roles = normalizedRoles },
            descriptor,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>> ReadAuditEventsAsync(
        ReadIdentityAuditEventsQuery query,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            AuditEventsReadAction,
            targetType: "AuditEvent",
            targetId: "events");
        var authorization = authorizer.Authorize(
            actor,
            AdministrativeCapability.AuditAndOperationalReports,
            CapabilityAccess.Read,
            allowLimited: false);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<IReadOnlyList<IdentityAuditEventView>>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        if (query.Take is < 1 or > 200)
        {
            return await AuditAllowedFailureAsync<IReadOnlyList<IdentityAuditEventView>>(
                descriptor,
                "invalid_take",
                "Audit queries must request between 1 and 200 events.").ConfigureAwait(false);
        }

        return await store.ReadAuditEventsAsync(query, descriptor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            AuditSummaryReadAction,
            targetType: "AuditEvent",
            targetId: "summary");
        var authorization = authorizer.Authorize(
            actor,
            AdministrativeCapability.AuditAndOperationalReports,
            CapabilityAccess.Read,
            allowLimited: true);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<IdentityAuditSummaryView>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        return await store.ReadAuditSummaryAsync(descriptor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<CapabilityProbeView, IdentityAdministrationError>> ProbeCapabilityAsync(
        AdministrativeCapability capability,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var descriptor = Descriptor(
            actor,
            CapabilityReadAction,
            targetType: nameof(AdministrativeCapability),
            targetId: capability.ToString());
        var authorization = authorizer.Authorize(
            actor,
            capability,
            CapabilityAccess.Read,
            allowLimited: true);
        if (!authorization.Allowed)
        {
            return await AuditDeniedAsync<CapabilityProbeView>(
                descriptor,
                authorization).ConfigureAwait(false);
        }

        await auditFinalizer.FinalizeOnceAsync(
            descriptor,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["capability"] = capability.ToString(),
                ["policy"] = AdministrativeCapabilityAuthorizer.GetPolicyName(capability),
                ["grantedAccess"] = authorization.GrantedAccess.ToString(),
            }).ConfigureAwait(false);

        return Result.Succeed<CapabilityProbeView, IdentityAdministrationError>(
            new CapabilityProbeView(
                capability,
                AdministrativeCapabilityAuthorizer.GetPolicyName(capability),
                authorization.GrantedAccess));
    }

    private async Task<Result<T, IdentityAdministrationError>> AuditDeniedAsync<T>(
        IdentityAuditDescriptor descriptor,
        CapabilityEvaluation authorization)
    {
        await auditFinalizer.FinalizeOnceAsync(
            descriptor,
            PrivilegedAttemptOutcome.Denied,
            new Dictionary<string, string?>
            {
                ["errorCode"] = authorization.ErrorCode,
                ["message"] = authorization.Message,
                ["grantedAccess"] = authorization.GrantedAccess.ToString(),
            }).ConfigureAwait(false);
        return Result.Fail<T, IdentityAdministrationError>(
            new IdentityAdministrationError(authorization.ErrorCode, authorization.Message));
    }

    private async Task<Result<T, IdentityAdministrationError>> AuditAllowedFailureAsync<T>(
        IdentityAuditDescriptor descriptor,
        string errorCode,
        string message)
    {
        await auditFinalizer.FinalizeOnceAsync(
            descriptor,
            PrivilegedAttemptOutcome.Allowed,
            new Dictionary<string, string?>
            {
                ["errorCode"] = errorCode,
                ["message"] = message,
            }).ConfigureAwait(false);
        return Result.Fail<T, IdentityAdministrationError>(
            new IdentityAdministrationError(errorCode, message));
    }

    private static IdentityAuditDescriptor Descriptor(
        AdministrativeRequestActor actor,
        string action,
        string targetType,
        string? targetId) =>
        new(
            actor.UserId,
            actor.Roles,
            action,
            targetType,
            NormalizeAuditTargetId(targetId),
            actor.CorrelationId);

    private static string? NormalizeAuditTargetId(string? targetId)
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

    private static string? HashEmailTarget(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var normalized = email.Trim().ToUpperInvariant();
        return "sha256:" + Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized)));
    }

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
            return normalizedEmail.Length <= 320;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRoles(
        IReadOnlyCollection<string> roles,
        out IReadOnlyList<string> normalizedRoles,
        out IdentityAdministrationError error)
    {
        normalizedRoles = [];
        error = new IdentityAdministrationError(string.Empty, string.Empty);
        ArgumentNullException.ThrowIfNull(roles);

        var ordered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role) ||
                !RoleNames.All.Contains(role.Trim()) ||
                !role.Equals(role.Trim(), StringComparison.Ordinal))
            {
                error = new IdentityAdministrationError(
                    "invalid_role",
                    "Assigned roles must exactly match the frozen role names.");
                return false;
            }

            if (!ordered.Add(role))
            {
                error = new IdentityAdministrationError(
                    "duplicate_role",
                    "Assigned roles must not contain duplicates.");
                return false;
            }
        }

        normalizedRoles = ordered.ToArray();
        return true;
    }
}
