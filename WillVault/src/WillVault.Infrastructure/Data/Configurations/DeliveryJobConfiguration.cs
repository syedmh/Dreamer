using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class DeliveryJobConfiguration : IEntityTypeConfiguration<DeliveryJob>
{
    public void Configure(EntityTypeBuilder<DeliveryJob> builder)
    {
        builder.ToTable("DeliveryJobs");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Channel)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.Status)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(d => d.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(d => d.ErrorMessage)
            .HasMaxLength(2000);

        builder.HasIndex(d => new { d.Status, d.ScheduledAt });
        builder.HasIndex(d => d.IdempotencyKey).IsUnique();

        builder.HasOne(d => d.VaultItem)
            .WithMany()
            .HasForeignKey(d => d.VaultItemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Recipient)
            .WithMany()
            .HasForeignKey(d => d.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
