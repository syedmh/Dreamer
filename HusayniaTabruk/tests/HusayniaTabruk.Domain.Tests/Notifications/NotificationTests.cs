using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;

namespace HusayniaTabruk.Domain.Tests.Notifications;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateProducesAuthoritativeInAppRecordAndGenericPushIntent()
    {
        Guid resourceId = Guid.CreateVersion7();

        NotificationCreated created = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.ThreadMessagePosted,
            NotificationResourceType.Thread,
            resourceId,
            "A new thread message is available",
            "Open Tabruk to view the update.",
            Now).Value;

        Assert.Equal("A new thread message is available", created.Notification.Title);
        Assert.Equal("Open Tabruk to view the update.", created.Notification.Body);
        Assert.False(created.Notification.IsRead);
        Assert.Equal(Notification.GenericPushTitle, created.PushIntent.GenericTitle);
        Assert.Equal(resourceId, created.PushIntent.ResourceId);
        Assert.DoesNotContain(created.Notification.Title, created.PushIntent.ToString());
        Assert.DoesNotContain(created.Notification.Body, created.PushIntent.ToString());
    }

    [Fact]
    public void PushIntentContainsNoMessageOrDirectContactPayload()
    {
        NotificationCreated created = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            Guid.CreateVersion7(),
            "Signup approved",
            "Private state details",
            Now).Value;

        string[] propertyNames = created.PushIntent.GetType()
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Body", propertyNames);
        Assert.DoesNotContain("Message", propertyNames);
        Assert.DoesNotContain("Email", propertyNames);
        Assert.DoesNotContain("Phone", propertyNames);
        Assert.Equal("New Tabruk update", created.PushIntent.GenericTitle);
    }

    [Fact]
    public void MarkReadIsMonotonicAndIdempotent()
    {
        Notification notification = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.ServiceDateCancelled,
            NotificationResourceType.ServiceDate,
            Guid.CreateVersion7(),
            "Date cancelled",
            "Open Tabruk for details.",
            Now).Value.Notification;

        NotificationRead first = notification.MarkRead(Now.AddMinutes(1)).Value;
        NotificationRead retry = notification.MarkRead(Now.AddMinutes(2)).Value;

        Assert.False(first.WasAlreadyRead);
        Assert.True(retry.WasAlreadyRead);
        Assert.Equal(first.ReadAt, retry.ReadAt);
        Assert.Equal(first.ReadAt, notification.ReadAt);
    }

    [Fact]
    public void InvalidStateAndChronologyAreRejectedWithoutMutation()
    {
        Result<NotificationCreated> invalidResource = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            Guid.Empty,
            "Title",
            "Body",
            Now);
        Assert.True(invalidResource.IsFailure);

        Notification notification = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.SignupStatusChanged,
            NotificationResourceType.Signup,
            Guid.CreateVersion7(),
            "Title",
            "Body",
            Now).Value.Notification;
        Result<NotificationRead> early = notification.MarkRead(Now.AddTicks(-1));

        Assert.True(early.IsFailure);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public void NotificationTimestampsMustBeUtc()
    {
        DateTimeOffset nonUtc = Now.ToOffset(TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(
            () => Notification.Create(
                NotificationId.New(),
                OrganizationId.New(),
                MembershipId.New(),
                NotificationType.AccountRoleChanged,
                NotificationResourceType.Membership,
                Guid.CreateVersion7(),
                "Role changed",
                "Open Tabruk for details.",
                nonUtc));
    }
}
