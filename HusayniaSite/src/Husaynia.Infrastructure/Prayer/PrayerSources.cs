using System.Globalization;
using System.Net;
using System.Text.Json;
using Husaynia.Application.Contracts;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Prayer;

namespace Husaynia.Infrastructure.Prayer;

public interface IPrayerExternalClient
{
    Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
        PrayerSourceRequest request,
        CancellationToken cancellationToken);
}

public sealed class ConfiguredPrayerSource(
    IPrayerProfileDefinitionReader profiles,
    DeterministicPrayerCalculator calculator,
    IPrayerExternalClient externalClient,
    TimeProvider timeProvider) : IPrayerSource
{
    private readonly IPrayerProfileDefinitionReader profiles =
        profiles ?? throw new ArgumentNullException(nameof(profiles));
    private readonly DeterministicPrayerCalculator calculator =
        calculator ?? throw new ArgumentNullException(nameof(calculator));
    private readonly IPrayerExternalClient externalClient =
        externalClient ?? throw new ArgumentNullException(nameof(externalClient));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
        PrayerSourceRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profileResult = await profiles.ReadProfileAsync(request.ProfileHash, ct)
            .ConfigureAwait(false);
        if (profileResult.IsFailure)
        {
            return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new("profile_not_found", "The prayer profile is unavailable."));
        }

        var definition = profileResult.Success;
        if (definition.ProviderKind == "external")
        {
            return await externalClient.GetAsync(request, ct).ConfigureAwait(false);
        }

        try
        {
            var profile = PrayerProfile.Create(
                definition.ProviderKind,
                definition.Latitude,
                definition.Longitude,
                definition.MethodJson,
                definition.AlgorithmVersion,
                definition.TimeZoneId,
                definition.EffectiveFrom,
                "prayer-calculator",
                timeProvider.GetUtcNow());
            if (!string.Equals(
                    profile.ProfileHash,
                    request.ProfileHash,
                    StringComparison.Ordinal))
            {
                return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                    new("malformed", "The prayer profile hash is inconsistent."));
            }

            var days = new List<PrayerDay>(
                DateTime.DaysInMonth(request.Month.Year, request.Month.Month));
            for (var day = 1;
                 day <= DateTime.DaysInMonth(request.Month.Year, request.Month.Month);
                 day++)
            {
                var calculated = calculator.Calculate(
                    profile,
                    new DateOnly(request.Month.Year, request.Month.Month, day));
                days.Add(new PrayerDay(
                    calculated.Date,
                    PrayerKeys.All
                        .Select(key => new PrayerTime(key, calculated.Times[key]))
                        .ToArray(),
                    false));
            }

            return Result.Succeed<PrayerSourceSnapshot, IntegrationError>(
                new(
                    new PrayerSchedule(
                        request.Month,
                        PrayerTimeZone.IanaId,
                        days,
                        timeProvider.GetUtcNow().ToUniversalTime(),
                        false),
                    "calculated"));
        }
        catch (ArgumentException)
        {
            return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new("malformed", "The prayer profile settings are invalid."));
        }
        catch (InvalidOperationException)
        {
            return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new("unavailable", "Prayer times cannot be calculated for the requested month."));
        }
    }
}

public sealed class HttpPrayerExternalClient(
    PrayerOptions options,
    HttpClient httpClient,
    TimeProvider timeProvider) : IPrayerExternalClient, IDisposable
{
    private readonly PrayerOptions options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly TimeProvider timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public HttpPrayerExternalClient(PrayerOptions options, HttpClient httpClient)
        : this(options, httpClient, TimeProvider.System)
    {
    }

    public async Task<Result<PrayerSourceSnapshot, IntegrationError>> GetAsync(
        PrayerSourceRequest request,
        CancellationToken cancellationToken)
    {
        if (!options.ExternalProviderEnabled ||
            options.ExternalProviderEndpoint is null)
        {
            return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new("not_configured", "The prayer provider is not configured."));
        }

        var endpoint = options.ExternalProviderEndpoint;
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !options.ExternalProviderAllowedHosts.Contains(endpoint.IdnHost))
        {
            return Result.Fail<PrayerSourceSnapshot, IntegrationError>(
                new("not_configured", "The prayer provider is not configured."));
        }

        var uri = new UriBuilder(endpoint)
        {
            Query = string.Join(
                '&',
                $"profileHash={Uri.EscapeDataString(request.ProfileHash)}",
                $"year={request.Month.Year.ToString(CultureInfo.InvariantCulture)}",
                $"month={request.Month.Month.ToString(CultureInfo.InvariantCulture)}",
                $"timeZoneId={Uri.EscapeDataString(request.TimeZoneId)}"),
        }.Uri;
        try
        {
            using var response = await httpClient.GetAsync(
                    uri,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.RequestTimeout or
                HttpStatusCode.GatewayTimeout)
            {
                return Timeout();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Unavailable();
            }

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 128_000)
            {
                return Malformed();
            }

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var boundedStream = new MemoryStream();
            var buffer = new byte[4_096];
            while (true)
            {
                var read = await responseStream.ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (boundedStream.Length + read > 128_000)
                {
                    return Malformed();
                }

                await boundedStream.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var responseModel = JsonSerializer.Deserialize<ExternalPrayerResponse>(
                boundedStream.ToArray(),
                PrayerJson.Strict);
            return responseModel is null
                ? Malformed()
                : ToSnapshot(request, responseModel);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Timeout();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (JsonException)
        {
            return Malformed();
        }
    }

    public void Dispose() => httpClient.Dispose();

    private Result<PrayerSourceSnapshot, IntegrationError> ToSnapshot(
        PrayerSourceRequest request,
        ExternalPrayerResponse response)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var generatedAtUtc = response.GeneratedAtUtc.ToUniversalTime();
        if (generatedAtUtc == default ||
            generatedAtUtc > now + options.ExternalProviderClockSkew ||
            response.Days is null ||
            response.Days.Count != DateTime.DaysInMonth(
                request.Month.Year,
                request.Month.Month) ||
            response.Days.Any(day =>
                day.Date.Year != request.Month.Year ||
                day.Date.Month != request.Month.Month ||
                day.Times is null))
        {
            return Malformed();
        }

        if (generatedAtUtc > now)
        {
            generatedAtUtc = now;
        }

        var days = response.Days.Select(day =>
            new PrayerDay(
                day.Date,
                day.Times?
                    .Select(pair => new PrayerTime(pair.Key, pair.Value))
                    .ToArray() ?? [],
                false)).ToArray();
        return Result.Succeed<PrayerSourceSnapshot, IntegrationError>(
            new(
                new PrayerSchedule(
                    request.Month,
                    PrayerTimeZone.IanaId,
                    days,
                    generatedAtUtc,
                    false),
                "external"));
    }

    private static Result<PrayerSourceSnapshot, IntegrationError> Timeout() =>
        Result.Fail<PrayerSourceSnapshot, IntegrationError>(
            new("timeout", "The prayer provider timed out."));

    private static Result<PrayerSourceSnapshot, IntegrationError> Unavailable() =>
        Result.Fail<PrayerSourceSnapshot, IntegrationError>(
            new("unavailable", "The prayer provider is unavailable."));

    private static Result<PrayerSourceSnapshot, IntegrationError> Malformed() =>
        Result.Fail<PrayerSourceSnapshot, IntegrationError>(
            new("malformed", "The prayer provider returned an invalid schedule."));

    private sealed record ExternalPrayerResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("generatedAtUtc")]
        DateTimeOffset GeneratedAtUtc,
        [property: System.Text.Json.Serialization.JsonPropertyName("days")]
        IReadOnlyList<ExternalPrayerDay>? Days);

    private sealed record ExternalPrayerDay(
        [property: System.Text.Json.Serialization.JsonPropertyName("date")]
        DateOnly Date,
        [property: System.Text.Json.Serialization.JsonPropertyName("times")]
        IReadOnlyDictionary<string, TimeOnly>? Times);

    private static class PrayerJson
    {
        internal static JsonSerializerOptions Strict { get; } = new()
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        };
    }
}
