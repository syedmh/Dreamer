using TCFAnimation;

const double TurnFps = 8.0;

RunTurnCases();
RunLeftWalkCases();
RunRightWalkCases();
RunWalkPlaybackSpeedCases();
RunEdgeCases();
RunValidationCases();

Console.WriteLine(
    "CONTROLLER_PROBE_PASS "
    + "left_turn=true right_turn=true returns=true "
    + "direct_reversal_both_ways=true both_held_neutral=true "
    + "left_walk_6_frames=true right_walk_6_frames=true "
    + "left_edge_latch=true right_edge_latch=true "
    + "fps_turn=8 fps_left_walk=6 fps_right_walk=6 "
    + "adjustable_walk_speed=true");
return;

static void RunTurnCases()
{
    DirectionalTurnStateMachine turn = NewState();
    double step = turn.FrameDurationSeconds;

    AssertPose("start uses left front", turn, TurnDirection.Left, 0);

    AssertChanged("left advances to L1", turn.Advance(true, false, step));
    AssertPose("left L1", turn, TurnDirection.Left, 1);
    AssertChanged("left advances to L2", turn.Advance(true, false, step));
    AssertPose("left L2 remains visible", turn, TurnDirection.Left, 2);
    AssertChanged("following call enters left walk", turn.Advance(true, false, 0.0));
    AssertWalk("left walk starts at frame zero", turn, TurnDirection.Left, 0);

    AssertChanged("release left walk exposes L2", turn.Advance(false, false, 0.0));
    AssertPose("released left walk pose", turn, TurnDirection.Left, 2);
    turn.Advance(false, false, step);
    AssertPose("left return L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, false, step);
    AssertPose("left return L0", turn, TurnDirection.Left, 0);

    AssertChanged("right selects R0", turn.Advance(false, true, 0.0));
    AssertPose("right sheet front", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, step);
    AssertPose("right R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, step);
    AssertPose("right R2 remains visible", turn, TurnDirection.Right, 2);
    AssertChanged("following call enters right walk", turn.Advance(false, true, 0.0));
    AssertWalk("right walk starts at frame zero", turn, TurnDirection.Right, 0);

    AssertChanged("release right walk exposes R2", turn.Advance(false, false, 0.0));
    AssertPose("released right walk pose", turn, TurnDirection.Right, 2);
    turn.Advance(false, false, step);
    AssertPose("right return R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, false, step);
    AssertPose("right return R0", turn, TurnDirection.Right, 0);

    turn.Advance(false, true, step);
    turn.Advance(false, true, step);
    AssertPose("prepare right reversal at R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, false, step);
    AssertPose("right reversal R1", turn, TurnDirection.Right, 1);
    turn.Advance(true, false, step);
    AssertPose("right reversal R0", turn, TurnDirection.Right, 0);
    turn.Advance(true, false, 0.0);
    AssertPose("right reversal selects L0", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, step);
    AssertPose("right reversal turns to L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, false, step);
    AssertPose("right reversal completes L2", turn, TurnDirection.Left, 2);

    turn.Advance(true, true, step);
    AssertPose("both held returns through L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, true, step);
    AssertPose("both held reaches L0", turn, TurnDirection.Left, 0);
    AssertChanged(
        "both held stays neutral",
        turn.Advance(true, true, step * 4.0),
        expected: false);
    AssertPose("both held remains L0", turn, TurnDirection.Left, 0);

    turn.Advance(true, false, step);
    turn.Advance(true, false, step);
    AssertPose("prepare left reversal at L2", turn, TurnDirection.Left, 2);
    turn.Advance(false, true, step);
    AssertPose("left reversal L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, true, step);
    AssertPose("left reversal L0", turn, TurnDirection.Left, 0);
    turn.Advance(false, true, 0.0);
    AssertPose("left reversal selects R0", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, step);
    AssertPose("left reversal turns to R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, step);
    AssertPose("left reversal completes R2", turn, TurnDirection.Right, 2);
}

static void RunLeftWalkCases()
{
    DirectionalTurnStateMachine turn = EnterWalk(TurnDirection.Left);
    double walkStep = turn.LeftWalkFrameDurationSeconds;

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.LeftWalkFrameCount;
        expectedFrame++
    )
    {
        AssertChanged(
            $"left walk advances to frame {expectedFrame}",
            turn.Advance(true, false, walkStep));
        AssertWalk(
            $"left walk frame {expectedFrame}",
            turn,
            TurnDirection.Left,
            expectedFrame);
    }

    AssertChanged("left walk wraps", turn.Advance(true, false, walkStep));
    AssertWalk("left walk wrapped frame zero", turn, TurnDirection.Left, 0);
    AssertChanged(
        "left multi-frame delta changes frame",
        turn.Advance(true, false, walkStep * 5.0));
    AssertWalk("left multi-frame reaches five", turn, TurnDirection.Left, 5);
    AssertChanged(
        "left full-cycle delta preserves texture",
        turn.Advance(
            true,
            false,
            walkStep * DirectionalTurnStateMachine.LeftWalkFrameCount),
        expected: false);
    AssertWalk("left full-cycle wraps in place", turn, TurnDirection.Left, 5);

    AssertChanged("left release cancels walking", turn.Advance(false, false, 0.0));
    AssertPose("left release restores L2", turn, TurnDirection.Left, 2);
    AssertWalkReset("left release resets walk frame", turn);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("both held cancels left walk", turn.Advance(true, true, walkStep));
    AssertPose("both held restores L2", turn, TurnDirection.Left, 2);
    turn.Advance(true, true, turn.FrameDurationSeconds);
    AssertPose("both held returns through L1", turn, TurnDirection.Left, 1);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("right cancels left walk", turn.Advance(false, true, walkStep));
    AssertPose("right cancellation restores L2", turn, TurnDirection.Left, 2);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("right reversal from left walk reaches L1", turn, TurnDirection.Left, 1);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("right reversal from left walk reaches L0", turn, TurnDirection.Left, 0);
    turn.Advance(false, true, 0.0);
    AssertPose("right reversal from left walk selects R0", turn, TurnDirection.Right, 0);
}

static void RunRightWalkCases()
{
    DirectionalTurnStateMachine turn = EnterWalk(TurnDirection.Right);
    double walkStep = turn.RightWalkFrameDurationSeconds;

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.RightWalkFrameCount;
        expectedFrame++
    )
    {
        AssertChanged(
            $"right walk advances to frame {expectedFrame}",
            turn.Advance(false, true, walkStep));
        AssertWalk(
            $"right walk frame {expectedFrame}",
            turn,
            TurnDirection.Right,
            expectedFrame);
    }

    AssertChanged("right walk wraps", turn.Advance(false, true, walkStep));
    AssertWalk("right walk wrapped frame zero", turn, TurnDirection.Right, 0);
    AssertChanged(
        "right multi-frame delta changes frame",
        turn.Advance(false, true, walkStep * 4.0));
    AssertWalk("right multi-frame reaches four", turn, TurnDirection.Right, 4);
    AssertChanged(
        "right full-cycle delta preserves texture",
        turn.Advance(
            false,
            true,
            walkStep * DirectionalTurnStateMachine.RightWalkFrameCount),
        expected: false);
    AssertWalk("right full-cycle wraps in place", turn, TurnDirection.Right, 4);

    AssertChanged("right release cancels walking", turn.Advance(false, false, 0.0));
    AssertPose("right release restores R2", turn, TurnDirection.Right, 2);
    AssertWalkReset("right release resets walk frame", turn);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("both held cancels right walk", turn.Advance(true, true, walkStep));
    AssertPose("both held restores R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, true, turn.FrameDurationSeconds);
    AssertPose("both held returns through R1", turn, TurnDirection.Right, 1);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("left cancels right walk", turn.Advance(true, false, walkStep));
    AssertPose("left cancellation restores R2", turn, TurnDirection.Right, 2);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("left reversal from right walk reaches R1", turn, TurnDirection.Right, 1);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("left reversal from right walk reaches R0", turn, TurnDirection.Right, 0);
    turn.Advance(true, false, 0.0);
    AssertPose("left reversal from right walk selects L0", turn, TurnDirection.Left, 0);
}

static void RunWalkPlaybackSpeedCases()
{
    double[] multipliers = [0.25, 0.5, 1.0, 2.0, 3.0];

    foreach (TurnDirection direction in Enum.GetValues<TurnDirection>())
    {
        foreach (double multiplier in multipliers)
        {
            DirectionalTurnStateMachine turn = EnterWalk(direction);
            double baseWalkStep = direction == TurnDirection.Left
                ? turn.LeftWalkFrameDurationSeconds
                : turn.RightWalkFrameDurationSeconds;
            bool leftHeld = direction == TurnDirection.Left;
            bool rightHeld = direction == TurnDirection.Right;

            AssertChanged(
                $"{direction} walk advances one frame at {multiplier:0.##}x",
                turn.Advance(
                    leftHeld,
                    rightHeld,
                    baseWalkStep / multiplier,
                    multiplier));
            AssertWalk(
                $"{direction} walk playback is proportional at "
                + $"{multiplier:0.##}x",
                turn,
                direction,
                1);
        }
    }

    DirectionalTurnStateMachine turning = NewState();
    double halfTurnStep = turning.FrameDurationSeconds / 2.0;
    AssertChanged(
        "walk multiplier does not accelerate first half of turn",
        turning.Advance(true, false, halfTurnStep, 3.0),
        expected: false);
    AssertPose(
        "turn remains at L0 after half step with 3x walk multiplier",
        turning,
        TurnDirection.Left,
        0);
    AssertChanged(
        "walk multiplier does not slow second half of turn",
        turning.Advance(true, false, halfTurnStep, 0.25));
    AssertPose(
        "turn reaches L1 after one fixed 8 FPS interval",
        turning,
        TurnDirection.Left,
        1);
}

static void RunEdgeCases()
{
    DirectionalTurnStateMachine turn = NewState();
    AssertChanged(
        "left edge outside left walk is no-op",
        turn.NotifyLeftEdgeReached(),
        expected: false);
    AssertChanged(
        "right edge outside right walk is no-op",
        turn.NotifyRightEdgeReached(),
        expected: false);

    turn = EnterWalk(TurnDirection.Left);
    AssertChanged("left edge stops left walking", turn.NotifyLeftEdgeReached());
    AssertPose("left edge restores L2", turn, TurnDirection.Left, 2);
    AssertTrue("left edge sets latch", turn.IsLeftEdgeLatched);
    AssertChanged(
        "right edge during latched left pose is no-op",
        turn.NotifyRightEdgeReached(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("latched held left forces L1", turn, TurnDirection.Left, 1);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose("latched held left forces L0", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, turn.FrameDurationSeconds * 4.0);
    AssertPose("latched held left remains L0", turn, TurnDirection.Left, 0);
    AssertNotWalking("latched held left cannot restart", turn);
    AssertTrue("left latch remains while left held", turn.IsLeftEdgeLatched);

    turn.Advance(false, true, 0.0);
    AssertTrue("releasing left clears only left latch", !turn.IsLeftEdgeLatched);
    AssertPose("right can select R0 after left latch", turn, TurnDirection.Right, 0);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged("right edge stops right walking", turn.NotifyRightEdgeReached());
    AssertPose("right edge restores R2", turn, TurnDirection.Right, 2);
    AssertTrue("right edge sets latch", turn.IsRightEdgeLatched);
    AssertChanged(
        "left edge during latched right pose is no-op",
        turn.NotifyLeftEdgeReached(),
        expected: false);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("latched held right forces R1", turn, TurnDirection.Right, 1);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("latched held right forces R0", turn, TurnDirection.Right, 0);
    turn.Advance(false, true, turn.FrameDurationSeconds * 4.0);
    AssertPose("latched held right remains R0", turn, TurnDirection.Right, 0);
    AssertNotWalking("latched held right cannot restart", turn);
    AssertTrue("right latch remains while right held", turn.IsRightEdgeLatched);

    turn.Advance(true, false, 0.0);
    AssertTrue("releasing right clears right latch", !turn.IsRightEdgeLatched);
    AssertPose("left can select L0 after right latch", turn, TurnDirection.Left, 0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk(
        "left still walks after right edge operations",
        turn,
        TurnDirection.Left,
        0);

    turn.Advance(false, false, 0.0);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, 0.0);
    AssertWalk(
        "right restarts after latch release",
        turn,
        TurnDirection.Right,
        0);
}

static void RunValidationCases()
{
    AssertThrows<ArgumentOutOfRangeException>(
        "constructor rejects zero fps",
        () => new DirectionalTurnStateMachine(0.0));
    AssertThrows<ArgumentOutOfRangeException>(
        "constructor rejects infinite fps",
        () => new DirectionalTurnStateMachine(double.PositiveInfinity));

    DirectionalTurnStateMachine turn = NewState();
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects negative delta",
        () => turn.Advance(true, false, -0.001));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects NaN delta",
        () => turn.Advance(true, false, double.NaN));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects infinite delta",
        () => turn.Advance(true, false, double.PositiveInfinity));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects zero walk multiplier",
        () => turn.Advance(true, false, 0.0, 0.0));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects negative walk multiplier",
        () => turn.Advance(true, false, 0.0, -0.25));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects NaN walk multiplier",
        () => turn.Advance(true, false, 0.0, double.NaN));
    AssertThrows<ArgumentOutOfRangeException>(
        "advance rejects infinite walk multiplier",
        () => turn.Advance(
            true,
            false,
            0.0,
            double.PositiveInfinity));

    AssertChanged(
        "large turn delta reaches L2",
        turn.Advance(true, false, 1_000_000.0));
    AssertPose("large turn delta stops at L2", turn, TurnDirection.Left, 2);
    AssertNotWalking("large turn delta does not spill into left walk", turn);
    AssertChanged(
        "later call enters left walk after large delta",
        turn.Advance(true, false, 1_000_000.0));
    AssertWalk("large left entry starts at zero", turn, TurnDirection.Left, 0);
    AssertChanged(
        "large left walk delta uses deterministic modulo",
        turn.Advance(
            true,
            false,
            1_000_000.0 + turn.LeftWalkFrameDurationSeconds * 1.5));
    AssertWalk("large left walk endpoint", turn, TurnDirection.Left, 1);

    turn = EnterWalk(TurnDirection.Right);
    AssertChanged(
        "large right walk delta uses deterministic modulo",
        turn.Advance(
            false,
            true,
            1_000_000.0 + turn.RightWalkFrameDurationSeconds * 4.5));
    AssertWalk("large right walk endpoint", turn, TurnDirection.Right, 4);
}

static DirectionalTurnStateMachine NewState()
{
    return new DirectionalTurnStateMachine(TurnFps);
}

static DirectionalTurnStateMachine EnterWalk(TurnDirection direction)
{
    DirectionalTurnStateMachine turn = NewState();
    bool leftHeld = direction == TurnDirection.Left;
    bool rightHeld = direction == TurnDirection.Right;

    if (direction == TurnDirection.Right)
    {
        turn.Advance(leftHeld, rightHeld, 0.0);
        AssertPose("right walk setup selects R0", turn, TurnDirection.Right, 0);
    }

    turn.Advance(leftHeld, rightHeld, turn.FrameDurationSeconds);
    turn.Advance(leftHeld, rightHeld, turn.FrameDurationSeconds);
    AssertPose(
        $"{direction} walk setup renders full turn",
        turn,
        direction,
        DirectionalTurnStateMachine.FullTurnFrame);
    AssertNotWalking($"{direction} setup does not spill into walking", turn);
    turn.Advance(leftHeld, rightHeld, 0.0);
    AssertWalk($"{direction} setup enters frame zero", turn, direction, 0);
    return turn;
}

static void AssertPose(
    string stepName,
    DirectionalTurnStateMachine state,
    TurnDirection expectedDirection,
    int expectedFrame)
{
    if (
        state.IsWalking
        || state.CurrentDirection != expectedDirection
        || state.CurrentFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected non-walking {expectedDirection} "
            + $"frame {expectedFrame}, actual direction={state.CurrentDirection} "
            + $"frame={state.CurrentFrame} walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight}");
    }

    Console.WriteLine(
        $"PASS {stepName}: direction={state.CurrentDirection} "
        + $"frame={state.CurrentFrame}");
}

static void AssertWalk(
    string stepName,
    DirectionalTurnStateMachine state,
    TurnDirection expectedDirection,
    int expectedWalkFrame)
{
    bool expectedWalkingDirection = expectedDirection == TurnDirection.Left
        ? state.IsWalkingLeft && !state.IsWalkingRight
        : state.IsWalkingRight && !state.IsWalkingLeft;

    if (
        !state.IsWalking
        || !expectedWalkingDirection
        || state.CurrentDirection != expectedDirection
        || state.CurrentFrame != DirectionalTurnStateMachine.FullTurnFrame
        || state.CurrentWalkFrame != expectedWalkFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected walking-{expectedDirection} "
            + $"frame {expectedWalkFrame}, actual direction={state.CurrentDirection} "
            + $"turnFrame={state.CurrentFrame} walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"walkFrame={state.CurrentWalkFrame}");
    }

    Console.WriteLine(
        $"PASS {stepName}: direction={state.CurrentDirection} "
        + $"walkFrame={state.CurrentWalkFrame}");
}

static void AssertWalkReset(
    string stepName,
    DirectionalTurnStateMachine state)
{
    if (state.IsWalking || state.CurrentWalkFrame != 0)
    {
        throw new InvalidOperationException(
            $"{stepName}: expected stopped frame zero, "
            + $"actual walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"walkFrame={state.CurrentWalkFrame}");
    }

    Console.WriteLine($"PASS {stepName}");
}

static void AssertNotWalking(
    string stepName,
    DirectionalTurnStateMachine state)
{
    if (state.IsWalking || state.IsWalkingLeft || state.IsWalkingRight)
    {
        throw new InvalidOperationException($"{stepName}: unexpectedly walking.");
    }

    Console.WriteLine($"PASS {stepName}");
}

static void AssertChanged(
    string stepName,
    bool actual,
    bool expected = true)
{
    if (actual != expected)
    {
        throw new InvalidOperationException(
            $"{stepName}: expected changed={expected}, actual={actual}");
    }

    Console.WriteLine($"PASS {stepName}: changed={actual}");
}

static void AssertTrue(string stepName, bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{stepName}: condition was false.");
    }

    Console.WriteLine($"PASS {stepName}");
}

static void AssertThrows<TException>(string stepName, Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        Console.WriteLine($"PASS {stepName}: threw {typeof(TException).Name}");
        return;
    }

    throw new InvalidOperationException(
        $"{stepName}: expected {typeof(TException).Name}");
}
