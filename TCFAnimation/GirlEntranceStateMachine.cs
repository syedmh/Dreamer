using System;

namespace TCFAnimation;

public enum GirlEntrancePhase
{
    Hidden,
    EnteringFromLeft,
    Visible,
}

public enum GirlCaptureSnapshot
{
    Start,
    Mid,
    Final,
}

public sealed class GirlEntranceStateMachine
{
    public const int FrameCount = 12;

    private double _startX;
    private double _centerX;
    private double _elapsedSeconds;
    private double _durationSeconds;

    public GirlEntrancePhase Phase { get; private set; } =
        GirlEntrancePhase.Hidden;

    public double CharacterX { get; private set; } =
        AnimationGeometry.ViewportWidth / 2.0;

    public int CurrentFrame { get; private set; }

    public bool IsVisible => Phase != GirlEntrancePhase.Hidden;

    public bool IsEntering =>
        Phase == GirlEntrancePhase.EnteringFromLeft;

    public bool TryStart(double leftOffscreenX, double centerX)
    {
        ValidateFinite(leftOffscreenX, nameof(leftOffscreenX));
        ValidateFinite(centerX, nameof(centerX));
        if (Phase != GirlEntrancePhase.Hidden)
        {
            return false;
        }

        Phase = GirlEntrancePhase.EnteringFromLeft;
        CharacterX = leftOffscreenX;
        CurrentFrame = 0;
        _startX = leftOffscreenX;
        _centerX = centerX;
        _elapsedSeconds = 0.0;
        _durationSeconds = AnimationConfig.Current.GirlEntrySeconds;
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
        if (!IsEntering || deltaSeconds == 0.0)
        {
            return false;
        }

        _elapsedSeconds = Math.Min(
            _elapsedSeconds + deltaSeconds,
            _durationSeconds);
        if (_elapsedSeconds >= _durationSeconds)
        {
            CharacterX = _centerX;
            CurrentFrame = FrameCount - 1;
            Phase = GirlEntrancePhase.Visible;
            return true;
        }

        double progress = _elapsedSeconds / _durationSeconds;
        CharacterX = Lerp(_startX, _centerX, progress);
        CurrentFrame = CalculateEnteringFrame(_elapsedSeconds);
        return true;
    }

    public void SetDevelopmentSnapshot(
        GirlCaptureSnapshot snapshot,
        double leftOffscreenX,
        double centerX)
    {
        if (!Enum.IsDefined(snapshot))
        {
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
        if (!TryStart(leftOffscreenX, centerX))
        {
            throw new InvalidOperationException(
                "Girl development snapshot requires the hidden state.");
        }

        switch (snapshot)
        {
            case GirlCaptureSnapshot.Start:
                return;
            case GirlCaptureSnapshot.Mid:
                Advance(AnimationConfig.Current.GirlEntrySeconds / 2.0);
                return;
            case GirlCaptureSnapshot.Final:
                Advance(AnimationConfig.Current.GirlEntrySeconds);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(snapshot));
        }
    }

    private static int CalculateEnteringFrame(double elapsedSeconds)
    {
        return (int)(
            Math.Floor(
                elapsedSeconds * AnimationConfig.Current.GirlWalkFps)
            % (FrameCount - 1));
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
