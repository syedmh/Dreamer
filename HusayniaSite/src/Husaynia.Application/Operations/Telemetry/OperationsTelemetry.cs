using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Husaynia.Application.Operations.Telemetry;

public sealed record CorrelationSnapshot(string CorrelationId, string TraceId, string? SpanId);

public interface ICorrelationContext
{
    CorrelationSnapshot Current { get; }

    IDisposable Begin(string? suppliedCorrelationId = null, string operationName = "operation");
}

public sealed class CorrelationContext : ICorrelationContext
{
    public const string ActivitySourceName = "Husaynia.Operations";
    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly AsyncLocal<string?> AmbientCorrelation = new();

    public CorrelationSnapshot Current
    {
        get
        {
            var activity = Activity.Current;
            var correlationId = AmbientCorrelation.Value ??
                (activity is null ? ActivityTraceId.CreateRandom().ToString() : activity.TraceId.ToString());
            return new CorrelationSnapshot(
                correlationId,
                activity?.TraceId.ToString() ?? correlationId,
                activity?.SpanId.ToString());
        }
    }

    public IDisposable Begin(string? suppliedCorrelationId = null, string operationName = "operation")
    {
        if (string.IsNullOrWhiteSpace(operationName) || operationName.Length > 200)
        {
            throw new ArgumentException("A bounded operation name is required.", nameof(operationName));
        }

        var correlationId = NormalizeCorrelationId(suppliedCorrelationId);
        var prior = AmbientCorrelation.Value;
        AmbientCorrelation.Value = correlationId;
        var activity = Source.StartActivity(operationName, ActivityKind.Internal) ??
            new Activity(operationName).SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.SetTag("correlation.id", correlationId);
        return new CorrelationScope(activity, prior);
    }

    private static string NormalizeCorrelationId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ActivityTraceId.CreateRandom().ToString();
        }

        var normalized = value.Trim();
        return normalized.Length <= 128 &&
            normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
                ? normalized
                : ActivityTraceId.CreateRandom().ToString();
    }

    private sealed class CorrelationScope(Activity activity, string? prior) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activity.Dispose();
            AmbientCorrelation.Value = prior;
        }
    }
}

public interface ISensitiveDataRedactor
{
    string Redact(string key, string? value);

    IReadOnlyDictionary<string, string> Redact(
        IReadOnlyDictionary<string, string?> values);
}

public sealed partial class HostileSensitiveDataRedactor : ISensitiveDataRedactor
{
    public const string Redacted = "[REDACTED]";
    private const int MaximumInputLength = 16_000;
    private const int MaximumDepth = 8;
    private const int MaximumNormalizationPasses = 4;
    private const int MaximumNormalizationWork = MaximumInputLength * MaximumNormalizationPasses;
    private static readonly string[] SensitiveKeyFragments =
    [
        "secret", "token", "authorization", "password", "passwd", "apikey", "api_key",
        "card", "cvc", "cvv", "iban", "routing", "accountnumber", "payment",
        "email", "phone", "address", "donor", "form", "message", "body", "payload",
        "cookie", "session", "signature",
    ];

    public string Redact(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalizedKey = NormalizeKey(key);
        if (SensitiveKeyFragments.Any(fragment =>
                normalizedKey.Contains(fragment, StringComparison.Ordinal)))
        {
            return Redacted;
        }

        var bounded = value.Length <= MaximumInputLength
            ? value
            : value[..MaximumInputLength];
        var redacted = RedactStructuredValue(
            bounded,
            0,
            new RedactionBudget(MaximumNormalizationWork));
        return redacted.Length <= 2_000 ? redacted : redacted[..2_000] + "…";
    }

    private string RedactStructuredValue(
        string value,
        int depth,
        RedactionBudget budget)
    {
        if (depth >= MaximumDepth)
        {
            return Redacted;
        }

        var normalized = NormalizeLeadingCharacters(value);
        for (var pass = 0; pass <= MaximumNormalizationPasses; pass++)
        {
            if (TryRedactJson(normalized, depth, budget, out var json))
            {
                return json;
            }

            if (TryRedactForm(normalized, depth, budget, out var form))
            {
                return form;
            }

            normalized = RedactFreeText(normalized);

            if (pass == MaximumNormalizationPasses)
            {
                return normalized.Contains('%')
                    ? Redacted
                    : normalized;
            }

            if (!TryDecodeOnce(normalized, budget, out var decoded))
            {
                return Redacted;
            }

            if (string.Equals(decoded, normalized, StringComparison.Ordinal))
            {
                return normalized;
            }

            normalized = NormalizeLeadingCharacters(decoded);
        }

        return Redacted;
    }

    public IReadOnlyDictionary<string, string> Redact(
        IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToDictionary(
            pair => pair.Key,
            pair => Redact(pair.Key, pair.Value),
            StringComparer.Ordinal);
    }

    private static string NormalizeKey(string key) =>
        new([.. (key ?? string.Empty)
            .Where(char.IsAsciiLetterOrDigit)
            .Select(char.ToLowerInvariant)]);

    private bool TryRedactJson(
        string value,
        int depth,
        RedactionBudget budget,
        out string redacted)
    {
        redacted = string.Empty;
        if (value.Length == 0 || value[0] is not ('{' or '['))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(
                value,
                documentOptions: new JsonDocumentOptions { MaxDepth = MaximumDepth });
            if (node is null)
            {
                return false;
            }

            RedactNode(node, depth + 1, budget);
            redacted = node.ToJsonString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void RedactNode(JsonNode node, int depth, RedactionBudget budget)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (!TryDecodeUntilStable(property.Key, budget, out var decodedKey) ||
                    SensitiveKeyFragments.Any(fragment =>
                        NormalizeKey(decodedKey).Contains(fragment, StringComparison.Ordinal)))
                {
                    jsonObject[property.Key] = Redacted;
                }
                else if (property.Value is JsonValue jsonValue &&
                    jsonValue.TryGetValue<string>(out var stringValue))
                {
                    jsonObject[property.Key] = RedactStructuredValue(stringValue, depth + 1, budget);
                }
                else if (property.Value is not null)
                {
                    if (depth + 1 >= MaximumDepth)
                    {
                        jsonObject[property.Key] = Redacted;
                    }
                    else
                    {
                        RedactNode(property.Value, depth + 1, budget);
                    }
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = 0; index < jsonArray.Count; index++)
            {
                if (jsonArray[index] is JsonValue jsonValue &&
                    jsonValue.TryGetValue<string>(out var stringValue))
                {
                    jsonArray[index] = RedactStructuredValue(stringValue, depth + 1, budget);
                }
                else if (jsonArray[index] is { } child)
                {
                    if (depth + 1 >= MaximumDepth)
                    {
                        jsonArray[index] = Redacted;
                    }
                    else
                    {
                        RedactNode(child, depth + 1, budget);
                    }
                }
            }
        }
        else if (node is JsonValue jsonValue)
        {
            var scalar = jsonValue.ToJsonString();
            if (LooksLikePaymentCard(scalar))
            {
                jsonValue.ReplaceWith(Redacted);
            }
        }
    }

    private bool TryRedactForm(
        string value,
        int depth,
        RedactionBudget budget,
        out string redacted)
    {
        redacted = string.Empty;
        var form = value.StartsWith('?') ? value[1..] : value;
        if (!form.Contains('='))
        {
            return false;
        }

        var parts = form.Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 ||
            parts.Any(part =>
            {
                var separator = part.IndexOf('=');
                return separator <= 0 ||
                    part[..separator].Any(character =>
                        !(char.IsAsciiLetterOrDigit(character) ||
                            character is '_' or '-' or '.' or '%'));
            }))
        {
            return false;
        }

        var redactedParts = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var separator = part.IndexOf('=');
            if (!TryDecodeUntilStable(
                    part[..separator].Replace('+', ' '),
                    budget,
                    out var key))
            {
                redacted = Redacted;
                return true;
            }

            var itemValue = part[(separator + 1)..].Replace('+', ' ');
            var normalizedKey = NormalizeKey(key);
            var itemRedacted = SensitiveKeyFragments.Any(fragment =>
                normalizedKey.Contains(fragment, StringComparison.Ordinal))
                    ? Redacted
                    : RedactStructuredValue(itemValue, depth + 1, budget);
            redactedParts.Add(
                $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(itemRedacted)}");
        }

        redacted = string.Join("&", redactedParts);
        return true;
    }

    private static string NormalizeLeadingCharacters(string value)
    {
        var start = 0;
        while (start < value.Length &&
            (value[start] == '\uFEFF' || char.IsWhiteSpace(value[start])))
        {
            start++;
        }

        return start == 0 ? value : value[start..];
    }

    private static string RedactFreeText(string value)
    {
        var redacted = BearerRegex().Replace(value, "$1" + Redacted);
        redacted = EmailRegex().Replace(redacted, Redacted);
        redacted = JwtRegex().Replace(redacted, Redacted);
        redacted = SecretAssignmentRegex().Replace(redacted, "$1=" + Redacted);
        return CardCandidateRegex().Replace(
            redacted,
            match => LooksLikePaymentCard(match.Value) ? Redacted : match.Value);
    }

    private static bool TryDecodeUntilStable(
        string value,
        RedactionBudget budget,
        out string decoded)
    {
        decoded = value;
        for (var pass = 0; pass < MaximumNormalizationPasses; pass++)
        {
            if (!TryDecodeOnce(decoded, budget, out var next))
            {
                return false;
            }

            if (string.Equals(next, decoded, StringComparison.Ordinal))
            {
                return true;
            }

            decoded = next;
        }

        if (!decoded.Contains('%'))
        {
            return true;
        }

        return TryDecodeOnce(decoded, budget, out var extra) &&
            string.Equals(extra, decoded, StringComparison.Ordinal);
    }

    private static bool TryDecodeOnce(
        string value,
        RedactionBudget budget,
        out string decoded)
    {
        decoded = value;
        if (!budget.TrySpend(value.Length))
        {
            return false;
        }

        try
        {
            decoded = Uri.UnescapeDataString(value);
            return true;
        }
        catch (UriFormatException)
        {
            return true;
        }
    }

    private sealed class RedactionBudget(int remainingWork)
    {
        private int remainingWork = remainingWork;

        internal bool TrySpend(int work)
        {
            if (work < 0 || work > remainingWork)
            {
                return false;
            }

            remainingWork -= work;
            return true;
        }
    }

    private static bool LooksLikePaymentCard(string value)
    {
        Span<int> digits = stackalloc int[19];
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsAsciiDigit(character) && count < digits.Length)
            {
                digits[count++] = character - '0';
            }
        }

        if (count is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        var alternate = false;
        for (var index = count - 1; index >= 0; index--)
        {
            var digit = digits[index];
            if (alternate && (digit *= 2) > 9)
            {
                digit -= 9;
            }

            sum += digit;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    [GeneratedRegex(@"(?i)\b(bearer\s+)([a-z0-9._~+/=-]+)")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\beyJ[a-zA-Z0-9_-]{5,}\.[a-zA-Z0-9_-]{5,}\.[a-zA-Z0-9_-]{5,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\b(secret|token|password|api[_-]?key|signature)\s*=\s*([^\s&;,]+)")]
    private static partial Regex SecretAssignmentRegex();

    [GeneratedRegex(@"\b(?:\d[ -]*?){13,19}\b")]
    private static partial Regex CardCandidateRegex();
}

public interface IOperationsMetrics
{
    void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0);

    void RecordJob(string definition, string outcome, int attempt);

    void RecordStaleness(string dataSet, TimeSpan age);
}

public sealed class OperationsMetrics : IOperationsMetrics, IDisposable
{
    private readonly ICorrelationContext correlation;
    private readonly Meter meter = new("Husaynia.Operations", "1.0.0");
    private readonly Histogram<double> dependencyDuration;
    private readonly Counter<long> dependencyOutcomes;
    private readonly Counter<long> jobOutcomes;
    private readonly Histogram<double> staleDataAge;

    public OperationsMetrics(ICorrelationContext correlation)
    {
        this.correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
        dependencyDuration = meter.CreateHistogram<double>("husaynia.dependency.duration", "ms");
        dependencyOutcomes = meter.CreateCounter<long>("husaynia.dependency.outcomes");
        jobOutcomes = meter.CreateCounter<long>("husaynia.jobs.outcomes");
        staleDataAge = meter.CreateHistogram<double>("husaynia.data.staleness", "s");
    }

    public void RecordDependency(string dependency, string outcome, double durationMilliseconds = 0)
    {
        var tags = CorrelationTags();
        tags.Add("dependency.name", dependency);
        tags.Add("outcome", outcome);
        dependencyOutcomes.Add(1, tags);
        dependencyDuration.Record(durationMilliseconds, tags);
    }

    public void RecordJob(string definition, string outcome, int attempt)
    {
        var tags = CorrelationTags();
        tags.Add("job.definition", definition);
        tags.Add("outcome", outcome);
        tags.Add("attempt", attempt);
        jobOutcomes.Add(1, tags);
    }

    public void RecordStaleness(string dataSet, TimeSpan age)
    {
        var tags = CorrelationTags();
        tags.Add("data.name", dataSet);
        staleDataAge.Record(age.TotalSeconds, tags);
    }

    private TagList CorrelationTags()
    {
        var current = correlation.Current;
        return new TagList
        {
            { "correlation.id", current.CorrelationId },
            { "trace.id", current.TraceId },
        };
    }

    public void Dispose() => meter.Dispose();
}
