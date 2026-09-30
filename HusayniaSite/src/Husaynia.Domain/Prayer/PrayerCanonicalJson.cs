using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Husaynia.Domain.Prayer;

public static class PrayerCanonicalJson
{
    public static string CanonicalizeObject(string json, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > maximumLength)
        {
            throw new ArgumentException("A bounded JSON object is required.", nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 12,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A JSON object is required.", nameof(json));
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteElement(writer, document.RootElement, normalizePropertyNames: false);
            }

            var canonical = Encoding.UTF8.GetString(stream.ToArray());
            if (canonical.Length > maximumLength)
            {
                throw new ArgumentException("A bounded JSON object is required.", nameof(json));
            }

            return canonical;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid JSON object is required.", nameof(json), exception);
        }
    }

    private static void WriteElement(
        Utf8JsonWriter writer,
        JsonElement element,
        bool normalizePropertyNames)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element, normalizePropertyNames);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(writer, item, normalizePropertyNames: false);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(
                    (element.GetString() ?? string.Empty).Normalize(NormalizationForm.FormC));
                break;
            case JsonValueKind.Number:
                WriteNumber(writer, element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("The JSON value is not supported.", nameof(element));
        }
    }

    private static void WriteObject(
        Utf8JsonWriter writer,
        JsonElement element,
        bool normalizePropertyNames)
    {
        var properties = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var normalizedName = property.Name.Normalize(NormalizationForm.FormC);
            if (normalizePropertyNames)
            {
                normalizedName = normalizedName.ToLowerInvariant();
            }

            if (!properties.TryAdd(normalizedName, property.Value))
            {
                throw new ArgumentException("Duplicate JSON properties are not allowed.", nameof(element));
            }
        }

        writer.WriteStartObject();
        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Key);
            WriteElement(
                writer,
                property.Value,
                normalizePropertyNames: property.Key.Equals(
                    "offsets",
                    StringComparison.Ordinal));
        }

        writer.WriteEndObject();
    }

    private static void WriteNumber(Utf8JsonWriter writer, string raw)
    {
        if (decimal.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var decimalValue))
        {
            writer.WriteRawValue(
                decimalValue.ToString("G29", CultureInfo.InvariantCulture),
                skipInputValidation: false);
            return;
        }

        if (!double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var doubleValue) ||
            !double.IsFinite(doubleValue))
        {
            throw new ArgumentException("The JSON number is invalid.", nameof(raw));
        }

        writer.WriteRawValue(
            doubleValue.ToString("R", CultureInfo.InvariantCulture),
            skipInputValidation: false);
    }
}
