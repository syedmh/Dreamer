using Husaynia.Application.Contracts;
using Husaynia.Domain.Prayer;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Infrastructure.Prayer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;

namespace Husaynia.IntegrationTests.Prayer;

public sealed class PrayerConfigurationAndModelTests
{
    [Fact]
    public void DefaultsAreSafeAndExternalProviderIsDisabled()
    {
        var configuration = new ConfigurationBuilder().Build();
        var validator = new PrayerConfigurationValidator();

        Assert.Empty(validator.Validate(configuration));
    }

    [Fact]
    public void EnabledProviderRequiresBoundedAllowlistedHttpsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Prayer:ExternalProvider:Enabled"] = "true",
                ["Prayer:ExternalProvider:Endpoint"] = "https://provider.example.test/schedule",
                ["Prayer:ExternalProvider:AllowedHosts:0"] = "different.example.test",
                ["Prayer:ExternalProvider:Timeout"] = "00:01:01",
                ["Prayer:ExternalProvider:ClockSkew"] = "00:16:00",
            })
            .Build();

        var failures = new PrayerConfigurationValidator().Validate(configuration);

        Assert.Contains(failures, failure => failure.Contains("AllowedHosts", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("00:01:00", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("ClockSkew", StringComparison.Ordinal));
    }

    [Fact]
    public void PrayerConfigurationsAreDiscoveredWithStableConstraintsAndRowversions()
    {
        var options = new DbContextOptionsBuilder<HusayniaDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=HusayniaT07_Model;Integrated Security=true;Encrypt=false")
            .Options;
        using var context = new HusayniaDbContext(options);

        var model = context.GetService<IDesignTimeModel>().Model;
        var profile = model.FindEntityType(typeof(PrayerProfile)) ??
            throw new InvalidOperationException("PrayerProfile configuration was not discovered.");
        var snapshot = model.FindEntityType(typeof(PrayerSnapshot)) ??
            throw new InvalidOperationException("PrayerSnapshot configuration was not discovered.");
        var prayerOverride = model.FindEntityType(typeof(PrayerOverride)) ??
            throw new InvalidOperationException("PrayerOverride configuration was not discovered.");
        var state = model.FindEntityType(typeof(PrayerIntegrationState)) ??
            throw new InvalidOperationException("PrayerIntegrationState configuration was not discovered.");

        Assert.Equal("PrayerProfiles", profile.GetTableName());
        Assert.Equal("AK_PrayerProfiles_ProfileHash", profile.GetKeys().Single(key => !key.IsPrimaryKey()).GetName());
        Assert.Null(profile.FindProperty(PersistencePropertyNames.RowVersion));
        AssertIndex(
            profile,
            "IX_PrayerProfiles_EffectiveFrom",
            false,
            nameof(PrayerProfile.EffectiveFrom));
        Assert.Single(profile.GetIndexes());
        Assert.Equal("PrayerSnapshots", snapshot.GetTableName());
        Assert.Equal(
            "AK_PrayerSnapshots_ProfileHash_Date",
            snapshot.GetKeys().Single(key => !key.IsPrimaryKey()).GetName());
        AssertRequiredRowVersion(snapshot);
        AssertIndex(
            snapshot,
            "IX_PrayerSnapshots_ProfileHash_GeneratedAtUtc",
            false,
            nameof(PrayerSnapshot.ProfileHash),
            nameof(PrayerSnapshot.GeneratedAtUtc));
        Assert.Single(snapshot.GetIndexes());
        Assert.Equal("PrayerOverrides", prayerOverride.GetTableName());
        AssertRequiredRowVersion(prayerOverride);
        AssertIndex(
            prayerOverride,
            "IX_PrayerOverrides_ActiveLookup",
            false,
            nameof(PrayerOverride.ProfileHash),
            nameof(PrayerOverride.Date),
            nameof(PrayerOverride.PrayerKey),
            nameof(PrayerOverride.IsActive),
            nameof(PrayerOverride.EffectiveRevision));
        Assert.Single(prayerOverride.GetIndexes());
        Assert.Equal("PrayerIntegrationStates", state.GetTableName());
        AssertRequiredRowVersion(state);
        AssertIndex(
            state,
            "IX_PrayerIntegrationStates_ActiveProfileHash",
            false,
            nameof(PrayerIntegrationState.ActiveProfileHash));
        AssertIndex(
            state,
            "IX_PrayerIntegrationStates_RefreshIntentProfileHash",
            false,
            nameof(PrayerIntegrationState.RefreshIntentProfileHash));
        Assert.Equal(2, state.GetIndexes().Count());
        Assert.NotNull(state.FindProperty(nameof(PrayerIntegrationState.RefreshIntentProfileHash)));
        Assert.NotNull(state.FindProperty(nameof(PrayerIntegrationState.RefreshIntentCreatedAtUtc)));
        Assert.Contains(
            state.GetForeignKeys(),
            foreignKey => foreignKey.GetConstraintName() ==
                "FK_PrayerIntegrationStates_PrayerProfiles_RefreshIntentProfileHash");
        Assert.Contains(
            profile.GetCheckConstraints(),
            constraint => constraint.Name == "CK_PrayerProfiles_TimeZoneId");
    }

    [Fact]
    public void PrayerCreateSqlUsesExplicitRefreshIntentIndexAndNonNullRowversions()
    {
        var options = new DbContextOptionsBuilder<HusayniaDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=HusayniaT07_Sql;Integrated Security=true;Encrypt=false")
            .Options;
        using var context = new HusayniaDbContext(options);

        var sql = context.Database.GenerateCreateScript();

        Assert.Contains(
            "CREATE INDEX [IX_PrayerIntegrationStates_RefreshIntentProfileHash] ON [PrayerIntegrationStates] ([RefreshIntentProfileHash]);",
            sql,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            sql.Split("[RowVersion] rowversion NOT NULL", StringSplitOptions.None).Length - 1);
    }

    private static void AssertRequiredRowVersion(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType)
    {
        var rowVersion = entityType.FindProperty(PersistencePropertyNames.RowVersion) ??
            throw new InvalidOperationException(
                $"{entityType.DisplayName()} rowversion was not configured.");
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.False(rowVersion.IsNullable);
    }

    private static void AssertIndex(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType,
        string expectedName,
        bool expectedUnique,
        params string[] expectedProperties)
    {
        var index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.GetDatabaseName() == expectedName);
        Assert.Equal(expectedUnique, index.IsUnique);
        Assert.Equal(expectedProperties, index.Properties.Select(property => property.Name));
    }
}
