namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.OrderNumber).HasMaxLength(20).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(50);
        builder.Property(o => o.SubTotal).HasPrecision(10, 2);
        builder.Property(o => o.ShippingCost).HasPrecision(10, 2);
        builder.Property(o => o.Tax).HasPrecision(10, 2);
        builder.Property(o => o.TotalAmount).HasPrecision(10, 2);
        builder.Property(o => o.StripePaymentIntentId).HasMaxLength(200);
        builder.Property(o => o.StripeChargeId).HasMaxLength(200);
        builder.Property(o => o.ShippingStreet).HasMaxLength(200);
        builder.Property(o => o.ShippingCity).HasMaxLength(100);
        builder.Property(o => o.ShippingState).HasMaxLength(50);
        builder.Property(o => o.ShippingZipCode).HasMaxLength(20);
        builder.Property(o => o.ShippingCountry).HasMaxLength(10).HasDefaultValue("US");

        builder.HasIndex(o => o.OrderNumber).IsUnique();
        builder.HasIndex(o => o.CustomerId);
        builder.HasIndex(o => o.Status);

        builder.HasMany(o => o.Items).WithOne(i => i.Order).HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
