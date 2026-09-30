using System.Net;
using System.Text;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Social;
using Husaynia.Domain.Social;
using Husaynia.Infrastructure.Social;

namespace Husaynia.IntegrationTests.Social;

public sealed class JsonSocialFeedProviderTests
{
    [Fact]
    public async Task HealthyResponseCarriesNormalizedMediaContractAndInjectedCredential()
    {
        const string json =
            """
            {
              "fetchedAtUtc": "2026-08-20T12:00:00Z",
              "items": [{
                "externalId": "post-1",
                "text": "Update",
                "sourceLink": "https://facebook.example/posts/1",
                "publishedAtUtc": "2026-08-20T11:00:00Z",
                "media": {
                  "type": "Video",
                  "url": "https://media.example/video.mp4",
                  "thumbnailUrl": "https://media.example/poster.jpg",
                  "width": 640,
                  "height": 360,
                  "durationSeconds": 30,
                  "altText": "Video alt",
                  "caption": "Video caption"
                }
              }]
            }
            """;
        var factory = new StubFactory(SuccessResponse(json));
        var provider = CreateProvider(factory, new StaticCredentialSource("secret-token"));

        var result = await provider.FetchAsync(
            new("facebook", 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Success.Items);
        Assert.Equal(SocialMediaType.Video, item.Media!.Type);
        Assert.Equal(640, item.Media.Width);
        Assert.Equal("secret-token", factory.BearerToken);
    }

    [Fact]
    public async Task TransportRateLimitPassesStableStructuredErrorWithoutDiagnostics()
    {
        var retryAfter = new DateTimeOffset(2026, 8, 20, 12, 5, 0, TimeSpan.Zero);
        var factory = new StubFactory(
            Result.Fail<HttpResponseMessage, SocialSourceError>(
                new SocialSourceError(SocialSourceErrorKind.RateLimited, retryAfter)));
        var provider = CreateProvider(factory, new StaticCredentialSource(null));

        var result = await provider.FetchAsync(
            new("facebook", 10),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(SocialSourceErrorKind.RateLimited, result.Error.Kind);
        Assert.Equal(retryAfter, result.Error.RetryAfterUtc);
    }

    [Fact]
    public async Task MalformedAndOversizedResponsesAreRejected()
    {
        var malformed = await CreateProvider(
            new StubFactory(SuccessResponse("{not-json")),
            new StaticCredentialSource(null)).FetchAsync(
                new("facebook", 10),
                CancellationToken.None);
        var oversized = await CreateProvider(
            new StubFactory(Result.Succeed<HttpResponseMessage, SocialSourceError>(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[2_000]),
                })),
            new StaticCredentialSource(null),
            maximumResponseBytes: 1_024).FetchAsync(
                new("facebook", 10),
                CancellationToken.None);

        Assert.Equal(SocialSourceErrorKind.Malformed, malformed.Error.Kind);
        Assert.Equal(SocialSourceErrorKind.Malformed, oversized.Error.Kind);
    }

    [Fact]
    public async Task ProviderItemCountAcceptsMaximumAndRejectsMaximumPlusOne()
    {
        const int maximumItems = 2;
        var atMaximum = await CreateProvider(
            new StubFactory(SuccessResponse(Payload(maximumItems))),
            new StaticCredentialSource(null)).FetchAsync(
                new("facebook", maximumItems),
                CancellationToken.None);
        var overMaximum = await CreateProvider(
            new StubFactory(SuccessResponse(Payload(maximumItems + 1))),
            new StaticCredentialSource(null)).FetchAsync(
                new("facebook", maximumItems),
                CancellationToken.None);

        Assert.True(atMaximum.IsSuccess);
        Assert.Equal(maximumItems, atMaximum.Success.Items.Count);
        Assert.True(overMaximum.IsFailure);
        Assert.Equal(SocialSourceErrorKind.Malformed, overMaximum.Error.Kind);
    }

    private static JsonSocialFeedProvider CreateProvider(
        ISocialProviderHttpClientFactory factory,
        ISocialProviderCredentialSource credentials,
        int maximumResponseBytes = 1_000_000) =>
        new(
            factory,
            Settings(maximumResponseBytes),
            credentials);

    internal static SocialProviderSettings Settings(
        int maximumResponseBytes = 1_000_000,
        int maximumAttempts = 2,
        int circuitFailureThreshold = 3,
        TimeSpan? maximumRetryDelay = null,
        TimeSpan? circuitBreakDuration = null) =>
        new(
            "facebook",
            new Uri("https://api.facebook.example/feed"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "api.facebook.example",
                "facebook.example",
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "media.example",
            },
            maximumResponseBytes,
            maximumAttempts: maximumAttempts,
            maximumRetryDelay: maximumRetryDelay,
            circuitFailureThreshold: circuitFailureThreshold,
            circuitBreakDuration: circuitBreakDuration);

    private static Result<HttpResponseMessage, SocialSourceError> SuccessResponse(string json) =>
        Result.Succeed<HttpResponseMessage, SocialSourceError>(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });

    private static string Payload(int itemCount) =>
        JsonSerializer.Serialize(new
        {
            fetchedAtUtc = new DateTimeOffset(
                2026,
                8,
                20,
                12,
                0,
                0,
                TimeSpan.Zero),
            items = Enumerable.Range(0, itemCount).Select(index => new
            {
                externalId = $"post-{index}",
                text = $"Update {index}",
                sourceLink = $"https://facebook.example/posts/{index}",
                publishedAtUtc = new DateTimeOffset(
                    2026,
                    8,
                    20,
                    11,
                    0,
                    0,
                    TimeSpan.Zero),
            }),
        });

    private sealed class StubFactory(
        Result<HttpResponseMessage, SocialSourceError> result)
        : ISocialProviderHttpClientFactory
    {
        internal string? BearerToken { get; private set; }

        public Task<Result<HttpResponseMessage, SocialSourceError>> SendGetAsync(
            SocialProviderSettings providerSettings,
            string? bearerToken,
            CancellationToken cancellationToken)
        {
            BearerToken = bearerToken;
            return Task.FromResult(result);
        }
    }

    internal sealed class StaticCredentialSource(string? token)
        : ISocialProviderCredentialSource
    {
        public ValueTask<string?> GetBearerTokenAsync(
            string provider,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(token);
    }
}
