using WillVault.Domain.Entities;

namespace WillVault.Application.Interfaces.Repositories;

/// <summary>
/// Repository interface for <see cref="VaultOwner"/> with identity and email lookups.
/// </summary>
public interface IVaultOwnerRepository : IRepository<VaultOwner>
{
    /// <summary>
    /// Retrieves a vault owner by their ASP.NET Identity user identifier.
    /// </summary>
    Task<VaultOwner?> GetByIdentityUserIdAsync(string identityUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a vault owner by their email address.
    /// </summary>
    Task<VaultOwner?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
}
