using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class DeliveryJobRepository : Repository<DeliveryJob>, IDeliveryJobRepository
{
    public DeliveryJobRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<IReadOnlyList<DeliveryJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(d => d.Status == DeliveryStatus.Pending && d.ScheduledAt <= DateTime.UtcNow)
            .OrderBy(d => d.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryJob>> GetByOwnerIdAsync(Guid ownerId, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(d => d.VaultItem)
            .Where(d => d.VaultItem.OwnerId == ownerId)
            .OrderByDescending(d => d.ScheduledAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryJob>> GetFailedJobsForRetryAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(d => d.Status == DeliveryStatus.Failed &&
                        d.Attempts < d.MaxAttempts &&
                        d.NextRetryAt.HasValue &&
                        d.NextRetryAt.Value <= DateTime.UtcNow)
            .OrderBy(d => d.NextRetryAt)
            .ToListAsync(cancellationToken);
    }
}
