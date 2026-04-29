using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class DeathVerificationRequestConfiguration : IEntityTypeConfiguration<DeathVerificationRequest>
{
    public void Configure(EntityTypeBuilder<DeathVerificationRequest> builder)
    {
        builder.ToTable("DeathVerificationRequests");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.DocumentPaths)
            .IsRequired()
            .HasMaxLength(4000);

        builder.Property(d => d.SubmitterNotes)
            .HasMaxLength(2000);

        builder.Property(d => d.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.ReviewerNotes)
            .HasMaxLength(2000);

        builder.Property(d => d.SecondReviewerNotes)
            .HasMaxLength(2000);

        builder.Property(d => d.RejectionReason)
            .HasMaxLength(2000);

        builder.Property(d => d.DisputeReason)
            .HasMaxLength(2000);

        builder.HasIndex(d => new { d.OwnerId, d.Status });

        builder.HasOne(d => d.Owner)
            .WithMany(o => o.DeathVerificationRequests)
            .HasForeignKey(d => d.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.SubmittedBy)
            .WithMany()
            .HasForeignKey(d => d.SubmittedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
