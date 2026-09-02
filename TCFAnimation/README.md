# TCFAnimation

Minimal Godot 4.5.1 Mono/C# directional three-frame turn animation.

## Controls

- The character starts on the `LTurning.png` front (`0°`) frame.
- Hold the **physical Left Arrow** to advance left `0° -> 45° -> 90°`.
- Hold the **physical Right Arrow** to advance right `0° -> 45° -> 90°`.
- A held direction remains at its `90°` frame.
- Release the active arrow to reverse `90° -> 45° -> 0°`.
- Returning left remains on the left-sheet front; returning right remains on the
  right-sheet front.
- Starting the opposite direction from front selects that direction's front
  texture before advancing to `45°`.
- Switching directly between Left and Right first returns the active direction
  through `45°` to its own front, switches to the requested direction's front,
  and then turns outward. It never jumps between `90°` poses.
- Holding both arrows is neutral and returns the active direction to front.
- There is no walking, translation, movement, or procedural animation.

The animation runs at **8 frames per second** (one transition every 0.125
seconds).

## Regenerate the frames

From the project directory:

```powershell
python -m pip install -r requirements.txt
python FrameExtraction\extract_directional_turns.py --evidence
```

The script uses only the top rows of `LTurning.png` and `RTurning.png` and
writes exactly:

- `Frames\LeftTurn\turn_0.png`
- `Frames\LeftTurn\turn_1.png`
- `Frames\LeftTurn\turn_2.png`
- `Frames\RightTurn\turn_0.png`
- `Frames\RightTurn\turn_1.png`
- `Frames\RightTurn\turn_2.png`

`RTurning.png` arrived with JPEG data under a `.png` extension. On first run,
the extractor rewrites it as a valid PNG container and verifies that every
decoded RGB pixel remains unchanged.

Both directions use calibrated native whole-panel crops on identical opaque
512x864 black canvases. The generator keeps each complete character together
with the original studio floor, shadow, and reflection. It only excludes panel
dividers, blacks out each sheet's calibrated label rectangle, translates the
complete native-size panels to a stable torso center and floor baseline, and
gently fades only the outer left/right/top studio-background edges into black.
At the existing 2.5x runtime scale, the canvases fill the 2160-pixel viewport
height so the retained floor ends naturally at the viewport edge. The process
does not segment the character, separate shoes from the floor, restore colors,
create transparency, deform the artwork, or scale source artwork during frame
generation.

Development contact-sheet evidence is written only under:

```text
.ai-org\missions\2026-09-02-add-right-turn-cecc239c
```

## Open and run

Open `TCFAnimation.sln` in Visual Studio with .NET 8 support, or import the
directory in the portable Godot 4.5.1 Mono editor under `.tools`.

The main scene is `Main.tscn`; the design viewport is 3840x2160 with a black
clear color.

## Deterministic controller probe

```powershell
dotnet run --project ControllerProbe\ControllerProbe.csproj -c Release
```

The probe covers left and right turns, both returns, direct reversal through
direction-specific front frames, and both-held neutral behavior.
