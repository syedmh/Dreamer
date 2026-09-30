using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Husaynia.Domain.Prayer;

public static class PrayerLimits
{
    public const int ProfileHashLength = 64;
    public const int ProviderKindLength = 32;
    public const int MethodJsonLength = 4_000;
    public const int AlgorithmVersionLength = 64;
    public const int TimeZoneIdLength = 64;
    public const int ActorIdLength = 256;
    public const int SourceLength = 64;
    public const int PrayerKeyLength = 32;
    public const int OverrideReasonLength = 500;
    public const int IntegrationStateIdLength = 32;
    public const int ErrorCodeLength = 100;
}

public static class PrayerKeys
{
    public const string Fajr = "fajr";
    public const string Sunrise = "sunrise";
    public const string Dhuhr = "dhuhr";
    public const string Asr = "asr";
    public const string Maghrib = "maghrib";
    public const string Isha = "isha";

    public static IReadOnlyList<string> All { get; } =
        [Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha];

    public static string Normalize(string value)
    {
        var normalized = PrayerValidation.RequireAsciiToken(
            value,
            PrayerLimits.PrayerKeyLength,
            nameof(value));
        if (!All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("The prayer key is not supported.", nameof(value));
        }

        return normalized;
    }
}

public static class PrayerTimeZone
{
    public const string IanaId = "America/Los_Angeles";

    public static TimeZoneInfo Get() => TimeZoneInfo.FindSystemTimeZoneById(IanaId);

    public static TimeSpan GetUtcOffset(DateOnly date)
    {
        var localNoon = DateTime.SpecifyKind(
            date.ToDateTime(new TimeOnly(12, 0)),
            DateTimeKind.Unspecified);
        return Get().GetUtcOffset(localNoon);
    }

    public static string Require(string value)
    {
        if (!string.Equals(value?.Trim(), IanaId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The prayer time zone must be {IanaId}.",
                nameof(value));
        }

        return IanaId;
    }
}

public sealed class PrayerProfile
{
    private PrayerProfile()
    {
    }

    private PrayerProfile(
        Guid id,
        string providerKind,
        decimal latitude,
        decimal longitude,
        string methodJson,
        string algorithmVersion,
        string timeZoneId,
        DateOnly effectiveFrom,
        string profileHash,
        string createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ProviderKind = providerKind;
        Latitude = latitude;
        Longitude = longitude;
        MethodJson = methodJson;
        AlgorithmVersion = algorithmVersion;
        TimeZoneId = timeZoneId;
        EffectiveFrom = effectiveFrom;
        ProfileHash = profileHash;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public string ProviderKind { get; private set; } = string.Empty;

    public decimal Latitude { get; private set; }

    public decimal Longitude { get; private set; }

    public string MethodJson { get; private set; } = "{}";

    public string AlgorithmVersion { get; private set; } = string.Empty;

    public string TimeZoneId { get; private set; } = PrayerTimeZone.IanaId;

    public DateOnly EffectiveFrom { get; private set; }

    public string ProfileHash { get; private set; } = string.Empty;

    public string CreatedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PrayerProfile Create(
        string providerKind,
        decimal latitude,
        decimal longitude,
        string methodJson,
        string algorithmVersion,
        string timeZoneId,
        DateOnly effectiveFrom,
        string createdBy,
        DateTimeOffset? createdAtUtc = null)
    {
        var normalizedProvider = PrayerValidation.RequireAsciiToken(
            providerKind,
            PrayerLimits.ProviderKindLength,
            nameof(providerKind));
        if (normalizedProvider is not ("local" or "external"))
        {
            throw new ArgumentException(
                "The prayer provider kind must be local or external.",
                nameof(providerKind));
        }

        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        var canonicalMethod = PrayerCanonicalJson.CanonicalizeObject(
            methodJson,
            PrayerLimits.MethodJsonLength);
        var normalizedAlgorithm = PrayerValidation.RequireAsciiToken(
            algorithmVersion,
            PrayerLimits.AlgorithmVersionLength,
            nameof(algorithmVersion));
        var normalizedZone = PrayerTimeZone.Require(timeZoneId);
        var normalizedActor = PrayerValidation.RequireText(
            createdBy,
            PrayerLimits.ActorIdLength,
            nameof(createdBy));
        if (effectiveFrom == default)
        {
            throw new ArgumentException("An effective date is required.", nameof(effectiveFrom));
        }

        var hashMaterial = string.Join(
            '\n',
            normalizedProvider,
            latitude.ToString("0.######", CultureInfo.InvariantCulture),
            longitude.ToString("0.######", CultureInfo.InvariantCulture),
            canonicalMethod,
            normalizedAlgorithm,
            normalizedZone,
            effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var profileHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashMaterial)));

        return new PrayerProfile(
            Guid.NewGuid(),
            normalizedProvider,
            latitude,
            longitude,
            canonicalMethod,
            normalizedAlgorithm,
            normalizedZone,
            effectiveFrom,
            profileHash,
            normalizedActor,
            (createdAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime());
    }
}

public sealed class PrayerSnapshot
{
    private PrayerSnapshot()
    {
    }

    private PrayerSnapshot(
        Guid id,
        string profileHash,
        DateOnly date,
        string valuesJson,
        DateTimeOffset generatedAtUtc,
        string source,
        bool isValid)
    {
        Id = id;
        ProfileHash = profileHash;
        Date = date;
        ValuesJson = valuesJson;
        GeneratedAtUtc = generatedAtUtc;
        Source = source;
        IsValid = isValid;
    }

    public Guid Id { get; private set; }

    public string ProfileHash { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string ValuesJson { get; private set; } = "{}";

    public DateTimeOffset GeneratedAtUtc { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public bool IsValid { get; private set; }

    public static PrayerSnapshot Create(
        string profileHash,
        DateOnly date,
        string valuesJson,
        DateTimeOffset generatedAtUtc,
        string source,
        bool isValid)
    {
        var normalizedHash = PrayerValidation.RequireProfileHash(profileHash);
        if (date == default)
        {
            throw new ArgumentException("A snapshot date is required.", nameof(date));
        }

        var canonicalValues = CanonicalPrayerValues.Create(
            CanonicalPrayerValues.Parse(valuesJson));
        var normalizedSource = PrayerValidation.RequireAsciiToken(
            source,
            PrayerLimits.SourceLength,
            nameof(source));
        return new PrayerSnapshot(
            Guid.NewGuid(),
            normalizedHash,
            date,
            canonicalValues,
            generatedAtUtc.ToUniversalTime(),
            normalizedSource,
            isValid);
    }

    public void Replace(string valuesJson, DateTimeOffset generatedAtUtc, string source, bool isValid)
    {
        ValuesJson = CanonicalPrayerValues.Create(CanonicalPrayerValues.Parse(valuesJson));
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        Source = PrayerValidation.RequireAsciiToken(
            source,
            PrayerLimits.SourceLength,
            nameof(source));
        IsValid = isValid;
    }
}

public sealed class PrayerOverride
{
    private PrayerOverride()
    {
    }

    private PrayerOverride(
        Guid id,
        string profileHash,
        DateOnly date,
        string prayerKey,
        TimeOnly localTime,
        string reason,
        long effectiveRevision,
        string createdBy)
    {
        Id = id;
        ProfileHash = profileHash;
        Date = date;
        PrayerKey = prayerKey;
        LocalTime = localTime;
        Reason = reason;
        EffectiveRevision = effectiveRevision;
        IsActive = true;
        CreatedBy = createdBy;
    }

    public Guid Id { get; private set; }

    public string ProfileHash { get; private set; } = string.Empty;

    public DateOnly Date { get; private set; }

    public string PrayerKey { get; private set; } = string.Empty;

    public TimeOnly LocalTime { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public long EffectiveRevision { get; private set; }

    public bool IsActive { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public string? DeactivatedBy { get; private set; }

    public DateTimeOffset? DeactivatedAtUtc { get; private set; }

    public static PrayerOverride Create(
        string profileHash,
        DateOnly date,
        string prayerKey,
        TimeOnly localTime,
        string reason,
        long effectiveRevision,
        string createdBy)
    {
        if (date == default)
        {
            throw new ArgumentException("An override date is required.", nameof(date));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveRevision, 1);
        return new PrayerOverride(
            Guid.NewGuid(),
            PrayerValidation.RequireProfileHash(profileHash),
            date,
            PrayerKeys.Normalize(prayerKey),
            localTime,
            PrayerValidation.RequireText(
                reason,
                PrayerLimits.OverrideReasonLength,
                nameof(reason)),
            effectiveRevision,
            PrayerValidation.RequireText(
                createdBy,
                PrayerLimits.ActorIdLength,
                nameof(createdBy)));
    }

    public bool Deactivate(string actorId, DateTimeOffset? deactivatedAtUtc = null)
    {
        if (!IsActive)
        {
            return false;
        }

        IsActive = false;
        DeactivatedBy = PrayerValidation.RequireText(
            actorId,
            PrayerLimits.ActorIdLength,
            nameof(actorId));
        DeactivatedAtUtc = (deactivatedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        return true;
    }
}

public sealed class PrayerIntegrationState
{
    public const string PrimaryId = "primary";

    private PrayerIntegrationState()
    {
    }

    public PrayerIntegrationState(string id = PrimaryId)
    {
        if (!string.Equals(id, PrimaryId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The prayer integration state identifier is invalid.", nameof(id));
        }

        Id = PrimaryId;
    }

    public string Id { get; private set; } = PrimaryId;

    public string? ActiveProfileHash { get; private set; }

    public string? RefreshIntentProfileHash { get; private set; }

    public DateTimeOffset? RefreshIntentCreatedAtUtc { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? LastSuccessAtUtc { get; private set; }

    public DateTimeOffset? LastFailureAtUtc { get; private set; }

    public string? LastErrorCode { get; private set; }

    public string? LastSource { get; private set; }

    public DateOnly? LastGeneratedMonth { get; private set; }

    public void Activate(string profileHash, DateTimeOffset refreshIntentCreatedAtUtc)
    {
        var normalizedHash = PrayerValidation.RequireProfileHash(profileHash);
        ActiveProfileHash = normalizedHash;
        RefreshIntentProfileHash = normalizedHash;
        RefreshIntentCreatedAtUtc = refreshIntentCreatedAtUtc.ToUniversalTime();
    }

    public void RecordSuccess(
        DateTimeOffset occurredAtUtc,
        string source,
        DateOnly generatedMonth)
    {
        LastAttemptAtUtc = occurredAtUtc.ToUniversalTime();
        LastSuccessAtUtc = LastAttemptAtUtc;
        LastErrorCode = null;
        LastSource = PrayerValidation.RequireAsciiToken(
            source,
            PrayerLimits.SourceLength,
            nameof(source));
        LastGeneratedMonth = new DateOnly(generatedMonth.Year, generatedMonth.Month, 1);
    }

    public void RecordFailure(DateTimeOffset occurredAtUtc, string errorCode)
    {
        LastAttemptAtUtc = occurredAtUtc.ToUniversalTime();
        LastFailureAtUtc = LastAttemptAtUtc;
        LastErrorCode = PrayerValidation.RequireMachineCode(errorCode);
    }
}

public sealed record PrayerSnapshotBatch(
    string ProfileHash,
    int Year,
    int Month,
    IReadOnlyList<PrayerSnapshot> Snapshots)
{
    public static PrayerSnapshotBatch Create(
        string profileHash,
        int year,
        int month,
        IReadOnlyCollection<PrayerSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var normalizedHash = PrayerValidation.RequireProfileHash(profileHash);
        var expectedCount = DateTime.DaysInMonth(year, month);
        var ordered = snapshots.OrderBy(snapshot => snapshot.Date).ToArray();
        if (ordered.Length != expectedCount ||
            ordered.Any(snapshot =>
                !string.Equals(snapshot.ProfileHash, normalizedHash, StringComparison.Ordinal) ||
                snapshot.Date.Year != year ||
                snapshot.Date.Month != month ||
                !snapshot.IsValid) ||
            ordered.Select(snapshot => snapshot.Date).Distinct().Count() != expectedCount)
        {
            throw new ArgumentException(
                "A complete valid single-profile month is required.",
                nameof(snapshots));
        }

        for (var day = 1; day <= expectedCount; day++)
        {
            if (ordered[day - 1].Date.Day != day)
            {
                throw new ArgumentException(
                    "A complete valid single-profile month is required.",
                    nameof(snapshots));
            }
        }

        return new PrayerSnapshotBatch(normalizedHash, year, month, ordered);
    }
}

public static class CanonicalPrayerValues
{
    public static string Create(IReadOnlyDictionary<string, TimeOnly> times)
    {
        ArgumentNullException.ThrowIfNull(times);
        if (times.Count != PrayerKeys.All.Count ||
            PrayerKeys.All.Any(key => !times.ContainsKey(key)))
        {
            throw new ArgumentException(
                "Every supported prayer time is required.",
                nameof(times));
        }

        for (var index = 1; index < PrayerKeys.All.Count; index++)
        {
            if (times[PrayerKeys.All[index]] <= times[PrayerKeys.All[index - 1]])
            {
                throw new ArgumentException(
                    "Prayer times must be strictly ordered.",
                    nameof(times));
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var key in PrayerKeys.All)
            {
                writer.WriteString(
                    key,
                    times[key].ToString("HH:mm", CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyDictionary<string, TimeOnly> Parse(string valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson) ||
            valuesJson.Length > PrayerLimits.MethodJsonLength)
        {
            throw new ArgumentException("Prayer values JSON is invalid.", nameof(valuesJson));
        }

        try
        {
            using var document = JsonDocument.Parse(
                valuesJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Prayer values JSON is invalid.", nameof(valuesJson));
            }

            var result = new Dictionary<string, TimeOnly>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = PrayerKeys.Normalize(property.Name);
                if (!result.TryAdd(
                        key,
                        property.Value.ValueKind == JsonValueKind.String &&
                        TimeOnly.TryParseExact(
                            property.Value.GetString(),
                            "HH:mm",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var time)
                            ? time
                            : throw new ArgumentException(
                                "Prayer values JSON is invalid.",
                                nameof(valuesJson))))
                {
                    throw new ArgumentException("Prayer values JSON is invalid.", nameof(valuesJson));
                }
            }

            if (result.Count != PrayerKeys.All.Count ||
                PrayerKeys.All.Any(key => !result.ContainsKey(key)))
            {
                throw new ArgumentException("Prayer values JSON is invalid.", nameof(valuesJson));
            }

            return result;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Prayer values JSON is invalid.", nameof(valuesJson), exception);
        }
    }
}

internal static class PrayerValidation
{
    internal static string RequireProfileHash(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != PrayerLimits.ProfileHashLength ||
            normalized.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterLower(character)))
        {
            throw new ArgumentException("A canonical SHA-256 profile hash is required.", nameof(value));
        }

        return normalized;
    }

    internal static string RequireAsciiToken(string value, int maximumLength, string paramName)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("A bounded ASCII token is required.", paramName);
        }

        return normalized;
    }

    internal static string RequireMachineCode(string value) =>
        RequireAsciiToken(value, PrayerLimits.ErrorCodeLength, nameof(value));

    internal static string RequireText(string value, int maximumLength, string paramName)
    {
        var normalized = value?.Trim().Normalize(NormalizationForm.FormC) ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maximumLength)
        {
            throw new ArgumentException("A bounded value is required.", paramName);
        }

        return normalized;
    }
}
