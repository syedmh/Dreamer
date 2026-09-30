using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Prayer;

namespace Husaynia.Application.Prayer;

public sealed class PrayerOptions
{
    public const string SectionName = "Prayer";

    public string TimeZoneId { get; init; } = PrayerTimeZone.IanaId;

    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromHours(36);

    public int GenerateMonthsAhead { get; init; } = 2;

    public bool ExternalProviderEnabled { get; init; }

    public Uri? ExternalProviderEndpoint { get; init; }

    public TimeSpan ExternalProviderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ExternalProviderClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    public IReadOnlySet<string> ExternalProviderAllowedHosts { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool ExternalProviderTestMode { get; init; }
}

public static class PrayerRefreshSchedulingStatuses
{
    public const string NotRequested = "not_requested";

    public const string Pending = "pending";

    public const string Scheduled = "scheduled";
}

public interface IPrayerAdministration
{
    Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
        CreatePrayerProfileCommand command,
        UserContext actor,
        CancellationToken cancellationToken);

    Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
        ActivatePrayerProfileCommand command,
        UserContext actor,
        CancellationToken cancellationToken);

    Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
        SavePrayerOverrideCommand command,
        UserContext actor,
        CancellationToken cancellationToken);

    Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
        DeactivatePrayerOverrideCommand command,
        UserContext actor,
        CancellationToken cancellationToken);

    Task<Result<PrayerRefreshReceipt, PrayerAdministrationError>> RefreshAsync(
        RefreshPrayerScheduleCommand command,
        UserContext actor,
        CancellationToken cancellationToken);
}

public interface IPrayerScheduleStore
{
    Task<PrayerMonthData?> ReadMonthAsync(
        YearMonth month,
        CancellationToken cancellationToken);
}

public interface IPrayerAdministrationStore
{
    Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
        PrayerProfile profile,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
        string profileHash,
        CancellationToken cancellationToken);

    Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken);

    Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
        ActivatePrayerProfileCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
        SavePrayerOverrideCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);

    Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
        DeactivatePrayerOverrideCommand command,
        IdentityAuditDescriptor audit,
        CancellationToken cancellationToken);
}

public interface IPrayerProfileDefinitionReader
{
    Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
        string profileHash,
        CancellationToken cancellationToken);
}

public interface IPrayerSchedulingStore
{
    Task<PrayerActiveProfile?> ReadActiveProfileAsync(CancellationToken cancellationToken);
}

public interface IPrayerRefreshStore
{
    Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> ReplaceMonthAsync(
        PrayerSnapshotBatch batch,
        string source,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken);

    Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RecordRefreshFailureAsync(
        string profileHash,
        YearMonth month,
        PrayerRefreshError refreshFailure,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken);
}

public interface IPrayerRefreshService
{
    Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshAsync(
        string profileHash,
        YearMonth month,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken);
}

public interface IPrayerRefreshJobCoordinator
{
    Task RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken);

    Task EnsureProfileMonthsAsync(
        string profileHash,
        DateOnly effectiveFrom,
        CancellationToken cancellationToken);
}

public sealed record CreatePrayerProfileCommand(
    string ProviderKind,
    decimal Latitude,
    decimal Longitude,
    string MethodJson,
    string AlgorithmVersion,
    DateOnly EffectiveFrom);

public sealed record ActivatePrayerProfileCommand(
    Guid ProfileId,
    RowVersion ExpectedStateRowVersion);

public sealed record SavePrayerOverrideCommand(
    string ProfileHash,
    DateOnly Date,
    string PrayerKey,
    TimeOnly LocalTime,
    string Reason);

public sealed record DeactivatePrayerOverrideCommand(
    Guid OverrideId,
    RowVersion ExpectedRowVersion);

public sealed record RefreshPrayerScheduleCommand(
    string ProfileHash,
    YearMonth Month);

public sealed record PrayerProfileView(
    Guid Id,
    string ProfileHash,
    string ProviderKind,
    decimal Latitude,
    decimal Longitude,
    string MethodJson,
    string AlgorithmVersion,
    string TimeZoneId,
    DateOnly EffectiveFrom,
    bool IsActive,
    RowVersion StateRowVersion,
    string RefreshSchedulingStatus)
{
    public static PrayerProfileView From(PrayerProfile profile) =>
        new(
            profile.Id,
            profile.ProfileHash,
            profile.ProviderKind,
            profile.Latitude,
            profile.Longitude,
            profile.MethodJson,
            profile.AlgorithmVersion,
            profile.TimeZoneId,
            profile.EffectiveFrom,
            false,
            new RowVersion(ReadOnlyMemory<byte>.Empty),
            PrayerRefreshSchedulingStatuses.NotRequested);
}

public sealed record PrayerOverrideView(
    Guid Id,
    string ProfileHash,
    DateOnly Date,
    string PrayerKey,
    TimeOnly LocalTime,
    string Reason,
    long EffectiveRevision,
    bool IsActive,
    RowVersion RowVersion);

public sealed record PrayerProfileDefinition(
    string ProfileHash,
    string ProviderKind,
    decimal Latitude,
    decimal Longitude,
    string MethodJson,
    string AlgorithmVersion,
    string TimeZoneId,
    DateOnly EffectiveFrom);

public sealed record PrayerActiveProfile(
    string ProfileHash,
    DateOnly EffectiveFrom);

public sealed record PrayerSnapshotData(
    DateOnly Date,
    IReadOnlyDictionary<string, TimeOnly> Times,
    DateTimeOffset GeneratedAtUtc,
    string Source,
    bool IsValid);

public sealed record PrayerOverrideData(
    Guid Id,
    DateOnly Date,
    string PrayerKey,
    TimeOnly LocalTime,
    long EffectiveRevision);

public sealed record PrayerMonthData(
    string ProfileHash,
    IReadOnlyList<PrayerSnapshotData> Snapshots,
    IReadOnlyList<PrayerOverrideData> Overrides);

public sealed record PrayerRefreshReceipt(
    string ProfileHash,
    YearMonth Month,
    int SnapshotCount);

public sealed record PrayerAdministrationError(string Code, string Message);

public sealed record PrayerRefreshError(string Code, string Message);
