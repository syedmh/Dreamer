using Husaynia.Application.Contracts;
using Husaynia.Application.Identity;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Identity;
using Husaynia.Domain.Prayer;
using Husaynia.Infrastructure.Identity;
using Husaynia.Infrastructure.Persistence.Core;
using Husaynia.Infrastructure.Prayer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Globalization;
using System.Data.Common;

namespace Husaynia.IntegrationTests.Prayer;

public sealed class PrayerStoreTests
{
    [Fact]
    public async Task RefreshPersistsCompleteProfileIsolatedMonthAndOverrideAppliesLast()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(RefreshPersistsCompleteProfileIsolatedMonthAndOverrideAppliesLast));
        var time = new FixedTimeProvider(
            DateTimeOffset.Parse("2026-02-20T12:00:00Z", CultureInfo.InvariantCulture));
        await using var resources = database.CreateStore(time);
        var store = resources.Store;
        var profile = SyntheticProfile();

        var created = await store.CreateProfileAsync(
            profile,
            Audit("prayer.profile.create", profile.ProfileHash),
            CancellationToken.None);
        var activated = await store.ActivateProfileAsync(
            new(profile.Id, new RowVersion(ReadOnlyMemory<byte>.Empty)),
            Audit("prayer.profile.activate", profile.ProfileHash),
            CancellationToken.None);
        var external = new NeverExternalClient();
        var source = new ConfiguredPrayerSource(
            store,
            new DeterministicPrayerCalculator(),
            external,
            time);
        var refreshed = await new PrayerRefreshService(source, store).RefreshAsync(
            profile.ProfileHash,
            new YearMonth(2026, 3),
            Audit("prayer.snapshot.refresh", $"{profile.ProfileHash}:2026-03"),
            CancellationToken.None);
        var savedOverride = await store.SaveOverrideAsync(
            new(
                profile.ProfileHash,
                new DateOnly(2026, 3, 8),
                PrayerKeys.Fajr,
                new TimeOnly(4, 44),
                "synthetic fixture"),
            Audit("prayer.override.save", "override"),
            CancellationToken.None);

        Assert.True(created.IsSuccess);
        Assert.True(activated.IsSuccess);
        Assert.Equal(
            PrayerRefreshSchedulingStatuses.Pending,
            activated.Success.RefreshSchedulingStatus);
        Assert.True(refreshed.IsSuccess);
        Assert.Equal(31, refreshed.Success.SnapshotCount);
        Assert.True(savedOverride.IsSuccess);

        var schedule = await new PrayerScheduleService(
                store,
                new PrayerOptions { SnapshotMaxAge = TimeSpan.FromHours(36) },
                time)
            .GetMonthAsync(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                CancellationToken.None);

        Assert.True(schedule.IsSuccess);
        Assert.Equal(31, schedule.Success.Days.Count);
        var overriddenDay = schedule.Success.Days.Single(day => day.Date.Day == 8);
        Assert.True(overriddenDay.IsOverride);
        Assert.Equal(
            new TimeOnly(4, 44),
            overriddenDay.Times.Single(prayer => prayer.Name == PrayerKeys.Fajr).LocalTime);
        var deactivated = await store.DeactivateOverrideAsync(
            new(savedOverride.Success.Id, savedOverride.Success.RowVersion),
            Audit("prayer.override.deactivate", savedOverride.Success.Id.ToString("N")),
            CancellationToken.None);
        Assert.True(deactivated.IsSuccess);
        var withoutOverride = await new PrayerScheduleService(
                store,
                new PrayerOptions { SnapshotMaxAge = TimeSpan.FromHours(36) },
                time)
            .GetMonthAsync(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                CancellationToken.None);
        var restoredDay = withoutOverride.Success.Days.Single(day => day.Date.Day == 8);
        Assert.False(restoredDay.IsOverride);
        Assert.NotEqual(
            new TimeOnly(4, 44),
            restoredDay.Times.Single(prayer => prayer.Name == PrayerKeys.Fajr).LocalTime);
        await store.RecordRefreshFailureAsync(
            profile.ProfileHash,
            new YearMonth(2026, 3),
            new("timeout", "The prayer provider timed out."),
            audit: null,
            CancellationToken.None);
        var cached = await new PrayerScheduleService(
                store,
                new PrayerOptions { SnapshotMaxAge = TimeSpan.FromHours(36) },
                new FixedTimeProvider(
                    DateTimeOffset.Parse(
                        "2026-02-23T12:00:00Z",
                        CultureInfo.InvariantCulture)))
            .GetMonthAsync(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                CancellationToken.None);
        Assert.True(cached.IsSuccess);
        Assert.True(cached.Success.IsStale);

        await using var verification = database.CreateContext();
        Assert.Equal(
            31,
            await verification.Set<PrayerSnapshot>()
                .CountAsync(snapshot => snapshot.ProfileHash == profile.ProfileHash));
        Assert.Equal(
            5,
            await verification.Set<AuditEvent>()
                .CountAsync(audit => audit.Action.StartsWith("prayer.")));
        Assert.Equal(0, external.Calls);
        var integrationState = await verification.Set<PrayerIntegrationState>()
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(integrationState.ActiveProfileHash, integrationState.RefreshIntentProfileHash);
        Assert.NotNull(integrationState.RefreshIntentCreatedAtUtc);
    }

    [Fact]
    public async Task ActiveProfileChangeNeverMixesPriorProfileSnapshots()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(ActiveProfileChangeNeverMixesPriorProfileSnapshots));
        var time = new FixedTimeProvider(
            DateTimeOffset.Parse("2026-02-20T12:00:00Z", CultureInfo.InvariantCulture));
        await using var resources = database.CreateStore(time);
        var first = SyntheticProfile();
        await resources.Store.CreateProfileAsync(
            first,
            Audit("prayer.profile.create", first.ProfileHash),
            CancellationToken.None);
        var firstActivation = await resources.Store.ActivateProfileAsync(
            new(first.Id, new RowVersion(ReadOnlyMemory<byte>.Empty)),
            Audit("prayer.profile.activate", first.ProfileHash),
            CancellationToken.None);
        Assert.True(firstActivation.IsSuccess);
        var source = new ConfiguredPrayerSource(
            resources.Store,
            new DeterministicPrayerCalculator(),
            new NeverExternalClient(),
            time);
        await new PrayerRefreshService(source, resources.Store).RefreshAsync(
            first.ProfileHash,
            new YearMonth(2026, 3),
            audit: null,
            CancellationToken.None);
        var second = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":17,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
            DeterministicPrayerCalculator.AlgorithmVersion,
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");
        await resources.Store.CreateProfileAsync(
            second,
            Audit("prayer.profile.create", second.ProfileHash),
            CancellationToken.None);
        await using var stateContext = database.CreateContext();
        var currentStateRowVersion = await stateContext.Set<PrayerIntegrationState>()
            .AsNoTracking()
            .Where(state => state.Id == PrayerIntegrationState.PrimaryId)
            .Select(state => EF.Property<byte[]>(
                state,
                PersistencePropertyNames.RowVersion))
            .SingleAsync();
        var secondActivation = await resources.Store.ActivateProfileAsync(
            new(second.Id, new RowVersion(currentStateRowVersion)),
            Audit("prayer.profile.activate", second.ProfileHash),
            CancellationToken.None);
        Assert.True(secondActivation.IsSuccess);

        var result = await new PrayerScheduleService(
                resources.Store,
                new PrayerOptions(),
                time)
            .GetMonthAsync(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                CancellationToken.None);

        Assert.Equal("unavailable", result.Error.Code);
    }

    [Fact]
    public async Task ActivationRejectsStaleRowversionAndAuditsExactlyOnce()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(ActivationRejectsStaleRowversionAndAuditsExactlyOnce));
        await using var resources = database.CreateStore(TimeProvider.System);
        var profile = SyntheticProfile();
        await resources.Store.CreateProfileAsync(
            profile,
            Audit("prayer.profile.create", profile.ProfileHash),
            CancellationToken.None);
        await resources.Store.ActivateProfileAsync(
            new(profile.Id, new RowVersion(ReadOnlyMemory<byte>.Empty)),
            Audit("prayer.profile.activate", profile.ProfileHash),
            CancellationToken.None);

        var conflict = await resources.Store.ActivateProfileAsync(
            new(profile.Id, new RowVersion(ReadOnlyMemory<byte>.Empty)),
            Audit("prayer.profile.activate", profile.ProfileHash),
            CancellationToken.None);

        Assert.Equal("concurrency_conflict", conflict.Error.Code);
        await using var verification = database.CreateContext();
        Assert.Equal(
            2,
            await verification.Set<AuditEvent>()
                .CountAsync(audit => audit.Action == "prayer.profile.activate"));
    }

    [Fact]
    public async Task OverrideForInactiveProfileIsRejectedWithoutMutationAndAudited()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(OverrideForInactiveProfileIsRejectedWithoutMutationAndAudited));
        await using var resources = database.CreateStore(TimeProvider.System);
        var active = SyntheticProfile();
        var inactive = PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":17,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
            DeterministicPrayerCalculator.AlgorithmVersion,
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator");
        await resources.Store.CreateProfileAsync(
            active,
            Audit("prayer.profile.create", active.ProfileHash),
            CancellationToken.None);
        await resources.Store.CreateProfileAsync(
            inactive,
            Audit("prayer.profile.create", inactive.ProfileHash),
            CancellationToken.None);
        await resources.Store.ActivateProfileAsync(
            new(active.Id, new RowVersion(ReadOnlyMemory<byte>.Empty)),
            Audit("prayer.profile.activate", active.ProfileHash),
            CancellationToken.None);

        var result = await resources.Store.SaveOverrideAsync(
            new(
                inactive.ProfileHash,
                new DateOnly(2026, 3, 8),
                PrayerKeys.Fajr,
                new TimeOnly(4, 44),
                "synthetic fixture"),
            Audit("prayer.override.save", inactive.ProfileHash),
            CancellationToken.None);

        Assert.Equal("profile_not_active", result.Error.Code);
        await using var verification = database.CreateContext();
        Assert.False(await verification.Set<PrayerOverride>().AnyAsync());
        Assert.Equal(
            1,
            await verification.Set<AuditEvent>()
                .CountAsync(audit => audit.Action == "prayer.override.save"));
    }

    [Fact]
    public async Task AuditConstructionFailureCannotCommitProfileMutation()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(AuditConstructionFailureCannotCommitProfileMutation));
        await using var resources = database.CreateStore(TimeProvider.System);
        var profile = SyntheticProfile();
        var invalidAudit = new IdentityAuditDescriptor(
            "administrator",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            "prayer.profile.create",
            "PrayerProfile",
            profile.ProfileHash,
            new string('x', 129));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            resources.Store.CreateProfileAsync(
                profile,
                invalidAudit,
                CancellationToken.None));

        await using var verification = database.CreateContext();
        Assert.False(await verification.Set<PrayerProfile>().AnyAsync());
    }

    [Fact]
    public async Task CreateProfileDoesNotQueryAfterCommittedAuditAndMutation()
    {
        await using var database = await PrayerSqlServerTestDatabase.CreateAsync(
            nameof(CreateProfileDoesNotQueryAfterCommittedAuditAndMutation));
        var interceptor = new RejectProjectionQueryAfterProfileInsertInterceptor();
        await using var resources = database.CreateStore(
            TimeProvider.System,
            interceptor);
        var profile = SyntheticProfile();

        var result = await resources.Store.CreateProfileAsync(
            profile,
            Audit("prayer.profile.create", profile.ProfileHash),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using var verification = database.CreateContext();
        Assert.True(await verification.Set<PrayerProfile>().AnyAsync());
        Assert.Equal(
            1,
            await verification.Set<AuditEvent>()
                .CountAsync(audit => audit.Action == "prayer.profile.create"));
    }

    private static PrayerProfile SyntheticProfile() =>
        PrayerProfile.Create(
            "local",
            47.9129m,
            -122.0982m,
            """{"fajrAngle":18,"ishaAngle":18,"asrShadowFactor":1,"offsets":{}}""",
            DeterministicPrayerCalculator.AlgorithmVersion,
            PrayerTimeZone.IanaId,
            new DateOnly(2026, 1, 1),
            "administrator",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));

    private static IdentityAuditDescriptor Audit(string action, string targetId) =>
        new(
            "administrator",
            new HashSet<string>([RoleNames.SiteAdministrator], StringComparer.Ordinal),
            action,
            action.Contains("override", StringComparison.Ordinal)
                ? "PrayerOverride"
                : action.Contains("snapshot", StringComparison.Ordinal)
                    ? "PrayerSnapshot"
                    : "PrayerProfile",
            targetId,
            Guid.NewGuid().ToString("N"));

    private sealed class NeverExternalClient : IPrayerExternalClient
    {
        public int Calls { get; private set; }

        public Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
            PrayerSourceRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("The external provider must not be called.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RejectProjectionQueryAfterProfileInsertInterceptor
        : DbCommandInterceptor
    {
        private bool profileInserted;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "INSERT INTO [PrayerProfiles]",
                    StringComparison.Ordinal))
            {
                profileInserted = true;
            }

            return await base.ReaderExecutedAsync(
                    command,
                    eventData,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (profileInserted &&
                command.CommandText.Contains(
                    "PrayerIntegrationStates",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Synthetic post-commit projection failure.");
            }

            return base.ReaderExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }
}

internal sealed class PrayerSqlServerTestDatabase : IAsyncDisposable
{
    private static int sequence;
    private readonly string databaseName;
    private bool disposed;

    private PrayerSqlServerTestDatabase(string databaseName, string connectionString)
    {
        this.databaseName = databaseName;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static async Task<PrayerSqlServerTestDatabase> CreateAsync(string testName)
    {
        var databaseName = DatabaseName.Create(
            "HusayniaT07",
            $"{testName}_{Environment.ProcessId}_{Interlocked.Increment(ref sequence)}");
        var connectionString = new SqlConnectionStringBuilder
        {
            DataSource = @"(localdb)\MSSQLLocalDB",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = false,
            TrustServerCertificate = true,
            ConnectTimeout = 30,
        }.ConnectionString;
        var database = new PrayerSqlServerTestDatabase(databaseName, connectionString);
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    internal HusayniaDbContext CreateContext(params IInterceptor[] interceptors)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var options = new DbContextOptionsBuilder<HusayniaDbContext>()
            .UseSqlServer(ConnectionString);
        if (interceptors.Length > 0)
        {
            options.AddInterceptors(interceptors);
        }

        return new HusayniaDbContext(options.Options);
    }

    internal PrayerStoreResources CreateStore(
        TimeProvider timeProvider,
        params IInterceptor[] interceptors)
    {
        var context = CreateContext(interceptors);
        var identityContext = new HusayniaIdentityDbContext(
            new DbContextOptionsBuilder<HusayniaIdentityDbContext>()
                .UseSqlServer(ConnectionString)
                .Options);
        var redactor = new HostileSensitiveDataRedactor();
        var auditWriter = new EfAuditWriter(identityContext, redactor, timeProvider);
        var appender = new PrayerTransactionalAuditAppender(context, redactor, timeProvider);
        return new PrayerStoreResources(
            new EfPrayerStore(context, appender, auditWriter, timeProvider),
            context,
            identityContext);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        var options = new DbContextOptionsBuilder<HusayniaDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
        await using var context = new HusayniaDbContext(options);
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (!databaseName.StartsWith("HusayniaT07_", StringComparison.Ordinal) ||
            !string.Equals(builder.InitialCatalog, databaseName, StringComparison.Ordinal) ||
            !string.Equals(
                builder.DataSource,
                @"(localdb)\MSSQLLocalDB",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to delete a database that is not a disposable T07 LocalDB database.");
        }

        await context.Database.EnsureDeletedAsync();
        SqlConnection.ClearAllPools();
    }
}

internal sealed class PrayerStoreResources(
    EfPrayerStore store,
    HusayniaDbContext context,
    HusayniaIdentityDbContext identityContext) : IAsyncDisposable
{
    internal EfPrayerStore Store { get; } = store;

    public async ValueTask DisposeAsync()
    {
        await context.DisposeAsync();
        await identityContext.DisposeAsync();
    }
}
