using HusayniaTabruk.Application.Abstractions.Persistence;
using HusayniaTabruk.Domain.Common.Identifiers;

namespace HusayniaTabruk.Application.Abstractions.Audit;

public interface IAuditWriter
{
    ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

public interface IPrivilegedAccessWriter
{
    ValueTask WriteAsync(PrivilegedAccessEntry entry, CancellationToken cancellationToken = default);
}

public enum PrivilegedAccessPurpose
{
    Support = 0,
    Moderation = 1,
    Safeguarding = 2,
}

public sealed record AuditEntry
{
    public AuditEntry(
        AuditEventId id,
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        string action,
        string resourceType,
        string resourceId,
        string reason,
        string purpose,
        string correlationId,
        string? beforeState,
        string? afterState,
        DateTimeOffset occurredAt)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        ActorMembershipId = actorMembershipId.EnsureValid();
        Action = Required(action);
        ResourceType = Required(resourceType);
        ResourceId = Required(resourceId);
        Reason = Required(reason);
        Purpose = Required(purpose);
        CorrelationId = Required(correlationId);
        BeforeState = beforeState;
        AfterState = afterState;
        OccurredAt = occurredAt;
    }

    public AuditEventId Id { get; }
    public OrganizationId OrganizationId { get; }
    public MembershipId ActorMembershipId { get; }
    public string Action { get; }
    public string ResourceType { get; }
    public string ResourceId { get; }
    public string Reason { get; }
    public string Purpose { get; }
    public string CorrelationId { get; }
    public string? BeforeState { get; }
    public string? AfterState { get; }
    public DateTimeOffset OccurredAt { get; }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}

public sealed record PrivilegedAccessEntry
{
    public PrivilegedAccessEntry(
        AuditEventId id,
        OrganizationId organizationId,
        MembershipId actorMembershipId,
        string resourceType,
        string resourceId,
        string reason,
        PrivilegedAccessPurpose purpose,
        string caseId,
        string? pageCursor,
        RequestFingerprint pageHash,
        DateTimeOffset occurredAt)
    {
        Id = id.EnsureValid();
        OrganizationId = organizationId.EnsureValid();
        ActorMembershipId = actorMembershipId.EnsureValid();
        ResourceType = Required(resourceType);
        ResourceId = Required(resourceId);
        Reason = Required(reason);
        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose));
        }

        Purpose = purpose;
        CaseId = Required(caseId);
        PageCursor = pageCursor;
        ArgumentNullException.ThrowIfNull(pageHash);
        PageHash = pageHash;
        OccurredAt = occurredAt;
    }

    public AuditEventId Id { get; }
    public OrganizationId OrganizationId { get; }
    public MembershipId ActorMembershipId { get; }
    public string ResourceType { get; }
    public string ResourceId { get; }
    public string Reason { get; }
    public PrivilegedAccessPurpose Purpose { get; }
    public string CaseId { get; }
    public string? PageCursor { get; }
    public RequestFingerprint PageHash { get; }
    public DateTimeOffset OccurredAt { get; }

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
