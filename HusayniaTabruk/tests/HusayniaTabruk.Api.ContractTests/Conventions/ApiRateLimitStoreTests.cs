using HusayniaTabruk.Api.Middleware;

namespace HusayniaTabruk.Api.ContractTests.Conventions;

public sealed class ApiRateLimitStoreTests
{
    [Fact]
    public void RejectedUniqueKeysDoNotGrowRetainedBucketCount()
    {
        ApiRateLimitStore store = new();
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule accountRule = new(
            ApiRateLimitPartitions.LoginAccount,
            10,
            TimeSpan.FromMinutes(1));
        RateLimitRule ipRule = new(
            ApiRateLimitPartitions.IpAddress,
            1,
            TimeSpan.FromMinutes(1));

        Assert.Null(store.TryAcquire(
            [("account-0", accountRule), ("203.0.113.10", ipRule)],
            now));

        for (int attempt = 1; attempt <= 100; attempt++)
        {
            Assert.NotNull(store.TryAcquire(
                [($"account-{attempt}", accountRule), ("203.0.113.10", ipRule)],
                now));
        }

        Assert.Equal(2, store.RetainedBucketCount);
    }

    [Fact]
    public void ExpiredBucketsAreRemovedOpportunistically()
    {
        ApiRateLimitStore store = new(maximumRetainedBuckets: 2);
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule rule = new(ApiRateLimitPartitions.LoginAccount, 1, TimeSpan.FromSeconds(30));

        Assert.Null(store.TryAcquire([("expired-account", rule)], now));
        Assert.Null(store.TryAcquire([("current-account", rule)], now.AddSeconds(31)));

        Assert.Equal(1, store.RetainedBucketCount);
    }

    [Fact]
    public async Task HardCapHoldsUnderConcurrentAcquisition()
    {
        const int maximumBuckets = 32;
        const int attempts = 500;
        ApiRateLimitStore store = new(maximumBuckets);
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule rule = new(ApiRateLimitPartitions.LoginAccount, 1, TimeSpan.FromMinutes(1));
        using ManualResetEventSlim start = new(initialState: false);

        Task<TimeSpan?>[] acquisitions = Enumerable.Range(0, attempts)
            .Select(attempt => Task.Run(() =>
            {
                start.Wait();
                return store.TryAcquire([($"account-{attempt}", rule)], now);
            }))
            .ToArray();

        start.Set();
        TimeSpan?[] results = await Task.WhenAll(acquisitions);

        Assert.Equal(maximumBuckets, results.Count(static result => result is null));
        Assert.All(results.Where(static result => result is not null), static retryAfter =>
            Assert.Equal(TimeSpan.FromMinutes(1), retryAfter));
        Assert.Equal(maximumBuckets, store.RetainedBucketCount);
        Assert.Equal(maximumBuckets, store.MaximumRetainedBuckets);
    }

    [Fact]
    public void CapacityPressureDoesNotEvictActiveCounters()
    {
        ApiRateLimitStore store = new(maximumRetainedBuckets: 1);
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule rule = new(ApiRateLimitPartitions.LoginAccount, 1, TimeSpan.FromMinutes(1));

        Assert.Null(store.TryAcquire([("retained-account", rule)], now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire([("new-account", rule)], now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire([("retained-account", rule)], now));
        Assert.Equal(1, store.RetainedBucketCount);
    }

    [Fact]
    public void LegitimateAccountsRemainIndependentBehindOneAddress()
    {
        ApiRateLimitStore store = new();
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule accountRule = new(
            ApiRateLimitPartitions.LoginAccount,
            1,
            TimeSpan.FromMinutes(1));
        RateLimitRule ipRule = new(
            ApiRateLimitPartitions.IpAddress,
            10,
            TimeSpan.FromMinutes(1));

        Assert.Null(store.TryAcquire(
            [("first-account", accountRule), ("198.51.100.42", ipRule)],
            now));
        Assert.Null(store.TryAcquire(
            [("second-account", accountRule), ("198.51.100.42", ipRule)],
            now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire(
                [("first-account", accountRule), ("198.51.100.42", ipRule)],
                now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire(
                [("second-account", accountRule), ("198.51.100.42", ipRule)],
                now));
        Assert.Equal(3, store.RetainedBucketCount);
    }

    [Fact]
    public void DeniedPartitionDoesNotUpdateOtherPartitions()
    {
        ApiRateLimitStore store = new();
        DateTimeOffset now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
        RateLimitRule accountRule = new(
            ApiRateLimitPartitions.LoginAccount,
            1,
            TimeSpan.FromMinutes(1));
        RateLimitRule ipRule = new(
            ApiRateLimitPartitions.IpAddress,
            2,
            TimeSpan.FromMinutes(1));
        const string address = "198.51.100.42";

        Assert.Null(store.TryAcquire([("first-account", accountRule), (address, ipRule)], now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire([("first-account", accountRule), (address, ipRule)], now));
        Assert.Null(store.TryAcquire([("second-account", accountRule), (address, ipRule)], now));
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            store.TryAcquire([("third-account", accountRule), (address, ipRule)], now));

        Assert.Equal(3, store.RetainedBucketCount);
    }
}
