using Microsoft.Extensions.Logging;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;
using MindScene.Core.Models;

namespace MindScene.DialogueEngine;

public class DialogueProcessor : IDialogueProcessor
{
    private readonly TtsService _tts;
    private readonly ILogger<DialogueProcessor> _logger;

    public DialogueProcessor(TtsService tts, ILogger<DialogueProcessor> logger)
    {
        _tts = tts;
        _logger = logger;
    }

    public async Task<Result<List<DialogueLine>>> ProcessDialogueAsync(
        List<DialogueLine> lines,
        List<ActorDefinition> actors,
        CancellationToken cancellationToken = default)
    {
        var processed = new List<DialogueLine>();
        float currentTime = 0f;

        foreach (var (line, index) in lines.Select((l, i) => (l, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resolve actor voice profile
            var actor = actors.FirstOrDefault(a =>
                a.Name.Equals(line.Speaker, StringComparison.OrdinalIgnoreCase));
            var voice = actor?.Voice ?? VoiceProfile.Default;

            // Calculate speech duration
            var wordCount = line.Text.Split(' ').Length;
            var speechDuration = wordCount * 0.46f / voice.Speed;
            var actionPause = string.IsNullOrEmpty(line.Action) ? 0f : 0.8f;

            // Assign timecode
            var startTime = line.Timing.IsAuto ? currentTime : line.Timing.StartSeconds;
            var duration = line.Timing.IsAuto ? speechDuration : line.Timing.DurationSeconds;

            // Generate TTS
            var audioPath = Path.Combine(Path.GetTempPath(), "mindscene_audio", $"line_{index:D3}.wav");
            var ttsResult = await _tts.SynthesizeAsync(line.Text, voice, audioPath, cancellationToken);
            if (ttsResult.IsFailure)
                _logger.LogWarning("TTS failed for line {Index}: {Error}", index, ttsResult.Error);

            // Generate lip-sync
            var lipSync = LipSyncGenerator.Generate(line.Text, startTime, duration);

            processed.Add(line with
            {
                Timing = new TimeCode(startTime, duration),
                AudioClipPath = ttsResult.IsSuccess ? ttsResult.Value : null,
                LipSyncData = lipSync,
                Emotion = line.Emotion == EmotionVector.Neutral
                    ? EmotionVector.FromLabel(actor?.InitialEmotion.DominantEmotion ?? "neutral")
                    : line.Emotion
            });

            currentTime = startTime + duration + actionPause;
            _logger.LogDebug("Processed line {Index}: {Speaker} at {Start:F2}s for {Duration:F2}s",
                index, line.Speaker, startTime, duration);
        }

        return Result.Ok(processed);
    }
}
