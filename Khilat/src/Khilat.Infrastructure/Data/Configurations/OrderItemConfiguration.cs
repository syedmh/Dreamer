namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.UnitPrice).HasPrecision(10, 2);
        builder.Property(i => i.ProductName).HasMaxLength(200);
        builder.Property(i => i.Color).HasConversion<string>().HasMaxLength(50);
        builder.Property(i => i.Size).HasConversion<string>().HasMaxLength(10);
        builder.Property(i => i.Craftsmanship).HasConversion<string>().HasMaxLength(50);
        builder.Ignore(i => i.Subtotal);

        builder.HasOne(i => i.ProductVariant).WithMany(v => v.OrderItems).HasForeignKey(i => i.ProductVariantId).OnDelete(DeleteBehavior.Restrict);
    }
}
