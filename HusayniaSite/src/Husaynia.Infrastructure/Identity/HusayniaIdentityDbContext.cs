using Husaynia.Domain.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Identity;

public sealed class HusayniaIdentityDbContext(DbContextOptions<HusayniaIdentityDbContext> options)
    : HusayniaDbContext(options, [typeof(HusayniaDbContext).Assembly])
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceAuditAppendOnly();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        EnforceAuditAppendOnly();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnforceAuditAppendOnly()
    {
        if (ChangeTracker.Entries<AuditEvent>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Audit events are append-only and cannot be updated or deleted.");
        }

        if (ChangeTracker.Entries<IdentityBootstrapState>().Any(entry =>
                entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("The identity bootstrap seal is permanent.");
        }
    }
}
