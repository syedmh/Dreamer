using HusayniaTabruk.Domain.Accounts;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Errors;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Dates;
using HusayniaTabruk.Domain.Signups;

namespace HusayniaTabruk.Domain.Threads;

public sealed class DateThread
{
    private readonly List<ThreadMessage> messages;
    private readonly List<MessageReport> reports;

    private DateThread(
        ThreadId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        ThreadStatus status,
        DateTimeOffset? lockedAt,
        long version,
        IReadOnlyCollection<ThreadMessage> messages,
        IReadOnlyCollection<MessageReport> reports)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        ServiceDateId = serviceDateId.EnsureValid();
        Status = status;
        LockedAt = lockedAt;
        Version = version;
        this.messages = messages.Select(message => message.DeepCopy()).ToList();
        this.reports = reports.Select(report => report.DeepCopy()).ToList();
    }

    public ThreadId Id { get; }
    public OrganizationId OrganizationId { get; }
    public ServiceDateId ServiceDateId { get; }
    public ThreadStatus Status { get; private set; }
    public DateTimeOffset? LockedAt { get; private set; }
    public long Version { get; private set; }
    public IReadOnlyCollection<ThreadMessage> Messages =>
        Array.AsReadOnly(messages.Select(message => message.DeepCopy()).ToArray());
    public IReadOnlyCollection<MessageReport> Reports =>
        Array.AsReadOnly(reports.Select(report => report.DeepCopy()).ToArray());

    public static Result<DateThread> Create(
        ThreadId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        serviceDateId.EnsureValid();

        return Result.Success(
            new DateThread(
                id,
                organizationId,
                serviceDateId,
                ThreadStatus.Open,
                lockedAt: null,
                version: 0,
                [],
                []));
    }

    public static Result<DateThread> Rehydrate(
        ThreadId id,
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        ThreadStatus status,
        DateTimeOffset? lockedAt,
        long version,
        IReadOnlyCollection<ThreadMessage> messages,
        IReadOnlyCollection<MessageReport> reports)
    {
        id.EnsureValid();
        organizationId.EnsureValid();
        serviceDateId.EnsureValid();
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(reports);
        if (lockedAt.HasValue)
        {
            ThreadText.EnsureUtc(lockedAt.Value);
        }

        if (!Enum.IsDefined(status)
            || (status == ThreadStatus.Locked) != lockedAt.HasValue
            || messages.Any(message => message is null)
            || reports.Any(report => report is null)
            || messages.Any(message => message.ThreadId != id)
            || messages.Select(message => message.Id).Distinct().Count() != messages.Count
            || messages.Select(message => message.ClientMessageId).Distinct().Count() != messages.Count
            || reports.Any(report => messages.All(message => message.Id != report.MessageId))
            || reports.Select(report => (report.MessageId, report.ReporterMembershipId)).Distinct().Count()
                != reports.Count)
        {
            return InvalidState("The persisted date thread state is inconsistent.");
        }

        long expectedVersion = messages.Count
            + (long)reports.Count
            + messages.Count(message => message.Visibility == MessageVisibility.Hidden)
            + (status == ThreadStatus.Locked ? 1L : 0L);
        if (version != expectedVersion
            || reports.Any(
                report => report.ReportedAt
                    < messages.Single(message => message.Id == report.MessageId).CreatedAt)
            || messages.Any(
                message => message.HiddenAt.HasValue
                    && reports.Any(
                        report => report.MessageId == message.Id
                            && report.ReportedAt > message.HiddenAt.Value))
            || (lockedAt.HasValue
                && (messages.Any(message => message.CreatedAt > lockedAt.Value)
                    || reports.Any(report => report.ReportedAt > lockedAt.Value)
                    || messages.Any(
                        message => message.HiddenAt.HasValue
                            && message.HiddenAt.Value > lockedAt.Value))))
        {
            return InvalidState("The persisted date thread history is unreachable.");
        }

        return Result.Success(
            new DateThread(
                id,
                organizationId,
                serviceDateId,
                status,
                lockedAt,
                version,
                messages,
                reports));
    }

    public Result AuthorizeRead(
        Membership actor,
        ServiceDate serviceDate,
        Signup? primaryContactSignup)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(serviceDate);

        return IsEligible(actor, serviceDate, primaryContactSignup)
            ? Result.Success()
            : Result.Failure(ThreadErrorCodes.Concealed());
    }

    public Result<MessagePosted> Post(
        MessageId messageId,
        IdempotencyKey clientMessageId,
        string body,
        Membership actor,
        ServiceDate serviceDate,
        Signup? primaryContactSignup,
        DateTimeOffset now)
    {
        ThreadText.EnsureUtc(now);
        messageId.EnsureValid();
        clientMessageId.EnsureValid();

        Result access = AuthorizeRead(actor, serviceDate, primaryContactSignup);
        if (access.IsFailure)
        {
            return Result.Failure<MessagePosted>(access.Error);
        }

        ThreadMessage? duplicate = messages.SingleOrDefault(
            message => message.ClientMessageId == clientMessageId);
        if (duplicate is not null)
        {
            if (duplicate.AuthorMembershipId != actor.Id || duplicate.Body != body)
            {
                return Result.Failure<MessagePosted>(
                    ThreadErrorCodes.Conflict(
                        ThreadErrorCodes.DuplicateClientMessage,
                        "The client message identifier is already bound to different content."));
            }

            return Result.Success(new MessagePosted(duplicate.DeepCopy(), WasDuplicate: true));
        }

        if (Status != ThreadStatus.Open)
        {
            return Result.Failure<MessagePosted>(
                ThreadErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "A locked thread does not accept new messages."));
        }

        Result content = ThreadText.ValidateMessage(body);
        if (content.IsFailure)
        {
            return Result.Failure<MessagePosted>(content.Error);
        }

        if (messages.Any(message => message.Id == messageId))
        {
            return Result.Failure<MessagePosted>(
                ThreadErrorCodes.Conflict(
                    ThreadErrorCodes.DuplicateClientMessage,
                    "The message identifier already exists."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<MessagePosted>(VersionExhausted());
        }

        ThreadMessage created = ThreadMessage.Create(
            messageId,
            Id,
            actor.Id,
            clientMessageId,
            body,
            now);
        messages.Add(created);
        Version++;
        return Result.Success(new MessagePosted(created.DeepCopy(), WasDuplicate: false));
    }

    public Result<MessageReported> Report(
        MessageId messageId,
        MessageReportReason reason,
        string? comment,
        Membership actor,
        ServiceDate serviceDate,
        Signup? primaryContactSignup,
        DateTimeOffset now)
    {
        ThreadText.EnsureUtc(now);
        messageId.EnsureValid();

        Result access = AuthorizeRead(actor, serviceDate, primaryContactSignup);
        if (access.IsFailure)
        {
            return Result.Failure<MessageReported>(access.Error);
        }

        ThreadMessage? message = messages.SingleOrDefault(candidate => candidate.Id == messageId);
        if (message is null)
        {
            return Result.Failure<MessageReported>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.MessageNotOwned,
                    "The message is not owned by this thread."));
        }

        if (now < message.CreatedAt)
        {
            return Result.Failure<MessageReported>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidReport,
                    "A message cannot be reported before it was created."));
        }

        Result validation = ThreadText.ValidateReport(reason, comment);
        if (validation.IsFailure)
        {
            return Result.Failure<MessageReported>(validation.Error);
        }

        MessageReport? duplicate = reports.SingleOrDefault(
            report => report.MessageId == messageId
                && report.ReporterMembershipId == actor.Id);
        if (duplicate is not null)
        {
            return Result.Success(new MessageReported(duplicate.DeepCopy(), WasDuplicate: true));
        }

        if (LockedAt.HasValue && now > LockedAt.Value)
        {
            return Result.Failure<MessageReported>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A message cannot be reported after its thread was locked."));
        }

        if (message.Visibility != MessageVisibility.Visible)
        {
            return Result.Failure<MessageReported>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.MessageNotOwned,
                    "The visible message is not owned by this thread."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<MessageReported>(VersionExhausted());
        }

        MessageReport created = MessageReport.Rehydrate(
            messageId,
            actor.Id,
            reason,
            comment,
            now).Value;
        reports.Add(created);
        Version++;
        return Result.Success(new MessageReported(created.DeepCopy(), WasDuplicate: false));
    }

    public Result<MessageHidden> Hide(
        MessageId messageId,
        string reason,
        Membership actor,
        ServiceDate serviceDate,
        DateTimeOffset now)
    {
        ThreadText.EnsureUtc(now);
        messageId.EnsureValid();

        if (!IsManagingFoodIncharge(actor, serviceDate))
        {
            return Result.Failure<MessageHidden>(ThreadErrorCodes.Concealed());
        }

        Result reasonValidation = ThreadText.ValidateReason(reason);
        if (reasonValidation.IsFailure)
        {
            return Result.Failure<MessageHidden>(reasonValidation.Error);
        }

        ThreadMessage? message = messages.SingleOrDefault(candidate => candidate.Id == messageId);
        if (message is null)
        {
            return Result.Failure<MessageHidden>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.MessageNotOwned,
                    "The message is not owned by this thread."));
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<MessageHidden>(VersionExhausted());
        }

        if (LockedAt.HasValue && now > LockedAt.Value)
        {
            return Result.Failure<MessageHidden>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A message cannot be hidden after its thread was locked."));
        }

        if (reports.Any(
            report => report.MessageId == messageId
                && report.ReportedAt > now))
        {
            return Result.Failure<MessageHidden>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A message cannot be hidden before one of its reports."));
        }

        Result hidden = message.Hide(now);
        if (hidden.IsFailure)
        {
            return Result.Failure<MessageHidden>(hidden.Error);
        }

        Version++;
        return Result.Success(
            new MessageHidden(message.DeepCopy(), actor.Id, reason.Trim(), now));
    }

    public Result<ThreadLocked> Lock(
        string reason,
        Membership actor,
        ServiceDate serviceDate,
        DateTimeOffset now)
    {
        ThreadText.EnsureUtc(now);

        if (!IsManagingFoodIncharge(actor, serviceDate))
        {
            return Result.Failure<ThreadLocked>(ThreadErrorCodes.Concealed());
        }

        if (Status == ThreadStatus.Locked)
        {
            if (LockedAt == now)
            {
                return Result.Success(
                    new ThreadLocked(
                        Id,
                        actor.Id,
                        reason?.Trim() ?? string.Empty,
                        now,
                        WasDuplicate: true));
            }

            return Result.Failure<ThreadLocked>(
                ThreadErrorCodes.Conflict(
                    ErrorCodes.InvalidTransition,
                    "The thread is already locked."));
        }

        Result reasonValidation = ThreadText.ValidateReason(reason);
        if (reasonValidation.IsFailure)
        {
            return Result.Failure<ThreadLocked>(reasonValidation.Error);
        }

        if (Version == long.MaxValue)
        {
            return Result.Failure<ThreadLocked>(VersionExhausted());
        }

        if (messages.Any(message => message.CreatedAt > now)
            || reports.Any(report => report.ReportedAt > now)
            || messages.Any(
                message => message.HiddenAt.HasValue
                    && message.HiddenAt.Value > now))
        {
            return Result.Failure<ThreadLocked>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A thread cannot be locked before its existing activity."));
        }

        Status = ThreadStatus.Locked;
        LockedAt = now;
        Version++;
        return Result.Success(
            new ThreadLocked(Id, actor.Id, reason.Trim(), now, WasDuplicate: false));
    }

    private bool IsEligible(
        Membership actor,
        ServiceDate serviceDate,
        Signup? primaryContactSignup) =>
        MatchesDate(serviceDate)
        && serviceDate.Status != ServiceDateStatus.Cancelled
        && actor.Status == MembershipStatus.Active
        && actor.OrganizationId == OrganizationId
        && (IsManagingFoodIncharge(actor, serviceDate)
            || IsApprovedPrimaryContact(actor, primaryContactSignup));

    private bool IsManagingFoodIncharge(Membership actor, ServiceDate serviceDate) =>
        MatchesDate(serviceDate)
        && serviceDate.Status != ServiceDateStatus.Cancelled
        && actor.Status == MembershipStatus.Active
        && actor.OrganizationId == OrganizationId
        && actor.Id == serviceDate.ManagerMembershipId
        && actor.HasRole(OrganizationRole.FoodIncharge);

    private bool IsApprovedPrimaryContact(Membership actor, Signup? signup) =>
        signup is not null
        && signup.OrganizationId == OrganizationId
        && signup.ServiceDateId == ServiceDateId
        && signup.PrimaryMembershipId == actor.Id
        && signup.Status == SignupStatus.Approved;

    private bool MatchesDate(ServiceDate serviceDate) =>
        serviceDate.Id == ServiceDateId
        && serviceDate.OrganizationId == OrganizationId;

    private static Result<DateThread> InvalidState(string message) =>
        Result.Failure<DateThread>(
            ThreadErrorCodes.Validation(ThreadErrorCodes.InvalidThreadState, message));

    private static DomainError VersionExhausted() =>
        ThreadErrorCodes.Conflict(
            "version_exhausted",
            "The thread version cannot be incremented.");
}

public sealed record MessagePosted(ThreadMessage Message, bool WasDuplicate);
public sealed record MessageReported(MessageReport Report, bool WasDuplicate);
public sealed record MessageHidden(
    ThreadMessage Message,
    MembershipId HiddenByMembershipId,
    string Reason,
    DateTimeOffset HiddenAt);
public sealed record ThreadLocked(
    ThreadId ThreadId,
    MembershipId LockedByMembershipId,
    string Reason,
    DateTimeOffset LockedAt,
    bool WasDuplicate);
