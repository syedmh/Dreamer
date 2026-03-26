namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.UserId).HasMaxLength(450).IsRequired();
        builder.Property(c => c.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(c => c.LastName).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Email).HasMaxLength(256).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(20);

        builder.HasIndex(c => c.UserId).IsUnique();
        builder.HasIndex(c => c.Email).IsUnique();

        builder.HasMany(c => c.Addresses).WithOne(a => a.Customer).HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(c => c.Orders).WithOne(o => o.Customer).HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(c => c.Cart).WithOne(cart => cart.Customer).HasForeignKey<Cart>(cart => cart.CustomerId).OnDelete(DeleteBehavior.Cascade);
    }
}
