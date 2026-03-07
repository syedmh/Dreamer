using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> builder)
    {
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Title)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(i => i.Description)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(i => i.Price)
            .HasPrecision(18, 2);

        builder.Property(i => i.BarterPreference)
            .HasMaxLength(500);

        builder.Property(i => i.Status)
            .HasDefaultValue(ItemStatus.Available);

        builder.Property(i => i.Quantity)
            .HasDefaultValue(1);

        builder.Property(i => i.IsActive)
            .HasDefaultValue(true);

        builder.Property(i => i.Views)
            .HasDefaultValue(0);

        builder.Property(i => i.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        builder.Property(i => i.UpdatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        // Relationships
        builder.HasOne(i => i.User)
            .WithMany(u => u.ListedItems)
            .HasForeignKey(i => i.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Category)
            .WithMany(c => c.Items)
            .HasForeignKey(i => i.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Location)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(i => i.Images)
            .WithOne(img => img.Item)
            .HasForeignKey(img => img.ItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(i => i.Transactions)
            .WithOne(t => t.Item)
            .HasForeignKey(t => t.ItemId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(i => i.Status);
        builder.HasIndex(i => i.CategoryId);
        builder.HasIndex(i => i.UserId);
        builder.HasIndex(i => i.IsActive);
        builder.HasIndex(i => i.CreatedAt);
    }
}
