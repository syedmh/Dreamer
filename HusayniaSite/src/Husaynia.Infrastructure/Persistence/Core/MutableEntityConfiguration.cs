using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Persistence.Core;

public abstract class MutableEntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : class
{
    public void Configure(EntityTypeBuilder<TEntity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property<DateTimeOffset>(PersistencePropertyNames.CreatedAtUtc)
            .HasPrecision(7)
            .IsRequired();
        builder.Property<DateTimeOffset>(PersistencePropertyNames.UpdatedAtUtc)
            .HasPrecision(7)
            .IsRequired();
        builder.Property<byte[]>(PersistencePropertyNames.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();

        builder.Metadata.SetAnnotation(PersistenceModelAnnotations.ManagedTimestamps, true);
        ConfigureMutableEntity(builder);
    }

    protected abstract void ConfigureMutableEntity(EntityTypeBuilder<TEntity> builder);
}

internal static class PersistenceModelAnnotations
{
    internal const string ManagedTimestamps = "Husaynia:ManagedTimestamps";
}
