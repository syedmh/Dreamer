using HusayniaTabruk.Application.Abstractions.Audit;
using HusayniaTabruk.Application.Abstractions.Messaging;
using HusayniaTabruk.Domain.Common;
using HusayniaTabruk.Domain.Common.Identifiers;
using HusayniaTabruk.Domain.Notifications;
using HusayniaTabruk.Domain.Threads;

namespace HusayniaTabruk.Application.Abstractions.Persistence;

public sealed record LoadedDateThread(DateThread Thread, long LoadedVersion);

public sealed record ThreadModerationWrite(
    Guid Id,
    OrganizationId OrganizationId,
    ThreadId ThreadId,
    MessageId? MessageId,
    MembershipId ActorMembershipId,
    string Action,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed record ThreadPersistenceEffects(
    IReadOnlyCollection<Notification> Notifications,
    IReadOnlyCollection<AuditEntry> AuditEntries,
    IReadOnlyCollection<PrivilegedAccessEntry> PrivilegedAccessEntries,
    IReadOnlyCollection<OutboxMessage> OutboxMessages,
    IReadOnlyCollection<ThreadModerationWrite> ModerationEvents)
{
    public static ThreadPersistenceEffects Empty { get; } =
        new([], [], [], [], []);
}

public partial interface IThreadRepository
{
    ValueTask<Result<LoadedDateThread>> GetAsync(
        OrganizationId organizationId,
        ThreadId threadId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This thread repository does not support thread hydration.");

    ValueTask<Result<LoadedDateThread>> GetByServiceDateAsync(
        OrganizationId organizationId,
        ServiceDateId serviceDateId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This thread repository does not support service-date thread hydration.");

    ValueTask<Result> SaveAsync(
        LoadedDateThread loaded,
        ThreadPersistenceEffects effects,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            "This thread repository does not support thread persistence.");
}
