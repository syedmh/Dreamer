using System.Globalization;
using System.Net;
using Husaynia.Application.Contracts;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Social;

public enum SocialFeedAvailability
{
    Healthy = 0,
    Stale = 1,
    Empty = 2,
    Unavailable = 3,
    Loading = 4,
    UpstreamError = 5,
}

public sealed record SocialEmbedDescriptor(
    Uri Source,
    string ConsentCategory,
    bool MayLoad,
    string AccessibleLabel);

public enum SocialMediaInteraction
{
    None,
    Lightbox,
    Playback,
}

public sealed record SocialMediaDescriptor(
    SocialMediaType Type,
    Uri? LoadUri,
    Uri? ThumbnailUri,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? AltText,
    string? Caption,
    SocialMediaInteraction Interaction,
    bool RequiresConsent,
    bool MayLoad);

public sealed record SocialFeedViewItem(
    string ExternalId,
    string Text,
    Uri? OutboundLink,
    DateTimeOffset PublishedAtUtc,
    SocialEmbedDescriptor? Embed,
    SocialMediaDescriptor? Media);

public sealed record SocialFeedView(
    string Provider,
    SocialFeedAvailability Availability,
    IReadOnlyList<SocialFeedViewItem> Items,
    string Heading,
    string StatusMessage,
    DateTimeOffset? LastUpdatedAtUtc,
    DateTimeOffset? StaleSinceUtc,
    string? NextCursor,
    bool HasMore,
    int PageSize,
    int TotalItems);

public sealed record StoredSocialFeedItem(
    string ExternalId,
    string Text,
    Uri? SourceLink,
    SocialMediaType MediaType,
    Uri? MediaUrl,
    Uri? ThumbnailUrl,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? AltText,
    string? Caption,
    DateTimeOffset PublishedAtUtc);

public sealed record StoredSocialFeed(
    string Provider,
    long Version,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    IReadOnlyList<StoredSocialFeedItem> Items);

public sealed record NormalizedSocialFeed(
    string Provider,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset RefreshedAtUtc,
    IReadOnlyList<StoredSocialFeedItem> Items);

public sealed record SocialRefreshState(
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFailureAtUtc,
    SocialRefreshError LastError,
    DateTimeOffset? RetryAfterUtc,
    int ConsecutiveFailures);

public sealed record SocialRefreshSchedulingState(
    long? ActiveSnapshotVersion,
    DateTimeOffset? ActiveSnapshotExpiresAtUtc,
    SocialRefreshState? RefreshState);

public interface ISocialSnapshotStore
{
    Task<StoredSocialFeed?> ReadAsync(string provider, CancellationToken cancellationToken);

    Task<SocialRefreshState?> ReadRefreshStateAsync(
        string provider,
        CancellationToken cancellationToken);

    Task<SocialRefreshSchedulingState> ReadSchedulingStateAsync(
        string provider,
        CancellationToken cancellationToken);

    Task<long> ReplaceAsync(
        NormalizedSocialFeed feed,
        int retainedVersions,
        CancellationToken cancellationToken);

    Task RecordFailureAsync(
        string provider,
        SocialRefreshError failure,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset? retryAfterUtc,
        CancellationToken cancellationToken);

    Task DeferUntilAsync(
        string provider,
        SocialRefreshError failure,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset retryAfterUtc,
        CancellationToken cancellationToken);
}

public interface ISocialFeedReader
{
    Task<SocialFeedView> ReadAsync(
        string provider,
        bool socialConsentGranted,
        CancellationToken cancellationToken);

    Task<SocialFeedView> ReadPageAsync(
        string provider,
        bool socialConsentGranted,
        SocialFeedPageRequest page,
        CancellationToken cancellationToken);
}

public sealed record SocialFeedPageRequest(int PageSize = 10, string? Cursor = null)
{
    public void Validate()
    {
        if (PageSize is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(PageSize));
        }
    }
}

public interface ISocialFeedRefreshService
{
    Task<SocialRefreshReceipt> RefreshAsync(
        string provider,
        CancellationToken cancellationToken,
        TimeSpan? terminalRecoveryDelay = null);
}

public interface ISocialLinkPolicy
{
    Uri NormalizeSourceLink(string provider, Uri uri);

    Uri NormalizeMediaLink(string provider, Uri uri);
}

public sealed record SocialRefreshOptions
{
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan SnapshotLifetime { get; init; } = TimeSpan.FromHours(6);

    public TimeSpan MaximumItemAge { get; init; } = TimeSpan.FromDays(180);

    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan MaximumRateLimitDelay { get; init; } = TimeSpan.FromHours(24);

    public int MaximumItems { get; init; } = 20;

    public int RetainedVersions { get; init; } = 5;

    public void Validate()
    {
        if (ProviderTimeout <= TimeSpan.Zero ||
            SnapshotLifetime <= TimeSpan.Zero ||
            MaximumItemAge <= TimeSpan.Zero ||
            RateLimitDelay <= TimeSpan.Zero ||
            MaximumRateLimitDelay < RateLimitDelay ||
            MaximumRateLimitDelay > TimeSpan.FromDays(7))
        {
            throw new InvalidOperationException("Social refresh time spans are invalid.");
        }

        if (MaximumItems is < 1 or > SocialFeedLimits.MaximumItems ||
            RetainedVersions is < 1 or > 20)
        {
            throw new InvalidOperationException("Social refresh bounds are invalid.");
        }
    }
}

public enum SocialRefreshOutcome
{
    Refreshed,
    RefreshedWithRejectedItems,
    Empty,
    TimedOut,
    RateLimited,
    Unavailable,
    Malformed,
    Cancelled,
}

public sealed record SocialRefreshReceipt(
    SocialRefreshOutcome Outcome,
    long? Version,
    int AcceptedItems,
    int RejectedItems,
    DateTimeOffset AttemptedAtUtc);

public sealed record SocialProviderMedia(
    SocialMediaType Type,
    Uri Url,
    Uri? ThumbnailUrl,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? AltText,
    string? Caption);

public sealed record SocialProviderItem(
    string ExternalId,
    string Text,
    Uri? SourceLink,
    SocialProviderMedia? Media,
    DateTimeOffset PublishedAtUtc);

public sealed record SocialProviderFeed(
    string Provider,
    IReadOnlyList<SocialProviderItem> Items,
    DateTimeOffset FetchedAtUtc);

public enum SocialSourceErrorKind
{
    Timeout,
    RateLimited,
    Unavailable,
    Malformed,
    CircuitOpen,
}

public sealed record SocialSourceError(
    SocialSourceErrorKind Kind,
    DateTimeOffset? RetryAfterUtc = null);

public interface ISocialFeedSource
{
    Task<Result<SocialProviderFeed, SocialSourceError>> FetchAsync(
        SocialFeedRequest request,
        CancellationToken cancellationToken);
}

public sealed class SocialProviderSettings
{
    public SocialProviderSettings(
        string provider,
        Uri endpoint,
        IReadOnlySet<string> sourceHosts,
        IReadOnlySet<string> mediaHosts,
        int maximumResponseBytes = 1_000_000,
        TimeSpan? connectTimeout = null,
        TimeSpan? requestTimeout = null,
        int maximumAttempts = 2,
        TimeSpan? maximumRetryDelay = null,
        int circuitFailureThreshold = 3,
        TimeSpan? circuitBreakDuration = null)
    {
        Provider = SocialFeedValidation.NormalizeProvider(provider);
        var normalizedEndpoint = SocialFeedValidation.NormalizePersistablePublicUri(endpoint);
        if (normalizedEndpoint.Port != 443 ||
            endpoint.Query.Length > 0 ||
            endpoint.Fragment.Length > 0)
        {
            throw new ArgumentException("Provider endpoints require exact HTTPS port 443 origins.");
        }

        Endpoint = normalizedEndpoint;
        SourceHosts = NormalizeHosts(sourceHosts, nameof(sourceHosts));
        MediaHosts = NormalizeHosts(mediaHosts, nameof(mediaHosts));
        if (!SourceHosts.Contains(Endpoint.IdnHost))
        {
            throw new ArgumentException("The provider endpoint host must be an approved source host.");
        }

        if (maximumResponseBytes is < 1_024 or > 5_000_000 ||
            maximumAttempts is < 1 or > 3 ||
            circuitFailureThreshold is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        }

        MaximumResponseBytes = maximumResponseBytes;
        ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        MaximumRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(2);
        CircuitBreakDuration = circuitBreakDuration ?? TimeSpan.FromMinutes(1);
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromSeconds(30) ||
            RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(1) ||
            MaximumRetryDelay < TimeSpan.Zero || MaximumRetryDelay > TimeSpan.FromSeconds(30) ||
            CircuitBreakDuration <= TimeSpan.Zero || CircuitBreakDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        MaximumAttempts = maximumAttempts;
        CircuitFailureThreshold = circuitFailureThreshold;
    }

    public string Provider { get; }
    public Uri Endpoint { get; }
    public IReadOnlySet<string> SourceHosts { get; }
    public IReadOnlySet<string> MediaHosts { get; }
    public int MaximumResponseBytes { get; }
    public TimeSpan ConnectTimeout { get; }
    public TimeSpan RequestTimeout { get; }
    public int MaximumAttempts { get; }
    public TimeSpan MaximumRetryDelay { get; }
    public int CircuitFailureThreshold { get; }
    public TimeSpan CircuitBreakDuration { get; }

    private static HashSet<string> NormalizeHosts(
        IReadOnlySet<string> hosts,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(hosts, parameterName);
        var normalized = hosts
            .Select(host => new IdnMapping().GetAscii(
                SocialFeedValidation.RequiredText(host, 253, parameterName).TrimEnd('.')))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0 ||
            normalized.Any(host => IPAddress.TryParse(host, out _)))
        {
            throw new ArgumentException("At least one non-IP host is required.", parameterName);
        }

        return normalized;
    }
}

public interface ISocialProviderCredentialSource
{
    ValueTask<string?> GetBearerTokenAsync(
        string provider,
        CancellationToken cancellationToken);
}
