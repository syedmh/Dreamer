namespace TCFAnimation;

public enum PresentationKey
{
    Other,
    C,
    D,
    E,
    F,
    G,
    I,
    L,
    N,
    O,
    R,
    S,
    Zero,
    School,
}

public readonly record struct PresentationInputDecision(
    bool ToggleLegend,
    bool HideBubble,
    bool StartCelebration,
    bool StopFireworks,
    bool AllowSchoolAction,
    bool StartLogoRain,
    bool ExitAvatar,
    bool EnterAvatar)
{
    public bool StartGirlEntrance { get; init; }
    public bool ToggleNeonBackground { get; init; }
    public bool ToggleNeonAnimation { get; init; }
    public bool ToggleNeonColorCycle { get; init; }
}

public static class PresentationInputPolicy
{
    public static bool ShouldUseTransparentCharacter(
        bool schoolOverlay,
        bool logoRainActive,
        bool neonBackgroundVisible = false)
    {
        return
            schoolOverlay
            || logoRainActive
            || neonBackgroundVisible;
    }

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
                    false,
                    false,
                    false,
                    false),
            PresentationKey.N =>
                new PresentationInputDecision
                {
                    ToggleNeonBackground = true,
                },
            PresentationKey.O =>
                new PresentationInputDecision
                {
                    ToggleNeonAnimation = true,
                },
            PresentationKey.I =>
                new PresentationInputDecision
                {
                    ToggleNeonColorCycle = true,
                },
            PresentationKey.G =>
                new PresentationInputDecision
                {
                    StartGirlEntrance = true,
                },
            PresentationKey.D =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    true,
                    false),
            PresentationKey.E =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    true),
            PresentationKey.Zero =>
                new PresentationInputDecision(
                    false,
                    true,
                    false,
                    false,
                    celebrationPhase == CelebrationPhase.Inactive,
                    false,
                    false,
                    false),
            PresentationKey.F =>
                new PresentationInputDecision(
                    false,
                    false,
                    celebrationPhase is
                        CelebrationPhase.Inactive
                        or CelebrationPhase.StoppedCrossHold,
                    false,
                    false,
                    false,
                    false,
                    false),
            PresentationKey.S =>
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
                    false,
                    false,
                    false,
                    false),
            PresentationKey.R =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    false,
                    false,
                    true,
                    false,
                    false),
            PresentationKey.School =>
                new PresentationInputDecision(
                    false,
                    false,
                    false,
                    false,
                    celebrationPhase is
                        CelebrationPhase.Inactive
                        or CelebrationPhase.StoppedCrossHold,
                    false,
                    false,
                    false),
            _ => default,
        };
    }
}
