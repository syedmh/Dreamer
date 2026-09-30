using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Notifications;

public enum NotificationType
{
    SignupStatusChanged,
    ThreadMessagePosted,
    ServiceDateCancelled,
    AccountRoleChanged,
}

public enum NotificationResourceType
{
    Signup,
    Thread,
    ServiceDate,
    Membership,
}

public sealed class Notification
{
    public const string GenericPushTitle = "New Tabruk update";

    private Notification(
        NotificationId id,
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationType type,
        NotificationResourceType resourceType,
        Guid resourceId,
        string title,
        string body,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        RecipientMembershipId = recipientMembershipId.EnsureValid();
        Type = type;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Title = title;
        Body = body;
        CreatedAt = createdAt;
        ReadAt = readAt;
    }

    public NotificationId Id { get; }
    public OrganizationId OrganizationId { get; }
    public MembershipId RecipientMembershipId { get; }
    public NotificationType Type { get; }
    public NotificationResourceType ResourceType { get; }
    public Guid ResourceId { get; }
    public string Title { get; }
    public string Body { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? ReadAt { get; private set; }
    public bool IsRead => ReadAt.HasValue;

    public static Result<NotificationCreated> Create(
        NotificationId id,
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationType type,
        NotificationResourceType resourceType,
        Guid resourceId,
        string title,
        string body,
        DateTimeOffset createdAt)
    {
        Result<Notification> notification = Rehydrate(
            id,
            organizationId,
            recipientMembershipId,
            type,
            resourceType,
            resourceId,
            title,
            body,
            createdAt,
            readAt: null);
        if (notification.IsFailure)
        {
            return Result.Failure<NotificationCreated>(notification.Error);
        }

        Notification value = notification.Value;
        return Result.Success(
            new NotificationCreated(
                value,
                new PushNotificationRequested(
                    value.Id,
                    value.OrganizationId,
                    value.RecipientMembershipId,
                    value.ResourceType,
                    value.ResourceId,
                    GenericPushTitle,
                    value.CreatedAt)));
    }

    public static Result<Notification> Rehydrate(
        NotificationId id,
        OrganizationId organizationId,
        MembershipId recipientMembershipId,
        NotificationType type,
        NotificationResourceType resourceType,
        Guid resourceId,
        string title,
        string body,
        DateTimeOffset createdAt,
        DateTimeOffset? readAt)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        recipientMembershipId.EnsureValid();
        EnsureUtc(createdAt);
        if (readAt.HasValue)
        {
            EnsureUtc(readAt.Value);
        }

        if (!Enum.IsDefined(type)
            || !Enum.IsDefined(resourceType)
            || resourceId == Guid.Empty
            || string.IsNullOrWhiteSpace(title)
            || string.IsNullOrWhiteSpace(body)
            || (readAt.HasValue && readAt.Value < createdAt))
        {
            return Result.Failure<Notification>(
                Common.Errors.DomainError.Validation(
                    "invalid_notification_state",
                    "The notification state is invalid."));
        }

        return Result.Success(
            new Notification(
                id,
                organizationId,
                recipientMembershipId,
                type,
                resourceType,
                resourceId,
                title.Trim(),
                body.Trim(),
                createdAt,
                readAt));
    }

    public Result<NotificationRead> MarkRead(DateTimeOffset readAt)
    {
        EnsureUtc(readAt);

        if (readAt < CreatedAt)
        {
            return Result.Failure<NotificationRead>(
                Common.Errors.DomainError.Validation(
                    "invalid_notification_state",
                    "A notification cannot be read before it was created."));
        }

        if (ReadAt.HasValue)
        {
            return Result.Success(new NotificationRead(Id, ReadAt.Value, WasAlreadyRead: true));
        }

        ReadAt = readAt;
        return Result.Success(new NotificationRead(Id, readAt, WasAlreadyRead: false));
    }

    private static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}

public sealed record NotificationCreated(
    Notification Notification,
    PushNotificationRequested PushIntent);

public sealed record PushNotificationRequested(
    NotificationId NotificationId,
    OrganizationId OrganizationId,
    MembershipId RecipientMembershipId,
    NotificationResourceType ResourceType,
    Guid ResourceId,
    string GenericTitle,
    DateTimeOffset OccurredAt);

public sealed record NotificationRead(
    NotificationId NotificationId,
    DateTimeOffset ReadAt,
    bool WasAlreadyRead);
