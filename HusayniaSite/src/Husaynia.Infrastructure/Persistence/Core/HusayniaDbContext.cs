using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Husaynia.Infrastructure.Persistence.Core;

public class HusayniaDbContext : DbContext
{
    private readonly IReadOnlyCollection<Assembly> configurationAssemblies;

    public HusayniaDbContext(DbContextOptions<HusayniaDbContext> options)
        : this(options, [typeof(HusayniaDbContext).Assembly])
    {
    }

    protected HusayniaDbContext(
        DbContextOptions options,
        IReadOnlyCollection<Assembly> configurationAssemblies)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(configurationAssemblies);
        this.configurationAssemblies = configurationAssemblies;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var assembly in configurationAssemblies
                     .Distinct()
                     .OrderBy(assembly => assembly.FullName, StringComparer.Ordinal))
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyManagedTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyManagedTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyManagedTimestamps()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries().Where(entry =>
                     entry.Metadata.FindAnnotation(PersistenceModelAnnotations.ManagedTimestamps)?.Value
                         is true &&
                     entry.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(PersistencePropertyNames.CreatedAtUtc).CurrentValue = now;
            }

            entry.Property(PersistencePropertyNames.UpdatedAtUtc).CurrentValue = now;
        }
    }
}
