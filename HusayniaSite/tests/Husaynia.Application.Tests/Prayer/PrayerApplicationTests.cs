using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Identity;
using Husaynia.Domain.Prayer;
using System.Globalization;

namespace Husaynia.Application.Tests.Prayer;

public sealed class PrayerApplicationTests
{
    [Fact]
    public async Task PublicReadUsesOnlyPersistedCompleteMonthAndLabelsStale()
    {
        var store = new StubPrayerStore
        {
            Month = SyntheticMonth(
                DateTimeOffset.Parse("2026-03-01T00:00:00Z", CultureInfo.InvariantCulture)),
        };
        var service = new PrayerScheduleService(
            store,
            new PrayerOptions { SnapshotMaxAge = TimeSpan.FromHours(36) },
            new FixedTimeProvider(
                DateTimeOffset.Parse("2026-03-04T00:00:00Z", CultureInfo.InvariantCulture)));

        var result = await service.GetMonthAsync(
            new YearMonth(2026, 3),
            PrayerTimeZone.IanaId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Success.IsStale);
        Assert.Equal(31, result.Success.Days.Count);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task PublicReadRejectsWrongZoneAndUnavailableMonth()
    {
        var store = new StubPrayerStore();
        var service = new PrayerScheduleService(
            store,
            new PrayerOptions(),
            TimeProvider.System);

        var wrongZone = await service.GetMonthAsync(
            new YearMonth(2026, 3),
            "UTC",
            CancellationToken.None);
        var missing = await service.GetMonthAsync(
            new YearMonth(2026, 3),
            PrayerTimeZone.IanaId,
            CancellationToken.None);

        Assert.Equal("invalid_time_zone", wrongZone.Error.Code);
        Assert.Equal("unavailable", missing.Error.Code);
    }

    [Fact]
    public async Task PublicReadRejectsIncompleteMonthAndUsesLatestOverride()
    {
        var month = SyntheticMonth(DateTimeOffset.UtcNow);
        var incompleteStore = new StubPrayerStore
        {
            Month = month with
            {
                Snapshots = month.Snapshots.Take(month.Snapshots.Count - 1).ToArray(),
            },
        };
        var incompleteService = new PrayerScheduleService(
            incompleteStore,
            new PrayerOptions(),
            TimeProvider.System);

        var incomplete = await incompleteService.GetMonthAsync(
            new YearMonth(2026, 3),
            PrayerTimeZone.IanaId,
            CancellationToken.None);

        Assert.Equal("unavailable", incomplete.Error.Code);

        var overriddenStore = new StubPrayerStore
        {
            Month = month with
            {
                Overrides =
                [
                    new(
                        Guid.Parse("00000000-0000-0000-0000-000000000001"),
                        new DateOnly(2026, 3, 8),
                        PrayerKeys.Fajr,
                        new TimeOnly(4, 55),
                        1),
                    new(
                        Guid.Parse("00000000-0000-0000-0000-000000000002"),
                        new DateOnly(2026, 3, 8),
                        PrayerKeys.Fajr,
                        new TimeOnly(4, 44),
                        2),
                ],
            },
        };
        var overridden = await new PrayerScheduleService(
                overriddenStore,
                new PrayerOptions(),
                TimeProvider.System)
            .GetMonthAsync(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                CancellationToken.None);

        Assert.Equal(
            new TimeOnly(4, 44),
            overridden.Success.Days.Single(day => day.Date.Day == 8)
                .Times.Single(time => time.Name == PrayerKeys.Fajr)
                .LocalTime);
    }

    [Fact]
    public async Task FuturePersistedGenerationCanNeverBypassStaleLabel()
    {
        var store = new StubPrayerStore
        {
            Month = SyntheticMonth(
                DateTimeOffset.Parse(
                    "2026-03-05T00:00:00Z",
                    CultureInfo.InvariantCulture)),
        };
        var service = new PrayerScheduleService(
            store,
            new PrayerOptions { SnapshotMaxAge = TimeSpan.FromHours(36) },
            new FixedTimeProvider(
                DateTimeOffset.Parse(
                    "2026-03-04T00:00:00Z",
                    CultureInfo.InvariantCulture)));

        var result = await service.GetMonthAsync(
            new YearMonth(2026, 3),
            PrayerTimeZone.IanaId,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Success.IsStale);
    }

    [Fact]
    public async Task AuthorizationRunsBeforeValidationAndStoreAccess()
    {
        var store = new StubPrayerStore();
        var finalizer = new RecordingFinalizer();
        var service = new PrayerAdministrationService(
            store,
            new StubRefreshService(),
            new StubCoordinator(),
            new AdministrativeCapabilityAuthorizer(),
            finalizer);
        var actor = new UserContext(
            "content-editor",
            new HashSet<string>([RoleNames.ContentEditor], StringComparer.Ordinal),
            "correlation");

        var result = await service.CreateProfileAsync(
            new CreatePrayerProfileCommand(
                "",
                500,
                500,
                "{",
                "",
                default),
            actor,
            CancellationToken.None);

        Assert.Equal("forbidden", result.Error.Code);
        Assert.Equal(0, store.CreateCount);
        Assert.Single(finalizer.Outcomes);
        Assert.Equal(PrivilegedAttemptOutcome.Denied, finalizer.Outcomes[0]);
    }

    [Fact]
    public async Task RefreshCallsProviderBeforePersistenceAndPersistsFailureIsolation()
    {
        var order = new List<string>();
        var source = new OrderedSource(order);
        var store = new StubPrayerStore { Order = order };
        var service = new PrayerRefreshService(source, store);

        var result = await service.RefreshAsync(
            "A".PadLeft(64, '0'),
            new YearMonth(2026, 3),
            audit: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["provider", "persist"], order);

        source.Error = new IntegrationError("timeout", "The prayer provider timed out.");
        order.Clear();
        var failure = await service.RefreshAsync(
            "A".PadLeft(64, '0'),
            new YearMonth(2026, 4),
            audit: null,
            CancellationToken.None);

        Assert.Equal("timeout", failure.Error.Code);
        Assert.Equal(["provider", "failure"], order);
    }

    [Fact]
    public async Task ActivationPersistsPointerBeforeAnyRefreshJobCanRun()
    {
        var order = new List<string>();
        var store = new StubPrayerStore { Order = order };
        var coordinator = new StubCoordinator { Order = order };
        var service = new PrayerAdministrationService(
            store,
            new StubRefreshService(),
            coordinator,
            new AdministrativeCapabilityAuthorizer(),
            new RecordingFinalizer());
        var actor = new UserContext(
            "administrator",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            "correlation");

        var result = await service.ActivateProfileAsync(
            new(
                Guid.Parse("00000000-0000-0000-0000-000000000123"),
                new RowVersion(ReadOnlyMemory<byte>.Empty)),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["activate", "enqueue"], order);
        Assert.Equal(
            PrayerRefreshSchedulingStatuses.Scheduled,
            result.Success.RefreshSchedulingStatus);
    }

    [Fact]
    public async Task ActivationEnqueueFailureReturnsCommittedRecoverablePendingState()
    {
        var order = new List<string>();
        var store = new StubPrayerStore { Order = order };
        var finalizer = new RecordingFinalizer();
        var coordinator = new StubCoordinator
        {
            Order = order,
            Failure = new InvalidOperationException("synthetic enqueue failure"),
        };
        var service = new PrayerAdministrationService(
            store,
            new StubRefreshService(),
            coordinator,
            new AdministrativeCapabilityAuthorizer(),
            finalizer);
        var actor = new UserContext(
            "administrator",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            "correlation");

        var result = await service.ActivateProfileAsync(
            new(
                Guid.Parse("00000000-0000-0000-0000-000000000123"),
                new RowVersion(ReadOnlyMemory<byte>.Empty)),
            actor,
            CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.True(result.IsSuccess);
        Assert.Equal(["activate", "enqueue"], order);
        Assert.Equal(
            PrayerRefreshSchedulingStatuses.Pending,
            result.Success.RefreshSchedulingStatus);
        Assert.Empty(finalizer.Outcomes);
    }

    [Fact]
    public async Task ImmediateHandlerStateMutationCannotInvalidateCommittedActivation()
    {
        var order = new List<string>();
        var store = new StubPrayerStore
        {
            Order = order,
            EnforceInitialStateVersion = true,
        };
        var coordinator = new StubCoordinator
        {
            Order = order,
            OnEnsure = () => store.StateVersion++,
        };
        var service = new PrayerAdministrationService(
            store,
            new StubRefreshService(),
            coordinator,
            new AdministrativeCapabilityAuthorizer(),
            new RecordingFinalizer());
        var actor = new UserContext(
            "administrator",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            "correlation");

        var result = await service.ActivateProfileAsync(
            new(
                Guid.Parse("00000000-0000-0000-0000-000000000123"),
                new RowVersion(ReadOnlyMemory<byte>.Empty)),
            actor,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["activate", "enqueue"], order);
        Assert.Equal(2, store.StateVersion);
    }

    private static PrayerMonthData SyntheticMonth(DateTimeOffset generatedAtUtc)
    {
        var hash = "A".PadLeft(64, '0');
        var days = Enumerable.Range(1, 31)
            .Select(day => new PrayerSnapshotData(
                new DateOnly(2026, 3, day),
                SyntheticTimes(),
                generatedAtUtc,
                "calculated",
                true))
            .ToArray();
        return new PrayerMonthData(hash, days, []);
    }

    private static Dictionary<string, TimeOnly> SyntheticTimes() =>
        PrayerKeys.All.ToDictionary(
            key => key,
            key => key switch
            {
                PrayerKeys.Fajr => new TimeOnly(5, 0),
                PrayerKeys.Sunrise => new TimeOnly(6, 30),
                PrayerKeys.Dhuhr => new TimeOnly(12, 15),
                PrayerKeys.Asr => new TimeOnly(15, 30),
                PrayerKeys.Maghrib => new TimeOnly(18, 0),
                _ => new TimeOnly(19, 30),
            },
            StringComparer.Ordinal);

    private sealed class StubPrayerStore :
        IPrayerScheduleStore,
        IPrayerAdministrationStore,
        IPrayerRefreshStore
    {
        public PrayerMonthData? Month { get; init; }
        public int ReadCount { get; private set; }
        public int CreateCount { get; private set; }
        public List<string>? Order { get; init; }
        public int StateVersion { get; set; }
        public bool EnforceInitialStateVersion { get; init; }

        public Task<PrayerMonthData?> ReadMonthAsync(
            YearMonth month,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Month);
        }

        public Task<Result<PrayerProfileView, PrayerAdministrationError>> CreateProfileAsync(
            PrayerProfile profile,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                PrayerProfileView.From(profile)));
        }

        public Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
            string profileHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Succeed<PrayerProfileDefinition, PrayerAdministrationError>(
                new(
                    profileHash,
                    "local",
                    47.9129m,
                    -122.0982m,
                    """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
                    DeterministicPrayerCalculator.AlgorithmVersion,
                    PrayerTimeZone.IanaId,
                    new DateOnly(2026, 1, 1))));

        public Task<Result<PrayerProfileDefinition, PrayerAdministrationError>> ReadProfileAsync(
            Guid profileId,
            CancellationToken cancellationToken) =>
            ReadProfileAsync("A".PadLeft(64, '0'), cancellationToken);

        public Task<Result<PrayerProfileView, PrayerAdministrationError>> ActivateProfileAsync(
            ActivatePrayerProfileCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken)
        {
            Order?.Add("activate");
            if (EnforceInitialStateVersion && StateVersion != 0)
            {
                return Task.FromResult(
                    Result.Fail<PrayerProfileView, PrayerAdministrationError>(
                        new(
                            "concurrency_conflict",
                            "The prayer activation rowversion changed.")));
            }

            StateVersion++;
            var profile = PrayerProfile.Create(
                "local",
                47.9129m,
                -122.0982m,
                """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
                DeterministicPrayerCalculator.AlgorithmVersion,
                PrayerTimeZone.IanaId,
                new DateOnly(2026, 1, 1),
                "administrator");
            return Task.FromResult(
                Result.Succeed<PrayerProfileView, PrayerAdministrationError>(
                    PrayerProfileView.From(profile)));
        }

        public Task<Result<PrayerOverrideView, PrayerAdministrationError>> SaveOverrideAsync(
            SavePrayerOverrideCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PrayerOverrideView, PrayerAdministrationError>> DeactivateOverrideAsync(
            DeactivatePrayerOverrideCommand command,
            IdentityAuditDescriptor audit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> ReplaceMonthAsync(
            PrayerSnapshotBatch batch,
            string source,
            IdentityAuditDescriptor? audit,
            CancellationToken cancellationToken)
        {
            Order?.Add("persist");
            return Task.FromResult(Result.Succeed<PrayerRefreshReceipt, PrayerRefreshError>(
                new(batch.ProfileHash, new YearMonth(batch.Year, batch.Month), batch.Snapshots.Count)));
        }

        public Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RecordRefreshFailureAsync(
            string profileHash,
            YearMonth month,
            PrayerRefreshError refreshFailure,
            IdentityAuditDescriptor? audit,
            CancellationToken cancellationToken)
        {
            Order?.Add("failure");
            return Task.FromResult(Result.Fail<PrayerRefreshReceipt, PrayerRefreshError>(refreshFailure));
        }
    }

    private sealed class OrderedSource(List<string> order) : IPrayerSource
    {
        public IntegrationError? Error { get; set; }

        public Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
            PrayerSourceRequest request,
            CancellationToken ct)
        {
            order.Add("provider");
            if (Error is not null)
            {
                return Task.FromResult(
                    Result.Fail<PrayerSourceSnapshot, IntegrationError>(Error));
            }

            var days = Enumerable.Range(
                    1,
                    DateTime.DaysInMonth(request.Month.Year, request.Month.Month))
                .Select(day => new PrayerDay(
                    new DateOnly(request.Month.Year, request.Month.Month, day),
                    SyntheticTimes().Select(pair => new PrayerTime(pair.Key, pair.Value)).ToArray(),
                    false))
                .ToArray();
            return Task.FromResult(
                Result.Succeed<PrayerSourceSnapshot, IntegrationError>(
                    new(
                        new PrayerSchedule(
                            request.Month,
                            request.TimeZoneId,
                            days,
                            DateTimeOffset.Parse(
                                "2026-02-20T00:00:00Z",
                                CultureInfo.InvariantCulture),
                            false),
                        "calculated")));
        }
    }

    private sealed class StubRefreshService : IPrayerRefreshService
    {
        public Task<Result<PrayerRefreshReceipt, PrayerRefreshError>> RefreshAsync(
            string profileHash,
            YearMonth month,
            IdentityAuditDescriptor? audit,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCoordinator : IPrayerRefreshJobCoordinator
    {
        public List<string>? Order { get; init; }

        public Exception? Failure { get; init; }

        public Action? OnEnsure { get; init; }

        public Task RegisterAndEnqueueCatchUpAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnsureProfileMonthsAsync(
            string profileHash,
            DateOnly effectiveFrom,
            CancellationToken cancellationToken)
        {
            Order?.Add("enqueue");
            OnEnsure?.Invoke();
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFinalizer : IIdentityAuditFinalizer
    {
        public List<PrivilegedAttemptOutcome> Outcomes { get; } = [];

        public Task FinalizeOnceAsync(
            IdentityAuditDescriptor descriptor,
            PrivilegedAttemptOutcome outcome,
            IReadOnlyDictionary<string, string?> details,
            Func<CancellationToken, Task>? completePersistenceAsync = null)
        {
            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
