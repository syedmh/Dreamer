namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CartItemConfiguration : IEntityTypeConfiguration<CartItem>
{
    public void Configure(EntityTypeBuilder<CartItem> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.UnitPrice).HasPrecision(10, 2);
        builder.Ignore(i => i.Subtotal);

        builder.HasOne(i => i.ProductVariant).WithMany().HasForeignKey(i => i.ProductVariantId).OnDelete(DeleteBehavior.Restrict);
    }
}
