using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class ItemImageConfiguration : IEntityTypeConfiguration<ItemImage>
{
    public void Configure(EntityTypeBuilder<ItemImage> builder)
    {
        builder.HasKey(img => img.Id);

        builder.Property(img => img.ImageUrl)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(img => img.IsPrimary)
            .HasDefaultValue(false);

        builder.Property(img => img.DisplayOrder)
            .HasDefaultValue(0);

        builder.Property(img => img.UploadedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        // Relationships
        builder.HasOne(img => img.Item)
            .WithMany(i => i.Images)
            .HasForeignKey(img => img.ItemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes
        builder.HasIndex(img => img.ItemId);
        builder.HasIndex(img => new { img.ItemId, img.DisplayOrder });
    }
}
