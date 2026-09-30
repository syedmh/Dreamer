using System.Net;
using System.Text;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Prayer;
using Husaynia.Infrastructure.Prayer;

namespace Husaynia.IntegrationTests.Prayer;

public sealed class PrayerProviderIsolationTests
{
    [Fact]
    public async Task ProviderTimeoutMapsToSanitizedBoundedError()
    {
        var options = ProviderOptions(TimeSpan.FromMilliseconds(50));
        using var client = new HttpClient(new CancellingHandler())
        {
            Timeout = options.ExternalProviderTimeout,
        };
        using var provider = new HttpPrayerExternalClient(options, client);

        var result = await provider.GetAsync(
            new(
                new YearMonth(2026, 3),
                "America/Los_Angeles",
                new string('A', 64)),
            CancellationToken.None);

        Assert.Equal("timeout", result.Error.Code);
        Assert.DoesNotContain("provider.example.test", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedProviderBodyDoesNotExposeDiagnostics()
    {
        var options = ProviderOptions(TimeSpan.FromSeconds(1));
        using var client = new HttpClient(
            new StaticHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{secret-body", Encoding.UTF8, "application/json"),
                }))
        {
            Timeout = options.ExternalProviderTimeout,
        };
        using var provider = new HttpPrayerExternalClient(options, client);

        var result = await provider.GetAsync(
            new(
                new YearMonth(2026, 3),
                "America/Los_Angeles",
                new string('A', 64)),
            CancellationToken.None);

        Assert.Equal("malformed", result.Error.Code);
        Assert.DoesNotContain("secret-body", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FarFutureProviderTimestampIsRejected()
    {
        var response = ValidResponse(DateTimeOffset.UtcNow.AddDays(1));
        var options = ProviderOptions(TimeSpan.FromSeconds(1));
        using var client = new HttpClient(new StaticHandler(response))
        {
            Timeout = options.ExternalProviderTimeout,
        };
        using var provider = new HttpPrayerExternalClient(options, client);

        var result = await provider.GetAsync(
            new(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                new string('A', 64)),
            CancellationToken.None);

        Assert.Equal("malformed", result.Error.Code);
    }

    [Fact]
    public async Task NearFutureProviderTimestampIsClampedToCurrentClock()
    {
        var before = DateTimeOffset.UtcNow;
        var response = ValidResponse(before.AddMinutes(1));
        var options = ProviderOptions(TimeSpan.FromSeconds(1));
        using var client = new HttpClient(new StaticHandler(response))
        {
            Timeout = options.ExternalProviderTimeout,
        };
        using var provider = new HttpPrayerExternalClient(options, client);

        var result = await provider.GetAsync(
            new(
                new YearMonth(2026, 3),
                PrayerTimeZone.IanaId,
                new string('A', 64)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.InRange(
            result.Success.Schedule.GeneratedAtUtc,
            before,
            DateTimeOffset.UtcNow);
    }

    private static PrayerOptions ProviderOptions(TimeSpan timeout) =>
        new()
        {
            ExternalProviderEnabled = true,
            ExternalProviderEndpoint = new Uri("https://provider.example.test/schedule"),
            ExternalProviderAllowedHosts =
                new HashSet<string>(["provider.example.test"], StringComparer.OrdinalIgnoreCase),
            ExternalProviderTimeout = timeout,
        };

    private static HttpResponseMessage ValidResponse(DateTimeOffset generatedAtUtc)
    {
        var times = new Dictionary<string, TimeOnly>(StringComparer.Ordinal)
        {
            [PrayerKeys.Fajr] = new(5, 0),
            [PrayerKeys.Sunrise] = new(6, 30),
            [PrayerKeys.Dhuhr] = new(12, 15),
            [PrayerKeys.Asr] = new(15, 30),
            [PrayerKeys.Maghrib] = new(18, 0),
            [PrayerKeys.Isha] = new(19, 30),
        };
        var body = JsonSerializer.Serialize(new
        {
            generatedAtUtc,
            days = Enumerable.Range(1, 31).Select(day => new
            {
                date = new DateOnly(2026, 3, day),
                times,
            }),
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
