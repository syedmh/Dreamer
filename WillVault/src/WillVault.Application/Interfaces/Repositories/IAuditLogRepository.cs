using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Append-only repository for <see cref="AuditLog"/>. No update or delete operations are supported.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Appends a new audit log entry.
    /// </summary>
    Task AddAsync(AuditLog auditLog, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves audit log entries for a specific entity.
    /// </summary>
    Task<IReadOnlyList<AuditLog>> GetByEntityAsync(string entityType, Guid entityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves audit log entries performed by a specific actor.
    /// </summary>
    Task<IReadOnlyList<AuditLog>> GetByActorAsync(Guid actorId, CancellationToken cancellationToken = default);
}
