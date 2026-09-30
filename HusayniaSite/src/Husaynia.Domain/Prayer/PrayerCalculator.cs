using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Husaynia.Domain.Prayer;

public sealed record CalculatedPrayerDay(
    DateOnly Date,
    IReadOnlyDictionary<string, TimeOnly> Times,
    string ValuesJson);

public sealed class DeterministicPrayerCalculator
{
    public const string AlgorithmVersion = "solar-v1";
    private const double DegreesToRadians = Math.PI / 180;

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "The calculator is an injected deterministic domain service.")]
    public CalculatedPrayerDay Calculate(PrayerProfile profile, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(
                profile.AlgorithmVersion,
                AlgorithmVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The prayer calculation algorithm version is unsupported.",
                nameof(profile));
        }

        var settings = PrayerCalculationSettings.Parse(profile.MethodJson);
        var dayOfYear = date.DayOfYear;
        var gamma = (2 * Math.PI / 365) * (dayOfYear - 1);
        var equationOfTime = 229.18 *
            (0.000075 +
             (0.001868 * Math.Cos(gamma)) -
             (0.032077 * Math.Sin(gamma)) -
             (0.014615 * Math.Cos(2 * gamma)) -
             (0.040849 * Math.Sin(2 * gamma)));
        var declination =
            0.006918 -
            (0.399912 * Math.Cos(gamma)) +
            (0.070257 * Math.Sin(gamma)) -
            (0.006758 * Math.Cos(2 * gamma)) +
            (0.000907 * Math.Sin(2 * gamma)) -
            (0.002697 * Math.Cos(3 * gamma)) +
            (0.00148 * Math.Sin(3 * gamma));
        var solarNoonUtcMinutes =
            720 - (4 * Convert.ToDouble(profile.Longitude, CultureInfo.InvariantCulture)) - equationOfTime;
        var latitudeRadians =
            Convert.ToDouble(profile.Latitude, CultureInfo.InvariantCulture) * DegreesToRadians;

        var sunriseDelta = HourAngleMinutes(
            latitudeRadians,
            declination,
            altitudeDegrees: -0.833);
        var fajrDelta = HourAngleMinutes(
            latitudeRadians,
            declination,
            -settings.FajrAngle);
        var ishaDelta = HourAngleMinutes(
            latitudeRadians,
            declination,
            -settings.IshaAngle);
        var asrAltitude = Math.Atan(
            1 /
            (settings.AsrShadowFactor +
             Math.Tan(Math.Abs(latitudeRadians - declination)))) / DegreesToRadians;
        var asrDelta = HourAngleMinutes(
            latitudeRadians,
            declination,
            asrAltitude);

        var utcMinutes = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [PrayerKeys.Fajr] = solarNoonUtcMinutes - fajrDelta,
            [PrayerKeys.Sunrise] = solarNoonUtcMinutes - sunriseDelta,
            [PrayerKeys.Dhuhr] = solarNoonUtcMinutes + settings.DhuhrOffsetMinutes,
            [PrayerKeys.Asr] = solarNoonUtcMinutes + asrDelta,
            [PrayerKeys.Maghrib] = solarNoonUtcMinutes + sunriseDelta,
            [PrayerKeys.Isha] = solarNoonUtcMinutes + ishaDelta,
        };

        var times = new Dictionary<string, TimeOnly>(StringComparer.Ordinal);
        foreach (var key in PrayerKeys.All)
        {
            var offset = settings.Offsets.GetValueOrDefault(key);
            times[key] = ToLocalTime(date, utcMinutes[key] + offset);
        }

        for (var index = 1; index < PrayerKeys.All.Count; index++)
        {
            if (times[PrayerKeys.All[index]] <= times[PrayerKeys.All[index - 1]])
            {
                throw new InvalidOperationException(
                    "The calculated prayer schedule is not strictly ordered.");
            }
        }

        return new CalculatedPrayerDay(date, times, CanonicalPrayerValues.Create(times));
    }

    private static double HourAngleMinutes(
        double latitudeRadians,
        double declination,
        double altitudeDegrees)
    {
        var altitudeRadians = altitudeDegrees * DegreesToRadians;
        var denominator = Math.Cos(latitudeRadians) * Math.Cos(declination);
        var cosine =
            (Math.Sin(altitudeRadians) -
             (Math.Sin(latitudeRadians) * Math.Sin(declination))) /
            denominator;
        if (cosine is < -1 or > 1)
        {
            throw new InvalidOperationException(
                "Prayer times cannot be calculated for the configured date and latitude.");
        }

        return 4 * (Math.Acos(cosine) / DegreesToRadians);
    }

    private static TimeOnly ToLocalTime(DateOnly date, double utcMinutes)
    {
        var roundedMinutes = Math.Round(utcMinutes, MidpointRounding.AwayFromZero);
        var utc = new DateTimeOffset(
                date.Year,
                date.Month,
                date.Day,
                0,
                0,
                0,
                TimeSpan.Zero)
            .AddMinutes(roundedMinutes);
        var local = TimeZoneInfo.ConvertTime(utc, PrayerTimeZone.Get());
        if (DateOnly.FromDateTime(local.DateTime) != date)
        {
            throw new InvalidOperationException(
                "The calculated prayer time did not resolve to the requested local date.");
        }

        return TimeOnly.FromDateTime(local.DateTime);
    }

    private sealed record PrayerCalculationSettings(
        double FajrAngle,
        double IshaAngle,
        int AsrShadowFactor,
        int DhuhrOffsetMinutes,
        IReadOnlyDictionary<string, int> Offsets)
    {
        internal static PrayerCalculationSettings Parse(string methodJson)
        {
            using var document = JsonDocument.Parse(methodJson);
            var root = document.RootElement;
            var allowed = new HashSet<string>(
                ["fajrAngle", "ishaAngle", "asrShadowFactor", "dhuhrOffsetMinutes", "offsets"],
                StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            {
                throw new ArgumentException(
                    "The prayer method contains an unsupported setting.",
                    nameof(methodJson));
            }

            var fajrAngle = RequiredNumber(root, "fajrAngle", 1, 30);
            var ishaAngle = RequiredNumber(root, "ishaAngle", 1, 30);
            var asrShadowFactor = RequiredInteger(root, "asrShadowFactor", 1, 2);
            var dhuhrOffset = OptionalInteger(root, "dhuhrOffsetMinutes", -120, 120);
            var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            if (root.TryGetProperty("offsets", out var offsetsElement))
            {
                if (offsetsElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException(
                        "Prayer offsets must be a JSON object.",
                        nameof(methodJson));
                }

                foreach (var property in offsetsElement.EnumerateObject())
                {
                    var key = property.Name;
                    if (!PrayerKeys.All.Contains(key, StringComparer.Ordinal))
                    {
                        throw new ArgumentException(
                            "The prayer offset key is not canonical.",
                            nameof(methodJson));
                    }

                    if (!offsets.TryAdd(
                            key,
                            Integer(property.Value, -120, 120, property.Name)))
                    {
                        throw new ArgumentException(
                            "Duplicate prayer offsets are not allowed.",
                            nameof(methodJson));
                    }
                }
            }

            return new PrayerCalculationSettings(
                fajrAngle,
                ishaAngle,
                asrShadowFactor,
                dhuhrOffset,
                offsets);
        }

        private static double RequiredNumber(
            JsonElement root,
            string name,
            double minimum,
            double maximum)
        {
            if (!root.TryGetProperty(name, out var element) ||
                element.ValueKind != JsonValueKind.Number ||
                !element.TryGetDouble(out var value) ||
                !double.IsFinite(value) ||
                value < minimum ||
                value > maximum)
            {
                throw new ArgumentException(
                    $"The prayer method setting {name} is invalid.",
                    nameof(root));
            }

            return value;
        }

        private static int RequiredInteger(
            JsonElement root,
            string name,
            int minimum,
            int maximum)
        {
            if (!root.TryGetProperty(name, out var element))
            {
                throw new ArgumentException(
                    $"The prayer method setting {name} is required.",
                    nameof(root));
            }

            return Integer(element, minimum, maximum, name);
        }

        private static int OptionalInteger(
            JsonElement root,
            string name,
            int minimum,
            int maximum) =>
            root.TryGetProperty(name, out var element)
                ? Integer(element, minimum, maximum, name)
                : 0;

        private static int Integer(
            JsonElement element,
            int minimum,
            int maximum,
            string name)
        {
            if (element.ValueKind != JsonValueKind.Number ||
                !element.TryGetInt32(out var value) ||
                value < minimum ||
                value > maximum)
            {
                throw new ArgumentException(
                    $"The prayer method setting {name} is invalid.",
                    nameof(element));
            }

            return value;
        }
    }
}
