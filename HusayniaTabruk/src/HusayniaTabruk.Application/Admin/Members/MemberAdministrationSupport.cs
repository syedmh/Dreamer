using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Application.Admin.Members;

internal static class MemberAdministrationSupport
{
    private const int MaximumInvitationLifetimeHours = 24 * 30;
    private const int InvitationTokenBytes = 48;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Result<(string Email, int ExpiresInHours)> ValidateInvitation(
        IssueMembershipInvitationCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Email)
            || command.ExpiresInHours <= 0
            || command.ExpiresInHours > MaximumInvitationLifetimeHours)
        {
            return Result.Failure<(string, int)>(
                MemberAdministrationErrorCodes.Validation("The invitation request is invalid."));
        }

        string trimmedEmail = command.Email.Trim();
        try
        {
            MailAddress parsed = new(trimmedEmail);
            if (!string.Equals(parsed.Address, trimmedEmail, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Failure<(string, int)>(
                    MemberAdministrationErrorCodes.Validation("The invitation request is invalid."));
            }
        }
        catch (FormatException)
        {
            return Result.Failure<(string, int)>(
                MemberAdministrationErrorCodes.Validation("The invitation request is invalid."));
        }

        return Result.Success((trimmedEmail, command.ExpiresInHours));
    }

    public static Result<string> ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure<string>(
                MemberAdministrationErrorCodes.Validation("A reason is required."));
        }

        string trimmedReason = reason.Trim();
        if (trimmedReason.EnumerateRunes().Count() > ApplicationLimits.MaximumReasonUnicodeScalars
            || Encoding.UTF8.GetByteCount(trimmedReason) > ApplicationLimits.MaximumReasonUtf8Bytes)
        {
            return Result.Failure<string>(
                MemberAdministrationErrorCodes.Validation("The reason exceeds the allowed size."));
        }

        return Result.Success(trimmedReason);
    }

    public static Result<(int StartIndex, int PageSize)> ValidatePage(
        string? cursor,
        int? requestedPageSize)
    {
        if (requestedPageSize is <= 0)
        {
            return Result.Failure<(int, int)>(
                MemberAdministrationErrorCodes.Validation("The page size must be greater than zero."));
        }

        int pageSize = requestedPageSize is null
            ? ApplicationLimits.DefaultPageSize
            : Math.Min(requestedPageSize.Value, ApplicationLimits.MaximumPageSize);
        Result<int> startIndex = DecodeCursor(cursor);
        return startIndex.IsSuccess
            ? Result.Success((startIndex.Value, pageSize))
            : Result.Failure<(int, int)>(startIndex.Error);
    }

    public static string? EncodeCursor(int nextIndex, int totalCount)
    {
        if (nextIndex >= totalCount)
        {
            return null;
        }

        byte[] bytes = BitConverter.GetBytes(nextIndex);
        string base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static Result<Membership> ResolveAdministrator(
        OrganizationAccountGovernance governance,
        ICurrentActor currentActor)
    {
        ArgumentNullException.ThrowIfNull(governance);
        ArgumentNullException.ThrowIfNull(currentActor);

        Membership? actor = governance.Memberships.SingleOrDefault(
            membership => membership.Id == currentActor.MembershipId);
        if (actor is null
            || actor.Status != MembershipStatus.Active
            || !actor.HasRole(OrganizationRole.Admin))
        {
            return Result.Failure<Membership>(MemberAdministrationErrorCodes.Forbidden());
        }

        return Result.Success(actor);
    }

    public static Result<ActiveMembershipContext> EnsureAdministrator(
        ActiveMembershipContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return actor.Roles.Contains(OrganizationRole.Admin)
            ? Result.Success(actor)
            : Result.Failure<ActiveMembershipContext>(MemberAdministrationErrorCodes.Forbidden());
    }

    public static Result<Membership> FindMembership(
        OrganizationAccountGovernance governance,
        MembershipId membershipId)
    {
        ArgumentNullException.ThrowIfNull(governance);
        membershipId.EnsureValid();

        Membership? membership = governance.Memberships.SingleOrDefault(
            candidate => candidate.Id == membershipId);
        return membership is null
            ? Result.Failure<Membership>(MemberAdministrationErrorCodes.NotFoundMember())
            : Result.Success(membership);
    }

    public static Result<RoleChangeRequest> FindRoleChangeRequest(
        OrganizationAccountGovernance governance,
        RoleChangeRequestId requestId)
    {
        ArgumentNullException.ThrowIfNull(governance);
        requestId.EnsureValid();

        RoleChangeRequest? request = governance.RoleChangeRequests.SingleOrDefault(
            candidate => candidate.Id == requestId);
        return request is null
            ? Result.Failure<RoleChangeRequest>(MemberAdministrationErrorCodes.NotFoundRoleChangeRequest())
            : Result.Success(request);
    }

    public static Result EnsureExpectedVersion(
        long expectedVersion,
        OrganizationAccountGovernance governance)
    {
        ArgumentNullException.ThrowIfNull(governance);
        return expectedVersion == governance.OriginalVersion
            ? Result.Success()
            : Result.Failure(MemberAdministrationErrorCodes.StaleVersion());
    }

    public static string CreateInvitationToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(InvitationTokenBytes);
        string base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static AdminMemberSummary ToSummary(
        Membership membership,
        IReadOnlyCollection<AdminRoleChangeRequestSummary> pendingRequests)
    {
        ArgumentNullException.ThrowIfNull(membership);
        ArgumentNullException.ThrowIfNull(pendingRequests);

        return new AdminMemberSummary(
            membership.Id,
            membership.DisplayName,
            membership.Status,
            membership.IsEligibleAsNamedParticipant,
            membership.ActiveRoles.Order().ToArray(),
            pendingRequests);
    }

    public static AdminRoleChangeRequestSummary ToSummary(RoleChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AdminRoleChangeRequestSummary(
            request.Id,
            request.TargetMembershipId,
            request.Action,
            request.ProposerMembershipId,
            request.ApproverMembershipId,
            request.Reason,
            request.ProposedAt,
            request.ExpiresAt,
            request.ApprovedAt,
            request.Status);
    }

    public static MembershipAdministrationPersistenceEffects CreateEffects(
        IReadOnlyCollection<NotificationCreated> notifications,
        IReadOnlyCollection<AuditEntry> audits,
        IReadOnlyCollection<OutboxMessage>? additionalOutboxMessages = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(audits);

        List<OutboxMessage> outbox = notifications
            .Select(notification => CreatePushOutboxMessage(notification.PushIntent))
            .ToList();
        if (additionalOutboxMessages is not null)
        {
            outbox.AddRange(additionalOutboxMessages);
        }

        return new MembershipAdministrationPersistenceEffects(
            notifications.Select(notification => notification.Notification).ToArray(),
            audits.ToArray(),
            outbox.ToArray());
    }

    public static Result<NotificationCreated> CreateAccountRoleNotification(
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        Guid resourceId,
        string title,
        string body,
        DateTimeOffset occurredAt) =>
        Notification.Create(
            NotificationId.New(),
            organizationId,
            recipientMembershipId,
            NotificationType.AccountRoleChanged,
            NotificationResourceType.Membership,
            resourceId,
            title,
            body,
            occurredAt);

    public static AuditEntry CreateAuditEntry(
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        string action,
        string resourceType,
        string resourceId,
        string reason,
        string purpose,
        string? beforeState,
        string? afterState,
        DateTimeOffset occurredAt) =>
        new(
            AuditEventId.New(),
            organizationId,
            actorMembershipId,
            action,
            resourceType,
            resourceId,
            reason,
            purpose,
            Guid.NewGuid().ToString("N"),
            beforeState,
            afterState,
            occurredAt);

    public static MembershipAuditState ToAuditState(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);

        return new MembershipAuditState(
            membership.Id.ToString(),
            membership.Status.ToString(),
            membership.ActiveRoles
                .Order()
                .Select(role => role.ToString())
                .ToArray(),
            membership.IsEligibleAsNamedParticipant);
    }

    public static RoleChangeRequestAuditState ToAuditState(RoleChangeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RoleChangeRequestAuditState(
            request.Id.ToString(),
            request.TargetMembershipId.ToString(),
            request.Action.ToString(),
            request.ProposerMembershipId.ToString(),
            request.ApproverMembershipId?.ToString(),
            request.Reason,
            request.ProposedAt,
            request.ExpiresAt,
            request.ApprovedAt,
            request.Status.ToString());
    }

    public static GovernanceAuditState ToAuditState(OrganizationAccountGovernance governance)
    {
        ArgumentNullException.ThrowIfNull(governance);

        return new GovernanceAuditState(
            governance.OrganizationId.ToString(),
            governance.Version,
            governance.BootstrapStatus.ToString(),
            governance.BootstrapSealedAt,
            governance.Memberships
                .Where(membership => membership.Status == MembershipStatus.Active
                    && membership.HasRole(OrganizationRole.Admin))
                .Select(membership => membership.Id.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static string Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static Result<int> DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return Result.Success(0);
        }

        try
        {
            string normalized = cursor.Trim().Replace('-', '+').Replace('_', '/');
            int remainder = normalized.Length % 4;
            if (remainder > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
            }

            byte[] bytes = Convert.FromBase64String(normalized);
            if (bytes.Length != sizeof(int))
            {
                return Result.Failure<int>(
                    MemberAdministrationErrorCodes.Validation("The cursor is invalid."));
            }

            int startIndex = BitConverter.ToInt32(bytes, 0);
            return startIndex < 0
                ? Result.Failure<int>(
                    MemberAdministrationErrorCodes.Validation("The cursor is invalid."))
                : Result.Success(startIndex);
        }
        catch (FormatException)
        {
            return Result.Failure<int>(
                MemberAdministrationErrorCodes.Validation("The cursor is invalid."));
        }
    }

    private static OutboxMessage CreatePushOutboxMessage(PushNotificationRequested pushIntent)
    {
        ArgumentNullException.ThrowIfNull(pushIntent);

        return new OutboxMessage(
            OutboxMessageId.New(),
            pushIntent.OrganizationId,
            "notification.push.requested",
            Serialize(
                new
                {
                    notificationId = pushIntent.NotificationId.ToString(),
                    membershipId = pushIntent.RecipientMembershipId.ToString(),
                    resourceType = pushIntent.ResourceType.ToString(),
                    resourceId = pushIntent.ResourceId,
                    genericTitle = pushIntent.GenericTitle,
                    occurredAt = pushIntent.OccurredAt,
                }),
            pushIntent.OccurredAt);
    }
}

internal sealed record MembershipAuditState(
    string MembershipId,
    string Status,
    IReadOnlyCollection<string> Roles,
    bool EligibleAsNamedParticipant);

internal sealed record RoleChangeRequestAuditState(
    string RequestId,
    string TargetMembershipId,
    string Action,
    string ProposerMembershipId,
    string? ApproverMembershipId,
    string Reason,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    string Status);

internal sealed record GovernanceAuditState(
    string OrganizationId,
    long Version,
    string BootstrapStatus,
    DateTimeOffset? BootstrapSealedAt,
    IReadOnlyCollection<string> ActiveAdministratorMembershipIds);
