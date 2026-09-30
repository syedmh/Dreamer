using System.Net;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Social;

namespace Husaynia.IntegrationTests.Social;

public sealed class SocialHttpResilienceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TransientGetRetriesOnceAndUsesSinglePolicyLayer()
    {
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        var delay = new RecordingDelay();
        using var client = Client(handler, delay);

        var result = await client.SendGetAsync("secret", CancellationToken.None);

        Assert.True(result.IsSuccess);
        result.Success.Dispose();
        Assert.Equal(2, handler.Calls);
        Assert.Single(delay.Delays);
        Assert.All(handler.AuthorizationParameters, value => Assert.Equal("secret", value));
        Assert.All(handler.RequestUris, uri =>
            Assert.Equal("https://api.facebook.example/feed", uri.AbsoluteUri));
    }

    [Fact]
    public async Task LongRetryAfterIsRespectedWithoutRetry()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromMinutes(5));
        var handler = new SequenceHandler(response);
        var delay = new RecordingDelay();
        using var client = Client(handler, delay, maximumRetryDelay: TimeSpan.FromSeconds(2));

        var result = await client.SendGetAsync(null, CancellationToken.None);

        Assert.Equal(SocialSourceErrorKind.RateLimited, result.Error.Kind);
        Assert.Equal(Now.AddMinutes(5), result.Error.RetryAfterUtc);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task RedirectIsNotFollowedAndBearerTokenNeverReachesRedirectOrigin()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://attacker.example/steal");
        var handler = new SequenceHandler(redirect);
        using var client = Client(handler, new RecordingDelay());

        var result = await client.SendGetAsync("secret", CancellationToken.None);

        Assert.Equal(SocialSourceErrorKind.Unavailable, result.Error.Kind);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.facebook.example/feed", Assert.Single(handler.RequestUris).AbsoluteUri);
        Assert.Equal("secret", Assert.Single(handler.AuthorizationParameters));
    }

    [Fact]
    public async Task CircuitOpensDeterministicallyAndHalfOpenSuccessRecovers()
    {
        var clock = new MutableTimeProvider(Now);
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        using var client = Client(
            handler,
            new RecordingDelay(),
            maximumAttempts: 1,
            circuitFailureThreshold: 2,
            circuitBreakDuration: TimeSpan.FromMinutes(1),
            clock: clock);

        Assert.True((await client.SendGetAsync(null, CancellationToken.None)).IsFailure);
        Assert.True((await client.SendGetAsync(null, CancellationToken.None)).IsFailure);
        var open = await client.SendGetAsync(null, CancellationToken.None);
        Assert.Equal(SocialSourceErrorKind.CircuitOpen, open.Error.Kind);
        Assert.Equal(2, handler.Calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await client.SendGetAsync(null, CancellationToken.None);
        Assert.True(recovered.IsSuccess);
        recovered.Success.Dispose();
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationStopsRetries()
    {
        var handler = new BlockingHandler();
        using var client = Client(handler, new RecordingDelay());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendGetAsync(null, cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.88.99.1")]
    [InlineData("198.51.100.1")]
    [InlineData("203.0.113.1")]
    [InlineData("::1")]
    [InlineData("::127.0.0.1")]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("::ffff:0:93.184.216.34")]
    [InlineData("fe80::1")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("ff02::1")]
    [InlineData("100::1")]
    [InlineData("64:ff9b::1")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2001::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2002::1")]
    [InlineData("3fff::1")]
    public void PrivateReservedLoopbackLinkLocalAndMulticastAddressesAreRejected(
        string value) =>
        Assert.False(SocialFeedValidation.IsPublicNetworkAddress(IPAddress.Parse(value)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("192.0.1.1")]
    [InlineData("198.51.101.1")]
    [InlineData("203.0.114.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4009:81a::200e")]
    public void RepresentativePublicAddressesAreAccepted(string value) =>
        Assert.True(SocialFeedValidation.IsPublicNetworkAddress(IPAddress.Parse(value)));

    [Fact]
    public async Task DnsPinningRejectsMixedPublicPrivateAnswersAndUnexpectedHost()
    {
        var validator = new SocialDnsPinningValidator(new StubDnsResolver(
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Loopback));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            validator.ResolvePublicAsync(
                "api.facebook.example",
                "api.facebook.example",
                CancellationToken.None));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            validator.ResolvePublicAsync(
                "api.facebook.example",
                "attacker.example",
                CancellationToken.None));
    }

    [Theory]
    [InlineData("http://api.facebook.example/feed")]
    [InlineData("https://api.facebook.example:444/feed")]
    [InlineData("https://user:pass@api.facebook.example/feed")]
    [InlineData("https://127.0.0.1/feed")]
    public void UnsafeProviderOriginsAreRejected(string endpoint) =>
        Assert.Throws<ArgumentException>(() =>
            new SocialProviderSettings(
                "facebook",
                new Uri(endpoint),
                new HashSet<string> { "api.facebook.example" },
                new HashSet<string> { "media.example" }));

    private static SocialProviderHttpClient Client(
        HttpMessageHandler handler,
        ISocialDelay delay,
        int maximumAttempts = 2,
        int circuitFailureThreshold = 3,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? circuitBreakDuration = null,
        TimeProvider? clock = null) =>
        new(
            JsonSocialFeedProviderTests.Settings(
                maximumAttempts: maximumAttempts,
                circuitFailureThreshold: circuitFailureThreshold,
                maximumRetryDelay: maximumRetryDelay ?? TimeSpan.FromSeconds(2),
                circuitBreakDuration: circuitBreakDuration),
            new HttpMessageInvoker(handler, disposeHandler: true),
            clock ?? new MutableTimeProvider(Now),
            delay);

    private sealed class SequenceHandler(params HttpResponseMessage[] responses)
        : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> remaining = new(responses);

        internal int Calls { get; private set; }
        internal List<string?> AuthorizationParameters { get; } = [];
        internal List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(remaining.Dequeue());
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class RecordingDelay : ISocialDelay
    {
        internal List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class StubDnsResolver(params IPAddress[] addresses) : ISocialDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(
            string host,
            CancellationToken cancellationToken) =>
            Task.FromResult(addresses);
    }
}
