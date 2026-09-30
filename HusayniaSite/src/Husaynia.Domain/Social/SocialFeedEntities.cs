using System.Net;
using System.Net.Sockets;

namespace Husaynia.Domain.Social;

public static class SocialFeedLimits
{
    public const int ProviderLength = 64;
    public const int ExternalIdLength = 200;
    public const int TextLength = 4_000;
    public const int LinkLength = 2_048;
    public const int AltTextLength = 500;
    public const int CaptionLength = 1_000;
    public const int ErrorCodeLength = 64;
    public const int MaximumItems = 100;
}

public enum SocialMediaType
{
    None = 0,
    Image = 1,
    Video = 2,
}

public enum SocialRefreshError
{
    None = 0,
    Timeout = 1,
    RateLimited = 2,
    Unavailable = 3,
    Malformed = 4,
    Cancelled = 5,
}

public sealed class PersistedSocialFeedSnapshot
{
    private PersistedSocialFeedSnapshot()
    {
    }

    public PersistedSocialFeedSnapshot(
        Guid id,
        string provider,
        long version,
        DateTimeOffset fetchedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        if (expiresAtUtc <= fetchedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Snapshot expiry must be after its fetch time.");
        }

        Id = id == Guid.Empty ? Guid.NewGuid() : id;
        Provider = SocialFeedValidation.NormalizeProvider(provider);
        Version = version;
        FetchedAtUtc = fetchedAtUtc.ToUniversalTime();
        ExpiresAtUtc = expiresAtUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public long Version { get; private set; }

    public DateTimeOffset FetchedAtUtc { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public ICollection<PersistedSocialFeedItem> Items { get; } = [];
}

public sealed class PersistedSocialFeedItem
{
    private PersistedSocialFeedItem()
    {
    }

    public PersistedSocialFeedItem(
        Guid snapshotId,
        string externalId,
        string text,
        Uri? sourceLink,
        SocialMediaType mediaType,
        Uri? mediaUrl,
        Uri? thumbnailUrl,
        int? width,
        int? height,
        double? durationSeconds,
        string? altText,
        string? caption,
        DateTimeOffset publishedAtUtc,
        int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot identifier is required.", nameof(snapshotId));
        }

        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        ExternalId = SocialFeedValidation.RequiredText(
            externalId,
            SocialFeedLimits.ExternalIdLength,
            nameof(externalId));
        Text = SocialFeedValidation.RequiredText(
            text,
            SocialFeedLimits.TextLength,
            nameof(text));
        var normalizedSource = sourceLink is null
            ? null
            : SocialFeedValidation.NormalizePersistablePublicUri(sourceLink);
        var normalizedMedia = mediaUrl is null
            ? null
            : SocialFeedValidation.NormalizePersistablePublicUri(mediaUrl);
        var normalizedThumbnail = thumbnailUrl is null
            ? null
            : SocialFeedValidation.NormalizePersistablePublicUri(thumbnailUrl);
        SocialMediaValidation.ValidateMetadata(
            mediaType,
            normalizedMedia,
            normalizedThumbnail,
            width,
            height,
            durationSeconds);
        SourceLink = normalizedSource?.AbsoluteUri;
        MediaType = mediaType;
        MediaUrl = normalizedMedia?.AbsoluteUri;
        ThumbnailUrl = normalizedThumbnail?.AbsoluteUri;
        Width = width;
        Height = height;
        DurationSeconds = durationSeconds;
        AltText = OptionalText(altText, SocialFeedLimits.AltTextLength);
        Caption = OptionalText(caption, SocialFeedLimits.CaptionLength);
        PublishedAtUtc = publishedAtUtc.ToUniversalTime();
        Ordinal = ordinal;
    }

    public Guid Id { get; private set; }

    public Guid SnapshotId { get; private set; }

    public string ExternalId { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    public string? SourceLink { get; private set; }

    public SocialMediaType MediaType { get; private set; }

    public string? MediaUrl { get; private set; }

    public string? ThumbnailUrl { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public double? DurationSeconds { get; private set; }

    public string? AltText { get; private set; }

    public string? Caption { get; private set; }

    public DateTimeOffset PublishedAtUtc { get; private set; }

    public int Ordinal { get; private set; }

    private static string? OptionalText(string? value, int maximumLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : SocialFeedValidation.RequiredText(value, maximumLength, nameof(value));
}

public sealed class SocialIntegrationState
{
    private SocialIntegrationState()
    {
    }

    public SocialIntegrationState(string provider) =>
        Provider = SocialFeedValidation.NormalizeProvider(provider);

    public string Provider { get; private set; } = string.Empty;

    public Guid? ActiveSnapshotId { get; private set; }

    public DateTimeOffset? LastAttemptAtUtc { get; private set; }

    public DateTimeOffset? LastSuccessAtUtc { get; private set; }

    public DateTimeOffset? LastFailureAtUtc { get; private set; }

    public SocialRefreshError LastError { get; private set; }

    public DateTimeOffset? RetryAfterUtc { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public bool TryRecordSuccess(Guid snapshotId, DateTimeOffset occurredAtUtc)
    {
        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("A snapshot identifier is required.", nameof(snapshotId));
        }

        var occurred = occurredAtUtc.ToUniversalTime();
        if (LastAttemptAtUtc > occurred)
        {
            return false;
        }

        ActiveSnapshotId = snapshotId;
        LastAttemptAtUtc = occurred;
        LastSuccessAtUtc = occurred;
        LastFailureAtUtc = null;
        LastError = SocialRefreshError.None;
        RetryAfterUtc = null;
        ConsecutiveFailures = 0;
        return true;
    }

    public bool TryRecordFailure(
        SocialRefreshError error,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset? retryAfterUtc)
    {
        if (error is SocialRefreshError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error));
        }

        var occurred = occurredAtUtc.ToUniversalTime();
        if (LastAttemptAtUtc >= occurred)
        {
            return false;
        }

        LastAttemptAtUtc = occurred;
        LastFailureAtUtc = occurred;
        LastError = error;
        RetryAfterUtc = retryAfterUtc?.ToUniversalTime() ?? RetryAfterUtc;
        ConsecutiveFailures = checked(ConsecutiveFailures + 1);
        return true;
    }

    public bool TryDeferUntil(
        SocialRefreshError error,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset retryAfterUtc)
    {
        if (error is SocialRefreshError.None)
        {
            throw new ArgumentOutOfRangeException(nameof(error));
        }

        var occurred = occurredAtUtc.ToUniversalTime();
        var retryAfter = retryAfterUtc.ToUniversalTime();
        if (retryAfter <= occurred)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterUtc));
        }

        if (LastAttemptAtUtc != occurred ||
            LastFailureAtUtc != occurred ||
            LastError != error ||
            RetryAfterUtc >= retryAfter)
        {
            return false;
        }

        RetryAfterUtc = retryAfter;
        return true;
    }
}

public static class SocialFeedValidation
{
    private static readonly string[] SensitiveQueryNames =
    [
        "access_token",
        "api_key",
        "apikey",
        "auth",
        "key",
        "password",
        "secret",
        "token",
    ];

    public static string NormalizeProvider(string provider)
    {
        var normalized = RequiredText(
            provider,
            SocialFeedLimits.ProviderLength,
            nameof(provider)).ToLowerInvariant();
        if (!normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
        {
            throw new ArgumentException(
                "Provider names may contain only ASCII letters, digits, hyphens, and underscores.",
                nameof(provider));
        }

        return normalized;
    }

    public static string RequiredText(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength ||
            trimmed.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
        {
            throw new ArgumentException("The value is invalid or exceeds its allowed length.", parameterName);
        }

        return trimmed;
    }

    public static Uri NormalizePersistablePublicUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsoluteUri.Length > SocialFeedLimits.LinkLength ||
            string.IsNullOrWhiteSpace(uri.IdnHost) ||
            IPAddress.TryParse(uri.IdnHost, out _))
        {
            throw new ArgumentException(
                "Only credential-free, host-based absolute HTTPS links are allowed.",
                nameof(uri));
        }

        foreach (var pair in uri.Query.AsSpan().TrimStart('?').ToString()
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            if (IsSensitiveQueryName(name))
            {
                throw new ArgumentException("Sensitive query parameters are not allowed.", nameof(uri));
            }
        }

        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
        }.Uri;
    }

    public static bool IsPublicNetworkAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            return first is not 0 and not 10 and not 127 &&
                   !(first == 100 && second is >= 64 and <= 127) &&
                   !(first == 169 && second == 254) &&
                   !(first == 172 && second is >= 16 and <= 31) &&
                   !(first == 192 && second == 0 && bytes[2] is 0 or 2) &&
                   !(first == 192 && second == 88 && bytes[2] == 99) &&
                   !(first == 192 && second == 168) &&
                   !(first == 198 && second is 18 or 19) &&
                   !(first == 198 && second == 51 && bytes[2] == 100) &&
                   !(first == 203 && second == 0 && bytes[2] == 113) &&
                   first < 224;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // Routable IPv6 global unicast allocations are within 2000::/3.
        if ((bytes[0] & 0xE0) != 0x20)
        {
            return false;
        }

        var protocolAssignments =
            bytes[0] == 0x20 &&
            bytes[1] == 0x01 &&
            (bytes[2] & 0xFE) == 0;
        var documentation =
            bytes[0] == 0x20 &&
            bytes[1] == 0x01 &&
            bytes[2] == 0x0D &&
            bytes[3] == 0xB8;
        var sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        var extendedDocumentation =
            bytes[0] == 0x3F &&
            bytes[1] == 0xFF &&
            (bytes[2] & 0xF0) == 0;
        return !protocolAssignments &&
               !documentation &&
               !sixToFour &&
               !extendedDocumentation;
    }

    private static bool IsSensitiveQueryName(string name) =>
        SensitiveQueryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("sig", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("expires", StringComparison.OrdinalIgnoreCase);
}

public static class SocialMediaValidation
{
    public static void ValidateMetadata(
        SocialMediaType mediaType,
        Uri? mediaUrl,
        Uri? thumbnailUrl,
        int? width,
        int? height,
        double? durationSeconds)
    {
        if (!Enum.IsDefined(mediaType))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType));
        }

        if (mediaType == SocialMediaType.None)
        {
            if (mediaUrl is not null || thumbnailUrl is not null ||
                width is not null || height is not null || durationSeconds is not null)
            {
                throw new ArgumentException("Items without media cannot contain media metadata.");
            }

            return;
        }

        ArgumentNullException.ThrowIfNull(mediaUrl);
        if (width is <= 0 or > 16_384 || height is <= 0 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (mediaType == SocialMediaType.Image && durationSeconds is not null ||
            mediaType == SocialMediaType.Video &&
            (durationSeconds is <= 0 or > 86_400))
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
    }
}
