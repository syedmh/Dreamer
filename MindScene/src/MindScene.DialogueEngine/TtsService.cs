using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MindScene.Core.Common;
using MindScene.Core.Models;

namespace MindScene.DialogueEngine;

/// <summary>
/// Text-to-speech service. MVP uses a stub (silent WAV placeholder).
/// Set TtsOptions.Provider to "ElevenLabs" to enable real TTS.
/// </summary>
public class TtsService
{
    private readonly TtsOptions _options;
    private readonly ILogger<TtsService> _logger;
    private readonly HttpClient _httpClient;

    public TtsService(IOptions<TtsOptions> options, ILogger<TtsService> logger, HttpClient httpClient)
    {
        _options = options.Value;
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<Result<string>> SynthesizeAsync(
        string text,
        VoiceProfile voice,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return _options.Provider.ToUpperInvariant() switch
        {
            "ELEVENLABS" => await SynthesizeElevenLabsAsync(text, voice, outputPath, cancellationToken),
            _ => GenerateSilentWav(text, voice, outputPath)
        };
    }

    private async Task<Result<string>> SynthesizeElevenLabsAsync(
        string text, VoiceProfile voice, string outputPath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.ApiKey))
            return Result.Fail<string>("ElevenLabs API key not configured");

        try
        {
            var voiceId = string.IsNullOrEmpty(voice.VoiceId) || voice.VoiceId == "default"
                ? _options.DefaultVoiceId
                : voice.VoiceId;

            var payload = new
            {
                text,
                model_id = "eleven_multilingual_v2",
                voice_settings = new { stability = 0.5, similarity_boost = 0.75, speed = voice.Speed }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("xi-api-key", _options.ApiKey);

            var response = await _httpClient.PostAsync(
                $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}", content, ct);
            response.EnsureSuccessStatusCode();

            var audioBytes = await response.Content.ReadAsByteArrayAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, audioBytes, ct);
            _logger.LogDebug("ElevenLabs TTS saved: {Path}", outputPath);
            return Result.Ok(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ElevenLabs TTS failed, falling back to silent");
            return GenerateSilentWav(text, voice, outputPath);
        }
    }

    /// <summary>Generates a minimal silent WAV file sized to match estimated speech duration.</summary>
    private Result<string> GenerateSilentWav(string text, VoiceProfile voice, string outputPath)
    {
        try
        {
            var wordCount = text.Split(' ').Length;
            var durationSeconds = wordCount * 0.46f / voice.Speed;
            var sampleRate = 44100;
            var numSamples = (int)(durationSeconds * sampleRate);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var stream = new FileStream(outputPath, FileMode.Create);
            using var writer = new BinaryWriter(stream);
            WriteWavHeader(writer, sampleRate, numSamples);
            for (int i = 0; i < numSamples; i++) writer.Write((short)0);
            _logger.LogDebug("Silent WAV generated: {Path} ({Duration:F2}s)", outputPath, durationSeconds);
            return Result.Ok(outputPath);
        }
        catch (Exception ex)
        {
            return Result.Fail<string>($"WAV generation failed: {ex.Message}");
        }
    }

    private static void WriteWavHeader(BinaryWriter w, int sampleRate, int numSamples)
    {
        int byteRate = sampleRate * 2;
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + numSamples * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(byteRate); w.Write((short)2); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(numSamples * 2);
    }
}

public class TtsOptions
{
    public const string SectionName = "TTS";
    public string Provider { get; set; } = "Stub";
    public string ApiKey { get; set; } = string.Empty;
    public string DefaultVoiceId { get; set; } = "21m00Tcm4TlvDq8ikWAM"; // ElevenLabs Rachel
}
