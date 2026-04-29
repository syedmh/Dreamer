using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class VaultItemConfiguration : IEntityTypeConfiguration<VaultItem>
{
    public void Configure(EntityTypeBuilder<VaultItem> builder)
    {
        builder.ToTable("VaultItems");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.Title)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(v => v.Description)
            .HasMaxLength(2000);

        builder.Property(v => v.ItemType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.Property(v => v.OriginalFileName)
            .HasMaxLength(500);

        builder.Property(v => v.ContentType)
            .HasMaxLength(100);

        builder.Property(v => v.EncryptionKeyId)
            .HasMaxLength(100);

        builder.Property(v => v.LegalNotes)
            .HasMaxLength(4000);

        builder.Property(v => v.SpecialInstructions)
            .HasMaxLength(4000);

        builder.HasIndex(v => v.OwnerId);

        builder.HasOne(v => v.Owner)
            .WithMany(o => o.VaultItems)
            .HasForeignKey(v => v.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
