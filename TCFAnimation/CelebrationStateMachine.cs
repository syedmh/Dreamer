using System;

namespace TCFAnimation;

public enum CelebrationPhase
{
    Inactive,
    ReleasingForCelebration,
    WalkingToCenter,
    Clapping,
    Crossing,
    FireworksHold,
    StoppedCrossHold,
    ReleasingToNormal,
}

public enum CelebrationCharacterAnimation
{
    Normal,
    WalkLeft,
    WalkRight,
    Clap,
    CrossArm,
    CrossArmRelease,
}

public enum CelebrationSnapshot
{
    WalkingToCenter,
    CenteredClapping,
    CrossedWithFireworks,
    StoppedWithMessage,
}

public sealed class CelebrationStateMachine
{
    public static double ClapDurationSeconds =>
        AnimationConfig.Current.CelebrationClapSeconds;
    public static double FullSpanWalkDurationSeconds =>
        AnimationConfig.Current.CelebrationWalkSeconds;

    private const double Epsilon = 1e-9;

    private double _phaseElapsedSeconds;
    private double _phaseDurationSeconds;
    private double _walkStartX;
    private double _centerX;
    private double _fullSpanDistance;
    private bool _fireworksActive;

    public CelebrationPhase Phase { get; private set; } =
        CelebrationPhase.Inactive;

    public double CharacterX { get; private set; }

    public TurnDirection WalkDirection { get; private set; } =
        TurnDirection.Right;

    public int CenterArrivalSerial { get; private set; }

    public int FireworksStartSerial { get; private set; }

    public double PhaseElapsedSeconds => _phaseElapsedSeconds;

    public double CurrentPhaseDurationSeconds => Phase switch
    {
        CelebrationPhase.ReleasingForCelebration
            or CelebrationPhase.ReleasingToNormal =>
            DirectionalTurnStateMachine.CrossArmReleaseFrameCount
            / DirectionalTurnStateMachine.CrossArmReleaseAnimationFps,
        CelebrationPhase.WalkingToCenter => _phaseDurationSeconds,
        CelebrationPhase.Clapping => ClapDurationSeconds,
        CelebrationPhase.Crossing =>
            DirectionalTurnStateMachine.CrossArmFrameCount
            / DirectionalTurnStateMachine.CrossArmAnimationFps,
        _ => 0.0,
    };

    public bool IsActive => Phase != CelebrationPhase.Inactive;

    public bool IsFireworksActive => _fireworksActive;

    public bool IsStoppedCrossHold =>
        Phase == CelebrationPhase.StoppedCrossHold;

    public bool SuppressesOrdinaryInput =>
        Phase is not (
            CelebrationPhase.Inactive
            or CelebrationPhase.StoppedCrossHold);

    public CelebrationCharacterAnimation CharacterAnimation => Phase switch
    {
        CelebrationPhase.ReleasingForCelebration
            or CelebrationPhase.ReleasingToNormal =>
            CelebrationCharacterAnimation.CrossArmRelease,
        CelebrationPhase.WalkingToCenter =>
            WalkDirection == TurnDirection.Left
                ? CelebrationCharacterAnimation.WalkLeft
                : CelebrationCharacterAnimation.WalkRight,
        CelebrationPhase.Clapping => CelebrationCharacterAnimation.Clap,
        CelebrationPhase.Crossing
            or CelebrationPhase.FireworksHold
            or CelebrationPhase.StoppedCrossHold =>
            CelebrationCharacterAnimation.CrossArm,
        _ => CelebrationCharacterAnimation.Normal,
    };

    public int CurrentAnimationFrame => CharacterAnimation switch
    {
        CelebrationCharacterAnimation.WalkLeft
            or CelebrationCharacterAnimation.WalkRight =>
            (int)Math.Floor(
                _phaseElapsedSeconds
                * DirectionalTurnStateMachine.RightWalkAnimationFps)
            % DirectionalTurnStateMachine.RightWalkFrameCount,
        CelebrationCharacterAnimation.Clap =>
            DirectionalTurnStateMachine.GetClapFrameForStep(
                (int)Math.Floor(
                    _phaseElapsedSeconds
                    * DirectionalTurnStateMachine.ClapAnimationFps)
                % DirectionalTurnStateMachine.ClapPlaybackStepCount),
        CelebrationCharacterAnimation.CrossArm =>
            Phase is
                CelebrationPhase.FireworksHold
                or CelebrationPhase.StoppedCrossHold
                ? DirectionalTurnStateMachine.CrossArmFrameCount - 1
                : Math.Min(
                    (int)Math.Floor(
                        _phaseElapsedSeconds
                        * DirectionalTurnStateMachine.CrossArmAnimationFps),
                    DirectionalTurnStateMachine.CrossArmFrameCount - 1),
        CelebrationCharacterAnimation.CrossArmRelease =>
            Math.Min(
                (int)Math.Floor(
                    _phaseElapsedSeconds
                    * DirectionalTurnStateMachine.CrossArmReleaseAnimationFps),
                DirectionalTurnStateMachine.CrossArmReleaseFrameCount - 1),
        _ => 0,
    };

    public bool TryStart(
        double currentCharacterX,
        double centerX,
        double fullSpanDistance,
        bool existingCrossHold)
    {
        ValidateFinite(currentCharacterX, nameof(currentCharacterX));
        ValidateFinite(centerX, nameof(centerX));
        if (
            !double.IsFinite(fullSpanDistance)
            || fullSpanDistance <= 0.0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(fullSpanDistance));
        }

        if (
            Phase is not (
                CelebrationPhase.Inactive
                or CelebrationPhase.StoppedCrossHold)
        )
        {
            return false;
        }

        CharacterX = currentCharacterX;
        _centerX = centerX;
        _fullSpanDistance = fullSpanDistance;
        _walkStartX = currentCharacterX;
        bool mustRelease =
            existingCrossHold
            || Phase == CelebrationPhase.StoppedCrossHold;
        if (mustRelease)
        {
            SetPhase(CelebrationPhase.ReleasingForCelebration);
        }
        else
        {
            BeginWalk(fullSpanDistance);
        }
        StartFireworks();

        return true;
    }

    public bool TryStopFireworks()
    {
        if (!_fireworksActive)
        {
            return false;
        }

        _fireworksActive = false;
        CharacterX = _centerX;
        SetPhase(CelebrationPhase.Inactive);
        return true;
    }

    public bool TryReleaseStoppedHold()
    {
        if (Phase != CelebrationPhase.StoppedCrossHold)
        {
            return false;
        }

        SetPhase(CelebrationPhase.ReleasingToNormal);
        return true;
    }

    public void Cancel()
    {
        _fireworksActive = false;
        SetPhase(CelebrationPhase.Inactive);
    }

    public bool Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Frame delta must be finite and non-negative.");
        }

        CelebrationPhase initialPhase = Phase;
        double initialX = CharacterX;
        int initialFrame = CurrentAnimationFrame;
        double remaining = deltaSeconds;

        for (int transitionCount = 0; transitionCount < 8; transitionCount++)
        {
            double duration = CurrentPhaseDurationSeconds;
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
            || Math.Abs(CharacterX - initialX) > Epsilon
            || CurrentAnimationFrame != initialFrame;
    }

    public void SetDevelopmentSnapshot(
        CelebrationSnapshot snapshot,
        double centerX)
    {
        ValidateFinite(centerX, nameof(centerX));
        _centerX = centerX;
        _walkStartX = centerX;
        CharacterX = centerX;
        switch (snapshot)
        {
            case CelebrationSnapshot.WalkingToCenter:
                _fullSpanDistance =
                    AnimationGeometry.DefaultSafeCenters.Right
                    - AnimationGeometry.DefaultSafeCenters.Left;
                CharacterX = centerX - 520.0;
                _walkStartX = CharacterX;
                WalkDirection = TurnDirection.Right;
                SetTimedPhase(
                    CelebrationPhase.WalkingToCenter,
                    520.0 / _fullSpanDistance
                        * FullSpanWalkDurationSeconds);
                _phaseElapsedSeconds = _phaseDurationSeconds / 2.0;
                UpdateProgress();
                StartFireworks();
                break;
            case CelebrationSnapshot.CenteredClapping:
                SetPhase(CelebrationPhase.Clapping);
                _phaseElapsedSeconds = 3.0;
                CenterArrivalSerial++;
                StartFireworks();
                break;
            case CelebrationSnapshot.CrossedWithFireworks:
                SetPhase(CelebrationPhase.FireworksHold);
                StartFireworks();
                break;
            case CelebrationSnapshot.StoppedWithMessage:
                _fireworksActive = false;
                SetPhase(CelebrationPhase.Inactive);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
    }

    private void BeginWalk(double fullSpanDistance)
    {
        double distance = Math.Abs(_centerX - CharacterX);
        if (distance <= Epsilon)
        {
            CharacterX = _centerX;
            BeginClap();
            return;
        }

        _walkStartX = CharacterX;
        WalkDirection =
            _centerX < CharacterX
                ? TurnDirection.Left
                : TurnDirection.Right;
        SetTimedPhase(
            CelebrationPhase.WalkingToCenter,
            distance / fullSpanDistance * FullSpanWalkDurationSeconds);
    }

    private void BeginClap()
    {
        CharacterX = _centerX;
        SetPhase(CelebrationPhase.Clapping);
        CenterArrivalSerial++;
    }

    private void UpdateProgress()
    {
        if (Phase != CelebrationPhase.WalkingToCenter)
        {
            return;
        }

        double progress = Math.Clamp(
            _phaseElapsedSeconds / _phaseDurationSeconds,
            0.0,
            1.0);
        CharacterX = Lerp(_walkStartX, _centerX, progress);
    }

    private void CompleteCurrentPhase()
    {
        switch (Phase)
        {
            case CelebrationPhase.ReleasingForCelebration:
                BeginWalk(_fullSpanDistance);
                break;
            case CelebrationPhase.WalkingToCenter:
                BeginClap();
                break;
            case CelebrationPhase.Clapping:
                CharacterX = _centerX;
                SetPhase(CelebrationPhase.Crossing);
                break;
            case CelebrationPhase.Crossing:
                CharacterX = _centerX;
                SetPhase(
                    _fireworksActive
                        ? CelebrationPhase.FireworksHold
                        : CelebrationPhase.StoppedCrossHold);
                break;
            case CelebrationPhase.ReleasingToNormal:
                SetPhase(CelebrationPhase.Inactive);
                break;
        }
    }

    private void SetPhase(CelebrationPhase phase)
    {
        Phase = phase;
        _phaseElapsedSeconds = 0.0;
        _phaseDurationSeconds = 0.0;
    }

    private void SetTimedPhase(
        CelebrationPhase phase,
        double durationSeconds)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        SetPhase(phase);
        _phaseDurationSeconds = durationSeconds;
    }

    private void StartFireworks()
    {
        _fireworksActive = true;
        FireworksStartSerial++;
    }

    private static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name);
        }
    }

    private static double Lerp(double from, double to, double weight)
    {
        return from + (to - from) * weight;
    }
}
