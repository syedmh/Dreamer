namespace Khilat.Infrastructure.Data.Configurations;

using Khilat.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class SizeChartConfiguration : IEntityTypeConfiguration<SizeChart>
{
    public void Configure(EntityTypeBuilder<SizeChart> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Size).HasConversion<string>().HasMaxLength(10);
        builder.Property(s => s.FrontLengthInches).HasPrecision(5, 2);
        builder.Property(s => s.BackLengthInches).HasPrecision(5, 2);
        builder.Property(s => s.ArmLengthInches).HasPrecision(5, 2);
        builder.Property(s => s.CuffInches).HasPrecision(5, 2);
        builder.Property(s => s.ButtonLengthInches).HasPrecision(5, 2);
        builder.Property(s => s.ShoulderInches).HasPrecision(5, 2);
        builder.Property(s => s.ChestInches).HasPrecision(5, 2);
        builder.Property(s => s.WaistInches).HasPrecision(5, 2);
        builder.Property(s => s.HipInches).HasPrecision(5, 2);

        builder.HasIndex(s => new { s.ProductId, s.Size }).IsUnique();
    }
}
