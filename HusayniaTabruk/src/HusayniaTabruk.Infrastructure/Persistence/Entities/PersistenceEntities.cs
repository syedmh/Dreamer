namespace HusayniaTabruk.Infrastructure.Persistence.Entities;

public sealed class OrganizationEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public int DefaultCancellationLeadMinutes { get; set; }
    public short Status { get; set; }
    public short BootstrapStatus { get; set; }
    public DateTimeOffset? BootstrapSealedAt { get; set; }
    public long Version { get; set; }
}

public sealed class MembershipEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public short Status { get; set; }
    public bool EligibleAsNamedParticipant { get; set; }
}

public sealed class InvitationEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string NormalizedEmail { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid IssuedByMembershipId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RoleAssignmentEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MembershipId { get; set; }
    public short Role { get; set; }
    public Guid AssignedByMembershipId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public Guid? RevokedByMembershipId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class RoleChangeRequestEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TargetMembershipId { get; set; }
    public short Action { get; set; }
    public Guid ProposerMembershipId { get; set; }
    public Guid? ApproverMembershipId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset ProposedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public short Status { get; set; }
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
}

public sealed class ServiceDateEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset CancellationDeadlineAt { get; set; }
    public Guid ManagerMembershipId { get; set; }
    public short Status { get; set; }
    public long Version { get; set; }
}

public sealed class HelpNeedEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ServiceDateId { get; set; }
    public short Category { get; set; }
    public string Instructions { get; set; } = string.Empty;
    public int? Capacity { get; set; }
    public short Status { get; set; }
    public long Version { get; set; }
    public long SignupVersion { get; set; }
    public long WaitlistOrderHighWater { get; set; }
}

public sealed class SignupEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ServiceDateId { get; set; }
    public Guid HelpNeedId { get; set; }
    public Guid PrimaryMembershipId { get; set; }
    public short Kind { get; set; }
    public string? Label { get; set; }
    public int UnnamedParticipantCount { get; set; }
    public short Status { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? LastTransitionAt { get; set; }
    public long? WaitlistOrder { get; set; }
    public long Version { get; set; }
}

public sealed class SignupMemberParticipantEntity
{
    public Guid SignupId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MembershipId { get; set; }
}

public sealed class DateThreadEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ServiceDateId { get; set; }
    public short Status { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public long Version { get; set; }
}

public sealed class ThreadMessageEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid AuthorMembershipId { get; set; }
    public Guid ClientMessageId { get; set; }
    public string Body { get; set; } = string.Empty;
    public short Visibility { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
}

public sealed class MessageReportEntity
{
    public Guid OrganizationId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ReporterMembershipId { get; set; }
    public short Reason { get; set; }
    public string? Comment { get; set; }
    public short State { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
}

public sealed class ThreadModerationEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ThreadId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid ActorMembershipId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class NotificationEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RecipientMembershipId { get; set; }
    public short Type { get; set; }
    public short ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public sealed class DeviceRegistrationEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid InstallationId { get; set; }
    public string ProviderToken { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class IdempotencyRecordEntity
{
    public Guid OrganizationId { get; set; }
    public Guid MembershipId { get; set; }
    public Guid Key { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public short Status { get; set; }
    public string? ResultReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorMembershipId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? BeforeState { get; set; }
    public string? AfterState { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PrivilegedAccessEventEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ActorMembershipId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public short Purpose { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public string? PageCursor { get; set; }
    public string PageHash { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
}
