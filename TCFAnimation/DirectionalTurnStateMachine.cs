using System;

namespace TCFAnimation;

public enum TurnDirection
{
    Left,
    Right,
}

public sealed class DirectionalTurnStateMachine
{
    public const int FrontFrame = 0;
    public const int HalfTurnFrame = 1;
    public const int FullTurnFrame = 2;
    public const double LeftWalkAnimationFps = 6.0;
    public const int LeftWalkFrameCount = 6;
    public const double RightWalkAnimationFps = 6.0;
    public const int RightWalkFrameCount = 6;

    private double _elapsedSeconds;
    private double _walkElapsedSeconds;
    private TurnRequest _lastRequest;
    private TurnDirection? _walkingDirection;

    public DirectionalTurnStateMachine(double animationFramesPerSecond)
    {
        if (!double.IsFinite(animationFramesPerSecond) || animationFramesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationFramesPerSecond),
                "Animation speed must be a finite positive number.");
        }

        FrameDurationSeconds = 1.0 / animationFramesPerSecond;
        LeftWalkFrameDurationSeconds = 1.0 / LeftWalkAnimationFps;
        RightWalkFrameDurationSeconds = 1.0 / RightWalkAnimationFps;
    }

    public TurnDirection CurrentDirection { get; private set; } = TurnDirection.Left;

    public int CurrentFrame { get; private set; } = FrontFrame;

    public double FrameDurationSeconds { get; }

    public bool IsWalking => _walkingDirection.HasValue;

    public bool IsWalkingLeft => _walkingDirection == TurnDirection.Left;

    public bool IsWalkingRight => _walkingDirection == TurnDirection.Right;

    public int CurrentWalkFrame { get; private set; }

    public bool IsLeftEdgeLatched { get; private set; }

    public bool IsRightEdgeLatched { get; private set; }

    public double LeftWalkFrameDurationSeconds { get; }

    public double RightWalkFrameDurationSeconds { get; }

    public bool Advance(
        bool leftHeld,
        bool rightHeld,
        double deltaSeconds,
        double walkPlaybackMultiplier = 1.0)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Frame delta must be finite and non-negative.");
        }

        if (
            !double.IsFinite(walkPlaybackMultiplier)
            || walkPlaybackMultiplier <= 0.0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(walkPlaybackMultiplier),
                "Walk playback multiplier must be a finite positive number.");
        }

        if (!leftHeld)
        {
            IsLeftEdgeLatched = false;
        }

        if (!rightHeld)
        {
            IsRightEdgeLatched = false;
        }

        TurnRequest request = GetRequest(leftHeld, rightHeld);
        if (IsLeftEdgeLatched && leftHeld)
        {
            request = TurnRequest.Neutral;
        }

        if (IsRightEdgeLatched && rightHeld)
        {
            request = TurnRequest.Neutral;
        }

        if (_walkingDirection.HasValue)
        {
            TurnDirection walkingDirection = _walkingDirection.Value;
            TurnRequest walkingRequest = GetRequest(walkingDirection);
            if (request != walkingRequest)
            {
                ExitWalking();
                _lastRequest = request;
                return true;
            }

            double walkFrameDurationSeconds =
                GetWalkFrameDurationSeconds(walkingDirection);
            int walkFrameCount = GetWalkFrameCount(walkingDirection);
            double walkCycleSeconds =
                walkFrameDurationSeconds * walkFrameCount;
            double scaledWalkDeltaSeconds =
                deltaSeconds * walkPlaybackMultiplier;
            _walkElapsedSeconds += scaledWalkDeltaSeconds % walkCycleSeconds;
            int elapsedFrames =
                (int)(_walkElapsedSeconds / walkFrameDurationSeconds);
            if (elapsedFrames == 0)
            {
                return false;
            }

            int previousFrame = CurrentWalkFrame;
            CurrentWalkFrame =
                (CurrentWalkFrame + elapsedFrames) % walkFrameCount;
            _walkElapsedSeconds -=
                elapsedFrames * walkFrameDurationSeconds;
            return CurrentWalkFrame != previousFrame;
        }

        if (request != _lastRequest)
        {
            _elapsedSeconds = 0.0;
            _lastRequest = request;
        }

        TurnDirection? requestedDirection = request switch
        {
            TurnRequest.Left => TurnDirection.Left,
            TurnRequest.Right => TurnDirection.Right,
            _ => null,
        };

        if (
            requestedDirection.HasValue
            && CurrentDirection == requestedDirection.Value
            && CurrentFrame == FullTurnFrame
        )
        {
            _walkingDirection = requestedDirection.Value;
            CurrentWalkFrame = 0;
            _walkElapsedSeconds = 0.0;
            _elapsedSeconds = 0.0;
            return true;
        }

        // At front, changing direction is its own visible state change. The
        // requested sheet's front texture is shown before its 45-degree frame.
        if (
            requestedDirection.HasValue
            && CurrentFrame == FrontFrame
            && CurrentDirection != requestedDirection.Value
        )
        {
            CurrentDirection = requestedDirection.Value;
            _elapsedSeconds = 0.0;
            return true;
        }

        int targetFrame =
            requestedDirection == CurrentDirection ? FullTurnFrame : FrontFrame;
        if (CurrentFrame == targetFrame)
        {
            _elapsedSeconds = 0.0;
            return false;
        }

        _elapsedSeconds += deltaSeconds;
        bool changed = false;

        while (_elapsedSeconds >= FrameDurationSeconds && CurrentFrame != targetFrame)
        {
            CurrentFrame += CurrentFrame < targetFrame ? 1 : -1;
            _elapsedSeconds -= FrameDurationSeconds;
            changed = true;
        }

        if (CurrentFrame == targetFrame)
        {
            // Discard excess time at an endpoint. In particular, a direct
            // reversal must display the active direction's front before the
            // next call switches to the requested direction's front.
            _elapsedSeconds = 0.0;
        }

        return changed;
    }

    public bool NotifyLeftEdgeReached()
    {
        if (!IsWalkingLeft)
        {
            return false;
        }

        ExitWalking();
        IsLeftEdgeLatched = true;
        _lastRequest = TurnRequest.Neutral;
        return true;
    }

    public bool NotifyRightEdgeReached()
    {
        if (!IsWalkingRight)
        {
            return false;
        }

        ExitWalking();
        IsRightEdgeLatched = true;
        _lastRequest = TurnRequest.Neutral;
        return true;
    }

    public void Reset(
        TurnDirection direction = TurnDirection.Left,
        int frame = FrontFrame)
    {
        if (frame is < FrontFrame or > FullTurnFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        CurrentDirection = direction;
        CurrentFrame = frame;
        _elapsedSeconds = 0.0;
        _walkElapsedSeconds = 0.0;
        _lastRequest = TurnRequest.Neutral;
        _walkingDirection = null;
        CurrentWalkFrame = 0;
        IsLeftEdgeLatched = false;
        IsRightEdgeLatched = false;
    }

    private void ExitWalking()
    {
        if (!_walkingDirection.HasValue)
        {
            return;
        }

        CurrentDirection = _walkingDirection.Value;
        _walkingDirection = null;
        CurrentFrame = FullTurnFrame;
        CurrentWalkFrame = 0;
        _elapsedSeconds = 0.0;
        _walkElapsedSeconds = 0.0;
    }

    private double GetWalkFrameDurationSeconds(TurnDirection direction)
    {
        return direction == TurnDirection.Left
            ? LeftWalkFrameDurationSeconds
            : RightWalkFrameDurationSeconds;
    }

    private static int GetWalkFrameCount(TurnDirection direction)
    {
        return direction == TurnDirection.Left
            ? LeftWalkFrameCount
            : RightWalkFrameCount;
    }

    private static TurnRequest GetRequest(TurnDirection direction)
    {
        return direction == TurnDirection.Left
            ? TurnRequest.Left
            : TurnRequest.Right;
    }

    private static TurnRequest GetRequest(bool leftHeld, bool rightHeld)
    {
        if (leftHeld == rightHeld)
        {
            return TurnRequest.Neutral;
        }

        return leftHeld ? TurnRequest.Left : TurnRequest.Right;
    }

    private enum TurnRequest
    {
        Neutral,
        Left,
        Right,
    }
}
