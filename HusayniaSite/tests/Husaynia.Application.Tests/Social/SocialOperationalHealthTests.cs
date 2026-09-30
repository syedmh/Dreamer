using Husaynia.Application.Operations.Health;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Tests.Social;

public sealed class SocialOperationalHealthTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SocialRefreshError.None, OperationalHealthStatus.Healthy, "dependency_healthy")]
    [InlineData(SocialRefreshError.RateLimited, OperationalHealthStatus.Degraded, "dependency_rate_limited")]
    [InlineData(SocialRefreshError.Timeout, OperationalHealthStatus.Unhealthy, "dependency_timeout")]
    [InlineData(SocialRefreshError.Malformed, OperationalHealthStatus.Unhealthy, "dependency_malformed")]
    [InlineData(SocialRefreshError.Unavailable, OperationalHealthStatus.Unhealthy, "dependency_unavailable")]
    public async Task DependencyProbeExposesBoundedPublicFailureState(
        SocialRefreshError error,
        OperationalHealthStatus expectedStatus,
        string expectedCode)
    {
        var store = new StaticStore
        {
            State = error == SocialRefreshError.None
                ? null
                : new SocialRefreshState(
                    Now,
                    null,
                    Now,
                    error,
                    null,
                    1),
        };
        var probe = new SocialDependencyHealthProbe("facebook", store);

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal("social.facebook.dependency", result.Name);
        Assert.Equal(HealthComponentKind.Dependency, result.Kind);
        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedCode, result.PublicCode);
        Assert.DoesNotContain("token", result.PublicCode, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            result.Name.Concat(result.PublicCode),
            character => Assert.True(
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-' or '.'));
    }

    [Fact]
    public async Task SnapshotProbeDistinguishesLoadingStaleExpiredAndHealthy()
    {
        var store = new StaticStore();
        var probe = new SocialSnapshotStalenessHealthProbe(
            "facebook",
            store,
            new FixedTimeProvider(Now));

        var loading = await probe.CheckAsync(CancellationToken.None);
        store.Snapshot = Feed(
            fetchedAtUtc: Now.AddMinutes(-10),
            expiresAtUtc: Now.AddMinutes(5));
        var healthy = await probe.CheckAsync(CancellationToken.None);
        store.Snapshot = Feed(
            fetchedAtUtc: Now.AddHours(-1),
            expiresAtUtc: Now.AddMinutes(-5));
        var stale = await probe.CheckAsync(CancellationToken.None);
        store.Snapshot = Feed(
            fetchedAtUtc: Now.AddHours(-2),
            expiresAtUtc: Now.AddMinutes(-16));
        var expired = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(OperationalHealthStatus.Degraded, loading.Status);
        Assert.Equal("snapshot_loading", loading.PublicCode);
        Assert.Equal(OperationalHealthStatus.Healthy, healthy.Status);
        Assert.Equal("snapshot_healthy", healthy.PublicCode);
        Assert.Equal(OperationalHealthStatus.Degraded, stale.Status);
        Assert.Equal("snapshot_stale", stale.PublicCode);
        Assert.Equal(OperationalHealthStatus.Unhealthy, expired.Status);
        Assert.Equal("snapshot_expired", expired.PublicCode);
        Assert.Equal(TimeSpan.FromHours(2), expired.DataAge);
    }

    [Fact]
    public async Task MissingSnapshotAfterFailureIsOperatorVisible()
    {
        var store = new StaticStore
        {
            State = new SocialRefreshState(
                Now,
                null,
                Now,
                SocialRefreshError.Unavailable,
                null,
                1),
        };
        var probe = new SocialSnapshotStalenessHealthProbe(
            "facebook",
            store,
            new FixedTimeProvider(Now));

        var result = await probe.CheckAsync(CancellationToken.None);

        Assert.Equal(OperationalHealthStatus.Unhealthy, result.Status);
        Assert.Equal("snapshot_unavailable", result.PublicCode);
    }

    private static StoredSocialFeed Feed(
        DateTimeOffset fetchedAtUtc,
        DateTimeOffset expiresAtUtc) =>
        new(
            "facebook",
            1,
            fetchedAtUtc,
            expiresAtUtc,
            fetchedAtUtc,
            null,
            []);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StaticStore : ISocialSnapshotStore
    {
        internal StoredSocialFeed? Snapshot { get; set; }

        internal SocialRefreshState? State { get; set; }

        public Task<StoredSocialFeed?> ReadAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<SocialRefreshState?> ReadRefreshStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(State);

        public Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
            string provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SocialRefreshSchedulingState(
                Snapshot?.Version,
                Snapshot?.ExpiresAtUtc,
                State));

        public Task<long> ReplaceAsync(
            NormalizedSocialFeed feed,
            int retainedVersions,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RecordFailureAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset? retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeferUntilAsync(
            string provider,
            SocialRefreshError failure,
            DateTimeOffset occurredAtUtc,
            DateTimeOffset retryAfterUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
