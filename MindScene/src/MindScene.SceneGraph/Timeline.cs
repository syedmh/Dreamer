using MindScene.Core.Models;

namespace MindScene.SceneGraph;

/// <summary>
/// Manages time-based mutations of the scene graph — what each actor is doing at any moment.
/// </summary>
public class SceneTimeline
{
    private readonly ResolvedScene _scene;

    public SceneTimeline(ResolvedScene scene) => _scene = scene;

    /// <summary>Returns the active pose/emotion for an actor at a given timestamp.</summary>
    public ActorState GetActorState(string actorName, float timeSeconds)
    {
        var placement = _scene.Actors.FirstOrDefault(a =>
            a.Actor.Name.Equals(actorName, StringComparison.OrdinalIgnoreCase));
        if (placement is null)
            return ActorState.Default;

        var baseEmotion = placement.Emotion;
        var currentPose = placement.Pose;
        var currentAction = AnimationAction.Idle;

        // Find active directives at this time
        var activeDirectives = _scene.Directives
            .Where(d => d.ActorName.Equals(actorName, StringComparison.OrdinalIgnoreCase)
                     && timeSeconds >= d.StartSeconds
                     && timeSeconds <= d.StartSeconds + d.DurationSeconds)
            .OrderByDescending(d => d.StartSeconds)
            .ToList();

        foreach (var directive in activeDirectives)
        {
            currentAction = directive.Action;
            if (directive.TargetEmotion is not null)
            {
                var t = (timeSeconds - directive.StartSeconds) / directive.DurationSeconds;
                baseEmotion = baseEmotion.Lerp(directive.TargetEmotion, Math.Clamp(t, 0f, 1f));
            }
        }

        // Check if actor is speaking at this time
        var activeLine = _scene.Dialogue.FirstOrDefault(d =>
            d.Speaker.Equals(actorName, StringComparison.OrdinalIgnoreCase)
            && !d.Timing.IsAuto
            && timeSeconds >= d.Timing.StartSeconds
            && timeSeconds <= d.Timing.EndSeconds);

        if (activeLine is not null)
        {
            currentAction = AnimationAction.Speak;
            baseEmotion = baseEmotion.Lerp(activeLine.Emotion, 0.7f);
        }

        return new ActorState(
            Emotion: baseEmotion,
            Action: currentAction,
            Pose: currentPose,
            IsSpeaking: activeLine is not null,
            ActiveLine: activeLine);
    }

    /// <summary>Returns the current viseme (mouth shape) for an actor at a given timestamp.</summary>
    public string GetViseme(string actorName, float timeSeconds)
    {
        var line = _scene.Dialogue.FirstOrDefault(d =>
            d.Speaker.Equals(actorName, StringComparison.OrdinalIgnoreCase)
            && !d.Timing.IsAuto
            && timeSeconds >= d.Timing.StartSeconds
            && timeSeconds <= d.Timing.EndSeconds);

        if (line is null || line.LipSyncData.Count == 0) return "rest";

        var viseme = line.LipSyncData
            .Where(v => timeSeconds >= v.StartSeconds && timeSeconds <= v.StartSeconds + v.DurationSeconds)
            .FirstOrDefault();

        return viseme?.Shape ?? "rest";
    }
}

public record ActorState(
    EmotionVector Emotion,
    AnimationAction Action,
    string Pose,
    bool IsSpeaking,
    DialogueLine? ActiveLine)
{
    public static readonly ActorState Default = new(
        EmotionVector.Neutral, AnimationAction.Idle, "standing", false, null);
}
