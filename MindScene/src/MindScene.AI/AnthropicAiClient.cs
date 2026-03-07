using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;

namespace MindScene.AI;

public class AnthropicAiClient : IAiClient
{
    private readonly AnthropicClient _client;
    private readonly AiOptions _options;
    private readonly ILogger<AnthropicAiClient> _logger;

    public AnthropicAiClient(IOptions<AiOptions> options, ILogger<AnthropicAiClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _client = new AnthropicClient(_options.ApiKey);
    }

    public async Task<Result<string>> CompleteAsync(
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Sending AI request, user message length: {Length}", userMessage.Length);
            var request = new MessageParameters
            {
                Model = _options.Model,
                MaxTokens = _options.MaxTokens,
                SystemMessage = systemPrompt,
                Messages = [new Message(RoleType.User, userMessage)]
            };
            var response = await _client.Messages.GetClaudeMessageAsync(request, cancellationToken);
            var text = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
            return Result.Ok(text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI completion failed");
            return Result.Fail<string>($"AI completion failed: {ex.Message}");
        }
    }

    public async Task<Result<T>> CompleteJsonAsync<T>(
        string systemPrompt,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var jsonSystemPrompt = systemPrompt + "\n\nIMPORTANT: Respond ONLY with valid JSON. No markdown fences, no explanations — pure JSON only.";
        var result = await CompleteAsync(jsonSystemPrompt, userMessage, cancellationToken);
        if (result.IsFailure) return Result.Fail<T>(result.Error);

        try
        {
            var raw = result.Value.Trim();
            // Strip markdown code fences if present
            if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
            if (raw.EndsWith("```")) raw = raw[..raw.LastIndexOf("```")].TrimEnd();
            var value = JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return value is null ? Result.Fail<T>("Deserialized null from AI response") : Result.Ok(value);
        }
        catch (JsonException ex)
        {
            return Result.Fail<T>($"JSON parse failed: {ex.Message}");
        }
    }
}
