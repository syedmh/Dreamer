namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Url).HasMaxLength(500).IsRequired();
        builder.Property(i => i.AltText).HasMaxLength(200);
    }
}
