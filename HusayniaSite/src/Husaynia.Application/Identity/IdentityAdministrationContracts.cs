using Husaynia.Application.Contracts;
using Husaynia.Domain.Identity;

namespace Husaynia.Application.Identity;

public interface IUserAdministration
{
    Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
        InviteIdentityUserCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
        DisableIdentityUserCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
        SetIdentityUserRolesCommand command,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>> ReadAuditEventsAsync(
        ReadIdentityAuditEventsQuery query,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);

    Task<Result<CapabilityProbeView, IdentityAdministrationError>> ProbeCapabilityAsync(
        AdministrativeCapability capability,
        AdministrativeRequestActor actor,
        CancellationToken cancellationToken);
}

public interface IIdentityAdministrationStore
{
    Task<Result<InviteIdentityUserReceipt, IdentityAdministrationError>> InviteAsync(
        InviteIdentityUserCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> DisableAsync(
        DisableIdentityUserCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<IdentityAccountMutationReceipt, IdentityAdministrationError>> SetRolesAsync(
        SetIdentityUserRolesCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<IdentityAuditEventView>, IdentityAdministrationError>> ReadAuditEventsAsync(
        ReadIdentityAuditEventsQuery query,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<IdentityAuditSummaryView, IdentityAdministrationError>> ReadAuditSummaryAsync(
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);
}

public interface IAuditWriter
{
    Task AppendAsync(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details,
        CancellationToken cancellationToken);
}

public interface IIdentityAuditFinalizer
{
    Task FinalizeOnceAsync(
        IdentityAuditDescriptor descriptor,
        PrivilegedAttemptOutcome outcome,
        IReadOnlyDictionary<string, string?> details,
        Func<CancellationToken, Task>? completePersistenceAsync = null);
}

public interface IIdentityAnonymousRateLimiter
{
    Task<IdentityRateLimitDecision> AttemptAsync(
        string endpointFamily,
        ReadOnlyMemory<byte> clientFingerprint,
        CancellationToken cancellationToken);
}

public sealed record IdentityRateLimitDecision(
    bool IsAllowed,
    DateTimeOffset WindowEndsAtUtc,
    int RequestCount,
    int PermitLimit);

public sealed class IdentityAuditFinalizationException : Exception
{
    public IdentityAuditFinalizationException(
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed record AdministrativeRequestActor(
    bool IsAuthenticated,
    string? UserId,
    IReadOnlySet<string> Roles,
    bool HasSatisfiedMfa,
    string CorrelationId);

public sealed record IdentityAuditDescriptor(
    string? ActorId,
    IReadOnlySet<string> Roles,
    string Action,
    string TargetType,
    string? TargetId,
    string CorrelationId);

public sealed record InviteIdentityUserCommand(
    string Email,
    IReadOnlyCollection<string> Roles);

public sealed record DisableIdentityUserCommand(
    string UserId,
    string ExpectedConcurrencyStamp,
    string? Reason);

public sealed record SetIdentityUserRolesCommand(
    string UserId,
    string ExpectedConcurrencyStamp,
    IReadOnlyCollection<string> Roles);

public sealed record ReadIdentityAuditEventsQuery(int Take);

public sealed record IdentityUserView(
    string UserId,
    string Email,
    bool IsDisabled,
    bool TwoFactorEnabled,
    bool HasPassword,
    string ConcurrencyStamp,
    IReadOnlyList<string> Roles);

public sealed record InviteIdentityUserReceipt(
    IdentityUserView User,
    bool WasCreated,
    string InvitationToken,
    DateTimeOffset ExpiresAtUtc);

public sealed record IdentityAccountMutationReceipt(IdentityUserView User);

public sealed record IdentityAuditEventView(
    Guid Id,
    string? ActorId,
    string RolesJson,
    string Action,
    string TargetType,
    string? TargetId,
    string Outcome,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string DetailJson);

public sealed record IdentityAuditSummaryView(
    int TotalEvents,
    int AllowedCount,
    int DeniedCount,
    DateTimeOffset? LatestOccurredAtUtc);

public sealed record CapabilityProbeView(
    AdministrativeCapability Capability,
    string PolicyName,
    CapabilityAccess GrantedAccess);

public sealed record IdentityAdministrationError(string Code, string Message);
