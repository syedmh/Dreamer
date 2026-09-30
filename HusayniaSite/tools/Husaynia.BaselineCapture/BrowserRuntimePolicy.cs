using System.Text.Json;

namespace Husaynia.BaselineCapture;

internal static class BrowserCookiePolicy
{
    public static string? ValidateRequestHeaders(IReadOnlyDictionary<string, string> headers) =>
        headers.Keys.Any(key => key.Equals("cookie", StringComparison.OrdinalIgnoreCase))
            ? "cookie-request-blocked"
            : null;

    public static string? ValidateContextCookieCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count == 0 ? null : "cookies-created";
    }

    public static ScreenshotPolicyDecision ClassifyCapability(string capability) =>
        capability switch
        {
            "cookie-read" => new(true, "cookie-read-empty-context"),
            "cookie-write" => new(false, "capability-cookie-write-blocked"),
            _ => new(false, $"capability-{capability}-blocked")
        };
}

internal sealed record CapabilityConsoleParseResult(
    bool Accepted,
    string Capability,
    string Url,
    string? FailureReason);

internal static class CapabilityConsolePolicy
{
    public const string Prefix = "__HUSAYNIA_BASELINE_CAPABILITY__";
    public const int MaximumMessageCount = 64;
    public const int MaximumMessageCharacters = 4096;
    public const int MaximumUrlCharacters = 2048;

    private static readonly HashSet<string> AllowedCapabilities =
        new(
            [
                "websocket",
                "eventsource",
                "webrtc",
                "webtransport",
                "direct-sockets",
                "payment-request",
                "sendbeacon",
                "serviceworker",
                "form-submit",
                "form-request-submit",
                "cookie-read",
                "cookie-write"
            ],
            StringComparer.Ordinal);

    public static CapabilityConsoleParseResult Parse(
        string message,
        int messageNumber,
        string fallbackUrl,
        string expectedNonce)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedNonce);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(messageNumber);

        if (messageNumber > MaximumMessageCount)
        {
            return new(false, "unknown", fallbackUrl, "capability-log-count-exceeded");
        }

        if (message.Length > MaximumMessageCharacters)
        {
            return new(false, "unknown", fallbackUrl, "capability-log-oversize");
        }

        if (!message.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new(false, "unknown", fallbackUrl, "malformed-capability-log");
        }

        try
        {
            using var payload = JsonDocument.Parse(message[Prefix.Length..]);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("capability", out var capabilityProperty)
                || !root.TryGetProperty("url", out var urlProperty)
                || !root.TryGetProperty("nonce", out var nonceProperty)
                || capabilityProperty.ValueKind != JsonValueKind.String
                || urlProperty.ValueKind != JsonValueKind.String
                || nonceProperty.ValueKind != JsonValueKind.String
                || nonceProperty.GetString() != expectedNonce)
            {
                return new(false, "unknown", fallbackUrl, "malformed-capability-log");
            }

            var capability = capabilityProperty.GetString();
            var url = urlProperty.GetString();
            if (string.IsNullOrWhiteSpace(capability)
                || !AllowedCapabilities.Contains(capability)
                || string.IsNullOrWhiteSpace(url)
                || url.Length > MaximumUrlCharacters
                || !Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                return new(false, "unknown", fallbackUrl, "malformed-capability-log");
            }

            return new(true, capability, url, null);
        }
        catch (JsonException)
        {
            return new(false, "unknown", fallbackUrl, "malformed-capability-log");
        }
    }
}

internal sealed class AttemptResourceLifetime(Func<ValueTask> disposeAsync) : IAsyncDisposable
{
    private Func<ValueTask>? _disposeAsync = disposeAsync;

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(ref _disposeAsync, null);
        if (current is not null)
        {
            await current();
        }
    }
}
