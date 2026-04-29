using Microsoft.EntityFrameworkCore;
using WillVault.Application.Interfaces.Repositories;
using WillVault.Domain.Entities;
using WillVault.Domain.Enums;
using WillVault.Infrastructure.Data;

namespace WillVault.Infrastructure.Repositories;

public class VaultItemRepository : Repository<VaultItem>, IVaultItemRepository
{
    public VaultItemRepository(WillVaultDbContext context) : base(context)
    {
    }

    public async Task<(IReadOnlyList<VaultItem> Items, int TotalCount)> GetByOwnerIdAsync(
        Guid ownerId, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var query = DbSet.Where(v => v.OwnerId == ownerId);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, totalCount);
    }

    public async Task<IReadOnlyList<VaultItem>> GetByOwnerIdAndTypeAsync(
        Guid ownerId, VaultItemType itemType, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Where(v => v.OwnerId == ownerId && v.ItemType == itemType)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<VaultItem?> GetWithRecipientsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await DbSet
            .Include(v => v.VaultItemRecipients)
                .ThenInclude(vr => vr.Recipient)
            .FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
    }

    public async Task AddVaultItemRecipientAsync(VaultItemRecipient assignment, CancellationToken cancellationToken = default)
    {
        await Context.Set<VaultItemRecipient>().AddAsync(assignment, cancellationToken);
        await Context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveVaultItemRecipientAsync(VaultItemRecipient assignment, CancellationToken cancellationToken = default)
    {
        Context.Set<VaultItemRecipient>().Remove(assignment);
        await Context.SaveChangesAsync(cancellationToken);
    }
}
