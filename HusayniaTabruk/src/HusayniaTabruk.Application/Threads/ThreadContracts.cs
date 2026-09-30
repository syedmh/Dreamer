using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Domain.Common.Enums;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Application.Threads;

public sealed record PostThreadMessageCommand(
    ServiceDateId ServiceDateId,
    string Body,
    IdempotencyKey IdempotencyKey);

public sealed record ReportThreadMessageCommand(
    ServiceDateId ServiceDateId,
    MessageId MessageId,
    MessageReportReason Reason,
    string? Comment);

public sealed record HideThreadMessageCommand(
    ServiceDateId ServiceDateId,
    MessageId MessageId,
    string Reason);

public sealed record LockThreadCommand(
    ServiceDateId ServiceDateId,
    string Reason);

public sealed record ReadPrivilegedThreadPageCommand(
    ServiceDateId ServiceDateId,
    string Reason,
    PrivilegedAccessPurpose Purpose,
    string CaseId,
    string? Cursor,
    int? PageSize);

public sealed record ThreadMessageSummary(
    MessageId Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt);

public sealed record ThreadMessagePage(
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    long Version,
    IReadOnlyList<ThreadMessageSummary> Items,
    string? NextCursor);

public sealed record VersionedThreadMessage(
    ThreadMessageSummary Message,
    long Version);

public sealed record VersionedThreadReport(
    long Version,
    bool WasDuplicate);

public sealed record VersionedThreadState(
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    long Version);

public sealed record PrivilegedThreadMessageSummary(
    MessageId Id,
    string SenderDisplayName,
    string Body,
    MessageVisibility Visibility,
    DateTimeOffset CreatedAt,
    DateTimeOffset? HiddenAt);

public sealed record PrivilegedThreadMessagePage(
    ThreadId ThreadId,
    ServiceDateId ServiceDateId,
    ThreadStatus Status,
    DateTimeOffset? LockedAt,
    IReadOnlyList<PrivilegedThreadMessageSummary> Items,
    string? NextCursor);

public static class ThreadApplicationErrorCodes
{
    public const string InvalidThreadRequest = "invalid_thread_request";
    public const string DuplicateReportMismatch = "duplicate_report_mismatch";
    public const string ThreadSenderDisplayUnavailable = "thread_sender_display_unavailable";
}
