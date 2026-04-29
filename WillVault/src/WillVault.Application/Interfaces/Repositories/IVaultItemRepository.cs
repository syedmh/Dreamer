using WillVault.Domain.Entities;
using WillVault.Domain.Enums;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="VaultItem"/> with owner-scoped and paginated queries.
/// </summary>
public interface IVaultItemRepository : IRepository<VaultItem>
{
    /// <summary>
    /// Retrieves vault items belonging to a specific owner with pagination support.
    /// </summary>
    Task<(IReadOnlyList<VaultItem> Items, int TotalCount)> GetByOwnerIdAsync(
        Guid ownerId, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves vault items belonging to a specific owner filtered by item type.
    /// </summary>
    Task<IReadOnlyList<VaultItem>> GetByOwnerIdAndTypeAsync(
        Guid ownerId, VaultItemType itemType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a vault item with its associated recipients eagerly loaded.
    /// </summary>
    Task<VaultItem?> GetWithRecipientsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a vault item recipient assignment directly.
    /// </summary>
    Task AddVaultItemRecipientAsync(VaultItemRecipient assignment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a vault item recipient assignment directly.
    /// </summary>
    Task RemoveVaultItemRecipientAsync(VaultItemRecipient assignment, CancellationToken cancellationToken = default);
}
