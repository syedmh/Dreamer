using System.Globalization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Prayer;
using Husaynia.Domain.Prayer;
using Microsoft.Extensions.Configuration;

namespace Husaynia.Infrastructure.Prayer;

public sealed class PrayerConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return PrayerConfiguration.Validate(configuration);
    }
}

internal static class PrayerConfiguration
{
    internal static PrayerOptions Read(IConfiguration configuration)
    {
        var section = PrayerOptions.SectionName;
        var allowedHosts = configuration
            .GetSection($"{section}:ExternalProvider:AllowedHosts")
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PrayerOptions
        {
            TimeZoneId = configuration[$"{section}:TimeZoneId"]?.Trim() ??
                PrayerTimeZone.IanaId,
            SnapshotMaxAge = ReadDuration(
                configuration[$"{section}:SnapshotMaxAge"],
                TimeSpan.FromHours(36)),
            GenerateMonthsAhead = ReadInteger(
                configuration[$"{section}:GenerateMonthsAhead"],
                2),
            ExternalProviderEnabled = ReadBoolean(
                configuration[$"{section}:ExternalProvider:Enabled"]),
            ExternalProviderEndpoint = Uri.TryCreate(
                configuration[$"{section}:ExternalProvider:Endpoint"],
                UriKind.Absolute,
                out var endpoint)
                    ? endpoint
                    : null,
            ExternalProviderTimeout = ReadDuration(
                configuration[$"{section}:ExternalProvider:Timeout"],
                TimeSpan.FromSeconds(10)),
            ExternalProviderClockSkew = ReadDuration(
                configuration[$"{section}:ExternalProvider:ClockSkew"],
                TimeSpan.FromMinutes(5)),
            ExternalProviderAllowedHosts = allowedHosts,
            ExternalProviderTestMode = ReadBoolean(
                configuration[$"{section}:ExternalProvider:TestMode"]),
        };
    }

    internal static IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        var failures = new List<string>();
        var section = PrayerOptions.SectionName;
        var zoneValue = configuration[$"{section}:TimeZoneId"];
        if (zoneValue is not null &&
            !string.Equals(zoneValue.Trim(), PrayerTimeZone.IanaId, StringComparison.Ordinal))
        {
            failures.Add($"Prayer:TimeZoneId must be {PrayerTimeZone.IanaId}.");
        }

        ValidateDuration(
            configuration,
            $"{section}:SnapshotMaxAge",
            TimeSpan.FromHours(1),
            TimeSpan.FromDays(7),
            failures);
        ValidateInteger(
            configuration,
            $"{section}:GenerateMonthsAhead",
            1,
            6,
            failures);
        ValidateBoolean(
            configuration,
            $"{section}:ExternalProvider:Enabled",
            failures);
        ValidateBoolean(
            configuration,
            $"{section}:ExternalProvider:TestMode",
            failures);
        ValidateDuration(
            configuration,
            $"{section}:ExternalProvider:Timeout",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(60),
            failures);
        ValidateDuration(
            configuration,
            $"{section}:ExternalProvider:ClockSkew",
            TimeSpan.Zero,
            TimeSpan.FromMinutes(15),
            failures);

        var enabledValue = configuration[$"{section}:ExternalProvider:Enabled"];
        if (!bool.TryParse(enabledValue, out var enabled) || !enabled)
        {
            return failures;
        }

        var options = Read(configuration);
        if (options.ExternalProviderEndpoint is not { IsAbsoluteUri: true } endpoint ||
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            failures.Add(
                "Prayer:ExternalProvider:Endpoint must be a credential-free allowlisted HTTPS URI without query or fragment.");
        }
        else if (!options.ExternalProviderAllowedHosts.Contains(endpoint.IdnHost))
        {
            failures.Add(
                "Prayer:ExternalProvider:Endpoint host must be listed in Prayer:ExternalProvider:AllowedHosts.");
        }

        if (options.ExternalProviderAllowedHosts.Count == 0 ||
            options.ExternalProviderAllowedHosts.Any(host =>
                host.Length > 253 ||
                Uri.CheckHostName(host) is UriHostNameType.Unknown ||
                host.Contains('*')))
        {
            failures.Add(
                "Prayer:ExternalProvider:AllowedHosts must contain exact valid host names.");
        }

        return failures;
    }

    private static void ValidateDuration(
        IConfiguration configuration,
        string key,
        TimeSpan minimum,
        TimeSpan maximum,
        List<string> failures)
    {
        var raw = configuration[key];
        if (raw is null)
        {
            return;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            failures.Add(
                $"{key} must be a TimeSpan from {minimum:c} through {maximum:c}.");
        }
    }

    private static void ValidateInteger(
        IConfiguration configuration,
        string key,
        int minimum,
        int maximum,
        List<string> failures)
    {
        var raw = configuration[key];
        if (raw is null)
        {
            return;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < minimum ||
            value > maximum)
        {
            failures.Add($"{key} must be from {minimum} through {maximum}.");
        }
    }

    private static void ValidateBoolean(
        IConfiguration configuration,
        string key,
        List<string> failures)
    {
        var raw = configuration[key];
        if (raw is not null && !bool.TryParse(raw, out _))
        {
            failures.Add($"{key} must be true or false.");
        }
    }

    private static TimeSpan ReadDuration(string? raw, TimeSpan fallback) =>
        TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static int ReadInteger(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool ReadBoolean(string? raw) =>
        bool.TryParse(raw, out var value) && value;
}
