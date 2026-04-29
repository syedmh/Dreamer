using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class RecipientConfiguration : IEntityTypeConfiguration<Recipient>
{
    public void Configure(EntityTypeBuilder<Recipient> builder)
    {
        builder.ToTable("Recipients");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.FullName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(r => r.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(r => r.Phone)
            .HasMaxLength(20);

        builder.Property(r => r.Relationship)
            .HasMaxLength(100);

        builder.Property(r => r.EmailVerificationToken)
            .HasMaxLength(256);

        builder.Property(r => r.PhoneVerificationToken)
            .HasMaxLength(256);

        builder.HasIndex(r => new { r.OwnerId, r.Email });

        builder.HasOne(r => r.Owner)
            .WithMany(o => o.Recipients)
            .HasForeignKey(r => r.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
