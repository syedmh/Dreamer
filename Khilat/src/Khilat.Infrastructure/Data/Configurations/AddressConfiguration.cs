namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class AddressConfiguration : IEntityTypeConfiguration<Address>
{
    public void Configure(EntityTypeBuilder<Address> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Street).HasMaxLength(200).IsRequired();
        builder.Property(a => a.City).HasMaxLength(100).IsRequired();
        builder.Property(a => a.State).HasMaxLength(50).IsRequired();
        builder.Property(a => a.ZipCode).HasMaxLength(20).IsRequired();
        builder.Property(a => a.Country).HasMaxLength(10).HasDefaultValue("US");
    }
}
