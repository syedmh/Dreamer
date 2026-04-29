using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class DeathVerificationRepository : Repository<DeathVerificationRequest>, IDeathVerificationRepository
{
    public DeathVerificationRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<DeathVerificationRequest>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(d => d.Status == VerificationStatus.Pending)
            .OrderBy(d => d.SubmittedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeathVerificationRequest>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(d => d.OwnerId == ownerId)
            .OrderByDescending(d => d.SubmittedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<DeathVerificationRequest?> GetActiveByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(d => d.OwnerId == ownerId &&
                        (d.Status == VerificationStatus.Pending || d.Status == VerificationStatus.UnderReview))
            .OrderByDescending(d => d.SubmittedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
