using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Application.Abstractions.Security;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Admin.Members;

public sealed record ListAdminMembersCommand(
    string? Cursor,
    int? PageSize);

public sealed record IssueMembershipInvitationCommand(
    string Email,
    int ExpiresInHours);

public sealed record AssignFoodInchargeCommand(
    MembershipId TargetMembershipId,
    string Reason);

public sealed record RevokeFoodInchargeCommand(
    MembershipId TargetMembershipId,
    string Reason);

public sealed record ProposeAdministratorRoleChangeCommand(
    MembershipId TargetMembershipId,
    AdministratorRoleChangeAction Action,
    string Reason);

public sealed record ApproveAdministratorRoleChangeCommand(
    RoleChangeRequestId RequestId,
    string Reason);

public sealed record DisableMembershipCommand(
    MembershipId TargetMembershipId,
    string Reason);

public sealed record BootstrapAdministratorsCommand(
    OrganizationId OrganizationId,
    IReadOnlyCollection<MembershipId> AdministratorMembershipIds,
    string Reason);

public sealed record AdminMembersPage(
    long Version,
    IReadOnlyList<AdminMemberSummary> Items,
    string? NextCursor);

public sealed record AdminMemberSummary(
    MembershipId Id,
    string DisplayName,
    MembershipStatus Status,
    bool EligibleAsNamedParticipant,
    IReadOnlyCollection<OrganizationRole> Roles,
    IReadOnlyCollection<AdminRoleChangeRequestSummary> PendingAdministratorRoleRequests);

public sealed record AdminRoleChangeRequestSummary(
    RoleChangeRequestId Id,
    MembershipId TargetMembershipId,
    AdministratorRoleChangeAction Action,
    MembershipId ProposerMembershipId,
    MembershipId? ApproverMembershipId,
    string Reason,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    RoleChangeRequestStatus Status);

public sealed record VersionedResult<T>(
    T Value,
    long Version);

public sealed record IssuedMembershipInvitation(
    MembershipId MembershipId,
    string InvitationToken,
    DateTimeOffset ExpiresAt);

public sealed record BootstrapAdministratorsResult(
    OrganizationId OrganizationId,
    IReadOnlyCollection<MembershipId> AdministratorMembershipIds,
    DateTimeOffset SealedAt,
    long Version);

public static class MemberAdministrationErrorCodes
{
    public const string InvalidAdminInput = "invalid_admin_input";
    public const string MemberNotFound = "member_not_found";
    public const string RoleChangeRequestNotFound = "role_change_request_not_found";
    public const string InvitationConflict = "invitation_conflict";

    public static DomainError Validation(string message) =>
        DomainError.Validation(InvalidAdminInput, message);

    public static DomainError NotFoundMember() =>
        DomainError.NotFound(MemberNotFound, "The member was not found.");

    public static DomainError NotFoundRoleChangeRequest() =>
        DomainError.NotFound(
            RoleChangeRequestNotFound,
            "The administrator role change request was not found.");

    public static DomainError InvitationConflictError() =>
        DomainError.Conflict(
            InvitationConflict,
            "The invitation cannot be issued for this account.");

    public static DomainError Forbidden() =>
        DomainError.Forbidden(
            "forbidden",
            "The current actor is not authorized to manage administrators.");

    public static DomainError StaleVersion() =>
        DomainError.PreconditionFailed(
            ErrorCodes.StaleVersion,
            "The administrator membership data changed. Refresh and retry.");
}

public static class MemberAdministrationStepUpPurposes
{
    public static StepUpPurpose InvitationIssue { get; } = new("membership.invite");
    public static StepUpPurpose MembershipDisable { get; } = new("membership.disable");
    public static StepUpPurpose GovernanceAssign { get; } = new("governance.assign");
    public static StepUpPurpose GovernanceApprove { get; } = new("governance.approve");
}

public sealed record MembershipAdministrationPersistenceEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages)
{
    public static MembershipAdministrationPersistenceEffects Empty { get; } =
        new([], [], []);
}

public sealed class IssueMembershipInvitationPersistenceRequest
{
    public IssueMembershipInvitationPersistenceRequest(
        UserId userId,
        MembershipId membershipId,
        OrganizationId organizationId,
        MembershipId issuedByMembershipId,
        string email,
        string invitationToken,
        string pendingDisplayName,
        bool eligibleAsNamedParticipant,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        MembershipAdministrationPersistenceEffects effects)
    {
        UserId = userId.EnsureValid();
        MembershipId = membershipId.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        IssuedByMembershipId = issuedByMembershipId.EnsureValid();
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(invitationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingDisplayName);
        ArgumentNullException.ThrowIfNull(effects);
        if (issuedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Invitation timestamps must be UTC.", nameof(issuedAt));
        }

        if (expiresAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Invitation timestamps must be UTC.", nameof(expiresAt));
        }

        if (expiresAt <= issuedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Expiry must be after issuance.");
        }

        Email = email.Trim();
        InvitationToken = invitationToken.Trim();
        PendingDisplayName = pendingDisplayName.Trim();
        EligibleAsNamedParticipant = eligibleAsNamedParticipant;
        IssuedAt = issuedAt;
        ExpiresAt = expiresAt;
        Effects = effects;
    }

    public UserId UserId { get; }
    public MembershipId MembershipId { get; }
    public OrganizationId OrganizationId { get; }
    public MembershipId IssuedByMembershipId { get; }
    public string Email { get; }
    public string InvitationToken { get; }
    public string PendingDisplayName { get; }
    public bool EligibleAsNamedParticipant { get; }
    public DateTimeOffset IssuedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public MembershipAdministrationPersistenceEffects Effects { get; }
}
