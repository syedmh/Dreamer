using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WillVault.Domain.Entities;

namespace WillVault.Infrastructure.Data.Configurations;

public class VaultOwnerConfiguration : IEntityTypeConfiguration<VaultOwner>
{
    public void Configure(EntityTypeBuilder<VaultOwner> builder)
    {
        builder.ToTable("VaultOwners");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.FullName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(v => v.Email)
            .IsRequired()
            .HasMaxLength(256);

        builder.Property(v => v.Phone)
            .HasMaxLength(20);

        builder.Property(v => v.IdentityUserId)
            .IsRequired()
            .HasMaxLength(450);

        builder.Property(v => v.AccountStatus)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(50);

        builder.HasIndex(v => v.Email).IsUnique();
        builder.HasIndex(v => v.IdentityUserId).IsUnique();
    }
}
