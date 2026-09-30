using Husaynia.Application.Operations.Health;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Social;

public sealed class SocialDependencyHealthProbe(
    string provider,
    ISocialSnapshotStore store) : IOperationalHealthProbe
{
    private readonly string provider =
        SocialFeedValidation.NormalizeProvider(provider);
    private readonly ISocialSnapshotStore store =
        store ?? throw new ArgumentNullException(nameof(store));

    public async Task<HealthProbeResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        var state = await store.ReadRefreshStateAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        var (status, code) = state?.LastError switch
        {
            null or SocialRefreshError.None =>
                (OperationalHealthStatus.Healthy, "dependency_healthy"),
            SocialRefreshError.RateLimited =>
                (OperationalHealthStatus.Degraded, "dependency_rate_limited"),
            SocialRefreshError.Cancelled =>
                (OperationalHealthStatus.Degraded, "dependency_cancelled"),
            SocialRefreshError.Timeout =>
                (OperationalHealthStatus.Unhealthy, "dependency_timeout"),
            SocialRefreshError.Malformed =>
                (OperationalHealthStatus.Unhealthy, "dependency_malformed"),
            _ =>
                (OperationalHealthStatus.Unhealthy, "dependency_unavailable"),
        };
        return new HealthProbeResult(
            $"social.{provider}.dependency",
            HealthComponentKind.Dependency,
            status,
            code);
    }
}

public sealed class SocialSnapshotStalenessHealthProbe(
    string provider,
    ISocialSnapshotStore store,
    TimeProvider timeProvider) : IOperationalHealthProbe
{
    private readonly string provider =
        SocialFeedValidation.NormalizeProvider(provider);
    private readonly ISocialSnapshotStore store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<HealthProbeResult> CheckAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await store.ReadAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        var state = await store.ReadRefreshStateAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            var failed = state?.LastError is not null and not SocialRefreshError.None;
            return new HealthProbeResult(
                $"social.{provider}.snapshot",
                HealthComponentKind.StaleData,
                failed
                    ? OperationalHealthStatus.Unhealthy
                    : OperationalHealthStatus.Degraded,
                failed ? "snapshot_unavailable" : "snapshot_loading");
        }

        var now = timeProvider.GetUtcNow();
        var age = now - snapshot.FetchedAtUtc;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        var failedAfterSuccess =
            snapshot.LastFailureAtUtc.HasValue &&
            (!snapshot.LastSuccessAtUtc.HasValue ||
             snapshot.LastFailureAtUtc > snapshot.LastSuccessAtUtc);
        if (!failedAfterSuccess && snapshot.ExpiresAtUtc > now)
        {
            return new HealthProbeResult(
                $"social.{provider}.snapshot",
                HealthComponentKind.StaleData,
                OperationalHealthStatus.Healthy,
                "snapshot_healthy",
                age);
        }

        var overdue = now - snapshot.ExpiresAtUtc;
        var unhealthy = overdue >=
            SocialRefreshJobDefinition.Registration.MaximumBackoff;
        return new HealthProbeResult(
            $"social.{provider}.snapshot",
            HealthComponentKind.StaleData,
            unhealthy
                ? OperationalHealthStatus.Unhealthy
                : OperationalHealthStatus.Degraded,
            unhealthy ? "snapshot_expired" : "snapshot_stale",
            age);
    }
}
