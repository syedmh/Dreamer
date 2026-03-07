using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShareApp.Web.Entities;

namespace ShareApp.Web.Configurations;

public class LocationConfiguration : IEntityTypeConfiguration<Location>
{
    public void Configure(EntityTypeBuilder<Location> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.AddressLine1)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(l => l.AddressLine2)
            .HasMaxLength(200);

        builder.Property(l => l.City)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(l => l.State)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.ZipCode)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(l => l.Country)
            .HasMaxLength(50)
            .HasDefaultValue("USA");

        builder.Property(l => l.Latitude)
            .IsRequired()
            .HasPrecision(10, 8);

        builder.Property(l => l.Longitude)
            .IsRequired()
            .HasPrecision(11, 8);

        builder.Property(l => l.IsDefault)
            .HasDefaultValue(false);

        builder.Property(l => l.CreatedAt)
            .HasDefaultValueSql("GETUTCDATE()");

        // Relationships
        builder.HasOne(l => l.User)
            .WithMany(u => u.Locations)
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Indexes for geospatial queries
        builder.HasIndex(l => l.Latitude);
        builder.HasIndex(l => l.Longitude);
        builder.HasIndex(l => new { l.Latitude, l.Longitude });
        builder.HasIndex(l => l.UserId);
    }
}
