using System.Globalization;
using Husaynia.Application.Contracts;
using Husaynia.Application.Forms;
using Microsoft.Extensions.Configuration;

namespace Husaynia.Infrastructure.Forms;

public sealed class FormsConfigurationValidator : IHusayniaConfigurationValidator
{
    public IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return FormsConfiguration.Validate(configuration);
    }
}

internal static class FormsConfiguration
{
    internal static FormsOptions Read(IConfiguration configuration)
    {
        var failures = Validate(configuration);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Forms configuration is invalid: {string.Join(" ", failures)}");
        }

        var enabled = ReadBoolean(configuration["Forms:Enabled"], defaultValue: false);
        return new FormsOptions
        {
            Enabled = enabled,
            FingerprintKey = enabled
                ? Convert.FromBase64String(configuration["Forms:FingerprintKey"]!)
                : [],
            DuplicateWindow = ReadDuration(
                configuration["Forms:DuplicateWindow"],
                TimeSpan.FromMinutes(15)),
            RateLimitPermitLimit = ReadInteger(
                configuration["Forms:RateLimit:PermitLimit"],
                10),
            RateLimitWindow = ReadDuration(
                configuration["Forms:RateLimit:Window"],
                TimeSpan.FromMinutes(5)),
            RateLimitRetention = ReadDuration(
                configuration["Forms:RateLimit:Retention"],
                TimeSpan.FromDays(1)),
            RetentionPeriod = ReadDuration(
                configuration["Forms:RetentionPeriod"],
                TimeSpan.FromDays(90)),
            MaximumRequestBytes = ReadInteger(
                configuration["Forms:MaximumRequestBytes"],
                64_000),
            MaximumFieldCount = ReadInteger(
                configuration["Forms:MaximumFieldCount"],
                50),
            MaximumFieldValueLength = ReadInteger(
                configuration["Forms:MaximumFieldValueLength"],
                4_000),
            MaximumTotalValueLength = ReadInteger(
                configuration["Forms:MaximumTotalValueLength"],
                16_000),
            DeliveryMode = Enum.TryParse<FormDeliveryMode>(
                configuration["Forms:Delivery:Mode"],
                ignoreCase: false,
                out var mode)
                    ? mode
                    : FormDeliveryMode.Disabled,
            PickupDirectory = configuration["Forms:Delivery:PickupDirectory"] ?? string.Empty,
            PickupDestinationKey =
                configuration["Forms:Delivery:PickupDestinationKey"] ?? string.Empty,
        };
    }

    internal static IReadOnlyCollection<string> Validate(IConfiguration configuration)
    {
        var failures = new List<string>();
        var enabledValue = configuration["Forms:Enabled"];
        if (enabledValue is not null && !bool.TryParse(enabledValue, out _))
        {
            failures.Add("Forms:Enabled must be true or false.");
        }

        var enabled = ReadBoolean(enabledValue, defaultValue: false);
        ValidateFingerprintKey(configuration, failures, required: enabled);
        ValidateDelivery(configuration, failures, required: enabled);
        if (!enabled)
        {
            return failures;
        }

        ValidateDuration(
            configuration,
            "Forms:DuplicateWindow",
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromDays(7),
            failures);
        ValidateInteger(
            configuration,
            "Forms:RateLimit:PermitLimit",
            10,
            1,
            1_000,
            failures);
        var rateWindow = ValidateDuration(
            configuration,
            "Forms:RateLimit:Window",
            TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromHours(1),
            failures);
        var rateRetention = ValidateDuration(
            configuration,
            "Forms:RateLimit:Retention",
            TimeSpan.FromDays(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromDays(30),
            failures);
        if (rateRetention < rateWindow)
        {
            failures.Add("Forms:RateLimit:Retention must be at least the rate-limit window.");
        }

        ValidateDuration(
            configuration,
            "Forms:RetentionPeriod",
            TimeSpan.FromDays(90),
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(3_650),
            failures);
        ValidateInteger(
            configuration,
            "Forms:MaximumRequestBytes",
            64_000,
            1_024,
            1_000_000,
            failures);
        ValidateInteger(
            configuration,
            "Forms:MaximumFieldCount",
            50,
            1,
            100,
            failures);
        var maximumValueLength = ValidateInteger(
            configuration,
            "Forms:MaximumFieldValueLength",
            4_000,
            1,
            4_000,
            failures);
        var maximumTotalLength = ValidateInteger(
            configuration,
            "Forms:MaximumTotalValueLength",
            16_000,
            1,
            64_000,
            failures);
        if (maximumTotalLength < maximumValueLength)
        {
            failures.Add(
                "Forms:MaximumTotalValueLength must be at least the maximum field value length.");
        }

        return failures;
    }

    private static void ValidateFingerprintKey(
        IConfiguration configuration,
        List<string> failures,
        bool required)
    {
        var value = configuration["Forms:FingerprintKey"];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                failures.Add("Forms:FingerprintKey is required when Forms is enabled.");
            }

            return;
        }

        try
        {
            if (Convert.FromBase64String(value).Length != 32)
            {
                failures.Add("Forms:FingerprintKey must contain exactly 32 bytes.");
            }
        }
        catch (FormatException)
        {
            failures.Add("Forms:FingerprintKey must be valid base64.");
        }
    }

    private static void ValidateDelivery(
        IConfiguration configuration,
        List<string> failures,
        bool required)
    {
        var supportedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "Forms:Delivery:Mode",
            "Forms:Delivery:PickupDirectory",
            "Forms:Delivery:PickupDestinationKey",
        };
        foreach (var pair in configuration.AsEnumerable())
        {
            if (pair.Key.StartsWith("Forms:Delivery:", StringComparison.Ordinal) &&
                !supportedKeys.Contains(pair.Key) &&
                !string.IsNullOrWhiteSpace(pair.Value))
            {
                failures.Add(
                    $"{pair.Key} is unsupported; live provider settings are prohibited.");
            }
        }

        var value = configuration["Forms:Delivery:Mode"];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                failures.Add("Forms:Delivery:Mode must be exactly Disabled or Pickup.");
            }

            return;
        }

        if (!Enum.TryParse<FormDeliveryMode>(value, ignoreCase: false, out var mode) ||
            !Enum.IsDefined(mode))
        {
            failures.Add("Forms:Delivery:Mode must be exactly Disabled or Pickup.");
            return;
        }

        var directory = configuration["Forms:Delivery:PickupDirectory"];
        var destination = configuration["Forms:Delivery:PickupDestinationKey"];
        if (mode == FormDeliveryMode.Disabled)
        {
            if (!string.IsNullOrWhiteSpace(directory) ||
                !string.IsNullOrWhiteSpace(destination))
            {
                failures.Add(
                    "Forms pickup settings must be empty when Forms:Delivery:Mode is Disabled.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(directory) ||
            directory.Trim().Length > 1_000 ||
            !Path.IsPathFullyQualified(directory))
        {
            failures.Add(
                "Forms:Delivery:PickupDirectory must be a bounded fully qualified test directory.");
        }

        if (!IsMachineKey(destination, 100))
        {
            failures.Add(
                "Forms:Delivery:PickupDestinationKey must be a bounded ASCII test destination key.");
        }
    }

    private static TimeSpan ValidateDuration(
        IConfiguration configuration,
        string key,
        TimeSpan defaultValue,
        TimeSpan minimum,
        TimeSpan maximum,
        List<string> failures)
    {
        var value = configuration[key];
        if (value is not null &&
            !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _))
        {
            failures.Add($"{key} must be a TimeSpan.");
        }

        var parsed = ReadDuration(value, defaultValue);
        if (parsed < minimum || parsed > maximum)
        {
            failures.Add(
                $"{key} must be between {minimum:c} and {maximum:c}.");
        }

        return parsed;
    }

    private static int ValidateInteger(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum,
        List<string> failures)
    {
        var value = configuration[key];
        if (value is not null &&
            !int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            failures.Add($"{key} must be an integer.");
        }

        var parsed = ReadInteger(value, defaultValue);
        if (parsed < minimum || parsed > maximum)
        {
            failures.Add($"{key} must be between {minimum} and {maximum}.");
        }

        return parsed;
    }

    private static bool ReadBoolean(string? value, bool defaultValue) =>
        bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    private static TimeSpan ReadDuration(string? value, TimeSpan defaultValue) =>
        TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static int ReadInteger(string? value, int defaultValue) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;

    private static bool IsMachineKey(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 3 &&
        value.Trim().Length <= maximumLength &&
        value.Trim().All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
