namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Color).HasConversion<string>().HasMaxLength(50);
        builder.Property(v => v.Size).HasConversion<string>().HasMaxLength(10);
        builder.Property(v => v.Craftsmanship).HasConversion<string>().HasMaxLength(50);
        builder.Property(v => v.Price).HasPrecision(10, 2);
        builder.Property(v => v.Sku).HasMaxLength(50).IsRequired();
        builder.Ignore(v => v.IsAvailable);

        builder.HasIndex(v => v.Sku).IsUnique();
        builder.HasIndex(v => new { v.ProductId, v.Color, v.Size, v.Craftsmanship }).IsUnique();
    }
}
