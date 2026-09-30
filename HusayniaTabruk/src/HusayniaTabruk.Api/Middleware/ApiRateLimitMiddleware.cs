using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HusayniaTabruk.Api.Configuration;
using HusayniaTabruk.Application.Abstractions.Authentication;
using HusayniaTabruk.Domain.Common.Errors;

[assembly: InternalsVisibleTo("HusayniaTabruk.Api.ContractTests")]

namespace HusayniaTabruk.Api.Middleware;

public static class ApiRateLimitPartitions
{
    public const string Account = "account";
    public const string Organization = "organization";
    public const string IpAddress = "ip_address";
    public const string LoginAccount = "login_account";
    public const string Invitation = "invitation";
    public const string RefreshToken = "refresh_token";
    public const string LogoutActorDevice = "logout_actor_device";
}

public sealed record RateLimitRule
{
    public RateLimitRule(string partition, int permitLimit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permitLimit);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The rate limit window must be positive.");
        }

        Partition = partition;
        PermitLimit = permitLimit;
        Window = window;
    }

    public string Partition { get; }
    public int PermitLimit { get; }
    public TimeSpan Window { get; }
}

public enum ApiRateLimitKeyStrategy
{
    Claims = 0,
    Login = 1,
    Invitation = 2,
    Refresh = 3,
    Logout = 4,
}

public sealed record ApiRateLimitMetadata(
    IReadOnlyList<RateLimitRule> Rules,
    ApiRateLimitKeyStrategy KeyStrategy = ApiRateLimitKeyStrategy.Claims);

public interface IApiRateLimitKeyProvider
{
    ValueTask<IReadOnlyList<(string Key, RateLimitRule Rule)>> GetKeysAsync(
        HttpContext context,
        ApiRateLimitMetadata metadata);
}

public interface IApiClientAddressProvider
{
    string GetAddress(HttpContext context);
}

public sealed class RemoteIpApiClientAddressProvider : IApiClientAddressProvider
{
    public string GetAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public sealed class EndpointApiRateLimitKeyProvider(
    IApiClientAddressProvider clientAddressProvider,
    IRefreshTokenFamilyFingerprintProvider refreshTokenFingerprintProvider) : IApiRateLimitKeyProvider
{
    private const int FingerprintLength = 24;

    public async ValueTask<IReadOnlyList<(string Key, RateLimitRule Rule)>> GetKeysAsync(
        HttpContext context,
        ApiRateLimitMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);

        string address = clientAddressProvider.GetAddress(context);
        IReadOnlyDictionary<string, string> bodyValues =
            metadata.KeyStrategy == ApiRateLimitKeyStrategy.Claims
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : await ReadBodyValuesAsync(context);

        return metadata.Rules
            .Select(rule => (GetKey(context, metadata.KeyStrategy, rule.Partition, address, bodyValues), rule))
            .ToArray();
    }

    private string GetKey(
        HttpContext context,
        ApiRateLimitKeyStrategy strategy,
        string partition,
        string address,
        IReadOnlyDictionary<string, string> bodyValues)
    {
        if (partition == ApiRateLimitPartitions.IpAddress)
        {
            return address;
        }

        if (strategy == ApiRateLimitKeyStrategy.Login
            && partition == ApiRateLimitPartitions.LoginAccount)
        {
            string email = GetBodyValue(bodyValues, "email").Trim().ToUpperInvariant();
            return Fingerprint("login", email, address);
        }

        if (strategy == ApiRateLimitKeyStrategy.Invitation
            && partition == ApiRateLimitPartitions.Invitation)
        {
            return Fingerprint("invitation", GetBodyValue(bodyValues, "token").Trim(), address);
        }

        if (strategy == ApiRateLimitKeyStrategy.Refresh
            && partition == ApiRateLimitPartitions.RefreshToken)
        {
            string refreshToken = GetBodyValue(bodyValues, "refreshToken").Trim();
            return refreshTokenFingerprintProvider.TryGetFingerprint(refreshToken, out string fingerprint)
                ? $"refresh:{fingerprint}"
                : Fingerprint("refresh", string.Empty, address);
        }

        if (strategy == ApiRateLimitKeyStrategy.Logout
            && partition == ApiRateLimitPartitions.LogoutActorDevice)
        {
            string actor = context.User.FindFirst("membership_id")?.Value ?? string.Empty;
            string device = context.Request.Headers[Auth.AuthHeaders.InstallationId].FirstOrDefault() ?? string.Empty;
            return Fingerprint("logout", $"{actor.Trim()}:{device.Trim().ToUpperInvariant()}", address);
        }

        string claimType = partition switch
        {
            ApiRateLimitPartitions.Account => "membership_id",
            ApiRateLimitPartitions.Organization => "organization_id",
            _ => partition,
        };

        return context.User.FindFirst(claimType)?.Value ?? address;
    }

    private static string Fingerprint(string scope, string value, string address)
    {
        string input = string.IsNullOrWhiteSpace(value) ? $"invalid:{address}" : value;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return $"{scope}:{Convert.ToHexString(hash)[..FingerprintLength]}";
    }

    private static string GetBodyValue(
        IReadOnlyDictionary<string, string> values,
        string propertyName) =>
        values.TryGetValue(propertyName, out string? value) ? value : string.Empty;

    private static async ValueTask<IReadOnlyDictionary<string, string>> ReadBodyValuesAsync(
        HttpContext context)
    {
        if (!context.Request.Body.CanSeek)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        long originalPosition = context.Request.Body.Position;
        try
        {
            context.Request.Body.Position = 0;
            using JsonDocument document = await JsonDocument.ParseAsync(
                context.Request.Body,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    MaxDepth = 16,
                },
                context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    values[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return values;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            context.Request.Body.Position = originalPosition;
        }
    }
}

public sealed class ApiRateLimitStore
{
    private const int DefaultMaximumRetainedBuckets = 10_000;
    private static readonly IComparer<ExpirationEntry> ExpirationComparer =
        Comparer<ExpirationEntry>.Create(static (left, right) =>
        {
            int expiryComparison = left.ExpiresAt.CompareTo(right.ExpiresAt);
            return expiryComparison != 0
                ? expiryComparison
                : left.BucketId.CompareTo(right.BucketId);
        });

    private readonly Lock _gate = new();
    private readonly int _maximumRetainedBuckets;
    private readonly Dictionary<BucketKey, Bucket> _buckets = [];
    private readonly SortedSet<ExpirationEntry> _expirations = new(ExpirationComparer);
    private long _nextBucketId;

    public ApiRateLimitStore()
        : this(DefaultMaximumRetainedBuckets)
    {
    }

    internal ApiRateLimitStore(int maximumRetainedBuckets)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedBuckets);
        _maximumRetainedBuckets = maximumRetainedBuckets;
    }

    internal int RetainedBucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    internal int MaximumRetainedBuckets => _maximumRetainedBuckets;

    public TimeSpan? TryAcquire(
        IReadOnlyList<(string Key, RateLimitRule Rule)> limits,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(limits);

        lock (_gate)
        {
            EvictExpiredBuckets(now);

            Dictionary<BucketKey, PendingAcquisition> acquisitions = [];
            foreach ((string key, RateLimitRule rule) in limits)
            {
                BucketKey bucketKey = new(
                    rule.Partition,
                    key,
                    rule.PermitLimit,
                    rule.Window.Ticks);
                if (acquisitions.TryGetValue(bucketKey, out PendingAcquisition? pending))
                {
                    pending.RequiredPermits++;
                    continue;
                }

                _buckets.TryGetValue(bucketKey, out Bucket? bucket);
                acquisitions.Add(bucketKey, new PendingAcquisition(bucketKey, rule, bucket));
            }

            TimeSpan? retryAfter = null;
            foreach (PendingAcquisition acquisition in acquisitions.Values)
            {
                if (acquisition.RequiredPermits > acquisition.Rule.PermitLimit)
                {
                    retryAfter = Max(retryAfter, acquisition.Rule.Window);
                    continue;
                }

                Bucket? bucket = acquisition.Bucket;
                if (bucket is null)
                {
                    continue;
                }

                DateTimeOffset cutoff = now - acquisition.Rule.Window;
                while (bucket.Requests.TryPeek(out DateTimeOffset recordedAt) && recordedAt <= cutoff)
                {
                    bucket.Requests.Dequeue();
                }

                int permitsToExpire =
                    bucket.Requests.Count + acquisition.RequiredPermits - acquisition.Rule.PermitLimit;
                if (permitsToExpire > 0)
                {
                    DateTimeOffset limitingRequest = bucket.Requests.ElementAt(permitsToExpire - 1);
                    TimeSpan candidate = limitingRequest + acquisition.Rule.Window - now;
                    retryAfter = Max(retryAfter, candidate);
                }
            }

            if (retryAfter is not null)
            {
                return retryAfter;
            }

            int missingBuckets = acquisitions.Values.Count(static acquisition => acquisition.Bucket is null);
            if (_buckets.Count + missingBuckets > _maximumRetainedBuckets)
            {
                return GetCapacityRetryAfter(acquisitions.Values, missingBuckets, now);
            }

            foreach (PendingAcquisition acquisition in acquisitions.Values)
            {
                Bucket bucket = acquisition.Bucket ?? AddBucket(acquisition.Key);
                if (bucket.Expiration is ExpirationEntry currentExpiration
                    && !_expirations.Remove(currentExpiration))
                {
                    throw new InvalidOperationException("The rate-limit bucket expiration index is inconsistent.");
                }

                for (int permit = 0; permit < acquisition.RequiredPermits; permit++)
                {
                    bucket.Requests.Enqueue(now);
                }

                ExpirationEntry expiration = new(
                    now + acquisition.Rule.Window,
                    bucket.Id,
                    acquisition.Key);
                bucket.Expiration = expiration;
                _expirations.Add(expiration);
            }

            return null;
        }
    }

    private static TimeSpan Max(TimeSpan? current, TimeSpan candidate) =>
        current is null || candidate > current ? candidate : current.Value;

    private TimeSpan GetCapacityRetryAfter(
        IEnumerable<PendingAcquisition> acquisitions,
        int missingBuckets,
        DateTimeOffset now)
    {
        int bucketsRequired = _buckets.Count + missingBuckets - _maximumRetainedBuckets;
        if (bucketsRequired > 0 && bucketsRequired <= _expirations.Count)
        {
            return _expirations.ElementAt(bucketsRequired - 1).ExpiresAt - now;
        }

        return acquisitions.Max(static acquisition => acquisition.Rule.Window);
    }

    private Bucket AddBucket(BucketKey key)
    {
        Bucket bucket = new(++_nextBucketId);
        _buckets.Add(key, bucket);
        return bucket;
    }

    private void EvictExpiredBuckets(DateTimeOffset now)
    {
        while (_expirations.Count > 0 && _expirations.Min.ExpiresAt <= now)
        {
            ExpirationEntry expiration = _expirations.Min;
            _expirations.Remove(expiration);
            if (_buckets.TryGetValue(expiration.Key, out Bucket? bucket)
                && bucket.Id == expiration.BucketId)
            {
                _buckets.Remove(expiration.Key);
            }
        }
    }

    private readonly record struct BucketKey(
        string Partition,
        string Key,
        int PermitLimit,
        long WindowTicks);

    private readonly record struct ExpirationEntry(
        DateTimeOffset ExpiresAt,
        long BucketId,
        BucketKey Key);

    private sealed class Bucket(long id)
    {
        public long Id { get; } = id;
        public Queue<DateTimeOffset> Requests { get; } = new();
        public ExpirationEntry? Expiration { get; set; }
    }

    private sealed class PendingAcquisition(
        BucketKey key,
        RateLimitRule rule,
        Bucket? bucket)
    {
        public BucketKey Key { get; } = key;
        public RateLimitRule Rule { get; } = rule;
        public Bucket? Bucket { get; } = bucket;
        public int RequiredPermits { get; set; } = 1;
    }
}

public sealed class ApiRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ApiRateLimitStore store,
        IApiRateLimitKeyProvider keyProvider)
    {
        ApiRateLimitMetadata? metadata = context.GetEndpoint()?.Metadata.GetMetadata<ApiRateLimitMetadata>();
        if (metadata is null)
        {
            await next(context);
            return;
        }

        IReadOnlyList<(string Key, RateLimitRule Rule)> limits =
            await keyProvider.GetKeysAsync(context, metadata);
        TimeSpan? retryAfter = store.TryAcquire(limits, DateTimeOffset.UtcNow);

        if (retryAfter is null)
        {
            await next(context);
            return;
        }

        int retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds));
        context.Response.Headers[ApiDefaults.RetryAfterHeaderName] =
            retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            "Rate limit exceeded",
            "The request quota is exhausted. Retry after the indicated number of seconds.");
    }
}

public static class ApiRateLimitMiddlewareExtensions
{
    public static IApplicationBuilder UseApiRateLimits(this IApplicationBuilder application) =>
        application.UseMiddleware<ApiRateLimitMiddleware>();
}
