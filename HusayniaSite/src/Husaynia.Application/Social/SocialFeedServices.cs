using System.Globalization;
using System.Text;
using Husaynia.Application.Contracts;
using Husaynia.Application.Operations.Telemetry;
using Husaynia.Domain.Social;

namespace Husaynia.Application.Social;

public sealed class SocialFeedReader(
    ISocialSnapshotStore store,
    ISocialLinkPolicy linkPolicy,
    TimeProvider timeProvider,
    IOperationsMetrics? operationsMetrics = null) : ISocialFeedReader
{
    private readonly IOperationsMetrics operationsMetrics =
        operationsMetrics ?? SocialNullOperationsMetrics.Instance;

    public Task<SocialFeedView> ReadAsync(
        string provider,
        bool socialConsentGranted,
        CancellationToken cancellationToken) =>
        ReadPageAsync(
            provider,
            socialConsentGranted,
            new SocialFeedPageRequest(20),
            cancellationToken);

    public async Task<SocialFeedView> ReadPageAsync(
        string provider,
        bool socialConsentGranted,
        SocialFeedPageRequest page,
        CancellationToken cancellationToken)
    {
        page.Validate();
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        var stored = await store.ReadAsync(normalizedProvider, cancellationToken)
            .ConfigureAwait(false);
        if (stored is null)
        {
            var refreshState = await store.ReadRefreshStateAsync(
                    normalizedProvider,
                    cancellationToken)
                .ConfigureAwait(false);
            var loading = refreshState is null ||
                refreshState.LastError == SocialRefreshError.None;
            return State(
                normalizedProvider,
                loading
                    ? SocialFeedAvailability.Loading
                    : SocialFeedAvailability.UpstreamError,
                loading
                    ? "Social updates are loading."
                    : "Social updates are temporarily unavailable. Please try again later.",
                page.PageSize);
        }

        var now = timeProvider.GetUtcNow();
        operationsMetrics.RecordStaleness(
            DataSetName(normalizedProvider),
            NonNegativeAge(now, stored.FetchedAtUtc));
        var failedAfterSuccess =
            stored.LastFailureAtUtc.HasValue &&
            (!stored.LastSuccessAtUtc.HasValue ||
             stored.LastFailureAtUtc > stored.LastSuccessAtUtc);
        var stale = stored.ExpiresAtUtc <= now || failedAfterSuccess;
        DateTimeOffset? staleSince = stale
            ? failedAfterSuccess ? stored.LastFailureAtUtc : stored.ExpiresAtUtc
            : null;

        if (stored.Items.Count == 0)
        {
            return new SocialFeedView(
                normalizedProvider,
                stale ? SocialFeedAvailability.UpstreamError : SocialFeedAvailability.Empty,
                [],
                "Social updates",
                stale
                    ? "Social updates are temporarily unavailable. Please try again later."
                    : "There are no social updates to display.",
                stored.LastSuccessAtUtc,
                staleSince,
                null,
                false,
                page.PageSize,
                0);
        }

        var offset = SocialFeedCursor.Decode(page.Cursor, stored.Version, stored.Items.Count);
        var selected = stored.Items.Skip(offset).Take(page.PageSize).ToArray();
        var nextOffset = offset + selected.Length;
        var hasMore = nextOffset < stored.Items.Count;
        var items = selected.Select(item => ToViewItem(
            normalizedProvider,
            item,
            socialConsentGranted)).ToArray();

        return new SocialFeedView(
            normalizedProvider,
            stale ? SocialFeedAvailability.Stale : SocialFeedAvailability.Healthy,
            items,
            "Social updates",
            stale
                ? "Showing saved social updates because new updates are temporarily unavailable."
                : "Social updates loaded.",
            stored.LastSuccessAtUtc,
            staleSince,
            hasMore ? SocialFeedCursor.Encode(stored.Version, nextOffset) : null,
            hasMore,
            page.PageSize,
            stored.Items.Count);
    }

    private SocialFeedViewItem ToViewItem(
        string provider,
        StoredSocialFeedItem item,
        bool consentGranted)
    {
        Uri? sourceLink = null;
        if (item.SourceLink is not null)
        {
            try
            {
                sourceLink = linkPolicy.NormalizeSourceLink(provider, item.SourceLink);
            }
            catch (ArgumentException)
            {
                sourceLink = null;
            }
        }

        SocialMediaDescriptor? media = null;
        if (item.MediaType != SocialMediaType.None && item.MediaUrl is not null)
        {
            try
            {
                var mediaUrl = linkPolicy.NormalizeMediaLink(provider, item.MediaUrl);
                var thumbnail = item.ThumbnailUrl is null
                    ? null
                    : linkPolicy.NormalizeMediaLink(provider, item.ThumbnailUrl);
                media = new SocialMediaDescriptor(
                    item.MediaType,
                    consentGranted ? mediaUrl : null,
                    consentGranted ? thumbnail : null,
                    item.Width,
                    item.Height,
                    item.DurationSeconds,
                    item.AltText,
                    item.Caption,
                    item.MediaType == SocialMediaType.Image
                        ? SocialMediaInteraction.Lightbox
                        : SocialMediaInteraction.Playback,
                    RequiresConsent: true,
                    MayLoad: consentGranted);
            }
            catch (ArgumentException)
            {
                media = null;
            }
        }

        var embed = sourceLink is null
            ? null
            : new SocialEmbedDescriptor(
                sourceLink,
                "social",
                consentGranted,
                consentGranted
                    ? "Embedded social media content"
                    : "Social media content is blocked until optional social consent is granted.");
        return new SocialFeedViewItem(
            item.ExternalId,
            item.Text,
            sourceLink,
            item.PublishedAtUtc,
            embed,
            media);
    }

    private static SocialFeedView State(
        string provider,
        SocialFeedAvailability availability,
        string message,
        int pageSize) =>
        new(
            provider,
            availability,
            [],
            "Social updates",
            message,
            null,
            null,
            null,
            false,
            pageSize,
            0);

    private static string DataSetName(string provider) =>
        $"social.{provider}.snapshot";

    private static TimeSpan NonNegativeAge(
        DateTimeOffset now,
        DateTimeOffset updatedAtUtc)
    {
        var age = now - updatedAtUtc;
        return age < TimeSpan.Zero ? TimeSpan.Zero : age;
    }
}

public sealed class SocialFeedRefreshService(
    ISocialFeedSource feedSource,
    ISocialSnapshotStore store,
    ISocialLinkPolicy linkPolicy,
    TimeProvider timeProvider,
    SocialRefreshOptions options,
    IOperationsMetrics? operationsMetrics = null) : ISocialFeedRefreshService
{
    private readonly IOperationsMetrics operationsMetrics =
        operationsMetrics ?? SocialNullOperationsMetrics.Instance;

    public async Task<SocialRefreshReceipt> RefreshAsync(
        string provider,
        CancellationToken cancellationToken,
        TimeSpan? terminalRecoveryDelay = null)
    {
        options.Validate();
        if (terminalRecoveryDelay is { } recoveryDelay &&
            (recoveryDelay <= TimeSpan.Zero ||
             recoveryDelay > options.MaximumRateLimitDelay))
        {
            throw new ArgumentOutOfRangeException(nameof(terminalRecoveryDelay));
        }

        var providerKey = SocialFeedValidation.NormalizeProvider(provider);
        var startedTimestamp = timeProvider.GetTimestamp();
        var attemptedAtUtc = timeProvider.GetUtcNow();
        var refreshState = await store.ReadRefreshStateAsync(providerKey, cancellationToken)
            .ConfigureAwait(false);
        var effectiveRetryAfter = SocialRateLimitPolicy.BoundStoredRetryAfter(
            refreshState,
            attemptedAtUtc,
            options);
        if (effectiveRetryAfter > attemptedAtUtc)
        {
            return Complete(
                Receipt(SocialRefreshOutcome.RateLimited, attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }

        Result<SocialProviderFeed, SocialSourceError> result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ProviderTimeout);
        try
        {
            result = await feedSource.FetchAsync(
                new SocialFeedRequest(providerKey, options.MaximumItems),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAsync(
                providerKey,
                SocialRefreshError.Cancelled,
                attemptedAtUtc,
                effectiveRetryAfter).ConfigureAwait(false);
            return Complete(
                Receipt(SocialRefreshOutcome.Cancelled, attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }
        catch (OperationCanceledException)
        {
            await RecordFailureAsync(
                providerKey,
                SocialRefreshError.Timeout,
                attemptedAtUtc,
                FailureRetryAfter(
                    SocialRefreshError.Timeout,
                    attemptedAtUtc,
                    effectiveRetryAfter,
                    terminalRecoveryDelay)).ConfigureAwait(false);
            return Complete(
                Receipt(SocialRefreshOutcome.TimedOut, attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }
        catch (HttpRequestException)
        {
            await RecordFailureAsync(
                providerKey,
                SocialRefreshError.Unavailable,
                attemptedAtUtc,
                FailureRetryAfter(
                    SocialRefreshError.Unavailable,
                    attemptedAtUtc,
                    effectiveRetryAfter,
                    terminalRecoveryDelay)).ConfigureAwait(false);
            return Complete(
                Receipt(SocialRefreshOutcome.Unavailable, attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }

        if (result.IsFailure)
        {
            var failure = MapError(result.Error.Kind);
            DateTimeOffset? retryAfter = failure == SocialRefreshError.RateLimited
                ? SocialRateLimitPolicy.BoundProviderRetryAfter(
                    result.Error.RetryAfterUtc,
                    attemptedAtUtc,
                    options)
                : FailureRetryAfter(
                    failure,
                    attemptedAtUtc,
                    effectiveRetryAfter,
                    terminalRecoveryDelay);
            await RecordFailureAsync(
                providerKey,
                failure,
                attemptedAtUtc,
                retryAfter).ConfigureAwait(false);
            return Complete(
                Receipt(ToOutcome(failure), attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }

        var normalization = Normalize(providerKey, result.Success, attemptedAtUtc);
        if (normalization.Feed is null)
        {
            await RecordFailureAsync(
                providerKey,
                SocialRefreshError.Malformed,
                attemptedAtUtc,
                FailureRetryAfter(
                    SocialRefreshError.Malformed,
                    attemptedAtUtc,
                    effectiveRetryAfter,
                    terminalRecoveryDelay)).ConfigureAwait(false);
            return Complete(
                new SocialRefreshReceipt(
                    SocialRefreshOutcome.Malformed,
                    null,
                    0,
                    normalization.RejectedItems,
                    attemptedAtUtc),
                providerKey,
                startedTimestamp);
        }

        var version = await store.ReplaceAsync(
            normalization.Feed,
            options.RetainedVersions,
            cancellationToken).ConfigureAwait(false);
        var outcome = normalization.Feed.Items.Count == 0
            ? SocialRefreshOutcome.Empty
            : normalization.RejectedItems > 0
                ? SocialRefreshOutcome.RefreshedWithRejectedItems
                : SocialRefreshOutcome.Refreshed;
        return Complete(
            new SocialRefreshReceipt(
                outcome,
                version,
                normalization.Feed.Items.Count,
                normalization.RejectedItems,
                attemptedAtUtc),
            providerKey,
            startedTimestamp);
    }

    private SocialRefreshReceipt Complete(
        SocialRefreshReceipt receipt,
        string provider,
        long startedTimestamp)
    {
        operationsMetrics.RecordDependency(
            $"social.{provider}",
            MetricOutcome(receipt.Outcome),
            Math.Max(
                0,
                timeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds));
        return receipt;
    }

    private async Task RecordFailureAsync(
        string provider,
        SocialRefreshError failure,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset? retryAfterUtc) =>
        await store.RecordFailureAsync(
            provider,
            failure,
            attemptedAtUtc,
            retryAfterUtc,
            CancellationToken.None).ConfigureAwait(false);

    private static DateTimeOffset? FailureRetryAfter(
        SocialRefreshError failure,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset? currentRecoveryAnchor,
        TimeSpan? terminalRecoveryDelay) =>
        terminalRecoveryDelay is { } recoveryDelay &&
        failure is SocialRefreshError.Timeout or
            SocialRefreshError.Unavailable or
            SocialRefreshError.Malformed
            ? SocialRateLimitPolicy.AddBounded(attemptedAtUtc, recoveryDelay)
            : currentRecoveryAnchor;

    private NormalizationResult Normalize(
        string requestedProvider,
        SocialProviderFeed snapshot,
        DateTimeOffset now)
    {
        if (snapshot.Items is null)
        {
            return new NormalizationResult(null, 0);
        }

        string actualProvider;
        try
        {
            actualProvider = SocialFeedValidation.NormalizeProvider(snapshot.Provider);
        }
        catch (ArgumentException)
        {
            return new NormalizationResult(null, snapshot.Items.Count);
        }

        if (!actualProvider.Equals(requestedProvider, StringComparison.Ordinal) ||
            snapshot.FetchedAtUtc > now + TimeSpan.FromMinutes(5) ||
            snapshot.FetchedAtUtc < now - options.MaximumItemAge)
        {
            return new NormalizationResult(null, snapshot.Items.Count);
        }

        var rejected = 0;
        var candidates = new List<StoredSocialFeedItem>();
        foreach (var item in snapshot.Items)
        {
            try
            {
                var externalId = SocialFeedValidation.RequiredText(
                    item.ExternalId,
                    SocialFeedLimits.ExternalIdLength,
                    nameof(item.ExternalId));
                var text = SocialFeedValidation.RequiredText(
                    item.Text,
                    SocialFeedLimits.TextLength,
                    nameof(item.Text));
                if (item.PublishedAtUtc > now + TimeSpan.FromMinutes(5) ||
                    item.PublishedAtUtc < now - options.MaximumItemAge)
                {
                    rejected++;
                    continue;
                }

                Uri? sourceLink = null;
                if (item.SourceLink is not null)
                {
                    sourceLink = linkPolicy.NormalizeSourceLink(
                        requestedProvider,
                        item.SourceLink);
                }

                var media = NormalizeMedia(requestedProvider, item.Media);
                candidates.Add(new StoredSocialFeedItem(
                    externalId,
                    text,
                    sourceLink,
                    media.Type,
                    media.Url,
                    media.ThumbnailUrl,
                    media.Width,
                    media.Height,
                    media.DurationSeconds,
                    media.AltText,
                    media.Caption,
                    item.PublishedAtUtc.ToUniversalTime()));
            }
            catch (ArgumentException)
            {
                rejected++;
            }
        }

        var normalized = candidates
            .OrderByDescending(item => item.PublishedAtUtc)
            .ThenBy(item => item.ExternalId, StringComparer.Ordinal)
            .GroupBy(item => item.ExternalId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(options.MaximumItems)
            .ToArray();
        rejected += candidates.Count - normalized.Length;

        if (snapshot.Items.Count > 0 && normalized.Length == 0)
        {
            return new NormalizationResult(null, rejected);
        }

        return new NormalizationResult(
            new NormalizedSocialFeed(
                requestedProvider,
                snapshot.FetchedAtUtc.ToUniversalTime(),
                now + options.SnapshotLifetime,
                now,
                normalized),
            rejected);
    }

    private NormalizedMedia NormalizeMedia(
        string provider,
        SocialProviderMedia? media)
    {
        if (media is null)
        {
            return NormalizedMedia.None;
        }

        var url = linkPolicy.NormalizeMediaLink(provider, media.Url);
        var thumbnail = media.ThumbnailUrl is null
            ? null
            : linkPolicy.NormalizeMediaLink(provider, media.ThumbnailUrl);
        SocialMediaValidation.ValidateMetadata(
            media.Type,
            url,
            thumbnail,
            media.Width,
            media.Height,
            media.DurationSeconds);
        var alt = string.IsNullOrWhiteSpace(media.AltText)
            ? null
            : SocialFeedValidation.RequiredText(
                media.AltText,
                SocialFeedLimits.AltTextLength,
                nameof(media.AltText));
        var caption = string.IsNullOrWhiteSpace(media.Caption)
            ? null
            : SocialFeedValidation.RequiredText(
                media.Caption,
                SocialFeedLimits.CaptionLength,
                nameof(media.Caption));
        return new NormalizedMedia(
            media.Type,
            url,
            thumbnail,
            media.Width,
            media.Height,
            media.DurationSeconds,
            alt,
            caption);
    }

    private static SocialRefreshError MapError(SocialSourceErrorKind kind) =>
        kind switch
        {
            SocialSourceErrorKind.Timeout => SocialRefreshError.Timeout,
            SocialSourceErrorKind.RateLimited => SocialRefreshError.RateLimited,
            SocialSourceErrorKind.Malformed => SocialRefreshError.Malformed,
            _ => SocialRefreshError.Unavailable,
        };

    private static SocialRefreshOutcome ToOutcome(SocialRefreshError error) =>
        error switch
        {
            SocialRefreshError.Timeout => SocialRefreshOutcome.TimedOut,
            SocialRefreshError.RateLimited => SocialRefreshOutcome.RateLimited,
            SocialRefreshError.Malformed => SocialRefreshOutcome.Malformed,
            SocialRefreshError.Cancelled => SocialRefreshOutcome.Cancelled,
            _ => SocialRefreshOutcome.Unavailable,
        };

    private static string MetricOutcome(SocialRefreshOutcome outcome) =>
        outcome switch
        {
            SocialRefreshOutcome.Refreshed => "success",
            SocialRefreshOutcome.RefreshedWithRejectedItems => "success_with_rejections",
            SocialRefreshOutcome.Empty => "empty",
            SocialRefreshOutcome.TimedOut => "timeout",
            SocialRefreshOutcome.RateLimited => "rate_limited",
            SocialRefreshOutcome.Unavailable => "unavailable",
            SocialRefreshOutcome.Malformed => "malformed",
            SocialRefreshOutcome.Cancelled => "cancelled",
            _ => "unknown",
        };

    private static SocialRefreshReceipt Receipt(
        SocialRefreshOutcome outcome,
        DateTimeOffset attemptedAtUtc) =>
        new(outcome, null, 0, 0, attemptedAtUtc);

    private sealed record NormalizationResult(
        NormalizedSocialFeed? Feed,
        int RejectedItems);

    private sealed record NormalizedMedia(
        SocialMediaType Type,
        Uri? Url,
        Uri? ThumbnailUrl,
        int? Width,
        int? Height,
        double? DurationSeconds,
        string? AltText,
        string? Caption)
    {
        internal static NormalizedMedia None { get; } =
            new(SocialMediaType.None, null, null, null, null, null, null, null);
    }
}

internal sealed class SocialNullOperationsMetrics : IOperationsMetrics
{
    internal static SocialNullOperationsMetrics Instance { get; } = new();

    private SocialNullOperationsMetrics()
    {
    }

    public void RecordDependency(
        string dependency,
        string outcome,
        double durationMilliseconds = 0)
    {
    }

    public void RecordJob(string definition, string outcome, int attempt)
    {
    }

    public void RecordStaleness(string dataSet, TimeSpan age)
    {
    }
}

internal static class SocialRateLimitPolicy
{
    internal static DateTimeOffset BoundProviderRetryAfter(
        DateTimeOffset? providerRetryAfterUtc,
        DateTimeOffset attemptedAtUtc,
        SocialRefreshOptions options)
    {
        var fallback = AddBounded(attemptedAtUtc, options.RateLimitDelay);
        if (providerRetryAfterUtc is not { } providerRetryAfter ||
            providerRetryAfter <= attemptedAtUtc)
        {
            return fallback;
        }

        var maximum = AddBounded(
            attemptedAtUtc,
            options.MaximumRateLimitDelay);
        return providerRetryAfter <= maximum ? providerRetryAfter : maximum;
    }

    internal static DateTimeOffset? BoundStoredRetryAfter(
        SocialRefreshState? state,
        DateTimeOffset now,
        SocialRefreshOptions options)
    {
        if (state?.RetryAfterUtc is not { } retryAfterUtc)
        {
            return null;
        }

        var basis = state.LastAttemptAtUtc ??
            state.LastFailureAtUtc ??
            now;
        var maximum = AddBounded(basis, options.MaximumRateLimitDelay);
        return retryAfterUtc <= maximum ? retryAfterUtc : maximum;
    }

    internal static DateTimeOffset AddBounded(
        DateTimeOffset value,
        TimeSpan delay) =>
        value > DateTimeOffset.MaxValue - delay
            ? DateTimeOffset.MaxValue
            : value + delay;
}

public sealed class SocialLinkPolicy : ISocialLinkPolicy
{
    private readonly Dictionary<string, IReadOnlySet<string>> sourceHosts;
    private readonly Dictionary<string, IReadOnlySet<string>> mediaHosts;

    public SocialLinkPolicy(
        IReadOnlyDictionary<string, IReadOnlySet<string>> sourceHosts,
        IReadOnlyDictionary<string, IReadOnlySet<string>> mediaHosts)
    {
        this.sourceHosts = Normalize(sourceHosts);
        this.mediaHosts = Normalize(mediaHosts);
    }

    public Uri NormalizeSourceLink(string provider, Uri uri) =>
        Normalize(provider, uri, sourceHosts);

    public Uri NormalizeMediaLink(string provider, Uri uri) =>
        Normalize(provider, uri, mediaHosts);

    private static Uri Normalize(
        string provider,
        Uri uri,
        Dictionary<string, IReadOnlySet<string>> hosts)
    {
        var normalizedProvider = SocialFeedValidation.NormalizeProvider(provider);
        var safe = SocialFeedValidation.NormalizePersistablePublicUri(uri);
        if (!hosts.TryGetValue(normalizedProvider, out var allowed) ||
            !allowed.Contains(safe.IdnHost))
        {
            throw new ArgumentException("The URI host is not approved.", nameof(uri));
        }

        return safe;
    }

    private static Dictionary<string, IReadOnlySet<string>> Normalize(
        IReadOnlyDictionary<string, IReadOnlySet<string>> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.ToDictionary(
            pair => SocialFeedValidation.NormalizeProvider(pair.Key),
            pair => (IReadOnlySet<string>)pair.Value
                .Select(host => new IdnMapping().GetAscii(host.Trim().TrimEnd('.')))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.Ordinal);
    }
}

public static class SocialDiagnosticSanitizer
{
    public static string SafeCode(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => "timeout",
            HttpRequestException => "provider_unavailable",
            _ => "unexpected_provider_error",
        };
    }
}

internal static class SocialFeedCursor
{
    internal static string Encode(long version, int offset) =>
        Convert.ToBase64String(
            Encoding.ASCII.GetBytes(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{version}:{offset}")))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static int Decode(string? cursor, long version, int totalItems)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var value = Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            var parts = value.Split(':', StringSplitOptions.None);
            if (parts.Length != 2 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var cursorVersion) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                cursorVersion != version ||
                offset < 0 ||
                offset >= totalItems)
            {
                throw new ArgumentException("The social feed cursor is invalid.", nameof(cursor));
            }

            return offset;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The social feed cursor is invalid.", nameof(cursor), exception);
        }
    }
}
