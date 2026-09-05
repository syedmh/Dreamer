using System;

namespace TCFAnimation;

public enum AvatarPresencePhase
{
    Hidden,
    EnteringFromRight,
    Visible,
    ExitingLeft,
    ExitingRight,
}

public sealed class AvatarPresenceStateMachine
{
    private const double Epsilon = 0.0000001;

    private double _startX;
    private double _targetX;
    private double _elapsedSeconds;
    private double _durationSeconds;

    public AvatarPresencePhase Phase { get; private set; } =
        AvatarPresencePhase.Hidden;

    public double CharacterX { get; private set; } =
        AnimationGeometry.ViewportWidth / 2.0;

    public bool IsVisible => Phase != AvatarPresencePhase.Hidden;

    public bool IsTransitioning =>
        Phase is
            AvatarPresencePhase.EnteringFromRight
            or AvatarPresencePhase.ExitingLeft
            or AvatarPresencePhase.ExitingRight;

    public bool IsWalkingLeft =>
        Phase is
            AvatarPresencePhase.EnteringFromRight
            or AvatarPresencePhase.ExitingLeft;

    public bool IsWalkingRight =>
        Phase == AvatarPresencePhase.ExitingRight;

    public int CurrentWalkFrame =>
        IsTransitioning
            ? (int)Math.Floor(
                    _elapsedSeconds * AnimationConfig.Current.WalkFps)
                % DirectionalTurnStateMachine.LeftWalkFrameCount
            : 0;

    public bool StartEnterFromRight(double rightOffscreenX, double centerX)
    {
        ValidateFinite(rightOffscreenX, nameof(rightOffscreenX));
        ValidateFinite(centerX, nameof(centerX));
        if (rightOffscreenX <= centerX)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rightOffscreenX));
        }
        if (Phase == AvatarPresencePhase.EnteringFromRight)
        {
            return false;
        }

        SetTransition(
            AvatarPresencePhase.EnteringFromRight,
            rightOffscreenX,
            centerX,
            AnimationConfig.Current.AvatarEntrySeconds);
        return true;
    }

    public bool StartExit(
        double currentX,
        double leftOffscreenX,
        double rightOffscreenX)
    {
        ValidateFinite(currentX, nameof(currentX));
        ValidateFinite(leftOffscreenX, nameof(leftOffscreenX));
        ValidateFinite(rightOffscreenX, nameof(rightOffscreenX));
        if (leftOffscreenX >= rightOffscreenX)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leftOffscreenX));
        }
        if (!IsVisible)
        {
            return false;
        }

        double clampedX = Math.Clamp(
            currentX,
            leftOffscreenX,
            rightOffscreenX);
        double leftDistance = clampedX - leftOffscreenX;
        double rightDistance = rightOffscreenX - clampedX;
        bool exitLeft = leftDistance <= rightDistance;
        double targetX = exitLeft
            ? leftOffscreenX
            : rightOffscreenX;
        double distance = exitLeft ? leftDistance : rightDistance;
        double fullSpan = rightOffscreenX - leftOffscreenX;
        double duration =
            distance
            / fullSpan
            * AnimationConfig.Current.AvatarExitFullSpanSeconds;

        if (duration <= Epsilon)
        {
            CharacterX = targetX;
            SetPhase(AvatarPresencePhase.Hidden);
            return true;
        }

        SetTransition(
            exitLeft
                ? AvatarPresencePhase.ExitingLeft
                : AvatarPresencePhase.ExitingRight,
            clampedX,
            targetX,
            duration);
        return true;
    }

    public bool Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        }
        if (!IsTransitioning || deltaSeconds == 0.0)
        {
            return false;
        }

        _elapsedSeconds = Math.Min(
            _elapsedSeconds + deltaSeconds,
            _durationSeconds);
        double progress = _elapsedSeconds / _durationSeconds;
        CharacterX = Lerp(_startX, _targetX, progress);
        if (_elapsedSeconds + Epsilon < _durationSeconds)
        {
            return true;
        }

        CharacterX = _targetX;
        SetPhase(
            Phase == AvatarPresencePhase.EnteringFromRight
                ? AvatarPresencePhase.Visible
                : AvatarPresencePhase.Hidden);
        return true;
    }

    public void SetVisible(double characterX)
    {
        ValidateFinite(characterX, nameof(characterX));
        CharacterX = characterX;
        SetPhase(AvatarPresencePhase.Visible);
    }

    private void SetTransition(
        AvatarPresencePhase phase,
        double startX,
        double targetX,
        double durationSeconds)
    {
        if (
            phase is not (
                AvatarPresencePhase.EnteringFromRight
                or AvatarPresencePhase.ExitingLeft
                or AvatarPresencePhase.ExitingRight)
            || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0.0
        )
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        Phase = phase;
        CharacterX = startX;
        _startX = startX;
        _targetX = targetX;
        _elapsedSeconds = 0.0;
        _durationSeconds = durationSeconds;
    }

    private void SetPhase(AvatarPresencePhase phase)
    {
        Phase = phase;
        _startX = CharacterX;
        _targetX = CharacterX;
        _elapsedSeconds = 0.0;
        _durationSeconds = 0.0;
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * Math.Clamp(progress, 0.0, 1.0);
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
