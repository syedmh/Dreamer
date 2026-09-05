using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace TCFAnimation;

public enum ActionMessageLoadStatus
{
    Success,
    MissingFile,
    InvalidUtf8,
    InvalidJson,
    InvalidSchema,
}

public sealed class ActionMessageCatalog
{
    private static readonly HashSet<string> SupportedKeys =
        new(StringComparer.Ordinal)
        {
            "1",
            "2",
            "3",
            "4",
            "5",
            "6",
            "Q",
            "R",
        };

    private readonly IReadOnlyDictionary<string, string> _messages;

    private ActionMessageCatalog(IReadOnlyDictionary<string, string> messages)
    {
        _messages = messages;
    }

    public static ActionMessageCatalog Empty { get; } =
        new ActionMessageCatalog(
            new Dictionary<string, string>(StringComparer.Ordinal));

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _messages.TryGetValue(key, out string? message)
            ? message
            : null;
    }

    public static ActionMessageLoadResult Load(ReadOnlySpan<byte> utf8Json)
    {
        string json;
        try
        {
            json = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(utf8Json);
        }
        catch (DecoderFallbackException exception)
        {
            return ActionMessageLoadResult.Failure(
                ActionMessageLoadStatus.InvalidUtf8,
                exception.Message);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ActionMessageLoadResult.Failure(
                    ActionMessageLoadStatus.InvalidSchema,
                    "The root JSON value must be an object.");
            }

            Dictionary<string, string> messages =
                new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (
                    !SupportedKeys.Contains(property.Name)
                    || property.Value.ValueKind != JsonValueKind.String
                )
                {
                    continue;
                }

                string normalized = DialogueLayout.NormalizeAndBoundText(
                    property.Value.GetString());
                if (normalized.Length > 0)
                {
                    messages[property.Name] = normalized;
                }
            }

            return new ActionMessageLoadResult(
                new ActionMessageCatalog(messages),
                ActionMessageLoadStatus.Success,
                null);
        }
        catch (JsonException exception)
        {
            return ActionMessageLoadResult.Failure(
                ActionMessageLoadStatus.InvalidJson,
                exception.Message);
        }
    }
}

public sealed record ActionMessageLoadResult(
    ActionMessageCatalog Catalog,
    ActionMessageLoadStatus Status,
    string? Error)
{
    public bool Succeeded => Status == ActionMessageLoadStatus.Success;

    public static ActionMessageLoadResult Failure(
        ActionMessageLoadStatus status,
        string error)
    {
        if (status == ActionMessageLoadStatus.Success)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new ActionMessageLoadResult(
            ActionMessageCatalog.Empty,
            status,
            error);
    }
}
