using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Domain.Threads;

public sealed class ThreadMessage
{
    private ThreadMessage(
        MessageId id,
        ThreadId threadId,
        MembershipId authorMembershipId,
        IdempotencyKey clientMessageId,
        string body,
        MessageVisibility visibility,
        DateTimeOffset createdAt,
        DateTimeOffset? hiddenAt)
    {
        Id = id.EnsureValid();
        ThreadId = threadId.EnsureValid();
        AuthorMembershipId = authorMembershipId.EnsureValid();
        ClientMessageId = clientMessageId.EnsureValid();
        Body = body;
        Visibility = visibility;
        CreatedAt = createdAt;
        HiddenAt = hiddenAt;
    }

    public MessageId Id { get; }
    public ThreadId ThreadId { get; }
    public MembershipId AuthorMembershipId { get; }
    public IdempotencyKey ClientMessageId { get; }
    public string Body { get; }
    public MessageVisibility Visibility { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? HiddenAt { get; private set; }

    public static Result<ThreadMessage> Rehydrate(
        MessageId id,
        ThreadId threadId,
        MembershipId authorMembershipId,
        IdempotencyKey clientMessageId,
        string body,
        MessageVisibility visibility,
        DateTimeOffset createdAt,
        DateTimeOffset? hiddenAt)
    {
        id.EnsureValid();
        threadId.EnsureValid();
        authorMembershipId.EnsureValid();
        clientMessageId.EnsureValid();
        ThreadText.EnsureUtc(createdAt);
        if (hiddenAt.HasValue)
        {
            ThreadText.EnsureUtc(hiddenAt.Value);
        }

        Result content = ThreadText.ValidateMessage(body);
        if (content.IsFailure)
        {
            return Result.Failure<ThreadMessage>(content.Error);
        }

        if (!Enum.IsDefined(visibility)
            || (visibility == MessageVisibility.Visible && hiddenAt.HasValue)
            || (visibility == MessageVisibility.Hidden
                && (!hiddenAt.HasValue || hiddenAt.Value < createdAt)))
        {
            return Result.Failure<ThreadMessage>(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "The persisted message visibility state is inconsistent."));
        }

        return Result.Success(
            new ThreadMessage(
                id,
                threadId,
                authorMembershipId,
                clientMessageId,
                body,
                visibility,
                createdAt,
                hiddenAt));
    }

    internal static ThreadMessage Create(
        MessageId id,
        ThreadId threadId,
        MembershipId authorMembershipId,
        IdempotencyKey clientMessageId,
        string body,
        DateTimeOffset createdAt) =>
        new(
            id,
            threadId,
            authorMembershipId,
            clientMessageId,
            body,
            MessageVisibility.Visible,
            createdAt,
            hiddenAt: null);

    internal Result Hide(DateTimeOffset hiddenAt)
    {
        ThreadText.EnsureUtc(hiddenAt);

        if (Visibility == MessageVisibility.Hidden)
        {
            return Result.Failure(
                ThreadErrorCodes.Conflict(
                    ThreadErrorCodes.MessageAlreadyHidden,
                    "The message is already hidden."));
        }

        if (hiddenAt < CreatedAt)
        {
            return Result.Failure(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A message cannot be hidden before it was created."));
        }

        Visibility = MessageVisibility.Hidden;
        HiddenAt = hiddenAt;
        return Result.Success();
    }

    internal ThreadMessage DeepCopy() =>
        new(
            Id,
            ThreadId,
            AuthorMembershipId,
            ClientMessageId,
            Body,
            Visibility,
            CreatedAt,
            HiddenAt);
}

public enum MessageReportReason
{
    Spam,
    Harassment,
    SensitiveInformation,
    Other,
}

public sealed record MessageReport
{
    private MessageReport(
        MessageId messageId,
        MembershipId reporterMembershipId,
        MessageReportReason reason,
        string? comment,
        DateTimeOffset reportedAt)
    {
        MessageId = messageId.EnsureValid();
        ReporterMembershipId = reporterMembershipId.EnsureValid();
        Reason = reason;
        Comment = comment;
        ReportedAt = reportedAt;
    }

    public MessageId MessageId { get; }
    public MembershipId ReporterMembershipId { get; }
    public MessageReportReason Reason { get; }
    public string? Comment { get; }
    public DateTimeOffset ReportedAt { get; }

    public static Result<MessageReport> Rehydrate(
        MessageId messageId,
        MembershipId reporterMembershipId,
        MessageReportReason reason,
        string? comment,
        DateTimeOffset reportedAt)
    {
        messageId.EnsureValid();
        reporterMembershipId.EnsureValid();
        ThreadText.EnsureUtc(reportedAt);

        Result validation = ThreadText.ValidateReport(reason, comment);
        return validation.IsFailure
            ? Result.Failure<MessageReport>(validation.Error)
            : Result.Success(
                new MessageReport(
                    messageId,
                    reporterMembershipId,
                    reason,
                    string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
                    reportedAt));
    }

    internal MessageReport DeepCopy() =>
        new(MessageId, ReporterMembershipId, Reason, Comment, ReportedAt);
}

internal static class ThreadText
{
    public static Result ValidateMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Result.Failure(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidMessageContent,
                    "A message body is required."));
        }

        if (body.EnumerateRunes().Count() > ApplicationLimits.MaximumMessageUnicodeScalars
            || System.Text.Encoding.UTF8.GetByteCount(body) > ApplicationLimits.MaximumMessageUtf8Bytes)
        {
            return Result.Failure(
                Common.Errors.DomainError.PayloadTooLarge(
                    "The message exceeds the permitted content size."));
        }

        return Result.Success();
    }

    public static Result ValidateReport(MessageReportReason reason, string? comment)
    {
        if (!Enum.IsDefined(reason))
        {
            return Result.Failure(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidReport,
                    "The report reason is invalid."));
        }

        if (comment is not null
            && (comment.EnumerateRunes().Count() > ApplicationLimits.MaximumReportCommentUnicodeScalars
                || System.Text.Encoding.UTF8.GetByteCount(comment) > ApplicationLimits.MaximumReportCommentUtf8Bytes))
        {
            return Result.Failure(
                Common.Errors.DomainError.PayloadTooLarge(
                    "The report comment exceeds the permitted content size."));
        }

        return Result.Success();
    }

    public static Result ValidateReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure(
                ThreadErrorCodes.Validation(
                    ThreadErrorCodes.InvalidThreadState,
                    "A moderation reason is required."));
        }

        if (reason.EnumerateRunes().Count() > ApplicationLimits.MaximumReasonUnicodeScalars
            || System.Text.Encoding.UTF8.GetByteCount(reason) > ApplicationLimits.MaximumReasonUtf8Bytes)
        {
            return Result.Failure(
                Common.Errors.DomainError.PayloadTooLarge(
                    "The moderation reason exceeds the permitted content size."));
        }

        return Result.Success();
    }

    public static void EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Domain timestamps must be UTC.", nameof(value));
        }
    }
}
