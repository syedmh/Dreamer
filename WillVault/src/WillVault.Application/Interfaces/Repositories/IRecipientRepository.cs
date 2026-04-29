using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="Recipient"/> with owner-scoped and verification queries.
/// </summary>
public interface IRecipientRepository : IRepository<Recipient>
{
    /// <summary>
    /// Retrieves all recipients belonging to a specific owner.
    /// </summary>
    Task<IReadOnlyList<Recipient>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a recipient by their email address.
    /// </summary>
    Task<Recipient?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves only recipients who have at least one verified contact method for a given owner.
    /// </summary>
    Task<IReadOnlyList<Recipient>> GetVerifiedByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default);
}
