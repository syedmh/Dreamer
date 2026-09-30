using System.Net.Http.Headers;

namespace Husaynia.BaselineCapture;

public static class EvidenceSanitizer
{
    private static readonly HashSet<string> SafeResponseHeaders =
        new(
            [
                "accept-ranges",
                "cache-control",
                "content-language",
                "content-length",
                "content-type",
                "date",
                "etag",
                "expires",
                "last-modified",
                "location"
            ],
            StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SensitiveQueryFragments =
    [
        "access_token", "accesstoken", "api_key", "apikey", "auth", "authorization",
        "assertion", "bearer", "card", "client_secret", "code", "credential", "digest",
        "hmac", "jwt", "key", "mac", "nonce", "password", "payment", "private",
        "sas", "secret", "session", "sig", "signature", "signed", "ticket", "token",
        "x-amz-", "x-goog-"
    ];

    public static bool HasSensitiveQueryParameter(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return ParseQueryNames(uri.Query).Any(IsSensitiveQueryName);
    }

    public static string RedactUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            return "[invalid-url]";
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        var names = ParseQueryNames(uri.Query).ToArray();
        builder.Query = names.Length == 0
            ? string.Empty
            : string.Join(
                '&',
                names.Select(name =>
                    $"{Uri.EscapeDataString(name)}={(IsSensitiveQueryName(name) ? "[REDACTED-SENSITIVE]" : "[REDACTED]")}"));
        return builder.Uri.AbsoluteUri;
    }

    public static IReadOnlyDictionary<string, string> SelectSafeResponseHeaders(
        HttpResponseHeaders responseHeaders,
        HttpContentHeaders contentHeaders)
    {
        ArgumentNullException.ThrowIfNull(responseHeaders);
        ArgumentNullException.ThrowIfNull(contentHeaders);
        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in responseHeaders.Concat(contentHeaders))
        {
            if (!SafeResponseHeaders.Contains(header.Key))
            {
                continue;
            }

            var value = string.Join(", ", header.Value);
            selected[header.Key] = header.Key.Equals("location", StringComparison.OrdinalIgnoreCase)
                ? RedactLocation(value)
                : value;
        }

        return selected;
    }

    private static IEnumerable<string> ParseQueryNames(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            yield break;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            string name;
            try
            {
                name = Uri.UnescapeDataString(encodedName.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                name = "[invalid-query-name]";
            }

            yield return name;
        }
    }

    private static bool IsSensitiveQueryName(string name)
    {
        if (name.Equals("[invalid-query-name]", StringComparison.Ordinal)
            || name.Contains('%', StringComparison.Ordinal))
        {
            return true;
        }

        var normalized = name
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);
        return SensitiveQueryFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                fragment
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("_", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase));
    }

    private static string RedactLocation(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return RedactUrl(value);
        }

        if (value.StartsWith("//", StringComparison.Ordinal)
            && Uri.TryCreate($"https:{value}", UriKind.Absolute, out var networkPath))
        {
            var redacted = RedactUrl(networkPath.AbsoluteUri);
            return Uri.TryCreate(redacted, UriKind.Absolute, out var sanitized)
                ? $"//{sanitized.GetComponents(UriComponents.HostAndPort | UriComponents.PathAndQuery, UriFormat.UriEscaped)}"
                : "[invalid-url]";
        }

        var fragmentSeparator = value.IndexOf('#');
        var withoutFragment = fragmentSeparator >= 0 ? value[..fragmentSeparator] : value;
        var querySeparator = withoutFragment.IndexOf('?');
        if (querySeparator < 0)
        {
            return withoutFragment;
        }

        var path = withoutFragment[..querySeparator];
        var names = ParseQueryNames(withoutFragment[querySeparator..]).ToArray();
        return names.Length == 0
            ? path
            : $"{path}?{string.Join(
                '&',
                names.Select(name =>
                    $"{Uri.EscapeDataString(name)}={(IsSensitiveQueryName(name) ? "[REDACTED-SENSITIVE]" : "[REDACTED]")}"))}";
    }
}
