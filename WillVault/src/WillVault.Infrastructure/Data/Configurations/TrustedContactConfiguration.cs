using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class TrustedContactConfiguration : IEntityTypeConfiguration<TrustedContact>
{
    public void Configure(EntityTypeBuilder<TrustedContact> builder)
    {
        builder.ToTable("TrustedContacts");

        builder.HasKey(tc => tc.Id);

        builder.Property(tc => tc.FullName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(tc => tc.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(tc => tc.Phone)
            .HasMaxLength(20);

        builder.Property(tc => tc.Relationship)
            .HasMaxLength(100);

        builder.Property(tc => tc.VerificationToken)
            .HasMaxLength(256);

        builder.HasIndex(tc => tc.OwnerId);
        builder.HasIndex(tc => tc.VerificationToken).IsUnique().HasFilter("[VerificationToken] IS NOT NULL");

        builder.HasOne(tc => tc.Owner)
            .WithMany(o => o.TrustedContacts)
            .HasForeignKey(tc => tc.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
