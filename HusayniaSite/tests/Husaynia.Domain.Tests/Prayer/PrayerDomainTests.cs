using Husaynia.Domain.Prayer;
using System.Globalization;

namespace Husaynia.Domain.Tests.Prayer;

public sealed class PrayerDomainTests
{
    [Fact]
    public void EquivalentProfilesProduceCanonicalJsonAndHash()
    {
        var first = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"ishaAngle":18,"offsets":{"isha":2,"fajr":-1},"fajrAngle":18.0}""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");
        var second = PrayerProfile.Create(
            "LOCAL",
            47.912900m,
            -122.098200m,
            """{ "fajrAngle":18, "offsets": { "fajr":-1.0, "isha":2.0 }, "ishaAngle":18.00 }""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");

        Assert.Equal(first.MethodJson, second.MethodJson);
        Assert.Equal(first.ProfileHash, second.ProfileHash);
    }

    [Fact]
    public void OffsetKeyCaseIsCanonicalAndCaseCollisionsAreRejected()
    {
        var lower = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{"fajr":1}}""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");
        var mixed = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{"Fajr":1}}""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");

        Assert.Equal(lower.MethodJson, mixed.MethodJson);
        Assert.Equal(lower.ProfileHash, mixed.ProfileHash);
        Assert.Throws<ArgumentException>(() => PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{"fajr":1,"Fajr":2}}""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator"));
    }

    [Fact]
    public void ProfileRejectsAmbiguousOrUnsupportedSettings()
    {
        Assert.Throws<ArgumentException>(() => PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"fajrAngle":17}""",
            "solar-v1",
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator"));
        Assert.Throws<ArgumentException>(() => PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            "{}",
            "solar-v1",
            "UTC",
            new DateOnly(2026, 1, 1),
            "administrator"));
    }

    [Fact]
    public void SnapshotBatchRequiresEveryDateAndOneProfile()
    {
        var month = new DateOnly(2026, 3, 1);
        var snapshots = Enumerable.Range(0, 31)
            .Select(day => PrayerSnapshot.Create(
                "A".PadLeft(64, '0'),
                month.AddDays(day),
                CanonicalPrayerValues.Create(SyntheticTimes()),
                DateTimeOffset.Parse("2026-02-20T12:00:00Z", CultureInfo.InvariantCulture),
                "calculated",
                true))
            .ToArray();

        var batch = PrayerSnapshotBatch.Create("A".PadLeft(64, '0'), 2026, 3, snapshots);

        Assert.Equal(31, batch.Snapshots.Count);
        Assert.Throws<ArgumentException>(() => PrayerSnapshotBatch.Create(
            "A".PadLeft(64, '0'),
            2026,
            3,
            snapshots[..^1]));
        snapshots[^1] = PrayerSnapshot.Create(
            "B".PadLeft(64, '0'),
            snapshots[^1].Date,
            snapshots[^1].ValuesJson,
            snapshots[^1].GeneratedAtUtc,
            snapshots[^1].Source,
            true);
        Assert.Throws<ArgumentException>(() => PrayerSnapshotBatch.Create(
            "A".PadLeft(64, '0'),
            2026,
            3,
            snapshots));
    }

    [Fact]
    public void OverrideDeactivationIsConcurrencyGuarded()
    {
        var prayerOverride = PrayerOverride.Create(
            "A".PadLeft(64, '0'),
            new DateOnly(2026, 3, 10),
            PrayerKeys.Fajr,
            new TimeOnly(5, 21),
            "community notice",
            4,
            "administrator");

        Assert.True(prayerOverride.Deactivate("administrator"));
        Assert.False(prayerOverride.IsActive);
        Assert.False(prayerOverride.Deactivate("administrator"));
    }

    private static Dictionary<string, TimeOnly> SyntheticTimes() =>
        new Dictionary<string, TimeOnly>(StringComparer.Ordinal)
        {
            [PrayerKeys.Fajr] = new(5, 0),
            [PrayerKeys.Sunrise] = new(6, 30),
            [PrayerKeys.Dhuhr] = new(12, 15),
            [PrayerKeys.Asr] = new(15, 30),
            [PrayerKeys.Maghrib] = new(18, 0),
            [PrayerKeys.Isha] = new(19, 30),
        };
}
