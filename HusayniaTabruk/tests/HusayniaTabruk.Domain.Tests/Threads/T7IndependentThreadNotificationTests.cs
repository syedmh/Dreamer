using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Domain.Tests.Threads;

public sealed class T7IndependentThreadNotificationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EligibilityRejectsAuthorOrganizationAndDateSubstitution()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        Membership otherMember = CreateMembership(fixture.OrganizationId);
        Membership otherOrganizationMember = CreateMembership(OrganizationId.New());
        ServiceDate otherDate = CreateDate(
            fixture.OrganizationId,
            fixture.Manager.Id,
            ServiceDateId.New());

        Assert.True(
            fixture.Thread.AuthorizeRead(otherMember, fixture.Date, approved).IsFailure);
        Assert.True(
            fixture.Thread.AuthorizeRead(
                otherOrganizationMember,
                fixture.Date,
                approved).IsFailure);
        Assert.True(
            fixture.Thread.AuthorizeRead(fixture.Contact, otherDate, approved).IsFailure);
        Assert.Empty(fixture.Thread.Messages);
    }

    [Fact]
    public void LockedThreadRemainsReadableButRejectsNewPosts()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);

        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);

        Assert.True(
            fixture.Thread.AuthorizeRead(fixture.Contact, fixture.Date, approved).IsSuccess);
        Assert.True(
            fixture.Thread.Post(
                MessageId.New(),
                IdempotencyKey.New(),
                "New post",
                fixture.Contact,
                fixture.Date,
                approved,
                Now.AddMinutes(1)).IsFailure);
    }

    [Fact]
    public void ExactMessageAndReportScalarBoundariesAreAccepted()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        string message = string.Concat(
            Enumerable.Repeat("😀", ApplicationLimits.MaximumMessageUnicodeScalars));
        string report = string.Concat(
            Enumerable.Repeat("😀", ApplicationLimits.MaximumReportCommentUnicodeScalars));

        MessagePosted posted = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            message,
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value;
        MessageReported reported = fixture.Thread.Report(
            posted.Message.Id,
            MessageReportReason.Other,
            report,
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(1)).Value;

        Assert.Equal(ApplicationLimits.MaximumMessageUnicodeScalars, posted.Message.Body.EnumerateRunes().Count());
        Assert.Equal(ApplicationLimits.MaximumReportCommentUnicodeScalars, reported.Report.Comment!.EnumerateRunes().Count());
    }

    [Fact]
    public void DuplicatePostRemainsIdempotentAfterThreadIsLocked()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        IdempotencyKey clientMessageId = IdempotencyKey.New();
        MessagePosted first = fixture.Thread.Post(
            MessageId.New(),
            clientMessageId,
            "Original",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value;
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now.AddMinutes(1)).IsSuccess);

        Result<MessagePosted> retry = fixture.Thread.Post(
            MessageId.New(),
            clientMessageId,
            "Original",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(2));

        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value.WasDuplicate);
        Assert.Equal(first.Message.Id, retry.Value.Message.Id);
        Assert.Single(fixture.Thread.Messages);
    }

    [Fact]
    public void DuplicateReportRemainsIdempotentAfterMessageIsHidden()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Original",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;
        MessageReported first = fixture.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            "First",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(1)).Value;
        Assert.True(
            fixture.Thread.Hide(
                messageId,
                "Moderated",
                fixture.Manager,
                fixture.Date,
                Now.AddMinutes(2)).IsSuccess);

        Result<MessageReported> retry = fixture.Thread.Report(
            messageId,
            MessageReportReason.Other,
            "Retry",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(3));

        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value.WasDuplicate);
        Assert.Equal(first.Report, retry.Value.Report);
        Assert.Single(fixture.Thread.Reports);
    }

    [Fact]
    public void RehydrateRejectsReportThatPredatesItsMessage()
    {
        Fixture fixture = Fixture.Create();
        MessageId messageId = MessageId.New();
        ThreadMessage message = ThreadMessage.Rehydrate(
            messageId,
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Message",
            MessageVisibility.Visible,
            Now,
            hiddenAt: null).Value;
        MessageReport report = MessageReport.Rehydrate(
            messageId,
            fixture.Manager.Id,
            MessageReportReason.Other,
            null,
            Now.AddTicks(-1)).Value;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Open,
            lockedAt: null,
            version: 2,
            [message],
            [report]);

        Assert.True(result.IsFailure);
    }

    [Theory]
    [InlineData(ThreadStatus.Open, 0, true, false)]
    [InlineData(ThreadStatus.Locked, 0, false, false)]
    [InlineData(ThreadStatus.Open, 1, true, true)]
    [InlineData(ThreadStatus.Locked, 1, false, true)]
    public void RehydrateAcceptsOnlyReachableVersions(
        ThreadStatus status,
        long version,
        bool includeMessage,
        bool expectedSuccess)
    {
        Fixture fixture = Fixture.Create();
        ThreadMessage[] messages = includeMessage
            ? [
                ThreadMessage.Rehydrate(
                    MessageId.New(),
                    fixture.Thread.Id,
                    fixture.Contact.Id,
                    IdempotencyKey.New(),
                    "Message",
                    MessageVisibility.Visible,
                    Now,
                    hiddenAt: null).Value,
            ]
            : [];
        DateTimeOffset? lockedAt =
            status == ThreadStatus.Locked ? Now : null;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            status,
            lockedAt,
            version,
            messages,
            []);

        Assert.Equal(expectedSuccess, result.IsSuccess);
    }

    [Fact]
    public void RehydrateRejectsUnreachableSaturatedVersion()
    {
        Fixture fixture = Fixture.Create();
        ThreadMessage message = ThreadMessage.Rehydrate(
            MessageId.New(),
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Message",
            MessageVisibility.Visible,
            Now,
            hiddenAt: null).Value;
        Result<DateThread> saturated = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Open,
            lockedAt: null,
            long.MaxValue,
            [message],
            []);

        Assert.True(saturated.IsFailure);
        Assert.Equal(ThreadErrorCodes.InvalidThreadState, saturated.Error.Code);
    }

    [Fact]
    public void RehydrateDefensivelyCopiesInputCollections()
    {
        Fixture fixture = Fixture.Create();
        List<ThreadMessage> messages =
        [
            ThreadMessage.Rehydrate(
                MessageId.New(),
                fixture.Thread.Id,
                fixture.Contact.Id,
                IdempotencyKey.New(),
                "Message",
                MessageVisibility.Visible,
                Now,
                hiddenAt: null).Value,
        ];
        List<MessageReport> reports = [];
        DateThread hydrated = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Open,
            lockedAt: null,
            version: 1,
            messages,
            reports).Value;

        messages.Clear();
        reports.Add(
            MessageReport.Rehydrate(
                hydrated.Messages.Single().Id,
                fixture.Manager.Id,
                MessageReportReason.Other,
                null,
                Now.AddMinutes(1)).Value);

        Assert.Single(hydrated.Messages);
        Assert.Empty(hydrated.Reports);
    }

    [Fact]
    public void NotificationRejectsUnknownEnumsDefaultIdsAndInvalidChronology()
    {
        Assert.Throws<InvalidOperationException>(
            () => Notification.Create(
                default,
                OrganizationId.New(),
                MembershipId.New(),
                NotificationType.ThreadMessagePosted,
                NotificationResourceType.Thread,
                Guid.CreateVersion7(),
                "Title",
                "Body",
                Now));

        Assert.True(
            Notification.Create(
                NotificationId.New(),
                OrganizationId.New(),
                MembershipId.New(),
                (NotificationType)int.MaxValue,
                (NotificationResourceType)int.MaxValue,
                Guid.CreateVersion7(),
                "Title",
                "Body",
                Now).IsFailure);

        Assert.True(
            Notification.Rehydrate(
                NotificationId.New(),
                OrganizationId.New(),
                MembershipId.New(),
                NotificationType.ThreadMessagePosted,
                NotificationResourceType.Thread,
                Guid.CreateVersion7(),
                "Title",
                "Body",
                Now,
                Now.AddTicks(-1)).IsFailure);
    }

    [Fact]
    public void PushIntentContainsOnlyGenericContentAndReadIsIdempotent()
    {
        const string privateTitle = "Ali approved";
        const string privateBody = "Call +1 555 0100 about the message";
        NotificationCreated created = Notification.Create(
            NotificationId.New(),
            OrganizationId.New(),
            MembershipId.New(),
            NotificationType.ThreadMessagePosted,
            NotificationResourceType.Thread,
            Guid.CreateVersion7(),
            privateTitle,
            privateBody,
            Now).Value;

        string push = created.PushIntent.ToString();
        Assert.DoesNotContain(privateTitle, push, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBody, push, StringComparison.Ordinal);
        Assert.Equal(Notification.GenericPushTitle, created.PushIntent.GenericTitle);

        NotificationRead first = created.Notification.MarkRead(Now.AddMinutes(1)).Value;
        NotificationRead duplicate = created.Notification.MarkRead(Now.AddMinutes(2)).Value;
        Assert.False(first.WasAlreadyRead);
        Assert.True(duplicate.WasAlreadyRead);
        Assert.Equal(first.ReadAt, duplicate.ReadAt);
    }

    private static Signup CreateSignup(Fixture fixture, SignupStatus status)
    {
        long version = status == SignupStatus.Pending ? 0 : 1;
        DateTimeOffset? transitionedAt =
            status == SignupStatus.Pending ? null : Now.AddHours(-1);
        long? waitlistOrder = status == SignupStatus.Waitlisted ? 1 : null;

        return Signup.Rehydrate(
            SignupId.New(),
            fixture.OrganizationId,
            fixture.Date.Id,
            HelpNeedId.New(),
            fixture.Contact.Id,
            SignupKind.Individual,
            [],
            0,
            status,
            Now.AddHours(-2),
            transitionedAt,
            waitlistOrder,
            version).Value;
    }

    private static Membership CreateMembership(OrganizationId organizationId)
    {
        Membership membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            eligibleAsNamedParticipant: true).Value;
        Assert.True(membership.Activate(Now.AddHours(-4)).IsSuccess);
        return membership;
    }

    private static ServiceDate CreateDate(
        OrganizationId organizationId,
        MembershipId managerId,
        ServiceDateId id)
    {
        ServiceDate date = ServiceDate.Create(
            id,
            organizationId,
            "Service date",
            "Instructions",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(12),
            managerId).Value;
        Assert.True(date.Open(Now.AddHours(-1)).IsSuccess);
        return date;
    }

    private sealed record Fixture(
        OrganizationId OrganizationId,
        DateThread Thread,
        ServiceDate Date,
        Membership Contact,
        Membership Manager)
    {
        public static Fixture Create()
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership administrator = CreateMembership(organizationId);
            Membership secondAdministrator = CreateMembership(organizationId);
            Membership manager = CreateMembership(organizationId);
            Membership contact = CreateMembership(organizationId);
            OrganizationAccountGovernance governance =
                OrganizationAccountGovernance.Rehydrate(
                    organizationId,
                    version: 0,
                    AdministratorBootstrapStatus.Unsealed,
                    bootstrapSealedAt: null,
                    [administrator, secondAdministrator, manager, contact],
                    []).Value;
            Assert.True(
                governance.BootstrapAdministrators(
                    [administrator, secondAdministrator],
                    Now.AddHours(-3)).IsSuccess);
            Assert.True(
                governance.AssignFoodIncharge(
                    administrator,
                    manager,
                    Now.AddHours(-2)).IsSuccess);

            Membership storedManager = governance.Memberships.Single(
                member => member.Id == manager.Id);
            Membership storedContact = governance.Memberships.Single(
                member => member.Id == contact.Id);
            ServiceDate date = CreateDate(
                organizationId,
                storedManager.Id,
                ServiceDateId.New());
            DateThread thread =
                DateThread.Create(ThreadId.New(), organizationId, date.Id).Value;
            return new(organizationId, thread, date, storedContact, storedManager);
        }
    }
}
