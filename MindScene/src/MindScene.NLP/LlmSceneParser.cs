using Microsoft.Extensions.Logging;
using MindScene.Core.Common;
using MindScene.Core.Interfaces;
using MindScene.Core.Models;
using MindScene.NLP.Dto;

namespace MindScene.NLP;

public class LlmSceneParser : ISceneParser
{
    private readonly IAiClient _ai;
    private readonly ILogger<LlmSceneParser> _logger;

    public LlmSceneParser(IAiClient ai, ILogger<LlmSceneParser> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public async Task<Result<ResolvedScene>> ParseAsync(SceneInput input, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Parsing scene: {Length} chars", input.Description.Length);

        var decompositionResult = await DecomposeSceneAsync(input, cancellationToken);
        if (decompositionResult.IsFailure) return Result.Fail<ResolvedScene>(decompositionResult.Error);
        var decomp = decompositionResult.Value;

        var actorNames = input.Actors.Select(a => a.Name).ToList();
        var spatialResult = await ResolveSpatialLayoutAsync(input.Description, actorNames, input.Actors, cancellationToken);
        if (spatialResult.IsFailure) return Result.Fail<ResolvedScene>(spatialResult.Error);

        var actions = input.Dialogue
            .Where(d => !string.IsNullOrEmpty(d.Action))
            .Select(d => $"{d.Speaker}: {d.Action}")
            .ToList();
        var directivesResult = await GenerateDirectivesAsync(input.Description, actions, cancellationToken);
        if (directivesResult.IsFailure) return Result.Fail<ResolvedScene>(directivesResult.Error);

        var duration = EstimateDuration(input);

        var setting = new SettingDescription
        {
            Location = decomp.Location,
            TimeOfDay = decomp.TimeOfDay,
            Weather = decomp.Weather,
            LightingMood = decomp.Lighting,
            Mood = decomp.Atmosphere,
            EnvironmentTemplate = decomp.EnvironmentTemplate,
            Props = decomp.Props
        };

        var scene = new ResolvedScene
        {
            SceneId = Guid.NewGuid().ToString("N")[..8],
            Setting = setting,
            Actors = spatialResult.Value,
            Directives = directivesResult.Value,
            CameraDirections = input.CameraDirections,
            Dialogue = input.Dialogue,
            TotalDurationSeconds = duration,
            Style = input.Style,
            Fps = input.Fps,
            Resolution = input.Resolution
        };

        _logger.LogInformation("Scene parsed: {Id}, {Actors} actors, {Duration:F1}s",
            scene.SceneId, scene.Actors.Count, duration);
        return Result.Ok(scene);
    }

    private async Task<Result<SceneDecompositionDto>> DecomposeSceneAsync(SceneInput input, CancellationToken ct)
    {
        var prompt = SceneParserPrompts.BuildSceneDecompositionPrompt(
            input.Description, input.Mood, input.Style.ToString());
        return await _ai.CompleteJsonAsync<SceneDecompositionDto>(
            SceneParserPrompts.SceneDecompositionSystem, prompt, ct);
    }

    private async Task<Result<List<ActorPlacement>>> ResolveSpatialLayoutAsync(
        string description, List<string> actorNames, List<ActorDefinition> actors, CancellationToken ct)
    {
        if (actorNames.Count == 0)
            return Result.Ok(new List<ActorPlacement>());

        var prompt = SceneParserPrompts.BuildSpatialLayoutPrompt(description, actorNames);
        var dtosResult = await _ai.CompleteJsonAsync<List<SpatialPlacementDto>>(
            SceneParserPrompts.SceneDecompositionSystem, prompt, ct);
        if (dtosResult.IsFailure) return Result.Fail<List<ActorPlacement>>(dtosResult.Error);

        var placements = new List<ActorPlacement>();
        foreach (var dto in dtosResult.Value)
        {
            var actor = actors.FirstOrDefault(a => a.Name.Equals(dto.Name, StringComparison.OrdinalIgnoreCase))
                        ?? actors.First();
            placements.Add(new ActorPlacement
            {
                Actor = actor,
                X = dto.X,
                Y = dto.Y,
                ZLayer = dto.ZLayer,
                FacingDirection = dto.FacingDirection,
                Pose = dto.Pose,
                Scale = dto.Scale,
                Emotion = actor.InitialEmotion
            });
        }

        // Fallback: place any unpositioned actors
        foreach (var actor in actors.Where(a => !placements.Any(p => p.Actor.Name == a.Name)))
        {
            placements.Add(new ActorPlacement
            {
                Actor = actor,
                X = 0.3f + placements.Count * 0.3f,
                Y = 0.7f,
                ZLayer = 0.5f,
                Emotion = actor.InitialEmotion
            });
        }

        return Result.Ok(placements);
    }

    private async Task<Result<List<AnimationDirective>>> GenerateDirectivesAsync(
        string description, List<string> actions, CancellationToken ct)
    {
        var prompt = SceneParserPrompts.BuildAnimationDirectivesPrompt(description, actions);
        var dtosResult = await _ai.CompleteJsonAsync<List<AnimationDirectiveDto>>(
            SceneParserPrompts.SceneDecompositionSystem, prompt, ct);

        if (dtosResult.IsFailure)
        {
            _logger.LogWarning("Failed to generate directives (non-fatal): {Error}", dtosResult.Error);
            return Result.Ok(new List<AnimationDirective>());
        }

        var directives = dtosResult.Value.Select(dto => new AnimationDirective
        {
            ActorName = dto.ActorName,
            Action = Enum.TryParse<AnimationAction>(dto.Action, true, out var a) ? a : AnimationAction.Idle,
            StartSeconds = dto.StartSeconds,
            DurationSeconds = dto.DurationSeconds,
            TargetEmotion = dto.TargetEmotion is null ? null : new EmotionVector(
                dto.TargetEmotion.Joy, dto.TargetEmotion.Sadness, dto.TargetEmotion.Anger,
                dto.TargetEmotion.Fear, dto.TargetEmotion.Surprise, dto.TargetEmotion.Disgust)
        }).ToList();

        return Result.Ok(directives);
    }

    private static float EstimateDuration(SceneInput input)
    {
        if (input.DurationSeconds.HasValue) return input.DurationSeconds.Value;
        // ~130 words/min = ~0.46s per word + 0.5s pause between lines
        var dialogue = input.Dialogue.Sum(d => d.Text.Split(' ').Length * 0.46f + 0.5f);
        return Math.Max(dialogue + 3f, 5f);
    }
}
