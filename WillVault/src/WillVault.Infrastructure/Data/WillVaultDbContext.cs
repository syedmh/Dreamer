using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using WillVault.Domain.Common;
using WillVault.Domain.Entities;
using WillVault.Infrastructure.Identity;

namespace WillVault.Infrastructure.Data;

public class WillVaultDbContext : IdentityDbContext<ApplicationUser>
{
    public WillVaultDbContext(DbContextOptions<WillVaultDbContext> options)
        : base(options)
    {
    }

    public DbSet<VaultOwner> VaultOwners => Set<VaultOwner>();
    public DbSet<VaultItem> VaultItems => Set<VaultItem>();
    public DbSet<Recipient> Recipients => Set<Recipient>();
    public DbSet<VaultItemRecipient> VaultItemRecipients => Set<VaultItemRecipient>();
    public DbSet<TrustedContact> TrustedContacts => Set<TrustedContact>();
    public DbSet<DeathVerificationRequest> DeathVerificationRequests => Set<DeathVerificationRequest>();
    public DbSet<DeliveryJob> DeliveryJobs => Set<DeliveryJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(WillVaultDbContext).Assembly);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTime.UtcNow;
            }
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
