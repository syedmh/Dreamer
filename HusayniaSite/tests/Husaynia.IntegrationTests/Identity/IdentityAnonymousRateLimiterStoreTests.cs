using System.Collections.Concurrent;
using Husaynia.Application.Identity;
using Husaynia.Infrastructure.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Husaynia.IntegrationTests.Identity;

public sealed class IdentityAnonymousRateLimiterStoreTests
{
    private static readonly byte[] FingerprintA =
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
    private static readonly byte[] FingerprintB =
        Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task TwoProvidersAtomicallyEnforceNAndNPlusOneAcrossAnAbsentRow()
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(TwoProvidersAtomicallyEnforceNAndNPlusOneAcrossAnAbsentRow));
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        await using var providerA = CreateProvider(database.ConnectionString, 8, timeProvider);
        await using var providerB = CreateProvider(database.ConnectionString, 8, timeProvider);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempts = Enumerable.Range(0, 9)
            .Select(index => AttemptAfterSignalAsync(
                index % 2 == 0 ? providerA : providerB,
                "login",
                FingerprintA,
                start.Task))
            .ToArray();
        start.SetResult();
        var decisions = await Task.WhenAll(attempts);

        Assert.Equal(8, decisions.Count(decision => decision.IsAllowed));
        Assert.Single(decisions, decision => !decision.IsAllowed);
        Assert.All(decisions, decision => Assert.Equal(8, decision.PermitLimit));
        await using var context = database.CreateContext();
        var row = await context.Set<IdentityAnonymousRateLimit>().AsNoTracking().SingleAsync();
        Assert.Equal("login", row.EndpointFamily);
        Assert.Equal(FingerprintA, row.ClientFingerprint);
        Assert.Equal(8, row.RequestCount);
        Assert.Equal(now, row.WindowStartedAtUtc);
    }

    [Fact]
    public async Task EndpointAndFingerprintPartitionsAreIndependent()
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(EndpointAndFingerprintPartitionsAreIndependent));
        var timeProvider = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        await using var provider = CreateProvider(database.ConnectionString, 1, timeProvider);

        Assert.True((await AttemptAsync(provider, "login", FingerprintA)).IsAllowed);
        Assert.False((await AttemptAsync(provider, "login", FingerprintA)).IsAllowed);
        Assert.True((await AttemptAsync(provider, "invitation-accept", FingerprintA)).IsAllowed);
        Assert.True((await AttemptAsync(provider, "login", FingerprintB)).IsAllowed);

        await using var context = database.CreateContext();
        var rows = await context.Set<IdentityAnonymousRateLimit>()
            .AsNoTracking()
            .OrderBy(row => row.EndpointFamily)
            .ThenBy(row => row.Id)
            .ToArrayAsync();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, row => Assert.Equal(32, row.ClientFingerprint.Length));
        Assert.All(rows, row => Assert.Equal(1, row.RequestCount));
    }

    [Fact]
    public async Task ExpiredWindowResetsAndRetentionCleanupDeletesStaleRows()
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(ExpiredWindowResetsAndRetentionCleanupDeletesStaleRows));
        var initialNow = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(initialNow);
        await using var provider = CreateProvider(
            database.ConnectionString,
            1,
            timeProvider,
            window: TimeSpan.FromMinutes(5),
            retention: TimeSpan.FromMinutes(5));

        Assert.True((await AttemptAsync(provider, "login", FingerprintA)).IsAllowed);
        Assert.False((await AttemptAsync(provider, "login", FingerprintA)).IsAllowed);

        timeProvider.SetUtcNow(initialNow.AddMinutes(5));
        var reset = await AttemptAsync(provider, "login", FingerprintA);
        Assert.True(reset.IsAllowed);
        Assert.Equal(1, reset.RequestCount);

        timeProvider.SetUtcNow(initialNow.AddMinutes(16));
        Assert.True((await AttemptAsync(provider, "login", FingerprintB)).IsAllowed);

        await using var context = database.CreateContext();
        var rows = await context.Set<IdentityAnonymousRateLimit>()
            .AsNoTracking()
            .ToArrayAsync();
        Assert.Single(rows);
        Assert.Equal(FingerprintB, rows[0].ClientFingerprint);
    }

    [Fact]
    public async Task RetryableInsertFailureIsRetriedOnceWithTheFixedTimestamp()
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(RetryableInsertFailureIsRetriedOnceWithTheFixedTimestamp));
        await CreateRetryTriggerAsync(database.ConnectionString, failOnce: true);
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var logs = new CollectingLoggerProvider();
        await using var provider = CreateProvider(database.ConnectionString, 2, timeProvider, logs: logs);

        var decision = await AttemptAsync(provider, "login", FingerprintA);

        Assert.True(decision.IsAllowed);
        Assert.Single(logs.Messages, message => message.Contains("Retrying", StringComparison.Ordinal));
        await using var context = database.CreateContext();
        var row = await context.Set<IdentityAnonymousRateLimit>().AsNoTracking().SingleAsync();
        Assert.Equal(now, row.WindowStartedAtUtc);
        Assert.Equal(now.AddMinutes(5), row.WindowEndsAtUtc);
    }

    [Fact]
    public async Task SecondRetryableFailureAndMissingSchemaFailClosed()
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(SecondRetryableFailureAndMissingSchemaFailClosed));
        await CreateRetryTriggerAsync(database.ConnectionString, failOnce: false);
        var logs = new CollectingLoggerProvider();
        await using var provider = CreateProvider(
            database.ConnectionString,
            2,
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero)),
            logs: logs);

        await Assert.ThrowsAsync<IdentityAnonymousRateLimitDependencyException>(
            () => AttemptAsync(provider, "login", FingerprintA));
        Assert.Single(logs.Messages, message => message.Contains("Retrying", StringComparison.Ordinal));

        await database.DownInvariantsAsync();
        await using var missingSchemaProvider = CreateProvider(
            database.ConnectionString,
            2,
            new ManualTimeProvider(new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero)));
        await Assert.ThrowsAsync<IdentityAnonymousRateLimitDependencyException>(
            () => AttemptAsync(missingSchemaProvider, "login", FingerprintB));
    }

    [Theory]
    [InlineData("", 32)]
    [InlineData("123456789012345678901234567890123", 32)]
    [InlineData("login", 31)]
    [InlineData("login", 33)]
    public async Task InvalidPartitionsAreRejectedBeforeSql(string endpointFamily, int fingerprintLength)
    {
        await using var database =
            await IdentitySqlServerTestDatabase.CreateAsync(nameof(InvalidPartitionsAreRejectedBeforeSql));
        await using var provider = CreateProvider(
            database.ConnectionString,
            1,
            new ManualTimeProvider(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ArgumentException>(
            () => AttemptAsync(provider, endpointFamily, new byte[fingerprintLength]));
    }

    private static async Task<IdentityRateLimitDecision> AttemptAfterSignalAsync(
        ServiceProvider provider,
        string endpointFamily,
        byte[] fingerprint,
        Task start)
    {
        await start;
        return await AttemptAsync(provider, endpointFamily, fingerprint);
    }

    private static async Task<IdentityRateLimitDecision> AttemptAsync(
        ServiceProvider provider,
        string endpointFamily,
        byte[] fingerprint)
    {
        await using var scope = provider.CreateAsyncScope();
        var limiter = scope.ServiceProvider.GetRequiredService<IIdentityAnonymousRateLimiter>();
        return await limiter.AttemptAsync(endpointFamily, fingerprint, CancellationToken.None);
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        int permitLimit,
        TimeProvider timeProvider,
        TimeSpan? window = null,
        TimeSpan? retention = null,
        ILoggerProvider? logs = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:HusayniaDatabase"] = connectionString,
            ["Identity:Bootstrap:Enabled"] = "false",
            ["Identity:InvitationLifetime"] = "7.00:00:00",
            ["Identity:AnonymousRateLimit:PermitLimit"] = permitLimit.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["Identity:AnonymousRateLimit:Window"] = (window ?? TimeSpan.FromMinutes(5)).ToString("c"),
            ["Identity:AnonymousRateLimit:Retention"] = (retention ?? TimeSpan.FromDays(1)).ToString("c"),
            ["Identity:AnonymousRateLimit:FingerprintKey"] = Convert.ToBase64String(new byte[32]),
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton(timeProvider);
        services.AddLogging(builder =>
        {
            if (logs is not null)
            {
                builder.AddProvider(logs);
            }
        });
        new IdentityInfrastructureModule().AddServices(services, configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
    }

    private static async Task CreateRetryTriggerAsync(string connectionString, bool failOnce)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            CREATE TABLE [dbo].[T6RetryCollision] ([Id] int NOT NULL PRIMARY KEY);
            INSERT INTO [dbo].[T6RetryCollision] ([Id]) VALUES (1);
            EXEC(N'
            CREATE OR ALTER TRIGGER [dbo].[TR_T6_IdentityAnonymousRateLimits_Retry]
            ON [dbo].[IdentityAnonymousRateLimits]
            AFTER INSERT
            AS
            BEGIN
                SET NOCOUNT ON;
                {{(failOnce ? "IF SESSION_CONTEXT(N''T6RetryOnce'') IS NULL" : string.Empty)}}
                BEGIN
                    EXEC sys.sp_set_session_context @key=N''T6RetryOnce'', @value=1;
                    INSERT INTO [dbo].[T6RetryCollision] ([Id]) VALUES (1);
                END
            END');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        internal ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}
