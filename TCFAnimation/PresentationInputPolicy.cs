namespace TCFAnimation;

public enum PresentationKey
{
    Other,
    C,
    L,
    Q,
    R,
    Zero,
    School,
}

public readonly record struct PresentationInputDecision(
    bool ToggleLegend,
    bool HideBubble,
    bool StartCelebration,
    bool StopFireworks,
    bool AllowSchoolAction);

public static class PresentationInputPolicy
{
    public static PresentationInputDecision Resolve(
        PresentationKey key,
        bool pressed,
        bool echo,
        bool dialogueEditing,
        CelebrationPhase celebrationPhase)
    {
        if (!pressed || echo || dialogueEditing)
        {
            return default;
        }

        return key switch
        {
            PresentationKey.L =>
                new PresentationInputDecision(
                    true,
                    false,
                    false,
                    false,
                    false),
            PresentationKey.Zero =>
                new PresentationInputDecision(
                    false,
                    true,
                    false,
                    false,
                    celebrationPhase == CelebrationPhase.Inactive),
            PresentationKey.Q =>
                new PresentationInputDecision(
                    false,
                    false,
                    celebrationPhase is
                        CelebrationPhase.Inactive
                        or CelebrationPhase.StoppedCrossHold,
                    false,
                    false),
            PresentationKey.R =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    celebrationPhase is
                        CelebrationPhase.ReleasingForCelebration
                        or CelebrationPhase.WalkingToCenter
                        or CelebrationPhase.Clapping
                        or CelebrationPhase.Crossing
                        or CelebrationPhase.FireworksHold,
                    false),
            PresentationKey.School =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    false,
                    celebrationPhase is
                        CelebrationPhase.Inactive
                        or CelebrationPhase.StoppedCrossHold),
            _ => default,
        };
    }
}
