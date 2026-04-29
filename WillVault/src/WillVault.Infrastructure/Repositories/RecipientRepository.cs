using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class RecipientRepository : Repository<Recipient>, IRecipientRepository
{
    public RecipientRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<Recipient>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(r => r.OwnerId == ownerId)
            .OrderBy(r => r.FullName)
            .ToListAsync(cancellationToken);
    }

    public async Task<Recipient?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(r => r.Email == email, cancellationToken);
    }

    public async Task<IReadOnlyList<Recipient>> GetVerifiedByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(r => r.OwnerId == ownerId && (r.IsEmailVerified || r.IsPhoneVerified))
            .OrderBy(r => r.FullName)
            .ToListAsync(cancellationToken);
    }
}
