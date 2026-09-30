using System.Data.Common;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Social;
using Husaynia.IntegrationTests.Persistence.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Husaynia.IntegrationTests.Social;

public sealed class SocialSnapshotPersistenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReplaceIsVersionedAtomicAndRetentionBounded()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ReplaceIsVersionedAtomicAndRetentionBounded));

        for (var version = 1; version <= 4; version++)
        {
            await using var context = database.CreateContext();
            var store = new EfSocialSnapshotStore(context);
            var storedVersion = await store.ReplaceAsync(
                Feed($"v{version}", 3),
                retainedVersions: 2,
                CancellationToken.None);
            Assert.Equal(version, storedVersion);
        }

        await using var verification = database.CreateContext();
        var snapshots = await verification.Set<PersistedSocialFeedSnapshot>()
            .AsNoTracking()
            .OrderBy(snapshot => snapshot.Version)
            .ToArrayAsync(CancellationToken.None);
        var items = await verification.Set<PersistedSocialFeedItem>()
            .AsNoTracking()
            .ToArrayAsync(CancellationToken.None);
        var active = await new EfSocialSnapshotStore(verification).ReadAsync(
            "facebook",
            CancellationToken.None);

        Assert.Equal([3L, 4L], snapshots.Select(snapshot => snapshot.Version));
        Assert.Equal(6, items.Length);
        Assert.Equal(4, active!.Version);
        Assert.All(active.Items, item => Assert.StartsWith("v4-", item.ExternalId, StringComparison.Ordinal));
        Assert.All(active.Items, item =>
        {
            Assert.Equal(SocialMediaType.Image, item.MediaType);
            Assert.Equal(800, item.Width);
            Assert.NotNull(item.MediaUrl);
            Assert.NotNull(item.ThumbnailUrl);
        });
    }

    [Fact]
    public async Task ConcurrentRefreshesPublishOnlyCompleteSnapshots()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(ConcurrentRefreshesPublishOnlyCompleteSnapshots));
        await using (var seedContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(seedContext).ReplaceAsync(
                Feed("seed", 20),
                retainedVersions: 5,
                CancellationToken.None);
        }

        var first = ReplaceAsync(database, Feed("alpha", 20));
        var second = ReplaceAsync(database, Feed("beta", 20));
        var observed = new List<string[]>();
        while (!first.IsCompleted || !second.IsCompleted)
        {
            await using var readContext = database.CreateContext();
            var current = await new EfSocialSnapshotStore(readContext).ReadAsync(
                "facebook",
                CancellationToken.None);
            observed.Add(current!.Items.Select(item => item.ExternalId).ToArray());
            await Task.Delay(5, CancellationToken.None);
        }

        await Task.WhenAll(first, second);
        await using var finalContext = database.CreateContext();
        var final = await new EfSocialSnapshotStore(finalContext).ReadAsync(
            "facebook",
            CancellationToken.None);
        observed.Add(final!.Items.Select(item => item.ExternalId).ToArray());

        Assert.All(observed, itemIds =>
        {
            Assert.Equal(20, itemIds.Length);
            var prefix = itemIds[0].Split('-')[0];
            Assert.All(itemIds, itemId => Assert.StartsWith($"{prefix}-", itemId, StringComparison.Ordinal));
        });
        Assert.Equal(3, final.Version);
    }

    [Fact]
    public async Task SchedulingStateReadIsAtomicAcrossStateAndSnapshot()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(SchedulingStateReadIsAtomicAcrossStateAndSnapshot));
        await using (var seedContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(seedContext).ReplaceAsync(
                Feed("seed", 1),
                retainedVersions: 3,
                CancellationToken.None);
        }

        var pause = new PauseAfterIntegrationStateReadInterceptor();
        await using var readContext = database.CreateContext(pause);
        var readTask = new EfSocialSnapshotStore(readContext)
            .ReadSchedulingStateAsync("facebook", CancellationToken.None);
        await pause.StateRead.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var writerProbe = new AppLockAttemptInterceptor();
        await using var writeContext = database.CreateContext(writerProbe);
        var writeTask = new EfSocialSnapshotStore(writeContext).ReplaceAsync(
            Feed("next", 1, Now.AddMinutes(1)),
            retainedVersions: 3,
            CancellationToken.None);
        try
        {
            await writerProbe.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50, CancellationToken.None);
            Assert.False(writeTask.IsCompleted);
        }
        finally
        {
            pause.Continue.SetResult();
        }

        var observed = await readTask;
        await writeTask;

        Assert.Equal(1, observed.ActiveSnapshotVersion);
        Assert.Equal(Now.AddHours(1), observed.ActiveSnapshotExpiresAtUtc);
        Assert.Equal(Now, observed.RefreshState!.LastSuccessAtUtc);
        Assert.False(pause.LoadedItems);
    }

    [Fact]
    public async Task FailureKeepsLastSuccessAndRecoveryClearsStaleState()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(FailureKeepsLastSuccessAndRecoveryClearsStaleState));
        await using (var initialContext = database.CreateContext())
        {
            var store = new EfSocialSnapshotStore(initialContext);
            await store.ReplaceAsync(
                Feed("healthy", 1),
                retainedVersions: 3,
                CancellationToken.None);
        }

        await using (var failureContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(failureContext).RecordFailureAsync(
                "facebook",
                SocialRefreshError.RateLimited,
                Now.AddMinutes(10),
                Now.AddMinutes(25),
                CancellationToken.None);
        }

        await using (var staleContext = database.CreateContext())
        {
            var stale = await new EfSocialSnapshotStore(staleContext).ReadAsync(
                "facebook",
                CancellationToken.None);
            Assert.Equal("healthy-00", Assert.Single(stale!.Items).ExternalId);
            Assert.Equal(Now.AddMinutes(10), stale.LastFailureAtUtc);
        }

        await using (var recoveryContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(recoveryContext).ReplaceAsync(
                Feed("recovered", 1, Now.AddMinutes(20)),
                retainedVersions: 3,
                CancellationToken.None);
        }

        await using var verification = database.CreateContext();
        var recovered = await new EfSocialSnapshotStore(verification).ReadAsync(
            "facebook",
            CancellationToken.None);
        Assert.Equal(2, recovered!.Version);
        Assert.Equal("recovered-00", Assert.Single(recovered.Items).ExternalId);
        Assert.Null(recovered.LastFailureAtUtc);
    }

    [Fact]
    public async Task DelayedFailureCannotOverwriteSuccessAtTheSameAttemptTimestamp()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DelayedFailureCannotOverwriteSuccessAtTheSameAttemptTimestamp));
        await using (var successContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(successContext).ReplaceAsync(
                Feed("newer", 1, Now),
                retainedVersions: 3,
                CancellationToken.None);
        }

        await using (var delayedFailureContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(delayedFailureContext).RecordFailureAsync(
                "facebook",
                SocialRefreshError.RateLimited,
                Now,
                Now.AddMinutes(30),
                CancellationToken.None);
        }

        await using var verification = database.CreateContext();
        var state = await new EfSocialSnapshotStore(verification).ReadRefreshStateAsync(
            "facebook",
            CancellationToken.None);
        Assert.Equal(SocialRefreshError.None, state!.LastError);
        Assert.Null(state.RetryAfterUtc);
        Assert.Equal(Now, state.LastSuccessAtUtc);
    }

    [Fact]
    public async Task DeferredRecoveryPersistsWithoutDoubleCountingFailure()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DeferredRecoveryPersistsWithoutDoubleCountingFailure));
        await using (var failureContext = database.CreateContext())
        {
            var store = new EfSocialSnapshotStore(failureContext);
            await store.RecordFailureAsync(
                "facebook",
                SocialRefreshError.Unavailable,
                Now,
                null,
                CancellationToken.None);
            await store.DeferUntilAsync(
                "facebook",
                SocialRefreshError.Unavailable,
                Now,
                Now.AddMinutes(15),
                CancellationToken.None);
        }

        await using var verification = database.CreateContext();
        var state = await new EfSocialSnapshotStore(verification)
            .ReadRefreshStateAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshError.Unavailable, state!.LastError);
        Assert.Equal(Now, state.LastAttemptAtUtc);
        Assert.Equal(Now.AddMinutes(15), state.RetryAfterUtc);
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task NonterminalFailureRetainsPastRecoveryAnchor()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(NonterminalFailureRetainsPastRecoveryAnchor));
        var recoveryAnchor = Now.AddMinutes(-5);
        await using (var context = database.CreateContext())
        {
            var store = new EfSocialSnapshotStore(context);
            await store.RecordFailureAsync(
                "facebook",
                SocialRefreshError.RateLimited,
                Now.AddMinutes(-20),
                recoveryAnchor,
                CancellationToken.None);
            await store.RecordFailureAsync(
                "facebook",
                SocialRefreshError.Unavailable,
                Now,
                null,
                CancellationToken.None);
        }

        await using var verification = database.CreateContext();
        var state = await new EfSocialSnapshotStore(verification)
            .ReadSchedulingStateAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshError.Unavailable, state.RefreshState!.LastError);
        Assert.Equal(Now, state.RefreshState.LastAttemptAtUtc);
        Assert.Equal(recoveryAnchor, state.RefreshState.RetryAfterUtc);
        Assert.Equal(2, state.RefreshState.ConsecutiveFailures);
    }

    [Fact]
    public async Task FinalAttemptFailureAtomicallyReplacesRecoveryAnchor()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(FinalAttemptFailureAtomicallyReplacesRecoveryAnchor));
        var recoveryAfter = Now.AddMinutes(15);
        await using (var context = database.CreateContext())
        {
            var store = new EfSocialSnapshotStore(context);
            await store.RecordFailureAsync(
                "facebook",
                SocialRefreshError.RateLimited,
                Now.AddMinutes(-20),
                Now.AddMinutes(-5),
                CancellationToken.None);
            await store.RecordFailureAsync(
                "facebook",
                SocialRefreshError.Unavailable,
                Now,
                recoveryAfter,
                CancellationToken.None);
        }

        await using var verification = database.CreateContext();
        var state = await new EfSocialSnapshotStore(verification)
            .ReadSchedulingStateAsync("facebook", CancellationToken.None);

        Assert.Equal(SocialRefreshError.Unavailable, state.RefreshState!.LastError);
        Assert.Equal(Now, state.RefreshState.LastFailureAtUtc);
        Assert.Equal(recoveryAfter, state.RefreshState.RetryAfterUtc);
        Assert.Equal(2, state.RefreshState.ConsecutiveFailures);
    }

    [Fact]
    public async Task DelayedOlderSuccessCannotReplaceNewerActiveSnapshot()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(
            nameof(DelayedOlderSuccessCannotReplaceNewerActiveSnapshot));
        await using (var newerContext = database.CreateContext())
        {
            await new EfSocialSnapshotStore(newerContext).ReplaceAsync(
                Feed("newer", 1, Now.AddMinutes(5)),
                retainedVersions: 3,
                CancellationToken.None);
        }

        await using (var olderContext = database.CreateContext())
        {
            var returnedVersion = await new EfSocialSnapshotStore(olderContext)
                .ReplaceAsync(
                    Feed("older", 1, Now),
                    retainedVersions: 3,
                    CancellationToken.None);
            Assert.Equal(1, returnedVersion);
        }

        await using var verification = database.CreateContext();
        var active = await new EfSocialSnapshotStore(verification).ReadAsync(
            "facebook",
            CancellationToken.None);
        var snapshots = await verification.Set<PersistedSocialFeedSnapshot>()
            .AsNoTracking()
            .ToArrayAsync(CancellationToken.None);

        Assert.Equal(1, active!.Version);
        Assert.Equal(Now.AddMinutes(5), active.LastSuccessAtUtc);
        Assert.Equal("newer-00", Assert.Single(active.Items).ExternalId);
        Assert.Single(snapshots);
    }

    private static async Task ReplaceAsync(
        SqlServerTestDatabase database,
        NormalizedSocialFeed feed)
    {
        await using var context = database.CreateContext();
        await new EfSocialSnapshotStore(context).ReplaceAsync(
            feed,
            retainedVersions: 5,
            CancellationToken.None);
    }

    private static NormalizedSocialFeed Feed(
        string prefix,
        int itemCount,
        DateTimeOffset? fetchedAtUtc = null)
    {
        var fetched = fetchedAtUtc ?? Now;
        return new NormalizedSocialFeed(
            "facebook",
            fetched,
            fetched.AddHours(1),
            fetched,
            Enumerable.Range(0, itemCount)
                .Select(index => new StoredSocialFeedItem(
                    $"{prefix}-{index:00}",
                    $"{prefix} item {index}",
                    new Uri($"https://facebook.example/posts/{prefix}-{index}"),
                    SocialMediaType.Image,
                    new Uri($"https://media.example/{prefix}-{index}.jpg"),
                    new Uri($"https://media.example/{prefix}-{index}-thumb.jpg"),
                    800,
                    600,
                    null,
                    $"{prefix} alt {index}",
                    $"{prefix} caption {index}",
                    fetched.AddMinutes(-index)))
                .ToArray());
    }

    private sealed class PauseAfterIntegrationStateReadInterceptor
        : DbCommandInterceptor
    {
        private int paused;

        internal TaskCompletionSource StateRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool LoadedItems { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            LoadedItems |= command.CommandText.Contains(
                "SocialFeedSnapshotItems",
                StringComparison.Ordinal);
            if (command.CommandText.Contains(
                    "SocialIntegrationStates",
                    StringComparison.Ordinal) &&
                Interlocked.Exchange(ref paused, 1) == 0)
            {
                StateRead.SetResult();
                await Continue.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class AppLockAttemptInterceptor : DbCommandInterceptor
    {
        internal TaskCompletionSource Attempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(
                    "sp_getapplock",
                    StringComparison.Ordinal))
            {
                Attempted.SetResult();
            }

            return base.NonQueryExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }
}
