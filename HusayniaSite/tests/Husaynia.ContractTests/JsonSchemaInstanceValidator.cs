using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Husaynia.ContractTests;

internal sealed class JsonSchemaInstanceValidator : IDisposable
{
    private readonly JsonDocument schemaDocument;

    public JsonSchemaInstanceValidator(string schemaPath)
    {
        schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
    }

    public IReadOnlyList<string> Validate(string instanceJson)
    {
        using var instanceDocument = JsonDocument.Parse(instanceJson);
        var errors = new List<string>();
        Evaluate(schemaDocument.RootElement, instanceDocument.RootElement, "$", errors);
        return errors;
    }

    public void Dispose() => schemaDocument.Dispose();

    private void Evaluate(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Evaluate(ResolveReference(reference.GetString()!), instance, path, errors);
            return;
        }

        EvaluateComposition(schema, instance, path, errors);
        EvaluateValue(schema, instance, path, errors);
    }

    private void EvaluateComposition(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var childSchema in allOf.EnumerateArray())
            {
                Evaluate(childSchema, instance, path, errors);
            }
        }

        if (schema.TryGetProperty("if", out var ifSchema))
        {
            var conditionErrors = new List<string>();
            Evaluate(ifSchema, instance, path, conditionErrors);
            var branchName = conditionErrors.Count == 0 ? "then" : "else";
            if (schema.TryGetProperty(branchName, out var branch))
            {
                Evaluate(branch, instance, path, errors);
            }
        }

        if (schema.TryGetProperty("not", out var notSchema))
        {
            var notErrors = new List<string>();
            Evaluate(notSchema, instance, path, notErrors);
            if (notErrors.Count == 0)
            {
                errors.Add($"{path} matched a forbidden schema.");
            }
        }
    }

    private void EvaluateValue(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, instance))
        {
            errors.Add($"{path} does not equal the required constant.");
        }

        if (schema.TryGetProperty("enum", out var allowed) &&
            !allowed.EnumerateArray().Any(value => JsonElement.DeepEquals(value, instance)))
        {
            errors.Add($"{path} is not an allowed enum value.");
        }

        if (schema.TryGetProperty("type", out var typeElement))
        {
            var type = typeElement.GetString();
            if (!MatchesType(instance, type))
            {
                errors.Add($"{path} must be of type {type}.");
                return;
            }
        }

        if (instance.ValueKind == JsonValueKind.Object &&
            (schema.TryGetProperty("required", out _) ||
             schema.TryGetProperty("properties", out _) ||
             schema.TryGetProperty("additionalProperties", out _)))
        {
            EvaluateObject(schema, instance, path, errors);
        }

        if (instance.ValueKind == JsonValueKind.Array &&
            (schema.TryGetProperty("items", out _) ||
             schema.TryGetProperty("minItems", out _) ||
             schema.TryGetProperty("uniqueItems", out _)))
        {
            EvaluateArray(schema, instance, path, errors);
        }

        if (instance.ValueKind == JsonValueKind.String)
        {
            EvaluateString(schema, instance.GetString()!, path, errors);
        }

        if (instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out var integer))
        {
            EvaluateInteger(schema, integer, path, errors);
        }
    }

    private void EvaluateObject(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var property in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!instance.TryGetProperty(property, out _))
                {
                    errors.Add($"{path}.{property} is required.");
                }
            }
        }

        var declaredProperties = schema.TryGetProperty("properties", out var properties)
            ? properties.EnumerateObject().ToDictionary(property => property.Name, StringComparer.Ordinal)
            : new Dictionary<string, JsonProperty>(StringComparer.Ordinal);

        foreach (var property in instance.EnumerateObject())
        {
            if (declaredProperties.TryGetValue(property.Name, out var propertySchema))
            {
                Evaluate(propertySchema.Value, property.Value, $"{path}.{property.Name}", errors);
                continue;
            }

            if (!schema.TryGetProperty("additionalProperties", out var additionalProperties))
            {
                continue;
            }

            if (additionalProperties.ValueKind == JsonValueKind.False)
            {
                errors.Add($"{path}.{property.Name} is not an allowed property.");
            }
            else if (additionalProperties.ValueKind == JsonValueKind.Object)
            {
                Evaluate(additionalProperties, property.Value, $"{path}.{property.Name}", errors);
            }
        }
    }

    private void EvaluateArray(JsonElement schema, JsonElement instance, string path, List<string> errors)
    {
        var items = instance.EnumerateArray().ToArray();
        if (schema.TryGetProperty("minItems", out var minItems) && items.Length < minItems.GetInt32())
        {
            errors.Add($"{path} has fewer than the required number of items.");
        }

        if (schema.TryGetProperty("uniqueItems", out var uniqueItems) &&
            uniqueItems.GetBoolean() &&
            items.Select(item => item.GetRawText()).Distinct(StringComparer.Ordinal).Count() != items.Length)
        {
            errors.Add($"{path} contains duplicate items.");
        }

        if (schema.TryGetProperty("items", out var itemSchema))
        {
            for (var index = 0; index < items.Length; index++)
            {
                Evaluate(itemSchema, items[index], $"{path}[{index}]", errors);
            }
        }
    }

    private static void EvaluateString(JsonElement schema, string value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minLength", out var minLength) && value.Length < minLength.GetInt32())
        {
            errors.Add($"{path} is shorter than the required length.");
        }

        if (schema.TryGetProperty("pattern", out var pattern) &&
            !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant))
        {
            errors.Add($"{path} does not match the required pattern.");
        }

        if (!schema.TryGetProperty("format", out var format))
        {
            return;
        }

        var isValid = format.GetString() switch
        {
            "date-time" => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _),
            "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
            "uuid" => Guid.TryParseExact(value, "D", out _),
            _ => true,
        };

        if (!isValid)
        {
            errors.Add($"{path} is not a valid {format.GetString()}.");
        }
    }

    private static void EvaluateInteger(JsonElement schema, long value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("minimum", out var minimum) && value < minimum.GetInt64())
        {
            errors.Add($"{path} is below the minimum.");
        }
    }

    private JsonElement ResolveReference(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Only local JSON Schema references are supported: {reference}");
        }

        var current = schemaDocument.RootElement;
        foreach (var segment in reference[2..].Split('/'))
        {
            current = current.GetProperty(segment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal));
        }

        return current;
    }

    private static bool MatchesType(JsonElement instance, string? type) =>
        type switch
        {
            "object" => instance.ValueKind == JsonValueKind.Object,
            "array" => instance.ValueKind == JsonValueKind.Array,
            "string" => instance.ValueKind == JsonValueKind.String,
            "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
            "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
            _ => true,
        };
}
