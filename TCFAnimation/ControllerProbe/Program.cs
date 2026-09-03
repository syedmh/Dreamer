using TCFAnimation;

const double TurnFps = 8.0;

RunTurnCases();
RunLeftWalkCases();
RunRightWalkCases();
RunWalkPlaybackSpeedCases();
RunClapCases();
RunCrossArmCases();
RunEdgeCases();
RunValidationCases();
RunDialogueCases();
RunDialogueLayoutCases();

Console.WriteLine(
    "CONTROLLER_PROBE_PASS "
    + "left_turn=true right_turn=true returns=true "
    + "direct_reversal_both_ways=true both_held_neutral=true "
    + "left_walk_6_frames=true right_walk_6_frames=true "
    + "left_edge_latch=true right_edge_latch=true "
    + "fps_turn=8 fps_left_walk=6 fps_right_walk=6 "
    + "adjustable_walk_speed=true "
    + "clap_6_frames=true clap_15_steps=true fps_clap=8 clap_one_shot=true "
    + "clap_interruptible=true "
    + "cross_arm_3_frames=true fps_cross_arm=8 crossed_hold_cross_02=true "
    + "release_6_frames=true release_source=CrossArm4 fps_cross_arm_release=8 "
    + "cross_arm_direction_lock=true dialogue_input=true "
    + "dialogue_layout=true");
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

static void RunClapCases()
{
    int[] expectedOrder = [0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0];

    if (DirectionalTurnStateMachine.ClapFrameCount != 6)
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must load exactly six selected source poses.");
    }
    if (
        DirectionalTurnStateMachine.ClapPlaybackStepCount
        != expectedOrder.Length
    )
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must use the requested 15-step sequence.");
    }
    if (DirectionalTurnStateMachine.ClapAnimationFps != 8.0)
    {
        throw new InvalidOperationException(
            "Clapping2 runtime must remain fixed at 8 FPS.");
    }

    DirectionalTurnStateMachine turn = NewState();
    double clapStep = turn.ClapFrameDurationSeconds;

    for (
        int step = 0;
        step < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        step++
    )
    {
        if (
            DirectionalTurnStateMachine.GetClapFrameForStep(step)
            != expectedOrder[step]
        )
        {
            throw new InvalidOperationException(
                $"clap playback step {step} did not map to Clapping2 "
                + $"runtime frame {expectedOrder[step]}.");
        }
    }

    AssertChanged("front idle starts clap", turn.TryStartClap());
    AssertClap("clap starts at source frame zero", turn, 0, 0);
    AssertChanged(
        "active clap does not restart",
        turn.TryStartClap(),
        expected: false);
    for (
        int expectedStep = 1;
        expectedStep < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        expectedStep++
    )
    {
        double multiplier = expectedStep % 2 == 0 ? 0.25 : 3.0;
        AssertChanged(
            $"clap advances to step {expectedStep} at walk {multiplier:0.##}x",
            turn.Advance(false, false, clapStep, multiplier));
        AssertClap(
            $"clap step {expectedStep} ignores walk multiplier",
            turn,
            expectedStep,
            expectedOrder[expectedStep]);
    }

    AssertChanged(
        "clap completes after final frame duration",
        turn.Advance(false, false, clapStep, 3.0));
    AssertPose(
        "completed clap returns to normal left front",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);

    turn.Advance(false, true, 0.0);
    AssertPose("clap restriction setup selects R0", turn, TurnDirection.Right, 0);
    AssertChanged(
        "clap ignored while directional request is active at front",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    AssertChanged("right front idle can clap", turn.TryStartClap());
    for (
        int expectedStep = 1;
        expectedStep < DirectionalTurnStateMachine.ClapPlaybackStepCount;
        expectedStep++
    )
    {
        turn.Advance(false, false, clapStep);
    }
    turn.Advance(false, false, clapStep);
    AssertPose(
        "completed clap returns to normal right front",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    AssertChanged("right front starts clap for left cancellation", turn.TryStartClap());
    AssertChanged(
        "left cancels clap immediately",
        turn.Advance(true, false, 0.0));
    AssertPose(
        "left clap cancellation selects left front",
        turn,
        TurnDirection.Left,
        0);

    turn.Advance(false, false, 0.0);
    AssertChanged("left front starts clap for right cancellation", turn.TryStartClap());
    AssertChanged(
        "right cancels clap immediately",
        turn.Advance(false, true, 0.0));
    AssertPose(
        "right clap cancellation selects right front",
        turn,
        TurnDirection.Right,
        0);

    turn.Advance(false, false, 0.0);
    turn.Advance(true, false, 0.0);
    turn.Advance(false, false, 0.0);
    AssertChanged("left front idle starts another clap", turn.TryStartClap());
    AssertChanged(
        "same-direction arrow cancels clap",
        turn.Advance(true, false, 0.0));
    AssertPose(
        "same-direction clap cancellation restores left front",
        turn,
        TurnDirection.Left,
        0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertPose(
        "left behavior proceeds after clap cancellation",
        turn,
        TurnDirection.Left,
        1);

    AssertChanged(
        "clap ignored while turning",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk("clap restriction setup enters left walk", turn, TurnDirection.Left, 0);
    AssertChanged(
        "clap ignored while walking",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    turn.Advance(false, false, turn.FrameDurationSeconds);
    AssertPose("clap restriction setup is returning", turn, TurnDirection.Left, 1);
    AssertChanged(
        "clap ignored while returning",
        turn.TryStartClap(),
        expected: false);

    turn = EnterWalk(TurnDirection.Right);
    turn.NotifyRightEdgeReached();
    turn.Advance(false, true, turn.FrameDurationSeconds);
    turn.Advance(false, true, turn.FrameDurationSeconds);
    AssertPose("clap edge latch reaches front", turn, TurnDirection.Right, 0);
    AssertTrue("right edge latch remains active at front", turn.IsRightEdgeLatched);
    AssertChanged(
        "clap ignored while edge latch is active",
        turn.TryStartClap(),
        expected: false);
    turn.Advance(false, false, 0.0);
    AssertChanged("clap starts after edge latch release", turn.TryStartClap());
    AssertClap("post-latch clap starts at zero", turn, 0, 0);
}

static void RunCrossArmCases()
{
    if (DirectionalTurnStateMachine.CrossArmFrameCount != 3)
    {
        throw new InvalidOperationException(
            "cross-arm runtime must load exactly three crossing frames.");
    }
    if (DirectionalTurnStateMachine.CrossArmReleaseFrameCount != 6)
    {
        throw new InvalidOperationException(
            "cross-arm release must load all six selected CrossArm4 frames.");
    }

    DirectionalTurnStateMachine turn = NewState();
    double crossStep = turn.CrossArmFrameDurationSeconds;
    double releaseStep = turn.CrossArmReleaseFrameDurationSeconds;

    AssertChanged("front idle starts cross-arm transition", turn.TryToggleCrossArms());
    AssertCrossing("cross-arm transition starts at frame zero", turn, 0);
    AssertChanged(
        "repeated X cannot restart crossing",
        turn.TryToggleCrossArms(),
        expected: false);
    AssertChanged(
        "C ignored while crossing",
        turn.TryStartClap(),
        expected: false);
    AssertChanged(
        "left ignored while crossing",
        turn.Advance(true, false, crossStep / 2.0),
        expected: false);
    AssertCrossing("crossing remains on frame zero after half step", turn, 0);

    AssertChanged(
        "crossing reaches frame one",
        turn.Advance(false, false, crossStep / 2.0, 3.0));
    AssertCrossing("cross-arm frame one", turn, 1);
    for (
        int expectedFrame = 2;
        expectedFrame < DirectionalTurnStateMachine.CrossArmFrameCount;
        expectedFrame++
    )
    {
        double multiplier = expectedFrame % 2 == 0 ? 0.25 : 3.0;
        AssertChanged(
            $"crossing reaches frame {expectedFrame} at walk "
            + $"{multiplier:0.##}x",
            turn.Advance(false, false, crossStep, multiplier));
        AssertCrossing(
            $"cross-arm frame {expectedFrame} ignores walk multiplier",
            turn,
            expectedFrame);
    }

    AssertChanged(
        "crossing enters persistent hold after final frame duration",
        turn.Advance(false, false, crossStep, 0.25));
    AssertCrossedHold("crossed hold uses exact final frame", turn);
    AssertChanged(
        "large delta does not churn crossed hold",
        turn.Advance(false, false, 1_000_000.0, 3.0),
        expected: false);
    AssertCrossedHold("crossed hold persists across large delta", turn);
    AssertChanged(
        "C ignored while crossed",
        turn.TryStartClap(),
        expected: false);
    AssertChanged(
        "left ignored while crossed",
        turn.Advance(true, false, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertCrossedHold("left cannot turn crossed hold", turn);
    AssertChanged(
        "right ignored while crossed",
        turn.Advance(false, true, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertCrossedHold("right cannot turn crossed hold", turn);

    AssertChanged(
        "second X from fully crossed hold starts release",
        turn.TryToggleCrossArms(false, true));
    AssertReleasing("release starts at frame zero", turn, 0);
    AssertChanged(
        "repeated X cannot restart or reverse release",
        turn.TryToggleCrossArms(),
        expected: false);
    AssertChanged(
        "C ignored while releasing",
        turn.TryStartClap(),
        expected: false);

    for (
        int expectedFrame = 1;
        expectedFrame < DirectionalTurnStateMachine.CrossArmReleaseFrameCount;
        expectedFrame++
    )
    {
        bool leftHeld = expectedFrame == 1;
        bool rightHeld = expectedFrame == 2;
        double multiplier = expectedFrame % 2 == 0 ? 3.0 : 0.25;
        AssertChanged(
            $"release reaches frame {expectedFrame} while arrows are ignored",
            turn.Advance(leftHeld, rightHeld, releaseStep, multiplier));
        AssertReleasing(
            $"release frame {expectedFrame} ignores walk multiplier",
            turn,
            expectedFrame);
    }

    AssertChanged(
        "release completes only after final frame duration",
        turn.Advance(false, true, releaseStep, 3.0));
    AssertPose(
        "release returns to normal left front",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);
    AssertChanged(
        "held arrow from release cannot queue a turn",
        turn.Advance(false, true, turn.FrameDurationSeconds * 4.0),
        expected: false);
    AssertPose(
        "queued right remains blocked after release",
        turn,
        TurnDirection.Left,
        DirectionalTurnStateMachine.FrontFrame);
    AssertChanged(
        "releasing arrows clears post-cross direction lock",
        turn.Advance(false, false, 0.0),
        expected: false);
    AssertChanged(
        "fresh right input selects right front",
        turn.Advance(false, true, 0.0));
    AssertPose(
        "fresh right works after crossed release",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    turn.Advance(false, false, 0.0);
    AssertChanged("right front idle can cross arms", turn.TryToggleCrossArms());
    for (
        int frame = 1;
        frame < DirectionalTurnStateMachine.CrossArmFrameCount;
        frame++
    )
    {
        turn.Advance(false, false, crossStep);
    }
    turn.Advance(false, false, crossStep);
    AssertCrossedHold("right-front crossing reaches hold", turn);
    AssertChanged("right-front hold starts release", turn.TryToggleCrossArms());
    for (
        int frame = 1;
        frame < DirectionalTurnStateMachine.CrossArmReleaseFrameCount;
        frame++
    )
    {
        turn.Advance(false, false, releaseStep);
    }
    turn.Advance(false, false, releaseStep);
    AssertPose(
        "right-front release restores right front",
        turn,
        TurnDirection.Right,
        DirectionalTurnStateMachine.FrontFrame);

    turn = NewState();
    AssertChanged("clap setup starts", turn.TryStartClap());
    AssertChanged(
        "X rejected from clap",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(true, false, 0.0);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertChanged(
        "X rejected while turning",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, 0.0);
    AssertWalk("cross restriction setup enters walk", turn, TurnDirection.Left, 0);
    AssertChanged(
        "X rejected while walking",
        turn.TryToggleCrossArms(),
        expected: false);
    turn.Advance(false, false, 0.0);
    turn.Advance(false, false, turn.FrameDurationSeconds);
    AssertPose(
        "cross restriction setup is returning",
        turn,
        TurnDirection.Left,
        1);
    AssertChanged(
        "X rejected while returning",
        turn.TryToggleCrossArms(),
        expected: false);

    turn = EnterWalk(TurnDirection.Left);
    turn.NotifyLeftEdgeReached();
    turn.Advance(true, false, turn.FrameDurationSeconds);
    turn.Advance(true, false, turn.FrameDurationSeconds);
    AssertTrue("cross restriction edge latch remains active", turn.IsLeftEdgeLatched);
    AssertChanged(
        "X rejected while edge-latched",
        turn.TryToggleCrossArms(),
        expected: false);
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

static void RunDialogueCases()
{
    DialogueModel dialogue = new();

    DialogueAction action = dialogue.HandleKey(DialogueKey.Enter, echo: false);
    AssertTrue("Enter closed opens input", action.Opened && dialogue.IsEditing);
    AssertTrue(
        "character input suppressed while editing",
        dialogue.SuppressCharacterInput);

    action = dialogue.HandleKey(
        DialogueKey.Enter,
        echo: false,
        "  Hello   brave   world!  ");
    AssertTrue(
        "Enter open submits normalized non-empty text",
        action.Submitted
        && !dialogue.IsEditing
        && dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");
    AssertTrue(
        "character input resumes after submit",
        !dialogue.SuppressCharacterInput);

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.Enter, echo: false, " \t\r\n ");
    AssertTrue(
        "whitespace submit preserves existing bubble",
        action.Closed
        && !action.Submitted
        && dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.Escape, echo: false, "discard me");
    AssertTrue(
        "Escape closes without changing bubble",
        action.Closed
        && dialogue.BubbleText == "Hello brave world!"
        && dialogue.IsBubbleVisible);

    action = dialogue.HandleKey(DialogueKey.P, echo: false);
    AssertTrue(
        "P closed hides bubble",
        action.Handled
        && action.Hidden
        && !dialogue.IsBubbleVisible
        && dialogue.BubbleText == "Hello brave world!");

    dialogue.HandleKey(DialogueKey.Enter, echo: false);
    action = dialogue.HandleKey(DialogueKey.P, echo: false, "p");
    AssertTrue(
        "P open remains text input and does not hide",
        !action.Handled && dialogue.IsEditing);

    action = dialogue.HandleKey(DialogueKey.Enter, echo: true, "ignored");
    AssertTrue(
        "key echo is ignored",
        !action.Handled
        && dialogue.IsEditing
        && dialogue.BubbleText == "Hello brave world!");
    dialogue.HandleKey(DialogueKey.Escape, echo: false);
}

static void RunDialogueLayoutCases()
{
    static float Measure(string text) => text.Length * 18.0f;

    string shortText = DialogueLayout.WrapText(
        "Hi!",
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);
    string mediumText = DialogueLayout.WrapText(
        "A medium sentence should wrap using measured word widths.",
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);
    string longText = DialogueLayout.WrapText(
        string.Join(' ', Enumerable.Repeat("extraordinary", 100)),
        Measure,
        DialogueLayout.MaximumBodyWidth
            - DialogueLayout.HorizontalPadding * 2.0f,
        DialogueLayout.MaximumLines);

    AssertBubbleSize("short text size", shortText, Measure, 42.0f);
    AssertBubbleSize("medium text size", mediumText, Measure, 84.0f);
    AssertBubbleSize("very long text size", longText, Measure, 500.0f);
    AssertTrue(
        "very long text is capped and ellipsized",
        longText.Split('\n').Length == DialogueLayout.MaximumLines
        && longText.EndsWith('…'));

    foreach (
        (string name, DialoguePoint anchor) in new[]
        {
            ("center", new DialoguePoint(960.0f, 290.0f)),
            ("left edge", new DialoguePoint(221.25f, 290.0f)),
            ("right edge", new DialoguePoint(1700.0f, 290.0f)),
        }
    )
    {
        BubbleLayoutResult layout = DialogueLayout.PlaceBubble(
            anchor,
            new DialogueSize(680.0f, 238.0f));
        AssertTrue(
            $"{name} bubble stays inside viewport",
            layout.Body.X >= DialogueLayout.SafeMargin
            && layout.Body.Y >= DialogueLayout.SafeMargin
            && layout.Body.Right
                <= DialogueLayout.ViewportWidth - DialogueLayout.SafeMargin
            && layout.Body.Bottom
                <= DialogueLayout.ViewportHeight - DialogueLayout.SafeMargin);
        AssertTrue(
            $"{name} tail target remains character anchor",
            layout.TailTarget == anchor);
    }
}

static void AssertBubbleSize(
    string stepName,
    string wrappedText,
    Func<string, float> measure,
    float measuredHeight)
{
    float widestLine = wrappedText
        .Split('\n')
        .Select(measure)
        .DefaultIfEmpty(0.0f)
        .Max();
    DialogueSize size = DialogueLayout.CalculateBodySize(
        new DialogueSize(widestLine, measuredHeight));
    AssertTrue(
        stepName,
        size.Width >= DialogueLayout.MinimumBodyWidth
        && size.Width <= DialogueLayout.MaximumBodyWidth
        && size.Height >= DialogueLayout.MinimumBodyHeight
        && size.Height <= DialogueLayout.MaximumBodyHeight);
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
        || state.IsClapping
        || state.IsCrossArmActive
        || state.CurrentDirection != expectedDirection
        || state.CurrentFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected non-walking {expectedDirection} "
            + $"frame {expectedFrame}, actual direction={state.CurrentDirection} "
            + $"frame={state.CurrentFrame} walkingLeft={state.IsWalkingLeft} "
            + $"walkingRight={state.IsWalkingRight} "
            + $"clapping={state.IsClapping} "
            + $"crossArmState={state.CrossArmState}");
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
        || state.ActiveGesture != FrontGesture.None
        || state.IsCrossArmActive
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

static void AssertClap(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedStep,
    int expectedSourceFrame)
{
    if (
        !state.IsClapping
        || state.IsCrossArmActive
        || state.IsWalking
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentGestureStep != expectedStep
        || state.CurrentClapFrame != expectedSourceFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected clap step {expectedStep} source frame "
            + $"{expectedSourceFrame}, actual clapping={state.IsClapping} "
            + $"step={state.CurrentGestureStep} "
            + $"sourceFrame={state.CurrentClapFrame} "
            + $"turnFrame={state.CurrentFrame} walking={state.IsWalking}");
    }

    Console.WriteLine(
        $"PASS {stepName}: clapStep={state.CurrentGestureStep} "
        + $"sourceFrame={state.CurrentClapFrame}");
}

static void AssertCrossing(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedFrame)
{
    if (
        !state.IsCrossingArms
        || state.IsCrossArmsHeld
        || state.IsReleasingCrossArms
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected crossing frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmFrame} "
            + $"turnFrame={state.CurrentFrame}");
    }

    Console.WriteLine(
        $"PASS {stepName}: crossArmFrame={state.CurrentCrossArmFrame}");
}

static void AssertCrossedHold(
    string stepName,
    DirectionalTurnStateMachine state)
{
    int expectedFrame = DirectionalTurnStateMachine.CrossArmFrameCount - 1;
    if (expectedFrame != 2)
    {
        throw new InvalidOperationException(
            "persistent crossed-arm hold must use cross_02.");
    }

    if (
        !state.IsCrossArmsHeld
        || state.IsCrossingArms
        || state.IsReleasingCrossArms
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected held cross-arm frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmFrame}");
    }

    Console.WriteLine(
        $"PASS {stepName}: crossArmHoldFrame={state.CurrentCrossArmFrame}");
}

static void AssertReleasing(
    string stepName,
    DirectionalTurnStateMachine state,
    int expectedFrame)
{
    if (
        !state.IsReleasingCrossArms
        || state.IsCrossingArms
        || state.IsCrossArmsHeld
        || state.IsWalking
        || state.ActiveGesture != FrontGesture.None
        || state.CurrentFrame != DirectionalTurnStateMachine.FrontFrame
        || state.CurrentCrossArmStep != expectedFrame
        || state.CurrentCrossArmReleaseFrame != expectedFrame
    )
    {
        throw new InvalidOperationException(
            $"{stepName}: expected release frame {expectedFrame}, "
            + $"actual state={state.CrossArmState} "
            + $"step={state.CurrentCrossArmStep} "
            + $"frame={state.CurrentCrossArmReleaseFrame} "
            + $"turnFrame={state.CurrentFrame}");
    }

    Console.WriteLine(
        $"PASS {stepName}: crossArmReleaseFrame="
        + $"{state.CurrentCrossArmReleaseFrame}");
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
