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

    private double _elapsedSeconds;
    private TurnRequest _lastRequest;

    public DirectionalTurnStateMachine(double animationFramesPerSecond)
    {
        if (!double.IsFinite(animationFramesPerSecond) || animationFramesPerSecond <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationFramesPerSecond),
                "Animation speed must be a finite positive number.");
        }

        FrameDurationSeconds = 1.0 / animationFramesPerSecond;
    }

    public TurnDirection CurrentDirection { get; private set; } = TurnDirection.Left;

    public int CurrentFrame { get; private set; } = FrontFrame;

    public double FrameDurationSeconds { get; }

    public bool Advance(bool leftHeld, bool rightHeld, double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaSeconds),
                "Frame delta must be finite and non-negative.");
        }

        TurnRequest request = GetRequest(leftHeld, rightHeld);
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
        _lastRequest = TurnRequest.Neutral;
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
