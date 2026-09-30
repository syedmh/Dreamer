using Husaynia.Domain.Prayer;
using Husaynia.Infrastructure.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Husaynia.Infrastructure.Prayer;

public sealed class PrayerProfileConfiguration : IEntityTypeConfiguration<PrayerProfile>
{
    public void Configure(EntityTypeBuilder<PrayerProfile> builder)
    {
        builder.ToTable(
            "PrayerProfiles",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_PrayerProfiles_ProviderKind",
                    "[ProviderKind] IN ('local','external')");
                table.HasCheckConstraint(
                    "CK_PrayerProfiles_Latitude",
                    "[Latitude] >= -90 AND [Latitude] <= 90");
                table.HasCheckConstraint(
                    "CK_PrayerProfiles_Longitude",
                    "[Longitude] >= -180 AND [Longitude] <= 180");
                table.HasCheckConstraint(
                    "CK_PrayerProfiles_TimeZoneId",
                    "[TimeZoneId] = 'America/Los_Angeles'");
            });
        builder.HasKey(profile => profile.Id).HasName("PK_PrayerProfiles");
        builder.Property(profile => profile.Id).ValueGeneratedNever();
        builder.Property(profile => profile.ProviderKind)
            .HasMaxLength(PrayerLimits.ProviderKindLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(profile => profile.Latitude)
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(profile => profile.Longitude)
            .HasPrecision(9, 6)
            .IsRequired();
        builder.Property(profile => profile.MethodJson)
            .HasMaxLength(PrayerLimits.MethodJsonLength)
            .IsRequired();
        builder.Property(profile => profile.AlgorithmVersion)
            .HasMaxLength(PrayerLimits.AlgorithmVersionLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(profile => profile.TimeZoneId)
            .HasMaxLength(PrayerLimits.TimeZoneIdLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(profile => profile.EffectiveFrom)
            .HasColumnType("date")
            .IsRequired();
        builder.Property(profile => profile.ProfileHash)
            .HasColumnType("char(64)")
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();
        builder.Property(profile => profile.CreatedBy)
            .HasMaxLength(PrayerLimits.ActorIdLength)
            .IsRequired();
        builder.Property(profile => profile.CreatedAtUtc)
            .HasPrecision(7)
            .IsRequired();
        builder.HasAlternateKey(profile => profile.ProfileHash)
            .HasName("AK_PrayerProfiles_ProfileHash");
        builder.HasIndex(profile => profile.EffectiveFrom)
            .HasDatabaseName("IX_PrayerProfiles_EffectiveFrom");
    }
}

public sealed class PrayerSnapshotConfiguration
    : MutableEntityConfiguration<PrayerSnapshot>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<PrayerSnapshot> builder)
    {
        builder.ToTable("PrayerSnapshots");
        builder.HasKey(snapshot => snapshot.Id).HasName("PK_PrayerSnapshots");
        builder.Property(snapshot => snapshot.Id).ValueGeneratedNever();
        builder.Property(snapshot => snapshot.ProfileHash)
            .HasColumnType("char(64)")
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();
        builder.Property(snapshot => snapshot.Date)
            .HasColumnType("date")
            .IsRequired();
        builder.Property(snapshot => snapshot.ValuesJson)
            .HasMaxLength(512)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(snapshot => snapshot.GeneratedAtUtc)
            .HasPrecision(7)
            .IsRequired();
        builder.Property(snapshot => snapshot.Source)
            .HasMaxLength(PrayerLimits.SourceLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(snapshot => snapshot.IsValid).IsRequired();
        builder.Property<byte[]>(PersistencePropertyNames.RowVersion)
            .IsRequired();
        builder.HasAlternateKey(snapshot => new { snapshot.ProfileHash, snapshot.Date })
            .HasName("AK_PrayerSnapshots_ProfileHash_Date");
        builder.HasIndex(snapshot => new { snapshot.ProfileHash, snapshot.GeneratedAtUtc })
            .HasDatabaseName("IX_PrayerSnapshots_ProfileHash_GeneratedAtUtc");
        builder.HasOne<PrayerProfile>()
            .WithMany()
            .HasForeignKey(snapshot => snapshot.ProfileHash)
            .HasPrincipalKey(profile => profile.ProfileHash)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PrayerSnapshots_PrayerProfiles_ProfileHash");
    }
}

public sealed class PrayerOverrideConfiguration
    : MutableEntityConfiguration<PrayerOverride>
{
    protected override void ConfigureMutableEntity(EntityTypeBuilder<PrayerOverride> builder)
    {
        builder.ToTable(
            "PrayerOverrides",
            table =>
            {
                table.HasCheckConstraint(
                    "CK_PrayerOverrides_EffectiveRevision",
                    "[EffectiveRevision] > 0");
            });
        builder.HasKey(prayerOverride => prayerOverride.Id).HasName("PK_PrayerOverrides");
        builder.Property(prayerOverride => prayerOverride.Id).ValueGeneratedNever();
        builder.Property(prayerOverride => prayerOverride.ProfileHash)
            .HasColumnType("char(64)")
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.Date)
            .HasColumnType("date")
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.PrayerKey)
            .HasMaxLength(PrayerLimits.PrayerKeyLength)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.LocalTime)
            .HasColumnType("time(0)")
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.Reason)
            .HasMaxLength(PrayerLimits.OverrideReasonLength)
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.EffectiveRevision).IsRequired();
        builder.Property(prayerOverride => prayerOverride.IsActive).IsRequired();
        builder.Property(prayerOverride => prayerOverride.CreatedBy)
            .HasMaxLength(PrayerLimits.ActorIdLength)
            .IsRequired();
        builder.Property(prayerOverride => prayerOverride.DeactivatedBy)
            .HasMaxLength(PrayerLimits.ActorIdLength);
        builder.Property(prayerOverride => prayerOverride.DeactivatedAtUtc)
            .HasPrecision(7);
        builder.Property<byte[]>(PersistencePropertyNames.RowVersion)
            .IsRequired();
        builder.HasAlternateKey(prayerOverride => new
        {
            prayerOverride.ProfileHash,
            prayerOverride.Date,
            prayerOverride.PrayerKey,
            prayerOverride.EffectiveRevision,
        })
            .HasName("AK_PrayerOverrides_ProfileHash_Date_PrayerKey_Revision");
        builder.HasIndex(prayerOverride => new
        {
            prayerOverride.ProfileHash,
            prayerOverride.Date,
            prayerOverride.PrayerKey,
            prayerOverride.IsActive,
            prayerOverride.EffectiveRevision,
        })
            .HasDatabaseName("IX_PrayerOverrides_ActiveLookup");
        builder.HasOne<PrayerProfile>()
            .WithMany()
            .HasForeignKey(prayerOverride => prayerOverride.ProfileHash)
            .HasPrincipalKey(profile => profile.ProfileHash)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PrayerOverrides_PrayerProfiles_ProfileHash");
    }
}

public sealed class PrayerIntegrationStateConfiguration
    : MutableEntityConfiguration<PrayerIntegrationState>
{
    protected override void ConfigureMutableEntity(
        EntityTypeBuilder<PrayerIntegrationState> builder)
    {
        builder.ToTable("PrayerIntegrationStates");
        builder.HasKey(state => state.Id).HasName("PK_PrayerIntegrationStates");
        builder.Property(state => state.Id)
            .HasMaxLength(PrayerLimits.IntegrationStateIdLength)
            .IsUnicode(false)
            .ValueGeneratedNever();
        builder.Property(state => state.ActiveProfileHash)
            .HasColumnType("char(64)")
            .IsUnicode(false)
            .IsFixedLength();
        builder.Property(state => state.RefreshIntentProfileHash)
            .HasColumnType("char(64)")
            .IsUnicode(false)
            .IsFixedLength();
        builder.Property(state => state.RefreshIntentCreatedAtUtc).HasPrecision(7);
        builder.Property(state => state.LastAttemptAtUtc).HasPrecision(7);
        builder.Property(state => state.LastSuccessAtUtc).HasPrecision(7);
        builder.Property(state => state.LastFailureAtUtc).HasPrecision(7);
        builder.Property(state => state.LastErrorCode)
            .HasMaxLength(PrayerLimits.ErrorCodeLength)
            .IsUnicode(false);
        builder.Property(state => state.LastSource)
            .HasMaxLength(PrayerLimits.SourceLength)
            .IsUnicode(false);
        builder.Property(state => state.LastGeneratedMonth)
            .HasColumnType("date");
        builder.Property<byte[]>(PersistencePropertyNames.RowVersion)
            .IsRequired();
        builder.HasIndex(state => state.ActiveProfileHash)
            .HasDatabaseName("IX_PrayerIntegrationStates_ActiveProfileHash");
        builder.HasIndex(state => state.RefreshIntentProfileHash)
            .HasDatabaseName("IX_PrayerIntegrationStates_RefreshIntentProfileHash");
        builder.HasOne<PrayerProfile>()
            .WithMany()
            .HasForeignKey(state => state.ActiveProfileHash)
            .HasPrincipalKey(profile => profile.ProfileHash)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PrayerIntegrationStates_PrayerProfiles_ActiveProfileHash");
        builder.HasOne<PrayerProfile>()
            .WithMany()
            .HasForeignKey(state => state.RefreshIntentProfileHash)
            .HasPrincipalKey(profile => profile.ProfileHash)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_PrayerIntegrationStates_PrayerProfiles_RefreshIntentProfileHash");
    }
}
