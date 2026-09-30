using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Accounts;

public enum AdministratorRoleChangeAction
{
    Grant,
    Revoke,
}

public enum RoleChangeRequestStatus
{
    Pending,
    Approved,
}

public sealed class RoleChangeRequest
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(24);

    private RoleChangeRequest(
        RoleChangeRequestId id,
        OrganizationId organizationId,
        MembershipId targetMembershipId,
        AdministratorRoleChangeAction action,
        MembershipId proposerMembershipId,
        MembershipId? approverMembershipId,
        string reason,
        DateTimeOffset proposedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? approvedAt,
        RoleChangeRequestStatus status)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        TargetMembershipId = targetMembershipId.EnsureValid();
        Action = action;
        ProposerMembershipId = proposerMembershipId.EnsureValid();
        ApproverMembershipId = approverMembershipId;
        Reason = reason;
        ProposedAt = proposedAt;
        ExpiresAt = expiresAt;
        ApprovedAt = approvedAt;
        Status = status;
    }

    public RoleChangeRequestId Id { get; }
    public OrganizationId OrganizationId { get; }
    public MembershipId TargetMembershipId { get; }
    public AdministratorRoleChangeAction Action { get; }
    public MembershipId ProposerMembershipId { get; }
    public MembershipId? ApproverMembershipId { get; private set; }
    public string Reason { get; }
    public DateTimeOffset ProposedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public RoleChangeRequestStatus Status { get; private set; }

    public static Result<RoleChangeRequest> Rehydrate(
        RoleChangeRequestId id,
        OrganizationId organizationId,
        MembershipId targetMembershipId,
        AdministratorRoleChangeAction action,
        MembershipId proposerMembershipId,
        MembershipId? approverMembershipId,
        string reason,
        DateTimeOffset proposedAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? approvedAt,
        RoleChangeRequestStatus status)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        targetMembershipId.EnsureValid();
        proposerMembershipId.EnsureValid();

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountInput,
                    "An administrator role change reason is required."));
        }

        if (!Enum.IsDefined(action))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAdministratorRoleChangeAction,
                    "The administrator role change action is invalid."));
        }

        if (!Enum.IsDefined(status))
        {
            return InvalidState("The role change request status is invalid.");
        }

        if (approverMembershipId.HasValue)
        {
            approverMembershipId.Value.EnsureValid();
        }

        if (proposerMembershipId == targetMembershipId)
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleParticipantsNotDistinct,
                    "The proposer and target must be distinct memberships."));
        }

        if (proposedAt.Offset != TimeSpan.Zero
            || expiresAt.Offset != TimeSpan.Zero
            || (approvedAt.HasValue && approvedAt.Value.Offset != TimeSpan.Zero)
            || expiresAt != proposedAt.Add(RequestLifetime)
            || (approvedAt.HasValue && (approvedAt.Value < proposedAt || approvedAt.Value >= expiresAt)))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.RoleChangeRequestChronologyInvalid,
                    "The role change request chronology is invalid."));
        }

        bool pendingStateIsValid = status == RoleChangeRequestStatus.Pending
            && approverMembershipId is null
            && approvedAt is null;
        bool approvedStateIsValid = status == RoleChangeRequestStatus.Approved
            && approverMembershipId is not null
            && approvedAt is not null
            && approverMembershipId != proposerMembershipId
            && approverMembershipId != targetMembershipId;
        if (!pendingStateIsValid && !approvedStateIsValid)
        {
            return InvalidState("The role change request state is inconsistent.");
        }

        return Result.Success(
            new RoleChangeRequest(
                id,
                organizationId,
                targetMembershipId,
                action,
                proposerMembershipId,
                approverMembershipId,
                reason.Trim(),
                proposedAt,
                expiresAt,
                approvedAt,
                status));
    }

    internal static Result<RoleChangeRequest> Propose(
        RoleChangeRequestId id,
        AdministratorRoleChangeAction action,
        Membership proposer,
        Membership target,
        string reason,
        DateTimeOffset proposedAt)
    {
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(target);
        EnsureUtc(proposedAt);

        if (!Enum.IsDefined(action))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAdministratorRoleChangeAction,
                    "The administrator role change action is invalid."));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.InvalidAccountInput,
                    "An administrator role change reason is required."));
        }

        Result membershipValidation = ValidateActiveSameOrganization(proposer, target);
        if (membershipValidation.IsFailure)
        {
            return Result.Failure<RoleChangeRequest>(membershipValidation.Error);
        }

        if (!proposer.HasRole(OrganizationRole.Admin))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.AdministratorRoleRequired,
                    "The proposer must be an active administrator."));
        }

        if (proposer.Id == target.Id)
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleParticipantsNotDistinct,
                    "The proposer and target must be distinct memberships."));
        }

        bool targetIsAdministrator = target.HasRole(OrganizationRole.Admin);
        if ((action == AdministratorRoleChangeAction.Grant && targetIsAdministrator)
            || (action == AdministratorRoleChangeAction.Revoke && !targetIsAdministrator))
        {
            return Result.Failure<RoleChangeRequest>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "The requested administrator role transition is not valid for the target."));
        }

        return Result.Success(
            new RoleChangeRequest(
                id,
                proposer.OrganizationId,
                target.Id,
                action,
                proposer.Id,
                null,
                reason.Trim(),
                proposedAt,
                proposedAt.Add(RequestLifetime),
                null,
                RoleChangeRequestStatus.Pending));
    }

    internal Result<AdministratorRoleChangeApproved> Approve(
        Membership approver,
        Membership target,
        DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentNullException.ThrowIfNull(target);
        EnsureUtc(approvedAt);

        if (Status != RoleChangeRequestStatus.Pending)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "The role change request has already been approved."));
        }

        if (approvedAt < ProposedAt)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Validation(
                    AccountErrorCodes.RoleChangeRequestChronologyInvalid,
                    "Approval cannot occur before the request was proposed."));
        }

        if (approvedAt >= ExpiresAt)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleChangeRequestExpired,
                    "The role change request has expired."));
        }

        Result membershipValidation = ValidateActiveSameOrganization(approver, target);
        if (membershipValidation.IsFailure)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(membershipValidation.Error);
        }

        if (approver.OrganizationId != OrganizationId || target.Id != TargetMembershipId)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.OrganizationMismatch,
                    "The approval memberships do not match this role change request."));
        }

        if (approver.Id == ProposerMembershipId
            || approver.Id == TargetMembershipId
            || ProposerMembershipId == TargetMembershipId)
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.RoleParticipantsNotDistinct,
                    "The proposer, approver, and target must be distinct memberships."));
        }

        if (!approver.HasRole(OrganizationRole.Admin))
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.AdministratorRoleRequired,
                    "The approver must be an active administrator."));
        }

        bool targetIsAdministrator = target.HasRole(OrganizationRole.Admin);
        if ((Action == AdministratorRoleChangeAction.Grant && targetIsAdministrator)
            || (Action == AdministratorRoleChangeAction.Revoke && !targetIsAdministrator))
        {
            return Result.Failure<AdministratorRoleChangeApproved>(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.InvalidRoleTransition,
                    "The target's administrator role changed after this request was proposed."));
        }

        switch (Action)
        {
            case AdministratorRoleChangeAction.Grant:
                target.GrantAdministrator();
                break;
            case AdministratorRoleChangeAction.Revoke:
                target.RevokeAdministrator();
                break;
            default:
                return Result.Failure<AdministratorRoleChangeApproved>(
                    AccountErrorCodes.Validation(
                        AccountErrorCodes.InvalidAdministratorRoleChangeAction,
                        "The administrator role change action is invalid."));
        }

        ApproverMembershipId = approver.Id;
        ApprovedAt = approvedAt;
        Status = RoleChangeRequestStatus.Approved;

        return Result.Success(
            new AdministratorRoleChangeApproved(
                Id,
                OrganizationId,
                TargetMembershipId,
                Action,
                ProposerMembershipId,
                approver.Id,
                approvedAt));
    }

    internal RoleChangeRequest DeepCopy() =>
        new(
            Id,
            OrganizationId,
            TargetMembershipId,
            Action,
            ProposerMembershipId,
            ApproverMembershipId,
            Reason,
            ProposedAt,
            ExpiresAt,
            ApprovedAt,
            Status);

    private static Result ValidateActiveSameOrganization(Membership first, Membership second)
    {
        if (first.OrganizationId != second.OrganizationId)
        {
            return Result.Failure(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.OrganizationMismatch,
                    "Role governance cannot cross organization boundaries."));
        }

        if (first.Status != MembershipStatus.Active || second.Status != MembershipStatus.Active)
        {
            return Result.Failure(
                AccountErrorCodes.Conflict(
                    AccountErrorCodes.ActiveMembershipRequired,
                    "Role governance requires active memberships."));
        }

        return Result.Success();
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }

    private static Result<RoleChangeRequest> InvalidState(string message) =>
        Result.Failure<RoleChangeRequest>(
            AccountErrorCodes.Validation(
                AccountErrorCodes.InvalidAccountGovernanceState,
                message));
}

public sealed record AdministratorRoleChangeApproved(
    RoleChangeRequestId RequestId,
    OrganizationId OrganizationId,
    MembershipId TargetMembershipId,
    AdministratorRoleChangeAction Action,
    MembershipId ProposerMembershipId,
    MembershipId ApproverMembershipId,
    DateTimeOffset OccurredAt);
