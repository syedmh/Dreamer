using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="DeathVerificationRequest"/> with status-based queries.
/// </summary>
public interface IDeathVerificationRepository : IRepository<DeathVerificationRequest>
{
    /// <summary>
    /// Retrieves all verification requests that are in a pending or under-review status.
    /// </summary>
    Task<IReadOnlyList<DeathVerificationRequest>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all verification requests for a specific vault owner.
    /// </summary>
    Task<IReadOnlyList<DeathVerificationRequest>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the currently active (non-terminal) verification request for a vault owner, if any.
    /// </summary>
    Task<DeathVerificationRequest?> GetActiveByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);
}
