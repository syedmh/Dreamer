# TCFAnimation

Godot 4.5.1 Mono/C# directional turn animation with sustained walking in
both directions.

## Run from the command line

From this directory, run:

```bat
run-animation.bat
```

The launcher uses the bundled Godot 4.5.1 Mono executable when available. If
the local `.tools` copy is absent, set `GODOT_EXE` to a Godot 4 Mono
executable or make `godot4`/`godot` available on `PATH`. Because the batch file
resolves the project relative to its own location, it can also be invoked by
full path from another working directory.

## Controls

- Press **F11** or **Alt+Enter** while text entry is closed to toggle
  fullscreen mode. Press **Escape** in fullscreen to return to windowed mode.
  Fullscreen shortcuts are ignored while typing so Enter and Escape retain
  their dialogue behavior.
- Press **physical Enter** while text entry is closed to open a focused,
  bottom-right message field. Type ordinary text, then press **Enter** to
  submit or **Escape** to cancel without changing the current speech bubble.
- While the field is open, all character controls are suppressed: Left, Right,
  C, X, P, +, and - are available only for text entry/editing. Ordinary text,
  including W, remains typeable through the focused field. In
  particular, P can be typed normally and does not hide the bubble.
- A non-empty submission is trimmed and internal whitespace is normalized,
  then displayed in a light comic-style bubble above the character. A
  whitespace-only submission closes the field but preserves the existing
  bubble text and visibility.
- Press **physical P** while text entry is closed to hide the bubble. Hidden
  text remains stored until a later non-empty submission replaces it.
- The bubble follows the character, auto-sizes for short text, wraps at a
  maximum width, shows at most four lines with an ellipsis for overflow, and
  clamps inside the 1920x1080 logical viewport while its tail continues to
  target the character's head. Enter and P key-repeat events are ignored.
- Press the **physical X key** while the character is exactly normal,
  front-facing idle to play the three-pose arm-crossing transition at a fixed
  **8 FPS** (`0.375` seconds). The final
  `Frames\CrossArm\cross_02.png` pose then remains displayed indefinitely;
  animation timing and frame indexes do not continue changing during the hold.
- Once that fully-crossed hold is reached, press physical X again to play
  `Frames\CrossArmRelease\release_00.png` through `release_05.png` once at a
  fixed **8 FPS** (`0.75` seconds), then restore the same directional sheet's
  normal front-idle frame. X repeat/echo and X during either transition are
  ignored, so crossing and release cannot restart, reverse, queue, stack, or
  skip frames.
- C, Left, and Right are ignored while crossing, holding crossed, or
  releasing. Directional input is not queued: an arrow held during those
  states must be released and freshly pressed after release completes before
  turning or walking can begin. The crossed hold can be exited only by X.
- X is ignored during clap, turning, walking, return-to-front, directional
  request-at-front, and edge-latched states. Walk-speed +/- remains available
  and changes only future walking; it never changes cross-arm or release
  playback timing.
- Press the **physical C key** while the character is fully idle and
  front-facing to play the `Clapping2.png` motion at a fixed **8 FPS**. The
  six extracted assets remain available as frames `0..5`, while runtime uses
  the requested 15-step texture order
  `0,1,2,3,4,3,2,1,2,3,4,3,2,1,0` (`1.875` seconds). Repeating the approach,
  contact, and rebound poses makes the action read as repeated clapping before
  it settles back on frame 0 and restores the normal front-facing idle frame.
- C is ignored while clapping, turning, walking, returning to front, while
  either arrow is held, or while an edge latch is active. The clap cannot
  restart, stack, or queue. Pressing Left or Right during a clap cancels it
  immediately and starts directional behavior from the front pose.
- Press **+** once to increase the shared walk-speed multiplier by `0.25x`,
  or press **-** once to decrease it by `0.25x`. The initial value is `1.0x`
  and the range is clamped from `0.25x` through `3.0x`; held-key repeat does
  not make additional adjustments.
- Main-keyboard `+` (`Shift+=`) and `-` are supported, along with numeric
  keypad Add and Subtract.
- The multiplier scales left and right movement and gait playback together.
  At `1.0x`, movement is **240 design pixels/second** and both six-frame walks
  play at **6 FPS**. For example, `0.5x` gives 120 pixels/second and 3 FPS,
  while `2.0x` gives 480 pixels/second and 12 FPS. Turn animation remains
  fixed at **8 FPS** at every walk-speed setting.
- The character starts on the `LTurning.png` front (`0°`) frame.
- Hold the **physical Left Arrow** to advance left `0° -> 45° -> 90°` at
  **8 FPS**. The `90°` pose remains visible for that process advance, and the
  following advance starts left-walk frame `0`.
- While Left remains exclusively held, the six poses cycle at **6 FPS** for a
  one-second loop, and the character moves left at
  **240 design pixels/second**.
- Releasing Left, pressing Right, or holding both arrows stops movement
  immediately, restores the left `90°` pose, and then uses the existing return
  or direct-reversal path through `45°` and front.
- At the visible left edge, the character center clamps to x=221.25 in the
  1920-wide design viewport. Walking stops, the left `90°` pose is restored,
  and the character is forced back to the left front even while Left remains
  held.
- Left cannot restart while it remains held at the edge. Releasing Left clears
  the edge latch; Right can turn during that release, and a later Left press
  can turn and walk again.
- Hold the **physical Right Arrow** to advance right `0° -> 45° -> 90°` at
  **8 FPS**. The `90°` pose remains visible for that process advance, and the
  following advance starts right-walk frame `0`.
- While Right remains exclusively held, the 6 supplied poses cycle at
  **6 FPS** for a one-second loop, and the character moves right at the shared
  **240 design pixels/second** speed.
- Releasing Right, pressing Left, or holding both arrows stops movement
  immediately, restores the right `90°` pose, and then uses the existing
  return or direct-reversal path through `45°` and front.
- At the visible right edge, the character center clamps to x=1700 in the
  1920-wide design viewport. Walking stops, the right `90°` pose is restored,
  and the character is forced back to the right front even while Right remains
  held.
- Right cannot restart while it remains held at the edge. Releasing Right
  clears the edge latch; Left can turn during that release, and a later Right
  press can turn and walk again.
- Release the active arrow to reverse `90° -> 45° -> 0°`.
- Returning left remains on the left-sheet front; returning right remains on the
  right-sheet front.
- Starting the opposite direction from front selects that direction's front
  texture before advancing to `45°`.
- Switching directly between Left and Right first returns the active direction
  through `45°` to its own front, switches to the requested direction's front,
  and then turns outward. It never jumps between `90°` poses.
- Holding both arrows is neutral and returns the active direction to front.

Turn transitions occur every 0.125 seconds. At the initial `1.0x` setting,
both walk animations advance every 1/6 second. Vertical position is unchanged
during movement.

## Regenerate the frames

From the project directory:

```powershell
python -m pip install -r requirements.txt
python FrameExtraction\extract_directional_turns.py --evidence
python FrameExtraction\extract_right_walk.py --evidence
python FrameExtraction\extract_clap.py --evidence
python FrameExtraction\extract_cross_arm.py --evidence
```

The directional-turn script generates only the six turn frames:

- `Frames\LeftTurn\turn_0.png`
- `Frames\LeftTurn\turn_1.png`
- `Frames\LeftTurn\turn_2.png`
- `Frames\RightTurn\turn_0.png`
- `Frames\RightTurn\turn_1.png`
- `Frames\RightTurn\turn_2.png`

`RTurning.png` arrived with JPEG data under a `.png` extension. On first run,
the extractor rewrites it as a valid PNG container and verifies that every
decoded RGB pixel remains unchanged.

Both turn directions use calibrated native whole-panel crops on identical opaque
512x864 black canvases. After divider and label removal and calibrated placement,
the extractor uses deterministic OpenCV foreground seeds plus mask-initialized
GrabCut to retain the complete character, including dark hair and both shoes.
The retained source RGB is composited onto exact opaque pure black; studio
panels, floor, shadows, and reflections are removed. Retained nonzero shoe
pixels with a source value below 22 receive an equal-channel lift to a maximum
channel value of 22. This shoe-box-only charcoal floor preserves the source hue,
leaves zero-valued connectivity pixels invisible, and does not alter trousers,
skin, hair, other artwork, or any pixel outside the final matte.

Regeneration requires Python, NumPy, OpenCV, and Pillow as listed in
`requirements.txt`.

The combined 18-frame development contact sheet is written to:

```text
.ai-org\missions\2026-09-03-black-backgrounds-cecc239c\black-background-contact-sheet.png
```

### Front clap extraction

`Clapping2.png` is authoritative. It contains JPEG data under its `.png`
extension, decodes as RGB 1086x1448, and has SHA-256
`FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00`.
Its keep-only `Clapping2.png.import` metadata prevents the mismatched source
container from being treated as a runtime texture.

The sheet is a labeled 3-column by 2-row sequence in normal left-to-right,
top-to-bottom human reading order. White divider bands occupy x=357..361,
x=721..724, and y=737..740. The top labels begin around y=681 and the bottom
labels around y=1397. The extractor uses these safe exclusive crops and
measured complete-character bounds:

```text
runtime 00 / human 1: crop (0,0)-(356,675), bounds (51,26)-(279,665)
runtime 01 / human 2: crop (363,0)-(718,675), bounds (429,27)-(650,665)
runtime 02 / human 3: crop (726,0)-(1086,675), bounds (778,27)-(1004,666)
runtime 03 / human 4: crop (0,743)-(356,1390), bounds (52,753)-(280,1384)
runtime 04 / human 5: crop (363,743)-(718,1390), bounds (424,753)-(649,1384)
runtime 05 / human 6: crop (726,743)-(1086,1390), bounds (783,753)-(1005,1383)
```

All six labeled poses are visually valid and retained as generated assets:

```text
human 1 hands down -> human 2 hands raise
-> human 3 hands approach -> human 4 clap contact
-> human 5 hands separate/rebound -> human 6 hands down
```

`DirectionalTurnStateMachine` deliberately repeats the motion using runtime
asset order `0,1,2,3,4,3,2,1,2,3,4,3,2,1,0` at a fixed 8 FPS for 1.875
seconds, independent of walk speed. Frame 5 remains part of the reproducible
source extraction but is intentionally not used by the requested playback
sequence.

`FrameExtraction\extract_clap.py` reuses the deterministic shared foreground
cutout and localized dark-shoe treatment. It first aligns source-derived green
vest torso axes to x=256 and source shoe anchors to y=847. Each isolated pose
then receives only a uniform canonical-height scale around torso x=256 and its
visible shoe baseline:

```text
canonical-height scales:
0.846547, 0.847631, 0.845467, 0.858625, 0.857328, 0.856034

effective source-equivalent scales:
1.032787, 1.034110, 1.031470, 1.047522, 1.045940, 1.044361
```

Against `Frames\LeftTurn\turn_0.png`, all six outputs have head top y=181,
visible shoe baseline y=843, and total visible body height 663 pixels.
Measured torso x is 256 for frames 00..03 and 05, and 257 for frame 04.
Shoulder residuals are -4..-7 pixels and waist residuals are -1..-10 pixels,
within the pose-sensitive landmark tolerance. Outputs are exactly
`Frames\Clap\clap_00.png` through `clap_05.png`; obsolete `clap_06` and
`clap_07` assets and import sidecars are removed. Every runtime frame is an
opaque 512x864 RGBA PNG with exact-black background outside the matte.

Visual evidence is written to:

```text
.ai-org\missions\2026-09-03-clapping2-cecc239c\clapping2-source-selection.png
.ai-org\missions\2026-09-03-clapping2-cecc239c\clapping2-runtime-sequence.png
.ai-org\missions\2026-09-03-clapping2-cecc239c\clapping2-canonical-height-overlay.png
.ai-org\missions\2026-09-03-clapping2-cecc239c\clapping2-canonical-height-overlay-zoomed.png
.ai-org\missions\2026-09-03-clapping2-cecc239c\clapping2-motion-strip.png
```

Run the deterministic asset-only alignment regression with:

```powershell
python FrameExtraction\extract_clap.py --validate-height-assets
```

### Front cross-arm extraction

`CrossArm3.png` and `CrossArm4.png` both contain JPEG data under `.png`
extensions and remain unchanged during extraction. Each source is 1086x1448
and uses keep-only Godot import metadata because the source sheets are not
runtime textures. Both are labeled 3-column by 2-row sheets in normal
row-major human reading order. Only human-numbered frames 4, 5, and 6 from
`CrossArm3.png` (the bottom row, left-to-right) are used for crossing:

```text
CrossArm3.png human frames 4, 5, 6:
arms crossing start -> arms crossing mid -> arms crossed complete

CrossArm4.png human frames 1, 2, 3, 4, 5, 6:
arms crossed release start -> uncrossing start -> uncrossing mid
-> hands dropping down -> arms relaxed at sides -> hands-down front idle
```

The real `CrossArm3.png` container is JPEG, decoded as RGB 1086x1448, with
SHA-256
`276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192`.
Its vertical dividers are at source x=360 and x=722, and the second visual row
begins below the horizontal separator at y=725. The exclusive source crops and
measured complete-character bounds are:

```text
cross_00 / human 4: crop (0,727)-(360,1388), bounds (47,738)-(304,1382)
cross_01 / human 5: crop (362,727)-(722,1388), bounds (426,738)-(657,1382)
cross_02 / human 6: crop (724,727)-(1086,1388), bounds (790,738)-(1022,1382)
```

The selected panels are first isolated at a common 1.22 working scale and
aligned to torso x=256 and source shoe anchor y=847. Because these synthesized
source sheets encode a character substantially taller than the canonical
normal-front texture, each isolated pose then receives a calibrated uniform
scale around torso x=256 and its visible shoe baseline. The final visible shoe
baseline is y=843, matching `Frames\LeftTurn\turn_0.png`; there is no
non-uniform stretching or per-state runtime scale.

No other `CrossArm3.png` pose is emitted or loaded at runtime. Human frame 6 is
the persistent hold.

The real `CrossArm4.png` container is JPEG, decoded as RGB 1086x1448, with
SHA-256
`475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F`.
Its vertical separators are at source x=356-358 and x=718-719. The horizontal
separator is at y=737, with the second visual row beginning at y=740 after the
separator. All six poses are complete, relevant release poses and are selected
in row-major temporal order. The exclusive source crops and measured
complete-character bounds are:

```text
release_00 / human 1: crop (0,0)-(355,675), bounds (64,12)-(339,664)
release_01 / human 2: crop (359,0)-(718,675), bounds (423,12)-(696,665)
release_02 / human 3: crop (720,0)-(1086,675), bounds (784,12)-(1026,667)
release_03 / human 4: crop (0,740)-(355,1388), bounds (55,746)-(354,1384)
release_04 / human 5: crop (359,740)-(718,1388), bounds (423,746)-(717,1385)
release_05 / human 6: crop (720,740)-(1086,1388), bounds (785,746)-(1025,1381)
```

Human frame 1 is retained even though it is another crossed pose: it is not a
byte-identical duplicate of `cross_02`, and its matching CrossArm4 synthesis
provides the smoothest bridge into that sheet's uncrossing sequence. Human
frame 6 is a natural hands-down, normal front-facing idle pose. It is displayed
for its final release interval before runtime restores the standard directional
front-idle texture.

The common 1.22 working scale is followed by explicit per-frame
canonical-height factors:

```text
cross_00..02: 0.833753, 0.833753, 0.833753
release_00..05: 0.836915, 0.833753, 0.836915,
                0.866492, 0.869908, 0.868766
effective source-equivalent scales:
cross_00..02: 1.017179, 1.017179, 1.017179
release_00..05: 1.021036, 1.017179, 1.021036,
                1.057120, 1.061288, 1.059895
```

The second-row CrossArm4 synthesis is intrinsically shorter than its first
row, which accounts for the small calibrated scale step between
`release_02` and `release_03`. Adjacent frames within each synthesized row
vary by less than 0.4%; the row-boundary correction is about 3.5%.
CrossArm4's six evenly progressing poses still play at fixed 8 FPS for exactly
0.75 seconds. `FrameExtraction\extract_cross_arm.py` verifies both real JPEG
containers, decoded dimensions, SHA-256 hashes, both 3x2 layouts, row-major
reading order, selected human frame numbers, cell and row crop geometry,
complete-character bounds, torso anchors, shoe baselines, calibrated uniform
scales, output order, fixed playback rates, and terminal-pose roles. It reuses
`FrameExtraction\foreground_cutout.py` for the deterministic matte and
localized dark-shoe treatment.

The generated runtime assets are:

```text
Frames\CrossArm\cross_00.png through cross_02.png
Frames\CrossArmRelease\release_00.png through release_05.png
```

Every output is opaque 512x864 RGBA with exact RGB black outside the
character, a torso axis fixed at x=256, and a visible shoe baseline at y=843.
The canonical-height regression compares visible head top, green-vest
shoulder and waist bands, shoe baseline, and total body height against
`Frames\LeftTurn\turn_0.png`. Head top and total height must remain within
6 pixels; the pose-sensitive vest landmarks must remain within 16 pixels.
Crossing and release use explicit orders `0,1,2` and `0,1,2,3,4,5`
respectively at fixed 8 FPS.
Crossing takes 0.375 seconds and release takes 0.75 seconds, independently of
the walking-speed multiplier. `cross_02.png` is the exact persistent hold
frame; all release runtime assets come exclusively from `CrossArm4.png`.

Numbered visual review sheets are written to:

```text
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-contact-sheet.png
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-release-contact-sheet.png
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-combined-contact-sheet.png
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-canonical-height-overlay-pre-fix.png
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-canonical-height-overlay-post-fix.png
.ai-org\missions\2026-09-03-front-cross-arms-cecc239c\cross-arm-canonical-height-overlay-post-fix-zoomed.png
```

Run the asset-only canonical-height regression without regenerating frames:

```powershell
python FrameExtraction\extract_cross_arm.py --validate-height-assets
```

### Shared walk extraction

`FrameExtraction\extract_right_walk.py` is the single source of truth for both
walk directions. It extracts the approved six-pose sequence from
`RWalking2.png` into `Frames\RightWalk\walk_00.png` through `walk_05.png`, then
writes `Frames\LeftWalk\walk_00.png` through `walk_05.png` as byte-exact
horizontal mirrors of the corresponding right RGBA canvases. Pose ordering is
identical and both directions run at 6 FPS. Each right pose is isolated once
onto opaque pure black before the exact left mirror is written.

Regenerate the preview frames and numbered review sheet with:

```powershell
python FrameExtraction\extract_right_walk.py --evidence
```

The optional review sheet contains all six turns, six right-walk poses, and six
exact mirrored-left poses at the combined contact-sheet path above.
`RWalking2.png` contains JPEG data under its `.png` extension, remains unchanged
during extraction, and uses keep-only Godot import metadata because the source
sheet itself is not a runtime texture.

## Open and run

Open `TCFAnimation.sln` in Visual Studio with .NET 8 support, or import the
directory in the portable Godot 4.5.1 Mono editor under `.tools`.

The main scene is `Main.tscn`; the design viewport is 1920x1080 with a black
clear color.

## Deterministic controller probe

```powershell
dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release
```

The probe covers left/right turn and reversal behavior; explicit walking
direction; all 6 left-walk frames and all 6 right-walk frames at 6 FPS;
wrap and multi-frame/full-cycle deltas; release, opposite-direction, and
both-held cancellation; both edge notifications and latches; forced return,
latch clearing, and restart; invalid deltas; large deterministic deltas; and
left behavior after right-edge operations. It also verifies proportional walk
playback at `0.25x`, `0.5x`, `1.0x`, `2.0x`, and `3.0x`; invalid playback
multipliers; and fixed turn timing regardless of walk multiplier.
The clap coverage verifies the explicit six-source-frame order at fixed
8 FPS, complete return to both directional front poses, front-only trigger
restrictions, no restart, both arrow interruptions, edge-latch rejection,
speed independence, and continued turn/walk behavior.
The cross-arm coverage verifies all three crossing frames in order, persistent
`cross_02` hold across large deltas, second-X release, all six CrossArm4 release
frames in order, normal-front restoration, X/C/arrow exclusions, no queued
directional input, rejection from clap/turn/walk/return/edge states, and
fixed cross/release timing at every walking-speed multiplier.
