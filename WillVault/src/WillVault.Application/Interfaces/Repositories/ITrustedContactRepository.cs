using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="TrustedContact"/> with owner and verification token lookups.
/// </summary>
public interface ITrustedContactRepository : IRepository<TrustedContact>
{
    /// <summary>
    /// Retrieves all trusted contacts belonging to a specific owner.
    /// </summary>
    Task<IReadOnlyList<TrustedContact>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a trusted contact by their verification token.
    /// </summary>
    Task<TrustedContact?> GetByVerificationTokenAsync(string token, CancellationToken cancellationToken = default);
}
