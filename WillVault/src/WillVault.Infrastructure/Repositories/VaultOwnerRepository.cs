using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class VaultOwnerRepository : Repository<VaultOwner>, IVaultOwnerRepository
{
    public VaultOwnerRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<VaultOwner?> GetByIdentityUserIdAsync(string identityUserId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(v => v.IdentityUserId == identityUserId, cancellationToken);
    }

    public async Task<VaultOwner?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(v => v.Email == email, cancellationToken);
    }
}
