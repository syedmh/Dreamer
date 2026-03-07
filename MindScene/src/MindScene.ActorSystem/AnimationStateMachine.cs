using MindScene.Core.Models;
using MindScene.SceneGraph;

namespace MindScene.ActorSystem;

/// <summary>
/// State machine that selects the correct sprite sheet row/frame for an actor
/// based on their current emotion, action, and pose.
/// </summary>
public class AnimationStateMachine
{
    private float _idlePhase;
    private float _blinkTimer;
    private const float BlinkInterval = 4.5f;
    private const float BlinkDuration = 0.15f;

    public AnimationFrame GetFrame(ActorState state, float timeSeconds, float deltaTime)
    {
        _idlePhase += deltaTime;
        _blinkTimer += deltaTime;

        var emotion = state.Emotion.DominantEmotion;
        var isBlinking = _blinkTimer % BlinkInterval < BlinkDuration;

        if (_blinkTimer > BlinkInterval + BlinkDuration) _blinkTimer = 0f;

        return state.Action switch
        {
            AnimationAction.Speak => BuildSpeakFrame(state, timeSeconds, emotion, isBlinking),
            AnimationAction.Walk => BuildWalkFrame(state, timeSeconds, emotion),
            AnimationAction.Gesture => BuildGestureFrame(state, emotion),
            AnimationAction.Nod => BuildNodFrame(state, emotion, timeSeconds),
            AnimationAction.React => BuildReactFrame(state, emotion),
            _ => BuildIdleFrame(state, emotion, isBlinking)
        };
    }

    private AnimationFrame BuildIdleFrame(ActorState state, string emotion, bool isBlinking)
    {
        var breathOffset = (float)(Math.Sin(_idlePhase * 0.8f) * 1.5); // subtle breathing
        return new AnimationFrame(
            BodySprite: $"body_{state.Pose}_{emotion}",
            FaceSprite: isBlinking ? "face_blink" : $"face_{emotion}",
            MouthSprite: "mouth_rest",
            EyeSprite: isBlinking ? "eyes_closed" : $"eyes_{emotion}",
            BodyOffsetY: breathOffset,
            HeadOffsetY: breathOffset * 0.5f);
    }

    private AnimationFrame BuildSpeakFrame(ActorState state, float time, string emotion, bool isBlinking)
    {
        // Subtle head bob while speaking
        var bobY = (float)(Math.Sin(time * 5.0f) * 0.8f);
        return new AnimationFrame(
            BodySprite: $"body_standing_{emotion}",
            FaceSprite: isBlinking ? "face_blink" : $"face_speaking_{emotion}",
            MouthSprite: $"mouth_{state.ActiveLine?.LipSyncData.FirstOrDefault()?.Shape ?? "rest"}",
            EyeSprite: isBlinking ? "eyes_closed" : $"eyes_{emotion}",
            HeadOffsetY: bobY);
    }

    private AnimationFrame BuildWalkFrame(ActorState state, float time, string emotion)
    {
        var walkFrame = (int)(time * 8f) % 4; // 4-frame walk cycle at 8fps
        return new AnimationFrame(
            BodySprite: $"body_walk_{walkFrame}",
            FaceSprite: $"face_{emotion}",
            MouthSprite: "mouth_rest",
            EyeSprite: $"eyes_{emotion}");
    }

    private AnimationFrame BuildGestureFrame(ActorState state, string emotion)
    {
        var gesturePhase = (int)(_idlePhase * 3f) % 3;
        return new AnimationFrame(
            BodySprite: $"body_gesture_{gesturePhase}",
            FaceSprite: $"face_{emotion}",
            MouthSprite: "mouth_rest",
            EyeSprite: $"eyes_{emotion}");
    }

    private AnimationFrame BuildNodFrame(ActorState state, string emotion, float time)
    {
        var nodAngle = (float)(Math.Sin(time * 6f) * 5f); // ±5 degrees
        return new AnimationFrame(
            BodySprite: $"body_standing_{emotion}",
            FaceSprite: $"face_{emotion}",
            MouthSprite: "mouth_rest",
            EyeSprite: $"eyes_{emotion}",
            HeadRotation: nodAngle);
    }

    private AnimationFrame BuildReactFrame(ActorState state, string emotion)
    {
        return new AnimationFrame(
            BodySprite: $"body_react_{emotion}",
            FaceSprite: $"face_react_{emotion}",
            MouthSprite: "mouth_open",
            EyeSprite: $"eyes_wide");
    }
}

public record AnimationFrame(
    string BodySprite,
    string FaceSprite,
    string MouthSprite,
    string EyeSprite,
    float BodyOffsetY = 0f,
    float HeadOffsetY = 0f,
    float HeadRotation = 0f);
