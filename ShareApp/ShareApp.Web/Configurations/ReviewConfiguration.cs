using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class ReviewConfiguration : IEntityTypeConfiguration<Review>
{
    public void Configure(EntityTypeBuilder<Review> builder)
    {
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Rating)
            .IsRequired();

        builder.Property(r => r.Comment)
            .HasMaxLength(1000);

        builder.Property(r => r.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        // Relationships
        builder.HasOne(r => r.Item)
            .WithMany(i => i.Reviews)
            .HasForeignKey(r => r.ItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.Reviewer)
            .WithMany(u => u.GivenReviews)
            .HasForeignKey(r => r.ReviewerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ReviewedUser)
            .WithMany(u => u.ReceivedReviews)
            .HasForeignKey(r => r.ReviewedUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes
        builder.HasIndex(r => r.ReviewerId);
        builder.HasIndex(r => r.ReviewedUserId);
        builder.HasIndex(r => r.ItemId);
        builder.HasIndex(r => r.CreatedAt);
    }
}
