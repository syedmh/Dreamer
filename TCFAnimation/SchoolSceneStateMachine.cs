using System;

namespace TCFAnimation;

public enum SchoolScenePhase
{
    NormalBlack,
    ReleasingForEntry,
    PreparingEntryLeft,
    Entering,
    Clapping,
    SchoolIdle,
    PreparingExitRight,
    NormalizingExitBackground,
    Exiting,
    CrossingFinal,
    BlackCrossHold,
    ReleasingFinalHold,
}

public enum SchoolCharacterAnimation
{
    Normal,
    WalkRight,
    WalkLeft,
    Clap,
    CrossArm,
    CrossArmRelease,
}

public enum SchoolSceneSnapshot
{
    EntryPreparationMid,
    EntryPreparationComplete,
    EntryStart,
    EntryMid,
    EntryEnd,
    Clap,
    ExitPreparationMid,
    ExitNormalizationMid,
    ExitMid,
    ExitEnd,
    FinalCrossHold,
}

public readonly record struct SchoolBackgroundLayout(
    float Scale,
    float DisplayWidth,
    float DisplayHeight,
    float CenterX,
    float CenterY,
    float OffscreenLeftX,
    float OffscreenRightX);

public static class SchoolSceneGeometry
{
    public static SchoolBackgroundLayout CalculateAspectCover(
        float viewportWidth,
        float viewportHeight,
        float textureWidth,
        float textureHeight)
    {
        ValidatePositiveFinite(viewportWidth, nameof(viewportWidth));
        ValidatePositiveFinite(viewportHeight, nameof(viewportHeight));
        ValidatePositiveFinite(textureWidth, nameof(textureWidth));
        ValidatePositiveFinite(textureHeight, nameof(textureHeight));

        float scale = MathF.Max(
            viewportWidth / textureWidth,
            viewportHeight / textureHeight);
        float displayWidth = textureWidth * scale;
        float displayHeight = textureHeight * scale;
        return new SchoolBackgroundLayout(
            scale,
            displayWidth,
            displayHeight,
            viewportWidth / 2.0f,
            viewportHeight / 2.0f,
            -displayWidth / 2.0f,
            viewportWidth + displayWidth / 2.0f);
    }

    public static void ValidateLayout(SchoolBackgroundLayout layout)
    {
        ValidatePositiveFinite(layout.Scale, nameof(layout));
        ValidatePositiveFinite(layout.DisplayWidth, nameof(layout));
        ValidatePositiveFinite(layout.DisplayHeight, nameof(layout));
        ValidateFinite(layout.CenterX, nameof(layout));
        ValidateFinite(layout.CenterY, nameof(layout));
        ValidateFinite(layout.OffscreenLeftX, nameof(layout));
        ValidateFinite(layout.OffscreenRightX, nameof(layout));
        if (
            layout.OffscreenLeftX >= layout.CenterX
            || layout.OffscreenRightX <= layout.CenterX
        )
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
    }

    public static float BackgroundCenterX(
        SchoolBackgroundLayout layout,
        double rightOffsetProgress)
    {
        if (
            !double.IsFinite(rightOffsetProgress)
            || rightOffsetProgress is < 0.0 or > 1.0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(rightOffsetProgress));
        }

        return Lerp(
            layout.CenterX,
            layout.OffscreenRightX,
            (float)rightOffsetProgress);
    }

    private static void ValidatePositiveFinite(float value, string name)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void ValidateFinite(float value, string name)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static float Lerp(float from, float to, float weight)
    {
        return from + (to - from) * weight;
    }
}

public sealed class SchoolSceneStateMachine
{
    public const int MinimumSchoolNumber = 1;
    public const int MaximumSchoolNumber = 6;
    public static double EntryDurationSeconds =>
        AnimationConfig.Current.SchoolEntrySeconds;
    public static double CharacterPrePositionFullSpanDurationSeconds =>
        AnimationConfig.Current.SchoolPrepositionSeconds;
    public static double BackgroundNormalizationFullSpanDurationSeconds =>
        AnimationConfig.Current.SchoolBackgroundNormalizationSeconds;
    public static double ExitTravelDurationSeconds =>
        AnimationConfig.Current.SchoolExitSeconds;
    public static double ClapDurationSeconds =>
        AnimationConfig.Current.SchoolClapSeconds;

    private const double Epsilon = 1e-9;

    private double _phaseElapsedSeconds;
    private double _phaseDurationSeconds;
    private double _phaseStartBackgroundProgress;
    private double _phaseStartCharacterProgress;
    private bool _entryEndpointReached;

    public SchoolScenePhase Phase { get; private set; } =
        SchoolScenePhase.NormalBlack;

    public double BackgroundRightOffsetProgress { get; private set; } = 1.0;

    public double CharacterProgress { get; private set; } = 0.5;

    public int? SelectedSchoolNumber { get; private set; }

    public SchoolBackgroundLayout? SelectedBackgroundLayout { get; private set; }

    public float CurrentBackgroundCenterX =>
        SchoolSceneGeometry.BackgroundCenterX(
            SelectedBackgroundLayout
                ?? throw new InvalidOperationException(
                    "No school background layout is selected."),
            BackgroundRightOffsetProgress);

    public double PhaseElapsedSeconds => _phaseElapsedSeconds;

    public bool EntryEndpointReachedOnLastAdvance =>
        _entryEndpointReached;

    public double CurrentPhaseDurationSeconds =>
        GetCurrentPhaseDurationSeconds();

    public double ExitDurationSeconds =>
        Phase == SchoolScenePhase.Exiting
            ? ExitTravelDurationSeconds
            : 0.0;

    public bool IsSchoolVisible =>
        Phase is
            SchoolScenePhase.Entering
            or SchoolScenePhase.Clapping
            or SchoolScenePhase.SchoolIdle
            or SchoolScenePhase.PreparingExitRight
            or SchoolScenePhase.NormalizingExitBackground
            or SchoolScenePhase.Exiting;

    public bool UsesTransparentCharacter => IsSchoolVisible;

    public bool SuppressesOrdinaryInput =>
        Phase is
            SchoolScenePhase.ReleasingForEntry
            or SchoolScenePhase.PreparingEntryLeft
            or SchoolScenePhase.Entering
            or SchoolScenePhase.Clapping
            or SchoolScenePhase.PreparingExitRight
            or SchoolScenePhase.NormalizingExitBackground
            or SchoolScenePhase.Exiting
            or SchoolScenePhase.CrossingFinal
            or SchoolScenePhase.ReleasingFinalHold;

    public bool CanUseNormalTurnControls =>
        Phase is SchoolScenePhase.NormalBlack or SchoolScenePhase.SchoolIdle;

    public bool IsFinalCrossHold => Phase == SchoolScenePhase.BlackCrossHold;

    public SchoolCharacterAnimation CharacterAnimation => Phase switch
    {
        SchoolScenePhase.ReleasingForEntry
            or SchoolScenePhase.ReleasingFinalHold =>
            SchoolCharacterAnimation.CrossArmRelease,
        SchoolScenePhase.PreparingEntryLeft =>
            SchoolCharacterAnimation.WalkLeft,
        SchoolScenePhase.Entering => SchoolCharacterAnimation.WalkRight,
        SchoolScenePhase.Clapping => SchoolCharacterAnimation.Clap,
        SchoolScenePhase.PreparingExitRight =>
            SchoolCharacterAnimation.WalkRight,
        SchoolScenePhase.Exiting => SchoolCharacterAnimation.WalkLeft,
        SchoolScenePhase.CrossingFinal
            or SchoolScenePhase.BlackCrossHold =>
            SchoolCharacterAnimation.CrossArm,
        _ => SchoolCharacterAnimation.Normal,
    };

    public int CurrentAnimationFrame => CharacterAnimation switch
    {
        SchoolCharacterAnimation.WalkLeft
            or SchoolCharacterAnimation.WalkRight =>
            (int)Math.Floor(
                _phaseElapsedSeconds
                * DirectionalTurnStateMachine.RightWalkAnimationFps)
            % DirectionalTurnStateMachine.RightWalkFrameCount,
        SchoolCharacterAnimation.Clap =>
            DirectionalTurnStateMachine.GetClapFrameForStep(
                (int)Math.Floor(
                    _phaseElapsedSeconds
                    * DirectionalTurnStateMachine.ClapAnimationFps)
                % DirectionalTurnStateMachine.ClapPlaybackStepCount),
        SchoolCharacterAnimation.CrossArm =>
            Phase == SchoolScenePhase.BlackCrossHold
                ? DirectionalTurnStateMachine.CrossArmFrameCount - 1
                : Math.Min(
                    (int)Math.Floor(
                        _phaseElapsedSeconds
                        * DirectionalTurnStateMachine.CrossArmAnimationFps),
                    DirectionalTurnStateMachine.CrossArmFrameCount - 1),
        SchoolCharacterAnimation.CrossArmRelease =>
            Math.Min(
                (int)Math.Floor(
                    _phaseElapsedSeconds
                    * DirectionalTurnStateMachine.CrossArmReleaseAnimationFps),
                DirectionalTurnStateMachine.CrossArmReleaseFrameCount - 1),
        _ => 0,
    };

    public bool TryStartEntry(
        int schoolNumber,
        SchoolBackgroundLayout backgroundLayout,
        bool pressed,
        bool echo,
        bool dialogueEditing,
        double currentCharacterProgress,
        bool existingCrossHold = false)
    {
        ValidateSchoolNumber(schoolNumber);
        SchoolSceneGeometry.ValidateLayout(backgroundLayout);

        if (!pressed || echo || dialogueEditing)
        {
            return false;
        }

        if (
            Phase is not (
                SchoolScenePhase.NormalBlack
                or SchoolScenePhase.BlackCrossHold)
        )
        {
            return false;
        }

        ValidateProgress(
            currentCharacterProgress,
            nameof(currentCharacterProgress));

        CharacterProgress = currentCharacterProgress;
        BackgroundRightOffsetProgress = 1.0;
        SelectedSchoolNumber = schoolNumber;
        SelectedBackgroundLayout = backgroundLayout;
        if (
            existingCrossHold
            || Phase == SchoolScenePhase.BlackCrossHold
        )
        {
            SetPhase(SchoolScenePhase.ReleasingForEntry);
        }
        else
        {
            BeginPreparingEntry();
        }

        return true;
    }

    public bool TryStartExit(
        bool pressed,
        bool echo,
        bool dialogueEditing,
        double currentCharacterProgress)
    {
        if (!pressed || echo || dialogueEditing)
        {
            return false;
        }

        if (
            Phase is not (
                SchoolScenePhase.Entering
                or SchoolScenePhase.Clapping
                or SchoolScenePhase.SchoolIdle)
        )
        {
            return false;
        }

        if (
            !double.IsFinite(currentCharacterProgress)
            || currentCharacterProgress is < 0.0 or > 1.0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentCharacterProgress));
        }

        CharacterProgress = currentCharacterProgress;
        BeginPreparingExit();
        return true;
    }

    public bool TryReleaseFinalCrossHold(
        bool pressed,
        bool echo,
        bool dialogueEditing)
    {
        if (
            !pressed
            || echo
            || dialogueEditing
            || Phase != SchoolScenePhase.BlackCrossHold
        )
        {
            return false;
        }

        SetPhase(SchoolScenePhase.ReleasingFinalHold);
        return true;
    }

    public bool Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Frame delta must be finite and non-negative.");
        }

        _entryEndpointReached = false;
        SchoolScenePhase initialPhase = Phase;
        double initialBackground = BackgroundRightOffsetProgress;
        double initialCharacter = CharacterProgress;
        int initialFrame = CurrentAnimationFrame;
        double remaining = deltaSeconds;

        for (int transitionCount = 0; transitionCount < 8; transitionCount++)
        {
            double duration = GetCurrentPhaseDurationSeconds();
            if (duration <= Epsilon || remaining <= Epsilon)
            {
                break;
            }

            double available = duration - _phaseElapsedSeconds;
            double consumed = Math.Min(remaining, available);
            _phaseElapsedSeconds += consumed;
            remaining -= consumed;
            UpdateProgress();

            if (_phaseElapsedSeconds + Epsilon < duration)
            {
                break;
            }

            CompleteCurrentPhase();
        }

        return
            Phase != initialPhase
            || Math.Abs(
                BackgroundRightOffsetProgress - initialBackground) > Epsilon
            || Math.Abs(CharacterProgress - initialCharacter) > Epsilon
            || CurrentAnimationFrame != initialFrame;
    }

    public void CancelToBlack(double currentCharacterProgress)
    {
        ValidateProgress(
            currentCharacterProgress,
            nameof(currentCharacterProgress));
        CharacterProgress = currentCharacterProgress;
        BackgroundRightOffsetProgress = 1.0;
        SelectedSchoolNumber = null;
        SelectedBackgroundLayout = null;
        _entryEndpointReached = false;
        SetPhase(SchoolScenePhase.NormalBlack);
    }

    public void SetDevelopmentSnapshot(
        int schoolNumber,
        SchoolBackgroundLayout backgroundLayout,
        SchoolSceneSnapshot snapshot)
    {
        ValidateSchoolNumber(schoolNumber);
        SchoolSceneGeometry.ValidateLayout(backgroundLayout);
        SelectedSchoolNumber = schoolNumber;
        SelectedBackgroundLayout = backgroundLayout;

        switch (snapshot)
        {
            case SchoolSceneSnapshot.EntryPreparationMid:
                CharacterProgress = 1.0;
                BackgroundRightOffsetProgress = 1.0;
                BeginPreparingEntry();
                _phaseElapsedSeconds =
                    CharacterPrePositionFullSpanDurationSeconds / 2.0;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.EntryPreparationComplete:
                CharacterProgress = 1.0;
                BackgroundRightOffsetProgress = 1.0;
                BeginPreparingEntry();
                _phaseElapsedSeconds =
                    CharacterPrePositionFullSpanDurationSeconds;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.EntryStart:
                BeginEntry();
                break;
            case SchoolSceneSnapshot.EntryMid:
                BeginEntry();
                _phaseElapsedSeconds = EntryDurationSeconds / 2.0;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.EntryEnd:
                SetPhase(SchoolScenePhase.SchoolIdle);
                BackgroundRightOffsetProgress = 0.0;
                CharacterProgress = 1.0;
                break;
            case SchoolSceneSnapshot.Clap:
                SetPhase(SchoolScenePhase.Clapping);
                BackgroundRightOffsetProgress = 0.0;
                CharacterProgress = 1.0;
                _phaseElapsedSeconds = 3.0;
                break;
            case SchoolSceneSnapshot.ExitPreparationMid:
                CharacterProgress = 0.0;
                BackgroundRightOffsetProgress = 0.0;
                BeginPreparingExit();
                _phaseElapsedSeconds =
                    CharacterPrePositionFullSpanDurationSeconds / 2.0;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.ExitNormalizationMid:
                CharacterProgress = 0.25;
                BackgroundRightOffsetProgress = 0.75;
                BeginPreparingExit();
                _phaseElapsedSeconds = _phaseDurationSeconds;
                UpdateProgress();
                CompleteCurrentPhase();
                _phaseElapsedSeconds = _phaseDurationSeconds / 2.0;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.ExitMid:
                BeginExit();
                _phaseElapsedSeconds = ExitTravelDurationSeconds / 2.0;
                UpdateProgress();
                break;
            case SchoolSceneSnapshot.ExitEnd:
                SetPhase(SchoolScenePhase.CrossingFinal);
                BackgroundRightOffsetProgress = 1.0;
                CharacterProgress = 0.0;
                break;
            case SchoolSceneSnapshot.FinalCrossHold:
                SetPhase(SchoolScenePhase.BlackCrossHold);
                BackgroundRightOffsetProgress = 1.0;
                CharacterProgress = 0.0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
    }

    private void BeginEntry()
    {
        SetPhase(SchoolScenePhase.Entering);
        BackgroundRightOffsetProgress = 1.0;
        CharacterProgress = 0.0;
    }

    private void BeginPreparingEntry()
    {
        BackgroundRightOffsetProgress = 1.0;
        _phaseStartCharacterProgress = CharacterProgress;
        double duration =
            _phaseStartCharacterProgress
            * CharacterPrePositionFullSpanDurationSeconds;
        if (duration <= Epsilon)
        {
            CharacterProgress = 0.0;
            BeginEntry();
            return;
        }

        SetTimedPhase(
            SchoolScenePhase.PreparingEntryLeft,
            duration);
    }

    private void BeginPreparingExit()
    {
        _phaseStartCharacterProgress = CharacterProgress;
        double duration =
            (1.0 - _phaseStartCharacterProgress)
            * CharacterPrePositionFullSpanDurationSeconds;
        if (duration <= Epsilon)
        {
            CharacterProgress = 1.0;
            BeginExitNormalizationOrExit();
            return;
        }

        SetTimedPhase(
            SchoolScenePhase.PreparingExitRight,
            duration);
    }

    private void BeginExitNormalizationOrExit()
    {
        if (BackgroundRightOffsetProgress <= Epsilon)
        {
            BackgroundRightOffsetProgress = 0.0;
            BeginExit();
            return;
        }

        _phaseStartBackgroundProgress =
            BackgroundRightOffsetProgress;
        SetTimedPhase(
            SchoolScenePhase.NormalizingExitBackground,
            _phaseStartBackgroundProgress
                * BackgroundNormalizationFullSpanDurationSeconds);
    }

    private void BeginExit()
    {
        SetPhase(SchoolScenePhase.Exiting);
        BackgroundRightOffsetProgress = 0.0;
        CharacterProgress = 1.0;
    }

    private double GetCurrentPhaseDurationSeconds()
    {
        return Phase switch
        {
            SchoolScenePhase.ReleasingForEntry
                or SchoolScenePhase.ReleasingFinalHold =>
                DirectionalTurnStateMachine.CrossArmReleaseFrameCount
                / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps,
            SchoolScenePhase.PreparingEntryLeft
                or SchoolScenePhase.PreparingExitRight
                or SchoolScenePhase.NormalizingExitBackground =>
                _phaseDurationSeconds,
            SchoolScenePhase.Entering => EntryDurationSeconds,
            SchoolScenePhase.Clapping => ClapDurationSeconds,
            SchoolScenePhase.Exiting => ExitTravelDurationSeconds,
            SchoolScenePhase.CrossingFinal =>
                DirectionalTurnStateMachine.CrossArmFrameCount
                / DirectionalTurnStateMachine.CrossArmAnimationFps,
            _ => 0.0,
        };
    }

    private void UpdateProgress()
    {
        switch (Phase)
        {
            case SchoolScenePhase.PreparingEntryLeft:
                double entryPreparationProgress = Math.Clamp(
                    _phaseElapsedSeconds / _phaseDurationSeconds,
                    0.0,
                    1.0);
                CharacterProgress = Lerp(
                    _phaseStartCharacterProgress,
                    0.0,
                    entryPreparationProgress);
                break;
            case SchoolScenePhase.Entering:
                double entryProgress = Math.Clamp(
                    _phaseElapsedSeconds / EntryDurationSeconds,
                    0.0,
                    1.0);
                BackgroundRightOffsetProgress = 1.0 - entryProgress;
                CharacterProgress = entryProgress;
                break;
            case SchoolScenePhase.PreparingExitRight:
                double exitPreparationProgress = Math.Clamp(
                    _phaseElapsedSeconds / _phaseDurationSeconds,
                    0.0,
                    1.0);
                CharacterProgress = Lerp(
                    _phaseStartCharacterProgress,
                    1.0,
                    exitPreparationProgress);
                break;
            case SchoolScenePhase.NormalizingExitBackground:
                double normalizationProgress = Math.Clamp(
                    _phaseElapsedSeconds / _phaseDurationSeconds,
                    0.0,
                    1.0);
                BackgroundRightOffsetProgress = Lerp(
                    _phaseStartBackgroundProgress,
                    0.0,
                    normalizationProgress);
                CharacterProgress = 1.0;
                break;
            case SchoolScenePhase.Exiting:
                double exitProgress = Math.Clamp(
                    _phaseElapsedSeconds / ExitTravelDurationSeconds,
                    0.0,
                    1.0);
                BackgroundRightOffsetProgress = exitProgress;
                CharacterProgress = 1.0 - exitProgress;
                break;
        }
    }

    private void CompleteCurrentPhase()
    {
        switch (Phase)
        {
            case SchoolScenePhase.ReleasingForEntry:
                BeginPreparingEntry();
                break;
            case SchoolScenePhase.PreparingEntryLeft:
                CharacterProgress = 0.0;
                BeginEntry();
                break;
            case SchoolScenePhase.Entering:
                BackgroundRightOffsetProgress = 0.0;
                CharacterProgress = 1.0;
                _entryEndpointReached = true;
                SetPhase(SchoolScenePhase.Clapping);
                break;
            case SchoolScenePhase.Clapping:
                BackgroundRightOffsetProgress = 0.0;
                CharacterProgress = 1.0;
                SetPhase(SchoolScenePhase.SchoolIdle);
                break;
            case SchoolScenePhase.PreparingExitRight:
                CharacterProgress = 1.0;
                BeginExitNormalizationOrExit();
                break;
            case SchoolScenePhase.NormalizingExitBackground:
                BackgroundRightOffsetProgress = 0.0;
                CharacterProgress = 1.0;
                BeginExit();
                break;
            case SchoolScenePhase.Exiting:
                BackgroundRightOffsetProgress = 1.0;
                CharacterProgress = 0.0;
                SetPhase(SchoolScenePhase.CrossingFinal);
                break;
            case SchoolScenePhase.CrossingFinal:
                SetPhase(SchoolScenePhase.BlackCrossHold);
                break;
            case SchoolScenePhase.ReleasingFinalHold:
                BackgroundRightOffsetProgress = 1.0;
                CharacterProgress = 0.0;
                SetPhase(SchoolScenePhase.NormalBlack);
                SelectedSchoolNumber = null;
                SelectedBackgroundLayout = null;
                break;
        }
    }

    private void SetPhase(SchoolScenePhase phase)
    {
        Phase = phase;
        _phaseElapsedSeconds = 0.0;
        _phaseDurationSeconds = 0.0;
    }

    private void SetTimedPhase(
        SchoolScenePhase phase,
        double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        SetPhase(phase);
        _phaseDurationSeconds = durationSeconds;
    }

    private static void ValidateProgress(double progress, string name)
    {
        if (!double.IsFinite(progress) || progress is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static void ValidateSchoolNumber(int schoolNumber)
    {
        if (
            schoolNumber is
                < MinimumSchoolNumber
                or > MaximumSchoolNumber
        )
        {
            throw new ArgumentOutOfRangeException(nameof(schoolNumber));
        }
    }

    private static double Lerp(double from, double to, double weight)
    {
        return from + (to - from) * weight;
    }
}
