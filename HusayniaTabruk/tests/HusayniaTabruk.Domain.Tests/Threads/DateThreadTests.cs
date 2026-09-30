using System.Reflection;
using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Domain.Tests.Threads;

public sealed class DateThreadTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SignupStatus.Pending, false)]
    [InlineData(SignupStatus.Approved, true)]
    [InlineData(SignupStatus.Waitlisted, false)]
    [InlineData(SignupStatus.Declined, false)]
    [InlineData(SignupStatus.Withdrawn, false)]
    [InlineData(SignupStatus.Cancelled, false)]
    public void OnlyApprovedPrimaryContactsMayReadAndPost(
        SignupStatus status,
        bool allowed)
    {
        Fixture fixture = Fixture.Create();
        Signup signup = CreateSignup(fixture, status);

        Result read = fixture.Thread.AuthorizeRead(
            fixture.Contact,
            fixture.Date,
            signup);
        Result<MessagePosted> post = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Coordination update",
            fixture.Contact,
            fixture.Date,
            signup,
            Now);

        Assert.Equal(allowed, read.IsSuccess);
        Assert.Equal(allowed, post.IsSuccess);
        if (!allowed)
        {
            Assert.Equal(ErrorType.NotFound, read.Error.Type);
            Assert.Equal(ErrorType.NotFound, post.Error.Type);
            Assert.Empty(fixture.Thread.Messages);
        }
    }

    [Fact]
    public void ManagingFoodInchargeMayUseThreadButOrdinaryAdministratorMayNot()
    {
        Fixture fixture = Fixture.Create();

        Assert.True(
            fixture.Thread.AuthorizeRead(
                fixture.Manager,
                fixture.Date,
                primaryContactSignup: null).IsSuccess);
        Assert.True(
            fixture.Thread.Post(
                MessageId.New(),
                IdempotencyKey.New(),
                "Manager update",
                fixture.Manager,
                fixture.Date,
                primaryContactSignup: null,
                Now).IsSuccess);

        Result administrator = fixture.Thread.AuthorizeRead(
            fixture.Administrator,
            fixture.Date,
            primaryContactSignup: null);
        Assert.True(administrator.IsFailure);
        Assert.Equal(ErrorType.NotFound, administrator.Error.Type);
    }

    [Fact]
    public void CurrentInputsRevokeAccessImmediately()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        Assert.True(
            fixture.Thread.AuthorizeRead(fixture.Contact, fixture.Date, approved).IsSuccess);

        Signup withdrawn = CreateSignup(fixture, SignupStatus.Withdrawn);
        Assert.True(
            fixture.Thread.AuthorizeRead(fixture.Contact, fixture.Date, withdrawn).IsFailure);

        ServiceDate cancelled = CreateDate(
            fixture.OrganizationId,
            fixture.Manager.Id,
            ServiceDateStatus.Cancelled);
        Assert.True(
            fixture.Thread.AuthorizeRead(fixture.Contact, cancelled, approved).IsFailure);

        Membership disabled = CreateMembership(fixture.OrganizationId, activate: false);
        Assert.True(disabled.Activate(Now.AddHours(-3)).IsSuccess);
        Membership disableActor = CreateMembership(fixture.OrganizationId);
        Membership disableApprover = CreateMembership(fixture.OrganizationId);
        OrganizationAccountGovernance governance = CreateGovernance(
            fixture.OrganizationId,
            [disableActor, disableApprover, disabled]);
        Assert.True(
            governance.BootstrapAdministrators(
                [disableActor, disableApprover],
                Now.AddHours(-2)).IsSuccess);
        Assert.True(
            governance.DisableMembership(
                disableActor,
                disabled,
                Now.AddHours(-1)).IsSuccess);
        Membership storedDisabled =
            governance.Memberships.Single(member => member.Id == disabled.Id);
        Signup disabledSignup = CreateSignup(
            fixture,
            SignupStatus.Approved,
            storedDisabled.Id);
        Assert.True(
            fixture.Thread.AuthorizeRead(storedDisabled, fixture.Date, disabledSignup).IsFailure);

        Membership outsider = CreateMembership(OrganizationId.New());
        Assert.True(
            fixture.Thread.AuthorizeRead(outsider, fixture.Date, approved).IsFailure);

        Assert.True(
            fixture.Governance.RevokeFoodIncharge(
                fixture.Administrator,
                fixture.Manager,
                Now.AddMinutes(1)).IsSuccess);
        Membership revokedManager = fixture.Governance.Memberships.Single(
            member => member.Id == fixture.Manager.Id);
        Assert.True(
            fixture.Thread.AuthorizeRead(
                revokedManager,
                fixture.Date,
                primaryContactSignup: null).IsFailure);
    }

    [Fact]
    public void PostEnforcesUnicodeAndUtf8BoundsWithoutMutation()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        string tooManyScalars =
            new('a', ApplicationLimits.MaximumMessageUnicodeScalars + 1);
        string tooManyBytes =
            string.Concat(Enumerable.Repeat(
                "😀",
                (ApplicationLimits.MaximumMessageUtf8Bytes / 4) + 1));

        Result<MessagePosted> scalarResult = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            tooManyScalars,
            fixture.Contact,
            fixture.Date,
            approved,
            Now);
        Result<MessagePosted> byteResult = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            tooManyBytes,
            fixture.Contact,
            fixture.Date,
            approved,
            Now);

        Assert.Equal(ErrorCodes.PayloadTooLarge, scalarResult.Error.Code);
        Assert.Equal(ErrorCodes.PayloadTooLarge, byteResult.Error.Code);
        Assert.Empty(fixture.Thread.Messages);
        Assert.Equal(0, fixture.Thread.Version);
    }

    [Fact]
    public void ClientMessageRetryIsIdempotentAndConflictingReuseIsRejected()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = MessageId.New();
        IdempotencyKey clientId = IdempotencyKey.New();

        MessagePosted created = fixture.Thread.Post(
            messageId,
            clientId,
            "Same body",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value;
        MessagePosted retry = fixture.Thread.Post(
            MessageId.New(),
            clientId,
            "Same body",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(1)).Value;
        Result<MessagePosted> conflict = fixture.Thread.Post(
            MessageId.New(),
            clientId,
            "Different body",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(2));

        Assert.False(created.WasDuplicate);
        Assert.True(retry.WasDuplicate);
        Assert.Equal(messageId, retry.Message.Id);
        Assert.Equal(ThreadErrorCodes.DuplicateClientMessage, conflict.Error.Code);
        Assert.Single(fixture.Thread.Messages);
        Assert.Equal(1, fixture.Thread.Version);
    }

    [Fact]
    public void ConflictingClientMessageReuseStillFailsAfterThreadIsLocked()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        IdempotencyKey clientId = IdempotencyKey.New();
        Assert.True(
            fixture.Thread.Post(
                MessageId.New(),
                clientId,
                "Original",
                fixture.Contact,
                fixture.Date,
                approved,
                Now).IsSuccess);
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now.AddMinutes(1)).IsSuccess);

        Result<MessagePosted> conflict = fixture.Thread.Post(
            MessageId.New(),
            clientId,
            "Different",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(2));

        Assert.True(conflict.IsFailure);
        Assert.Equal(ThreadErrorCodes.DuplicateClientMessage, conflict.Error.Code);
        Assert.Single(fixture.Thread.Messages);
        Assert.Equal(2, fixture.Thread.Version);
    }

    [Fact]
    public void ReportsAreBoundedAndDuplicateReporterMessagePairsAreIdempotent()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;

        MessageReported first = fixture.Thread.Report(
            messageId,
            MessageReportReason.SensitiveInformation,
            "Contains private contact information",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(1)).Value;
        MessageReported retry = fixture.Thread.Report(
            messageId,
            MessageReportReason.Other,
            "A different retry payload is not duplicated",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(2)).Value;
        Result<MessageReported> oversized = fixture.Thread.Report(
            messageId,
            MessageReportReason.Other,
            new string('x', ApplicationLimits.MaximumReportCommentUnicodeScalars + 1),
            fixture.Manager,
            fixture.Date,
            primaryContactSignup: null,
            Now.AddMinutes(3));

        Assert.False(first.WasDuplicate);
        Assert.True(retry.WasDuplicate);
        Assert.Equal(first.Report.Reason, retry.Report.Reason);
        Assert.Equal(ErrorCodes.PayloadTooLarge, oversized.Error.Code);
        Assert.Single(fixture.Thread.Reports);
    }

    [Fact]
    public void ReportCannotPredateMessageAndDoesNotMutateThread()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;

        Result<MessageReported> result = fixture.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            null,
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddTicks(-1));

        Assert.True(result.IsFailure);
        Assert.Equal(ThreadErrorCodes.InvalidReport, result.Error.Code);
        Assert.Empty(fixture.Thread.Reports);
        Assert.Equal(1, fixture.Thread.Version);
    }

    [Fact]
    public void LockCannotPredateExistingActivityAndDoesNotMutateThread()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        Assert.True(
            fixture.Thread.Post(
                MessageId.New(),
                IdempotencyKey.New(),
                "Message",
                fixture.Contact,
                fixture.Date,
                approved,
                Now.AddMinutes(2)).IsSuccess);

        Result<ThreadLocked> result = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Manager,
            fixture.Date,
            Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
        Assert.Equal(ThreadStatus.Open, fixture.Thread.Status);
        Assert.Equal(1, fixture.Thread.Version);
    }

    [Fact]
    public void IdenticalLockRetrySucceedsWithoutMutatingThreadAgain()
    {
        Fixture fixture = Fixture.Create();
        Result<ThreadLocked> initial = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Manager,
            fixture.Date,
            Now);
        long lockedVersion = fixture.Thread.Version;

        Result<ThreadLocked> retry = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Manager,
            fixture.Date,
            Now);

        Assert.True(initial.IsSuccess);
        Assert.True(retry.IsSuccess);
        Assert.False(initial.Value.WasDuplicate);
        Assert.True(retry.Value.WasDuplicate);
        Assert.Equal(initial.Value.ThreadId, retry.Value.ThreadId);
        Assert.Equal(initial.Value.LockedAt, retry.Value.LockedAt);
        Assert.Equal(ThreadStatus.Locked, fixture.Thread.Status);
        Assert.Equal(Now, fixture.Thread.LockedAt);
        Assert.Equal(lockedVersion, fixture.Thread.Version);
    }

    [Fact]
    public void ConflictingLockRetryFailsAtomically()
    {
        Fixture fixture = Fixture.Create();
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);
        long lockedVersion = fixture.Thread.Version;

        Result<ThreadLocked> retry = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Manager,
            fixture.Date,
            Now.AddTicks(1));

        Assert.True(retry.IsFailure);
        Assert.Equal(ErrorCodes.InvalidTransition, retry.Error.Code);
        Assert.Equal(ThreadStatus.Locked, fixture.Thread.Status);
        Assert.Equal(Now, fixture.Thread.LockedAt);
        Assert.Equal(lockedVersion, fixture.Thread.Version);
    }

    [Fact]
    public void IdenticalLockRetryStillRequiresCurrentManagerAuthorization()
    {
        Fixture fixture = Fixture.Create();
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);
        long lockedVersion = fixture.Thread.Version;

        Result<ThreadLocked> retry = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Administrator,
            fixture.Date,
            Now);

        Assert.True(retry.IsFailure);
        Assert.Equal(ErrorType.NotFound, retry.Error.Type);
        Assert.Equal(ThreadStatus.Locked, fixture.Thread.Status);
        Assert.Equal(Now, fixture.Thread.LockedAt);
        Assert.Equal(lockedVersion, fixture.Thread.Version);
    }

    [Fact]
    public void ConflictingLockRetryStillRequiresCurrentManagerAuthorization()
    {
        Fixture fixture = Fixture.Create();
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);
        long lockedVersion = fixture.Thread.Version;

        Result<ThreadLocked> retry = fixture.Thread.Lock(
            "Coordination complete",
            fixture.Administrator,
            fixture.Date,
            Now.AddTicks(1));

        Assert.True(retry.IsFailure);
        Assert.Equal(ErrorType.NotFound, retry.Error.Type);
        Assert.Equal(ThreadStatus.Locked, fixture.Thread.Status);
        Assert.Equal(Now, fixture.Thread.LockedAt);
        Assert.Equal(lockedVersion, fixture.Thread.Version);
    }

    [Fact]
    public void HideCannotPredateExistingReportAndDoesNotMutateThread()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;
        Assert.True(
            fixture.Thread.Report(
                messageId,
                MessageReportReason.Spam,
                null,
                fixture.Contact,
                fixture.Date,
                approved,
                Now.AddMinutes(2)).IsSuccess);

        Result<MessageHidden> result = fixture.Thread.Hide(
            messageId,
            "Moderated",
            fixture.Manager,
            fixture.Date,
            Now.AddMinutes(1));

        Assert.True(result.IsFailure);
        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
        Assert.Equal(MessageVisibility.Visible, fixture.Thread.Messages.Single().Visibility);
        Assert.Equal(2, fixture.Thread.Version);
    }

    [Fact]
    public void EqualActivityHideAndLockTimestampsAreAccepted()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;

        Assert.True(
            fixture.Thread.Report(
                messageId,
                MessageReportReason.Spam,
                null,
                fixture.Contact,
                fixture.Date,
                approved,
                Now).IsSuccess);
        Assert.True(
            fixture.Thread.Hide(
                messageId,
                "Moderated",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);

        Assert.Equal(Now, fixture.Thread.LockedAt);
        Assert.Equal(4, fixture.Thread.Version);
    }

    [Fact]
    public void NewReportsAndHidesCannotPostdateLock()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        MessageId messageId = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message.Id;
        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now).IsSuccess);

        Result<MessageReported> report = fixture.Thread.Report(
            messageId,
            MessageReportReason.Spam,
            null,
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddTicks(1));
        Result<MessageHidden> hide = fixture.Thread.Hide(
            messageId,
            "Moderated",
            fixture.Manager,
            fixture.Date,
            Now.AddTicks(1));

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, report.Error.Code);
        Assert.Equal(ThreadErrorCodes.InvalidThreadState, hide.Error.Code);
        Assert.Empty(fixture.Thread.Reports);
        Assert.Equal(MessageVisibility.Visible, fixture.Thread.Messages.Single().Visibility);
        Assert.Equal(2, fixture.Thread.Version);
    }

    [Fact]
    public void RehydrateRejectsLockTimestampStateMismatch()
    {
        Fixture fixture = Fixture.Create();

        Result<DateThread> lockedWithoutTimestamp = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: null,
            version: 1,
            [],
            []);
        Result<DateThread> openWithTimestamp = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Open,
            lockedAt: Now,
            version: 0,
            [],
            []);

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, lockedWithoutTimestamp.Error.Code);
        Assert.Equal(ThreadErrorCodes.InvalidThreadState, openWithTimestamp.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsActivityAfterLock()
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
        MessageReport lateReport = MessageReport.Rehydrate(
            messageId,
            fixture.Manager.Id,
            MessageReportReason.Spam,
            null,
            Now.AddTicks(1)).Value;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now,
            version: 3,
            [message],
            [lateReport]);

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsMessageAfterLock()
    {
        Fixture fixture = Fixture.Create();
        ThreadMessage lateMessage = ThreadMessage.Rehydrate(
            MessageId.New(),
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Message",
            MessageVisibility.Visible,
            Now.AddTicks(1),
            hiddenAt: null).Value;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now,
            version: 2,
            [lateMessage],
            []);

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsHideAfterLock()
    {
        Fixture fixture = Fixture.Create();
        ThreadMessage hiddenMessage = ThreadMessage.Rehydrate(
            MessageId.New(),
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Message",
            MessageVisibility.Hidden,
            Now,
            hiddenAt: Now.AddTicks(1)).Value;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now,
            version: 3,
            [hiddenMessage],
            []);

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsReportAfterMessageWasHidden()
    {
        Fixture fixture = Fixture.Create();
        MessageId messageId = MessageId.New();
        ThreadMessage hiddenMessage = ThreadMessage.Rehydrate(
            messageId,
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Message",
            MessageVisibility.Hidden,
            Now,
            hiddenAt: Now.AddMinutes(1)).Value;
        MessageReport lateReport = MessageReport.Rehydrate(
            messageId,
            fixture.Manager.Id,
            MessageReportReason.Spam,
            null,
            Now.AddMinutes(1).AddTicks(1)).Value;

        Result<DateThread> result = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Open,
            lockedAt: null,
            version: 3,
            [hiddenMessage],
            [lateReport]);

        Assert.Equal(ThreadErrorCodes.InvalidThreadState, result.Error.Code);
    }

    [Fact]
    public void RehydrateVersionCountsEverySuccessfulMutationExactlyOnce()
    {
        Fixture fixture = Fixture.Create();
        MessageId visibleMessageId = MessageId.New();
        MessageId hiddenMessageId = MessageId.New();
        ThreadMessage visibleMessage = ThreadMessage.Rehydrate(
            visibleMessageId,
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Visible",
            MessageVisibility.Visible,
            Now,
            hiddenAt: null).Value;
        ThreadMessage hiddenMessage = ThreadMessage.Rehydrate(
            hiddenMessageId,
            fixture.Thread.Id,
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Hidden",
            MessageVisibility.Hidden,
            Now,
            Now.AddMinutes(1)).Value;
        MessageReport report = MessageReport.Rehydrate(
            visibleMessageId,
            fixture.Manager.Id,
            MessageReportReason.Spam,
            null,
            Now.AddMinutes(1)).Value;

        Result<DateThread> exact = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now.AddMinutes(1),
            version: 5,
            [visibleMessage, hiddenMessage],
            [report]);
        Result<DateThread> under = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now.AddMinutes(1),
            version: 4,
            [visibleMessage, hiddenMessage],
            [report]);
        Result<DateThread> over = DateThread.Rehydrate(
            fixture.Thread.Id,
            fixture.OrganizationId,
            fixture.Date.Id,
            ThreadStatus.Locked,
            lockedAt: Now.AddMinutes(1),
            version: 6,
            [visibleMessage, hiddenMessage],
            [report]);

        Assert.True(exact.IsSuccess);
        Assert.Equal(Now.AddMinutes(1), exact.Value.LockedAt);
        Assert.True(under.IsFailure);
        Assert.True(over.IsFailure);
    }

    [Fact]
    public void OnlyCurrentManagerMayHideAndLockAndMessagesRemainImmutable()
    {
        Fixture fixture = Fixture.Create();
        Signup approved = CreateSignup(fixture, SignupStatus.Approved);
        ThreadMessage posted = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Original immutable body",
            fixture.Contact,
            fixture.Date,
            approved,
            Now).Value.Message;

        Assert.True(
            fixture.Thread.Hide(
                posted.Id,
                "Sensitive information",
                fixture.Contact,
                fixture.Date,
                Now.AddMinutes(1)).IsFailure);
        Assert.True(
            fixture.Thread.Hide(
                posted.Id,
                "Sensitive information",
                fixture.Manager,
                fixture.Date,
                Now.AddMinutes(1)).IsSuccess);
        ThreadMessage hidden = Assert.Single(fixture.Thread.Messages);
        Assert.Equal(MessageVisibility.Hidden, hidden.Visibility);
        Assert.Equal("Original immutable body", hidden.Body);
        Assert.Null(typeof(ThreadMessage).GetProperty("Body")!.SetMethod);
        Assert.DoesNotContain(
            typeof(ThreadMessage).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name is "Edit" or "Delete");

        Assert.True(
            fixture.Thread.Lock(
                "Coordination complete",
                fixture.Manager,
                fixture.Date,
                Now.AddMinutes(2)).IsSuccess);
        Assert.True(
            fixture.Thread.AuthorizeRead(
                fixture.Contact,
                fixture.Date,
                approved).IsSuccess);
        Result<MessagePosted> lockedPost = fixture.Thread.Post(
            MessageId.New(),
            IdempotencyKey.New(),
            "Late message",
            fixture.Contact,
            fixture.Date,
            approved,
            Now.AddMinutes(3));
        Assert.Equal(ErrorCodes.InvalidTransition, lockedPost.Error.Code);
    }

    [Fact]
    public void RehydrateRejectsMixedMessagesDuplicateReportsAndInvalidVersion()
    {
        Fixture fixture = Fixture.Create();
        ThreadMessage foreign = ThreadMessage.Rehydrate(
            MessageId.New(),
            ThreadId.New(),
            fixture.Contact.Id,
            IdempotencyKey.New(),
            "Foreign",
            MessageVisibility.Visible,
            Now,
            hiddenAt: null).Value;

        Assert.True(
            DateThread.Rehydrate(
                fixture.Thread.Id,
                fixture.OrganizationId,
                fixture.Date.Id,
                ThreadStatus.Open,
                lockedAt: null,
                0,
                [foreign],
                []).IsFailure);
        Assert.True(
            DateThread.Rehydrate(
                fixture.Thread.Id,
                fixture.OrganizationId,
                fixture.Date.Id,
                ThreadStatus.Open,
                lockedAt: null,
                -1,
                [],
                []).IsFailure);
    }

    private static Signup CreateSignup(
        Fixture fixture,
        SignupStatus status,
        MembershipId? primaryMembershipId = null)
    {
        long version = status switch
        {
            SignupStatus.Pending => 0,
            _ => 1,
        };
        DateTimeOffset? transitionAt =
            status == SignupStatus.Pending ? null : Now.AddHours(-1);
        long? waitlistOrder =
            status == SignupStatus.Waitlisted ? 1 : null;

        return Signup.Rehydrate(
            SignupId.New(),
            fixture.OrganizationId,
            fixture.Date.Id,
            HelpNeedId.New(),
            primaryMembershipId ?? fixture.Contact.Id,
            SignupKind.Individual,
            [],
            0,
            status,
            Now.AddHours(-2),
            transitionAt,
            waitlistOrder,
            version).Value;
    }

    private static Membership CreateMembership(
        OrganizationId organizationId,
        bool activate = true)
    {
        Membership membership = Membership.Invite(
            MembershipId.New(),
            organizationId,
            UserId.New(),
            "Member",
            eligibleAsNamedParticipant: true).Value;
        if (activate)
        {
            Assert.True(membership.Activate(Now.AddHours(-4)).IsSuccess);
        }

        return membership;
    }

    private static OrganizationAccountGovernance CreateGovernance(
        OrganizationId organizationId,
        IReadOnlyCollection<Membership> memberships) =>
        OrganizationAccountGovernance.Rehydrate(
            organizationId,
            version: 0,
            AdministratorBootstrapStatus.Unsealed,
            bootstrapSealedAt: null,
            memberships,
            []).Value;

    private static ServiceDate CreateDate(
        OrganizationId organizationId,
        MembershipId managerId,
        ServiceDateStatus status = ServiceDateStatus.Open)
    {
        ServiceDate draft = ServiceDate.Create(
            ServiceDateId.New(),
            organizationId,
            "Service date",
            "Instructions",
            Now.AddDays(1),
            Now.AddDays(1).AddHours(4),
            Now.AddHours(12),
            managerId).Value;
        Assert.True(draft.Open(Now.AddHours(-1)).IsSuccess);
        if (status == ServiceDateStatus.Cancelled)
        {
            Assert.True(draft.Cancel(Now).IsSuccess);
        }

        return draft;
    }

    private sealed record Fixture(
        OrganizationId OrganizationId,
        DateThread Thread,
        ServiceDate Date,
        Membership Contact,
        Membership Manager,
        Membership Administrator,
        Membership SecondAdministrator,
        OrganizationAccountGovernance Governance)
    {
        public static Fixture Create()
        {
            OrganizationId organizationId = OrganizationId.New();
            Membership administrator = CreateMembership(organizationId);
            Membership secondAdministrator = CreateMembership(organizationId);
            Membership manager = CreateMembership(organizationId);
            Membership contact = CreateMembership(organizationId);
            OrganizationAccountGovernance governance = CreateGovernance(
                organizationId,
                [administrator, secondAdministrator, manager, contact]);
            Assert.True(
                governance.BootstrapAdministrators(
                    [administrator, secondAdministrator],
                    Now.AddHours(-3)).IsSuccess);
            Assert.True(
                governance.AssignFoodIncharge(
                    administrator,
                    manager,
                    Now.AddHours(-2)).IsSuccess);

            Membership storedManager =
                governance.Memberships.Single(member => member.Id == manager.Id);
            Membership storedAdministrator =
                governance.Memberships.Single(member => member.Id == administrator.Id);
            Membership storedSecondAdministrator =
                governance.Memberships.Single(member => member.Id == secondAdministrator.Id);
            Membership storedContact =
                governance.Memberships.Single(member => member.Id == contact.Id);
            ServiceDate date = CreateDate(organizationId, storedManager.Id);
            DateThread thread =
                DateThread.Create(ThreadId.New(), organizationId, date.Id).Value;
            return new(
                organizationId,
                thread,
                date,
                storedContact,
                storedManager,
                storedAdministrator,
                storedSecondAdministrator,
                governance);
        }
    }
}
