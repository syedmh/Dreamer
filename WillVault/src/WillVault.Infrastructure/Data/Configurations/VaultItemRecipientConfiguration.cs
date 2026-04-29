using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class VaultItemRecipientConfiguration : IEntityTypeConfiguration<VaultItemRecipient>
{
    public void Configure(EntityTypeBuilder<VaultItemRecipient> builder)
    {
        builder.ToTable("VaultItemRecipients");

        builder.HasKey(vr => vr.Id);

        builder.HasIndex(vr => new { vr.VaultItemId, vr.RecipientId }).IsUnique();

        builder.HasOne(vr => vr.VaultItem)
            .WithMany(vi => vi.VaultItemRecipients)
            .HasForeignKey(vr => vr.VaultItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(vr => vr.Recipient)
            .WithMany(r => r.VaultItemRecipients)
            .HasForeignKey(vr => vr.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
