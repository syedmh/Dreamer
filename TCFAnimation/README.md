# TCFAnimation

Godot 4.5.1 Mono/C# directional turn animation with sustained walking in
both directions.

## Controls

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
