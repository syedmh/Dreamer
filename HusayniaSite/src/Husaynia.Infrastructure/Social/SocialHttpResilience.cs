using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Husaynia.Application.Contracts;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;

namespace Husaynia.Infrastructure.Social;

public interface ISocialProviderHttpClientFactory
{
    Task<Result<HttpResponseMessage, SocialSourceError>> SendGetAsync(
        SocialProviderSettings providerSettings,
        string? bearerToken,
        CancellationToken cancellationToken);
}

public interface ISocialDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface ISocialDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemSocialDnsResolver : ISocialDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(
        string host,
        CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class SystemSocialDelay : ISocialDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class SocialDnsPinningValidator(ISocialDnsResolver resolver)
{
    public async Task<IPAddress> ResolvePublicAsync(
        string expectedHost,
        string requestedHost,
        CancellationToken cancellationToken)
    {
        if (!expectedHost.Equals(requestedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("The connection host is not approved.");
        }

        var addresses = await resolver.ResolveAsync(requestedHost, cancellationToken)
            .ConfigureAwait(false);
        if (addresses.Length == 0 ||
            addresses.Any(address => !SocialFeedValidation.IsPublicNetworkAddress(address)))
        {
            throw new HttpRequestException("The provider host did not resolve to public addresses.");
        }

        return addresses
            .OrderBy(address => address.AddressFamily)
            .ThenBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
            .First();
    }
}

public sealed class SocialProviderHttpClientFactory : ISocialProviderHttpClientFactory, IDisposable
{
    private readonly IReadOnlyDictionary<string, SocialProviderSettings> settings;
    private readonly TimeProvider timeProvider;
    private readonly ISocialDnsResolver dnsResolver;
    private readonly ISocialDelay delay;
    private readonly ConcurrentDictionary<string, SocialProviderHttpClient> clients =
        new(StringComparer.Ordinal);

    public SocialProviderHttpClientFactory(
        IReadOnlyDictionary<string, SocialProviderSettings> settings,
        TimeProvider timeProvider,
        ISocialDnsResolver dnsResolver,
        ISocialDelay delay)
    {
        this.settings = settings;
        this.timeProvider = timeProvider;
        this.dnsResolver = dnsResolver;
        this.delay = delay;
    }

    public Task<Result<HttpResponseMessage, SocialSourceError>> SendGetAsync(
        SocialProviderSettings providerSettings,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        if (!settings.TryGetValue(providerSettings.Provider, out var configured) ||
            !ReferenceEquals(configured, providerSettings))
        {
            return Task.FromResult(Result.Fail<HttpResponseMessage, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.Unavailable)));
        }

        var client = clients.GetOrAdd(
            providerSettings.Provider,
            _ => CreateClient(providerSettings));
        return client.SendGetAsync(bearerToken, cancellationToken);
    }

    public void Dispose()
    {
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }
    }

    private SocialProviderHttpClient CreateClient(SocialProviderSettings providerSettings)
    {
        var pinning = new SocialDnsPinningValidator(dnsResolver);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = providerSettings.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var address = await pinning.ResolvePublicAsync(
                    providerSettings.Endpoint.IdnHost,
                    context.DnsEndPoint.Host,
                    cancellationToken).ConfigureAwait(false);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new SocialProviderHttpClient(
            providerSettings,
            new HttpMessageInvoker(handler, disposeHandler: true),
            timeProvider,
            delay);
    }
}

public sealed class SocialProviderHttpClient : IDisposable
{
    private readonly SocialProviderSettings settings;
    private readonly HttpMessageInvoker invoker;
    private readonly TimeProvider timeProvider;
    private readonly ISocialDelay delay;
    private readonly object circuitLock = new();
    private int consecutiveFailures;
    private DateTimeOffset? openUntilUtc;
    private bool halfOpenProbe;

    public SocialProviderHttpClient(
        SocialProviderSettings settings,
        HttpMessageInvoker invoker,
        TimeProvider timeProvider,
        ISocialDelay delay)
    {
        this.settings = settings;
        this.invoker = invoker;
        this.timeProvider = timeProvider;
        this.delay = delay;
    }

    public async Task<Result<HttpResponseMessage, SocialSourceError>> SendGetAsync(
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var startedAtUtc = timeProvider.GetUtcNow();
        if (!TryEnterCircuit(startedAtUtc, out var retryAfter))
        {
            return Result.Fail<HttpResponseMessage, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.CircuitOpen, retryAfter));
        }

        for (var attempt = 1; attempt <= settings.MaximumAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.RequestTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint);
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    request.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", bearerToken);
                }

                var response = await invoker.SendAsync(request, timeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    RecordSuccess();
                    return Result.Succeed<HttpResponseMessage, SocialSourceError>(response);
                }

                var transient = IsTransient(response.StatusCode);
                var error = ToError(response, timeProvider.GetUtcNow());
                if (!transient)
                {
                    response.Dispose();
                    RecordSuccess();
                    return Result.Fail<HttpResponseMessage, SocialSourceError>(error);
                }

                if (attempt == settings.MaximumAttempts ||
                    !TryGetRetryDelay(
                        response,
                        attempt,
                        timeProvider.GetUtcNow(),
                        out var retryDelay))
                {
                    response.Dispose();
                    RecordFailure(timeProvider.GetUtcNow());
                    return Result.Fail<HttpResponseMessage, SocialSourceError>(error);
                }

                response.Dispose();
                await DelayForRetryAsync(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReleaseHalfOpenProbe();
                throw;
            }
            catch (OperationCanceledException)
            {
                if (attempt == settings.MaximumAttempts)
                {
                    RecordFailure(timeProvider.GetUtcNow());
                    return Result.Fail<HttpResponseMessage, SocialSourceError>(
                        new SocialSourceError(SocialSourceErrorKind.Timeout));
                }

                await DelayForRetryAsync(
                    RetryDelay(attempt),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                if (attempt == settings.MaximumAttempts)
                {
                    RecordFailure(timeProvider.GetUtcNow());
                    return Result.Fail<HttpResponseMessage, SocialSourceError>(
                        new SocialSourceError(SocialSourceErrorKind.Unavailable));
                }

                await DelayForRetryAsync(
                    RetryDelay(attempt),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        RecordFailure(timeProvider.GetUtcNow());
        return Result.Fail<HttpResponseMessage, SocialSourceError>(
            new SocialSourceError(SocialSourceErrorKind.Unavailable));
    }

    public void Dispose() => invoker.Dispose();

    private bool TryEnterCircuit(
        DateTimeOffset now,
        out DateTimeOffset? retryAfter)
    {
        lock (circuitLock)
        {
            retryAfter = openUntilUtc;
            if (openUntilUtc > now)
            {
                return false;
            }

            if (openUntilUtc is not null)
            {
                if (halfOpenProbe)
                {
                    return false;
                }

                halfOpenProbe = true;
            }

            return true;
        }
    }

    private void RecordSuccess()
    {
        lock (circuitLock)
        {
            consecutiveFailures = 0;
            openUntilUtc = null;
            halfOpenProbe = false;
        }
    }

    private void RecordFailure(DateTimeOffset now)
    {
        lock (circuitLock)
        {
            consecutiveFailures++;
            halfOpenProbe = false;
            if (consecutiveFailures >= settings.CircuitFailureThreshold)
            {
                openUntilUtc = now + settings.CircuitBreakDuration;
            }
        }
    }

    private void ReleaseHalfOpenProbe()
    {
        lock (circuitLock)
        {
            halfOpenProbe = false;
        }
    }

    private async Task DelayForRetryAsync(
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await delay.DelayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseHalfOpenProbe();
            throw;
        }
    }

    private bool TryGetRetryDelay(
        HttpResponseMessage response,
        int attempt,
        DateTimeOffset now,
        out TimeSpan retryDelay)
    {
        retryDelay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date - now) ??
            RetryDelay(attempt);
        if (retryDelay < TimeSpan.Zero)
        {
            retryDelay = TimeSpan.Zero;
        }

        return retryDelay <= settings.MaximumRetryDelay;
    }

    private TimeSpan RetryDelay(int attempt)
    {
        if (settings.MaximumRetryDelay == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var ceiling = Math.Min(
            settings.MaximumRetryDelay.TotalMilliseconds,
            100 * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static SocialSourceError ToError(
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Date ??
                (response.Headers.RetryAfter?.Delta is { } delta ? now + delta : null);
            return new SocialSourceError(SocialSourceErrorKind.RateLimited, retryAfter);
        }

        return new SocialSourceError(SocialSourceErrorKind.Unavailable);
    }
}
