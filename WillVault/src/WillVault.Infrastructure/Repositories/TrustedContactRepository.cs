using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class TrustedContactRepository : Repository<TrustedContact>, ITrustedContactRepository
{
    public TrustedContactRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<TrustedContact>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(tc => tc.OwnerId == ownerId)
            .OrderBy(tc => tc.FullName)
            .ToListAsync(cancellationToken);
    }

    public async Task<TrustedContact?> GetByVerificationTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .FirstOrDefaultAsync(tc => tc.VerificationToken == token, cancellationToken);
    }
}
