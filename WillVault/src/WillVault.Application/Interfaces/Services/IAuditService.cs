using WillVault.Domain.Enums;

namespace WillVault.Application.Interfaces.Services;

/// <summary>
/// Service for recording audit trail entries for security and compliance.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Logs an auditable action performed by an actor on an entity.
    /// </summary>
    /// <param name="actorId">The identifier of the actor performing the action.</param>
    /// <param name="actorType">The type of actor (owner, admin, system, etc.).</param>
    /// <param name="action">A descriptive name for the action performed.</param>
    /// <param name="entityType">The type of entity being acted upon.</param>
    /// <param name="entityId">The identifier of the entity being acted upon.</param>
    /// <param name="details">Optional JSON-serialized details about the action.</param>
    /// <param name="ipAddress">The IP address of the request origin.</param>
    /// <param name="userAgent">The user agent string of the request origin.</param>
    /// <param name="reason">Optional reason or justification for the action.</param>
    Task LogAsync(
        Guid? actorId,
        ActorType actorType,
        string action,
        string? entityType = null,
        Guid? entityId = null,
        string? details = null,
        string? ipAddress = null,
        string? userAgent = null,
        string? reason = null,
        CancellationToken cancellationToken = default);
}
