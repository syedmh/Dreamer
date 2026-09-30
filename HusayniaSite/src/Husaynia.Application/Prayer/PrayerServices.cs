using System.Security.Cryptography;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Domain.Identity;
using Husaynia.Domain.Prayer;

namespace Husaynia.Application.Prayer;

public sealed class PrayerScheduleService(
    IPrayerScheduleStore store,
    PrayerOptions options,
    TimeProvider timeProvider) : IPrayerScheduleService
{
    private readonly IPrayerScheduleStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly PrayerOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<PrayerSchedule, PrayerError>> GetMonthAsync(
        YearMonth month,
        string timeZoneId,
        CancellationToken ct)
    {
        if (!string.Equals(timeZoneId, PrayerTimeZone.IanaId, StringComparison.Ordinal))
        {
            return Result.Fail<PrayerSchedule, PrayerError>(
                new(
                    "invalid_time_zone",
                    $"Prayer schedules use {PrayerTimeZone.IanaId}."));
        }

        PrayerMonthData? data;
        try
        {
            data = await store.ReadMonthAsync(month, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable();
        }

        if (!TryBuildSchedule(data, month, out var schedule))
        {
            return Unavailable();
        }

        return Result.Succeed<PrayerSchedule, PrayerError>(schedule);
    }

    private bool TryBuildSchedule(
        PrayerMonthData? data,
        YearMonth month,
        out PrayerSchedule schedule)
    {
        schedule = null!;
        if (data is null ||
            data.Snapshots.Count != DateTime.DaysInMonth(month.Year, month.Month))
        {
            return false;
        }

        var snapshots = data.Snapshots.OrderBy(snapshot => snapshot.Date).ToArray();
        for (var day = 1; day <= snapshots.Length; day++)
        {
            var snapshot = snapshots[day - 1];
            if (!snapshot.IsValid ||
                snapshot.Date != new DateOnly(month.Year, month.Month, day) ||
                snapshot.Times.Count != PrayerKeys.All.Count ||
                PrayerKeys.All.Any(key => !snapshot.Times.ContainsKey(key)))
            {
                return false;
            }
        }

        var latestOverrides = data.Overrides
            .GroupBy(
                prayerOverride => (prayerOverride.Date, prayerOverride.PrayerKey))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(prayerOverride => prayerOverride.EffectiveRevision)
                    .ThenByDescending(prayerOverride => prayerOverride.Id)
                    .First());
        var days = new List<PrayerDay>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            var hasOverride = false;
            var times = new List<PrayerTime>(PrayerKeys.All.Count);
            foreach (var key in PrayerKeys.All)
            {
                var localTime = snapshot.Times[key];
                if (latestOverrides.TryGetValue((snapshot.Date, key), out var prayerOverride))
                {
                    localTime = prayerOverride.LocalTime;
                    hasOverride = true;
                }

                times.Add(new PrayerTime(key, localTime));
            }

            days.Add(new PrayerDay(snapshot.Date, times, hasOverride));
        }

        var generatedAtUtc = snapshots.Min(snapshot => snapshot.GeneratedAtUtc).ToUniversalTime();
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var age = now - generatedAtUtc;
        schedule = new PrayerSchedule(
            month,
            PrayerTimeZone.IanaId,
            days,
            generatedAtUtc,
            generatedAtUtc > now || age > options.SnapshotMaxAge);
        return true;
    }

    private static Result<PrayerSchedule, PrayerError> Unavailable() =>
        Result.Fail<PrayerSchedule, PrayerError>(
            new(
                "unavailable",
                "A complete local prayer schedule is not available."));
}

public sealed class PrayerAdministrationService(
    IPrayerAdministrationStore store,
    IPrayerRefreshService refreshService,
    IPrayerRefreshJobCoordinator coordinator,
    AdministrativeCapabilityAuthorizer authorizer,
    IIdentityAuditFinalizer auditFinalizer) : IPrayerAdministration
{
    private const string TargetTypeProfile = "PrayerProfile";
    private const string TargetTypeOverride = "PrayerOverride";
    private const string TargetTypeSnapshot = "PrayerSnapshot";
    private readonly IPrayerAdministrationStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IPrayerRefreshService refreshService =
        refreshService ?? throw new ArgumentNullException(nameof(refreshService));
    private readonly IPrayerRefreshJobCoordinator coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly AdministrativeCapabilityAuthorizer authorizer =
        authorizer ?? throw new ArgumentNullException(nameof(authorizer));
    private readonly IIdentityAuditFinalizer auditFinalizer =
        auditFinalizer ?? throw new ArgumentNullException(nameof(auditFinalizer));

    public async Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
        CreatePrayerProfileCommand command,
        UserContext actor,
        CancellationToken cancellationToken)
    {
        const string action = "prayer.profile.create";
        var authorization = await AuthorizeAsync(
                actor,
                action,
                TargetTypeProfile,
                "new")
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return Result.Fail<PrayerProfileView, PrayerAdministrationError>(authorization);
        }

        PrayerProfile profile;
        try
        {
            ArgumentNullException.ThrowIfNull(command);
            profile = PrayerProfile.Create(
                command.ProviderKind,
                command.Latitude,
                command.Longitude,
                command.MethodJson,
                command.AlgorithmVersion,
                PrayerTimeZone.IanaId,
                command.EffectiveFrom,
                actor.UserId);
            if (profile.ProviderKind == "local")
            {
                _ = new DeterministicPrayerCalculator().Calculate(
                    profile,
                    profile.EffectiveFrom);
            }
        }
        catch (ArgumentException)
        {
            return await ValidationFailureAsync<PrayerProfileView>(
                    actor,
                    action,
                    TargetTypeProfile,
                    "new",
                    "invalid_profile",
                    "The prayer profile is invalid.")
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return await ValidationFailureAsync<PrayerProfileView>(
                    actor,
                    action,
                    TargetTypeProfile,
                    "new",
                    "invalid_profile",
                    "The prayer profile cannot produce a complete schedule.")
                .ConfigureAwait(false);
        }

        return await store.CreateProfileAsync(
                profile,
                Audit(actor, action, TargetTypeProfile, profile.ProfileHash),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
        ActivatePrayerProfileCommand command,
        UserContext actor,
        CancellationToken cancellationToken)
    {
        const string action = "prayer.profile.activate";
        var targetId = command?.ProfileId.ToString("N") ?? "request";
        var authorization = await AuthorizeAsync(
                actor,
                action,
                TargetTypeProfile,
                targetId)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return Result.Fail<PrayerProfileView, PrayerAdministrationError>(authorization);
        }

        if (command is null || command.ProfileId == Guid.Empty)
        {
            return await ValidationFailureAsync<PrayerProfileView>(
                    actor,
                    action,
                    TargetTypeProfile,
                    targetId,
                    "invalid_profile",
                    "A prayer profile identifier is required.")
                .ConfigureAwait(false);
        }

        var activation = await store.ActivateProfileAsync(
                command,
                Audit(actor, action, TargetTypeProfile, targetId),
                cancellationToken)
            .ConfigureAwait(false);
        if (activation.IsFailure)
        {
            return activation;
        }

        try
        {
            await coordinator.EnsureProfileMonthsAsync(
                    activation.Success.ProfileHash,
                    activation.Success.EffectiveFrom,
                    cancellationToken)
                .ConfigureAwait(false);
            return Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                activation.Success with
                {
                    RefreshSchedulingStatus = PrayerRefreshSchedulingStatuses.Scheduled,
                });
        }
        catch
        {
            return Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                activation.Success with
                {
                    RefreshSchedulingStatus = PrayerRefreshSchedulingStatuses.Pending,
                });
        }
    }

    public async Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
        SavePrayerOverrideCommand command,
        UserContext actor,
        CancellationToken cancellationToken)
    {
        const string action = "prayer.override.save";
        var targetId = command is null
            ? "request"
            : $"{command.ProfileHash}:{command.Date:yyyy-MM-dd}:{command.PrayerKey}";
        var authorization = await AuthorizeAsync(
                actor,
                action,
                TargetTypeOverride,
                targetId)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(authorization);
        }

        try
        {
            ArgumentNullException.ThrowIfNull(command);
            _ = PrayerOverride.Create(
                command.ProfileHash,
                command.Date,
                command.PrayerKey,
                command.LocalTime,
                command.Reason,
                1,
                actor.UserId);
        }
        catch (ArgumentException)
        {
            return await ValidationFailureAsync<PrayerOverrideView>(
                    actor,
                    action,
                    TargetTypeOverride,
                    targetId,
                    "invalid_override",
                    "The prayer override is invalid.")
                .ConfigureAwait(false);
        }

        var normalizedCommand = command with
        {
            ProfileHash = command.ProfileHash.Trim().ToUpperInvariant(),
            PrayerKey = PrayerKeys.Normalize(command.PrayerKey),
            Reason = command.Reason.Trim().Normalize(NormalizationForm.FormC),
        };
        return await store.SaveOverrideAsync(
                normalizedCommand,
                Audit(actor, action, TargetTypeOverride, targetId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
        DeactivatePrayerOverrideCommand command,
        UserContext actor,
        CancellationToken cancellationToken)
    {
        const string action = "prayer.override.deactivate";
        var targetId = command?.OverrideId.ToString("N") ?? "request";
        var authorization = await AuthorizeAsync(
                actor,
                action,
                TargetTypeOverride,
                targetId)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return Result.Fail<PrayerOverrideView, PrayerAdministrationError>(authorization);
        }

        if (command is null ||
            command.OverrideId == Guid.Empty ||
            command.ExpectedRowVersion.Value.IsEmpty)
        {
            return await ValidationFailureAsync<PrayerOverrideView>(
                    actor,
                    action,
                    TargetTypeOverride,
                    targetId,
                    "invalid_override",
                    "An override identifier and rowversion are required.")
                .ConfigureAwait(false);
        }

        return await store.DeactivateOverrideAsync(
                command,
                Audit(actor, action, TargetTypeOverride, targetId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<PrayerRefreshReceipt, PrayerAdministrationError>> RefreshAsync(
        RefreshPrayerScheduleCommand command,
        UserContext actor,
        CancellationToken cancellationToken)
    {
        const string action = "prayer.snapshot.refresh";
        var targetId = command is null
            ? "request"
            : $"{command.ProfileHash}:{command.Month.Year:D4}-{command.Month.Month:D2}";
        var authorization = await AuthorizeAsync(
                actor,
                action,
                TargetTypeSnapshot,
                targetId)
            .ConfigureAwait(false);
        if (authorization is not null)
        {
            return Result.Fail<PrayerRefreshReceipt, PrayerAdministrationError>(authorization);
        }

        if (command is null || !IsProfileHash(command.ProfileHash))
        {
            return await ValidationFailureAsync<PrayerRefreshReceipt>(
                    actor,
                    action,
                    TargetTypeSnapshot,
                    targetId,
                    "invalid_refresh",
                    "A valid profile hash and month are required.")
                .ConfigureAwait(false);
        }

        var result = await refreshService.RefreshAsync(
                command.ProfileHash,
                command.Month,
                Audit(actor, action, TargetTypeSnapshot, targetId),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? Result.Succeed<PrayerRefreshReceipt, PrayerAdministrationError>(result.Success)
            : Result.Fail<PrayerRefreshReceipt, PrayerAdministrationError>(
                new(result.Error.Code, result.Error.Message));
    }

    private async Task<PrayerAdministrationError?> AuthorizeAsync(
        UserContext actor,
        string action,
        string targetType,
        string targetId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var evaluation = authorizer.Authorize(
            new AdministrativeRequestActor(
                true,
                actor.UserId,
                actor.Roles,
                true,
                actor.CorrelationId),
            AdministrativeCapability.UsersRolesIntegrationsSettings,
            CapabilityAccess.Write,
            allowLimited: false);
        if (evaluation.Allowed)
        {
            return null;
        }

        await auditFinalizer.FinalizeOnceAsync(
                Audit(actor, action, targetType, targetId),
                PrivilegedAttemptOutcome.Denied,
                new Dictionary<string, string?>
                {
                    ["result"] = "denied",
                    ["errorCode"] = evaluation.ErrorCode,
                })
            .ConfigureAwait(false);
        return new PrayerAdministrationError(
            evaluation.ErrorCode,
            evaluation.Message);
    }

    private async Task<Result<T, PrayerAdministrationError>> ValidationFailureAsync<T>(
        UserContext actor,
        string action,
        string targetType,
        string targetId,
        string errorCode,
        string message)
    {
        await auditFinalizer.FinalizeOnceAsync(
                Audit(actor, action, targetType, targetId),
                PrivilegedAttemptOutcome.Allowed,
                new Dictionary<string, string?>
                {
                    ["result"] = "validation_failed",
                    ["errorCode"] = errorCode,
                })
            .ConfigureAwait(false);
        return Result.Fail<T, PrayerAdministrationError>(new(errorCode, message));
    }

    private static IdentityAuditDescriptor Audit(
        UserContext actor,
        string action,
        string targetType,
        string targetId) =>
        new(
            actor.UserId,
            actor.Roles,
            action,
            targetType,
            BoundedTarget(targetId),
            actor.CorrelationId);

    private static string BoundedTarget(string targetId)
    {
        var normalized = targetId.Trim();
        return normalized.Length <= 256
            ? normalized
            : "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static bool IsProfileHash(string value) =>
        value is { Length: PrayerLimits.ProfileHashLength } &&
        value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterLower(character));
}

public sealed class PrayerRefreshService(
    IPrayerSource source,
    IPrayerRefreshStore store) : IPrayerRefreshService
{
    private readonly IPrayerSource source =
        source ?? throw new ArgumentNullException(nameof(source));
    private readonly IPrayerRefreshStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshAsync(
        string profileHash,
        YearMonth month,
        IdentityAuditDescriptor? audit,
        CancellationToken cancellationToken)
    {
        Result<PrayerSourceSnapshot, IntegrationError> sourceResult;
        try
        {
            sourceResult = await source.GetAsync(
                    new PrayerSourceRequest(month, PrayerTimeZone.IanaId, profileHash),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            sourceResult = Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new(
                    "unavailable",
                    "The prayer provider is unavailable."));
        }

        if (sourceResult.IsFailure)
        {
            var error = Map(sourceResult.Error);
            return await store.RecordRefreshFailureAsync(
                    profileHash,
                    month,
                    error,
                    audit,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PrayerSnapshotBatch batch;
        try
        {
            batch = ToBatch(profileHash, month, sourceResult.Success);
        }
        catch (ArgumentException)
        {
            var error = new PrayerRefreshError(
                "malformed",
                "The prayer provider returned an invalid schedule.");
            return await store.RecordRefreshFailureAsync(
                    profileHash,
                    month,
                    error,
                    audit,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await store.ReplaceMonthAsync(
                batch,
                sourceResult.Success.Source,
                audit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static PrayerSnapshotBatch ToBatch(
        string profileHash,
        YearMonth month,
        PrayerSourceSnapshot sourceSnapshot)
    {
        if (sourceSnapshot.Schedule.Month != month ||
            !string.Equals(
                sourceSnapshot.Schedule.TimeZoneId,
                PrayerTimeZone.IanaId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException("The prayer provider schedule does not match the request.");
        }

        var snapshots = new List<PrayerSnapshot>(sourceSnapshot.Schedule.Days.Count);
        foreach (var day in sourceSnapshot.Schedule.Days)
        {
            var times = new Dictionary<string, TimeOnly>(StringComparer.Ordinal);
            foreach (var time in day.Times)
            {
                if (!times.TryAdd(PrayerKeys.Normalize(time.Name), time.LocalTime))
                {
                    throw new ArgumentException("The prayer provider returned duplicate values.");
                }
            }

            snapshots.Add(PrayerSnapshot.Create(
                profileHash,
                day.Date,
                CanonicalPrayerValues.Create(times),
                sourceSnapshot.Schedule.GeneratedAtUtc,
                sourceSnapshot.Source,
                true));
        }

        return PrayerSnapshotBatch.Create(
            profileHash,
            month.Year,
            month.Month,
            snapshots);
    }

    private static PrayerRefreshError Map(IntegrationError error) =>
        error.Code switch
        {
            "timeout" => new("timeout", "The prayer provider timed out."),
            "not_configured" => new("not_configured", "The prayer provider is not configured."),
            "malformed" => new("malformed", "The prayer provider returned an invalid schedule."),
            _ => new("unavailable", "The prayer provider is unavailable."),
        };
}
