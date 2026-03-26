namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(2000);
        builder.Property(p => p.ShortDescription).HasMaxLength(500);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(50);

        builder.HasMany(p => p.Variants).WithOne(v => v.Product).HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.Images).WithOne(i => i.Product).HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(p => p.SizeCharts).WithOne(s => s.Product).HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
