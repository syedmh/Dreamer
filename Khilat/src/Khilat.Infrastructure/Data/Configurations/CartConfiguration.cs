namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class CartConfiguration : IEntityTypeConfiguration<Cart>
{
    public void Configure(EntityTypeBuilder<Cart> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Ignore(c => c.TotalAmount);
        builder.Ignore(c => c.TotalItems);

        builder.HasMany(c => c.Items).WithOne(i => i.Cart).HasForeignKey(i => i.CartId).OnDelete(DeleteBehavior.Cascade);
    }
}
