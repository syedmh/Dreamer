using Husaynia.Domain.Prayer;

namespace Husaynia.Domain.Tests.Prayer;

public sealed class PrayerCalculatorTests
{
    [Fact]
    public void SameProfileAndDateProduceByteIdenticalValues()
    {
        var profile = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
            DeterministicPrayerCalculator.AlgorithmVersion,
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");
        var calculator = new DeterministicPrayerCalculator();

        var first = calculator.Calculate(profile, new DateOnly(2026, 3, 8));
        var second = calculator.Calculate(profile, new DateOnly(2026, 3, 8));

        Assert.Equal(first.ValuesJson, second.ValuesJson);
        Assert.Equal(first.Times, second.Times);
    }

    [Fact]
    public void LosAngelesOffsetsFollowDstBoundaries()
    {
        Assert.Equal(TimeSpan.FromHours(-8), PrayerTimeZone.GetUtcOffset(new DateOnly(2026, 3, 7)));
        Assert.Equal(TimeSpan.FromHours(-7), PrayerTimeZone.GetUtcOffset(new DateOnly(2026, 3, 8)));
        Assert.Equal(TimeSpan.FromHours(-7), PrayerTimeZone.GetUtcOffset(new DateOnly(2026, 10, 31)));
        Assert.Equal(TimeSpan.FromHours(-8), PrayerTimeZone.GetUtcOffset(new DateOnly(2026, 11, 1)));
    }
}
