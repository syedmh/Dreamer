# TCF Avatar — Platformer

A side-scrolling platform game starring the avatar. This first slice covers
exactly one thing properly: **left/right movement with a smooth, complete,
true-profile walk cycle**, presented on a static black stage so nothing but the
animation is moving.

## Run it

```powershell
npm install
npm run dev      # http://127.0.0.1:5173
npm run build    # production bundle in dist/
npm test         # all three gates -- needs `npm run dev` already running
```

Controls: `A` / `D` or `←` / `→` to walk, hold `Shift` to run.

`npm test` runs `verify` (the animation proof), `smoke` (does it actually play?)
and `shots` (1x and 3x screenshots). They drive the real game over
`http://127.0.0.1:5173`, so start the dev server first, or point them elsewhere
with `GAME_URL`.

**A green run is necessary, not sufficient.** The gates check frame indices,
continuity, distance lock and alpha; none of them can see what the pixels
*depict*. Eight visual defects have passed a fully green run — translucent
frames, a head that never turned, a double-exposed face, washed-out colour, a
missing texture placeholder, a character walking on three shoes, a far shoe that
read as the near one double-exposed, and a turn that handed off to the worst pose
in the cycle. After any sprite change, also run

```powershell
node tools/capture_walk.mjs   # a whole cycle, both directions, dpr 3
node tools/capture_turn.mjs   # the transitions, one tile per render tick
node tools/capture_stop.mjs   # the closing step, legs cropped, every tick
```

and **look at the strips**. `capture_walk` covers the walk body, where the
depiction defects have been; `capture_turn` covers the transitions, where the
sequencing defects have been.

## Tech

| Layer | Choice | Why |
|---|---|---|
| Engine | Phaser 3 (WebGL) | Best-in-class 2D platformer runtime — batched WebGL, arcade physics, atlas animation, texture filtering |
| Bundler | Vite | Instant HMR while iterating on feel; single-command production build |
| Sprite pipeline | Python + Pillow / NumPy / SciPy / OpenCV | The source art is authored poses on black; the pipeline keys, aligns and packs them, and measures the stride it must be played back at |
| Verification | Playwright (headless Chromium) | The smoothness claim is proved against the real running game, not asserted on paper |

## The source art

`Frames/` is the character sheet, and it is the real thing: a directory of
authored renders, one PNG per pose, 512×864, on pure black.

```
Frames/RightWalk/walk_00…05.png     one full step, right-facing
Frames/LeftWalk/walk_00…05.png      the same step, left-facing
Frames/RightTurn/turn_0…2.png       front stand -> three-quarter -> profile
Frames/LeftTurn/turn_0…2.png        the same, to the other side
```

The `.png.import` files beside them are Godot's metadata and are ignored.

This replaced two earlier sheets, `Walking.png` and `Walking2.png`, which were
**not** animation. Each was a row of variations on a single mid-stride pose:
`Walking.png` held the foot spread at 100–110 px in nearly every figure with no
passing pose at all, and `Walking2.png`'s walk row measured
`52 56 58 58 58 58 58 61 62 46 46 46 0 0` — nine near-identical contact poses and
then the legs closing, which is a *stop*, not a cycle. Neither row even closed
into a loop. Everything the character did therefore had to be **synthesised**:
the figures were cut into a seven-part rig (torso, near/far arm, near/far leg,
near/far foot) and driven by a continuous gait function, and the turn was a morph
of the front stand onto successive walk frames.

`Frames/` is animation. Every pose in the game is now a render somebody made, and
the rig, the gait function, the morph and the interpolation are all gone.

### What the art actually measures

Established by measurement, not assumption — worth not re-deriving:

- **Six frames are one step, not a stride.** The planted foot slides backwards
  monotonically through the cycle (bounding-box trailing edge 437 → 424 → 375 →
  345 → 313) and the foot spread oscillates exactly once:
  `319, 305, 104, 104, 137, 234`. So the cycle has a single passing pose, in the
  middle, not the two a full stride would give.
- **The loop closes.** Frame-to-frame greyscale distances are
  `30.2, 53.1, 69.8, 69.0, 70.2` and the wrap is `66.4` — *smaller* than the
  largest interior step, so the seam is no more visible than the cycle itself.
  The near/far shading does not flip across it either (front leg 128.8 → 131.7,
  brighter than the back leg at both ends), so there is no flash at the loop.
- **`LeftWalk` is a byte-exact horizontal mirror of `RightWalk`.** The two turn
  sets are *not* mirrors of each other; the two walk sets are. This is a real
  cost and it is recorded under *Known scope*.
- **There is no baked contact shadow.** Pixel counts fall to exactly 0 by row
  832, so nothing has to be masked off the floor.
- **The render hard-clips shadow to pure black.** A histogram of the far-shoe box
  is bimodal: 5247 px at exactly 0, **nothing at all between 1 and 14**, then 892
  in 15–40. Deep shadow in this art carries no recoverable detail. That is why
  the ground plane varies by nine rows between frames, and why the far limb
  cannot be lifted — see *The far limb is dark and stays dark*.
- **The sets do not share a ground plane.** Walk bottoms are 830/830/838/838/839/
  837; `RightTurn` 846/849/843; `LeftTurn` 843/859/846. Head tops are steady
  (walk 184–186, turn 180–182), and the turn figure is ~1.7 % taller because he
  stands upright. Each set is therefore aligned on **its own** ground, which
  keeps the natural head bob instead of normalising it away.
- **The two front stands are all but identical.** `RightTurn/turn_0` and
  `LeftTurn/turn_0` differ by mean |Δ| 16.5. One of them is used as the canonical
  stand for `idleFront_00`, `turn<Dir>_00` and `stop<Dir>A_00` in **both**
  directions, so settling always lands on the idle frame pixel-for-pixel.

## How the sprites are made

### `tools/frames_source.py` — lift the figures off the black

The key is a **border flood fill**, not a threshold. Black is filled inward from
the frame edge, so background is removed while black *enclosed* by the figure —
shoe interiors, the shadow between the legs, hair — stays opaque. Only the 1–2 px
shell that the fill reaches keeps a soft ramp, which is what stops a dark fringe
forming.

`_largest()` keeps any connected component at least 0.2 % of the main body rather
than the single biggest one. A strict "biggest component only" was silently
discarding 296 lit pixels, some at luminance 240 — a detached forearm or shoe
highlight. Component selection on this art has a **cliff**: change the input mask
slightly and a whole limb can vanish without any error.

### `tools/build_frames_sprites.py` — align it and bake it

- **Integer shifts only.** Each set is aligned on its own ground plane and its
  mean head x by whole-pixel translation. Nothing is resampled, so every frame
  keeps the sharpness it was rendered at. This is the whole reason the new atlas
  is 2005×2924 where the synthesised one was 6000×5936: there is no upscaling
  pass left to carry.
- **The stride is measured, not declared.** `measure_stride()` tracks the planted
  shoe by template matching and fits the slope: −35.2 px per frame, so
  **211.20 px per six-frame cycle**. `measure_step()` cross-checks it from the
  other side with a heel-to-heel centroid span, and the assertion between them is
  ±25 % — a "did the tracker change feet" net, not a precision check, because the
  clipped soles bias the span outwards.
- **Two measurement traps, both hit and both fixed.** `_planted()` needs an area
  floor of **900 px**: at 150 it started returning a 12×32 sliver at x199–210
  instead of the real shoe at x315–437, once `_largest()` was widened. And
  template matching on the **binary alpha mask** does not work at all — every
  solid blob looks alike, so the shoe matches the trouser; `TM_CCORR_NORMED` on
  alpha stuck at the origin and an unconstrained search jumped to the *other*
  shoe. The combination that works is a **luminance** template with
  `TM_SQDIFF_NORMED` and the search band restricted to `[x0 − 80·i, x0 + 6]`,
  which gives strictly monotonic offsets `0, −10, −52, −80, −141`.
- **Names are aliased, images are not duplicated.** 25 frame names resolve to 17
  distinct pictures: the turn and the settle are the same three drawings in
  opposite order, and their end frame is the idle itself.

Baked result: frame 401×731, `centerX` 200, `baselineY` 704,
`characterHeightPx` 650.8, `strideLengthPx` 211.20, hitbox 135×678. The character
height is within a pixel of the synthesised rig's 650, so nothing changed size on
screen when the art was swapped.

### In-betweening was tried three ways and rejected

Six poses per step is half what the synthesised rig produced, so the obvious move
is to interpolate. It does not work on this art, and the evidence is kept here so
it is not attempted a fourth time. `tools/frame_interp.py` is the (unused)
implementation.

Registration residual, mean |warp(A) − B| over the union mask — "no-flow" is the
raw distance between the two frames:

| pair | no-flow | DIS medium | Farneback 7lvl/win41 | coarse ¼ | coarse+refine |
|---|---|---|---|---|---|
| 0→1 (small step) | 30.2 | **11.1** | 10.7 | 13.0 | 10.7 |
| 2→3 (large step) | 69.8 | 32.9 | 32.8 | 31.3 | 34.7 |
| 4→5 (large step) | 70.2 | 34.1 | 41.4 | 33.4 | 37.3 |

Turn pairs are worse still (no-flow 68–102 → DIS 39–51), because a body rotation
reveals pixels present in neither frame. Then, rendered and looked at:

- **Blended bidirectional warp** turns the legs translucent — partly-transparent
  pixels jump from ~1,700 in the keys to ~20,000, and magenta shows straight
  through the leg. This is the same failure already rejected once as a runtime
  cross-fade.
- **Single-source warp** avoids the translucency (rim only ~3,300) and is fine on
  the small pair, marginal on 2→3, and on 4→5 **tears the shoes into a doubled
  black smear** — precisely the defect fixed at the shoes not long before.

The cause is dis-occlusion, not tuning. A swinging leg uncovers kurta that exists
in neither neighbouring frame, so no flow field can supply it. The cost of not
interpolating is stated plainly under *Known scope*.

### The far limb is dark and stays dark

The render crushes the far leg and far shoe almost to black while leaving the far
shoe's speculars bright, so at high magnification against the black stage the far
limb can read as a void with a few highlights floating in it.

Lifting it was tried: a local-contrast compression that lifts deep shadow toward
a readable floor while pulling speculars down, weighted by a blurred in-silhouette
luminance so it could not cut a halo at the edge. Rendered side by side, it
**barely moved the far leg and visibly damaged the near shoe**, washing it to a
muddy red. That is unsurprising given the histogram above — below luminance 15 the
source is exactly 0, so there is nothing to lift and the operator only finds the
near shoe's real detail to spoil.

So the far limb is left as authored. At panel resolution and in motion it reads
as a leg in shadow; it is only conspicuous under 4× nearest-neighbour zoom, which
is a property of the zoom.

The related lesson from the synthesised pipeline still holds wherever a far limb
*is* shaded by hand: a pure multiply preserves the highlight-to-base ratio, so a
dark glossy layer keeps its speculars and the far shoe reads as the near one
duplicated. Compress each channel toward the layer's diffuse anchor instead.

## Why the animation cannot jerk or drop a frame

**1. The cycle closes on itself.** The wrap from frame 05 back to frame 00 is a
greyscale distance of 66.4 against interior steps of `30.2, 53.1, 69.8, 69.0,
70.2` — smaller than the largest step inside the cycle, so the loop seam is not
merely small, it is unremarkable next to the cycle's own motion. This is a
property of the authored art, checked rather than engineered: the synthesised rig
guaranteed it by being C¹, and the measurement is now what replaces that
guarantee.

**2. Playback is driven by distance, not by a timer.** One cycle is measured to
carry the character exactly `strideLengthPx` forward, so the frame index is
`floor((distance / stride) · 6)`. The drawn contact foot therefore cannot slide,
at any speed, under any acceleration.

Showing all six frames requires the cycle to last at least six render ticks, so
the binding constraint is `topSpeed ≤ strideWorldPx · fps / 6`. Measured in the
running game, the walk advances **0.207 frames per tick at 60 Hz** and 0.414 at
30 Hz — a factor of two inside the bound at the design floor. Because the stride
of the authored step (211.2 px) is within a per-cent of the synthesised rig's
(419.27 px over two steps), the **cadence did not change** when the art was
swapped: 1.5 steps per second walking, 2.1 running.

**3. Setting off is timed, and the gait is held still through it.** This is the
one place where the authored art forced a design change, and the reasoning
matters because the obvious answer is wrong in both directions.

The synthesised turn was distance-locked, and correctly so: its frames were
morphs of the front stand onto successive *walk* frames, so his legs walked
underneath the rotation and the lock kept his feet planted. The artist's three
turn drawings are the opposite — a rotation **on the spot**, both feet planted.
Running the gait underneath them slides those planted feet across the floor, and
at the gait's own rate the three frames cover 66 world px, a sixth of his height.

So the gait is pinned at `WALK_START_FRAME` for a short `TURN_SECONDS`, which
confines the slide to what he covers while accelerating away — about 27 px.

The old objection to a timed turn was real but is no longer binding: a timed turn
had to be short enough not to slide his feet *and* long enough to show its frames,
and at **8** frames no value did both — at 0.08 s and 60 Hz the index ran
0 → 1 → 3 → 4 → 6 → 7, which is exactly the "missing frames in between" it looked
like. Three frames need only three ticks, 0.1 s at 30 Hz, so the conflict is gone.

**4. The handoff lands on the right pose, and this is asserted.** Coming out of
the turn, the character enters the walk at `WALK_START_FRAME = 3` — the frame
measured closest to the profile stand the last turn drawing leaves him in
(distance 68.6, inside the walk's own 30–70 frame-step range).

This is the defect the rebuild's first green run hid. The turn advanced the walk
phase as it played, so the handoff arrived at `(3 + 3) mod 6 = 0` — the pose
measured **furthest** from the profile stand, at 96.4, the worst of all six —
while every number in the gate stayed green, because the gate only asked whether
the gait stepped by one. It now asserts the pose by name.

**5. Stopping runs the same idea backwards.** Releasing the key mid-stride used
to shut his legs in a single frame, so the gait first runs on to the passing pose
and only then hands over to the settle set. With a one-step cycle there is
exactly one such pose, in the middle, so it is reached by the legs *closing* from
either side and the shorter way round is always taken — see `CLOSE_REWIND_MAX`'s
removal in `src/config.js`. The rate is capped at half a frame per tick so the
handover is covered by the same no-skip bound at any frame rate.

`tools/verify_walk.mjs` proves this against the real running game:

```powershell
npm run dev                    # in one shell
node tools/verify_walk.mjs     # in another
```

It drives Chromium, holds each walk key in turn, samples the displayed frame on
every render tick, and fails on a missing atlas, any console error, any tick that
advances the walk or the turn by more than one frame, any of the six walk frames
or three turn frames of either direction never being shown, a turn frame shown
after the walk has started, or a walk entered at the wrong pose. It also checks
the stand: the character must face the camera on load; the closing step must be
gap-free and must land on the frame its settle set was baked from; and the settle
must run through all three frames in descending order, ending on the front-facing
stand, opaque throughout.

```
atlas   loaded=true frames=25 (walkRight=6 walkLeft=6 stand=1 turnRight=3 turnLeft=3 stopRight=3 stopLeft=3) sheet=2005x2924
right   samples=241 4.01s (60.2 fps)  walkFrames=6/6 turnFrames=3/3 maxStepPerTick=1 skips=0 turnStep=1 enters=walk_3 minAlpha=1.000 travel=530.8px
left    samples=241 4.01s (60.1 fps)  walkFrames=6/6 turnFrames=3/3 maxStepPerTick=1 skips=0 turnStep=1 enters=walk_3 minAlpha=1.000 travel=-804.7px
stop R  close=A->3 ticks=1 step=0 handoff=0 | settle ticks=21 frames=3/3 from=2 maxStep=1 wrongWay=0 minAlpha=1.000 end=idleFront_00 turn=0.00
stop L  close=A->3 ticks=2 step=0 handoff=0 | settle ticks=21 frames=3/3 from=2 maxStep=1 wrongWay=0 minAlpha=1.000 end=idleFront_00 turn=0.00
errors  0

PASS  standing, setting off and walking are one gap-free distance-locked sequence
      in both directions, and the character settles back to face the camera.
```

**These gates are necessary, not sufficient.** Eight separate visual defects —
translucent frames, a head that did not turn, a double-exposed face, washed-out
colour, a green missing-texture placeholder at his feet, a character on three
shoes, a far shoe that read as the near one double-exposed, and a turn that
handed off to the worst pose in the cycle — passed every number above. They were
found by capturing the canvas on every animation frame and looking at the result:

```powershell
node tools/capture_walk.mjs    # a whole cycle, both directions, at dpr 3
node tools/capture_turn.mjs    # one tile per render tick, through a transition
node tools/capture_stop.mjs    # the closing step, legs cropped, every tick
node tools/measure_stop.mjs    # how far the planted foot slides while stopping
```

Both capture tiles are sized from the character at runtime, so raising
`CHARACTER_DISPLAY_HEIGHT` cannot quietly start cropping his head out of the
strip — which is precisely where a mismatch shows first. `capture_walk` also
refuses to pass unless it reached all six frames of the cycle *and* the tile is a
plausible fraction of the canvas: a strip that silently shows the wrong band of
the character, or three quarters of the cycle, is worse than no strip at all.
Both traps are real — the walk is distance-locked, so a run started mid-stage
stalls against the world bound with a quarter of the cycle unseen, and sizing the
tile off `canvas.width / camera.width` (which is 1 at zoom 3, not 3) crops a
chest-high band that looks perfectly clean.

Anything that changes the sprites or the transitions should finish there.

## The stage

The character is presented on plain black, on a screen that never scrolls. The
world is exactly one viewport wide (`WORLD_WIDTH = VIEW_WIDTH`), so the camera has
nowhere to scroll to and does not follow him; `GameScene` reads those two
constants and only calls `startFollow` if the world is actually wider. Set
`SCENERY = true` in `src/config.js` to get the sky, hills and rail fence back.

He walks until the *drawing* meets the screen edge, not until his collision box
does. The hitbox is measured from the standing silhouette (170px wide, so 85px
either side of his axis), but mid-stride his legs are spread and his leading arm
is out, so the widest frame reaches 190px either side — the baker measures that
and records it as `reach`.
The world bounds are inset by the difference, which is why he comes to rest flush
against the edge instead of being clipped in half by it.

The contact shadow is generated whatever the stage looks like. It belongs to the
character rather than the scenery: skipping it with the rest of the backdrop left
Phaser drawing its green missing-texture placeholder at his feet.

## Resolution

This is built to run on a 200" wall, so every avoidable resample between the
source drawing and the panel has been removed.

**The canvas is native, not stretched.** The game used to render a fixed 1280x720
framebuffer and let `Scale.FIT` stretch it, which on a 4K wall is a 3x bilinear
upscale of the finished image — by far the largest and cheapest-to-fix loss in the
chain. `RENDER_SCALE` in `src/config.js` reads `window.screen` and
`devicePixelRatio`, clamps to 1..4, and sizes the canvas to
`VIEW_WIDTH x RENDER_SCALE`. The main camera is zoomed by the same factor, so the
game's coordinate system is unchanged and every constant in `src/config.js` still
reads in 1280x720 units — but the character is now rasterised at full panel
resolution.

**The HUD gets its own camera.** Camera zoom is applied about the camera's
*centre*, and that includes `setScrollFactor(0)` objects: at 3x, HUD text at
(20, 18) lands at canvas x = 1920 + (20 - 1920) x 3, far off the left edge. A
second camera with its origin at the top-left, the same zoom, and no bounds maps
it to (60, 54) instead. `cameras.main.ignore(hudObjects)` and
`hudCamera.ignore(worldObjects)` keep each object on exactly one camera, or it
draws twice. Text also carries `setResolution(RENDER_SCALE)` so glyphs are
rasterised at panel resolution rather than upscaled.

**Frames are baked above display size.** 650px of character is baked and 406 is
drawn (`CHARACTER_DISPLAY_HEIGHT`), so the runtime only ever *reduces* the sprite.
108 frames of 400x742 pack as 15 columns x 8 rows into a 6000x5936 atlas, inside
the 8192 texture limit reported by even the low-end software renderer. That is
roughly 142 MB of VRAM — fine for a dedicated display machine, and worth knowing
before the frame count grows.

### Sharpness is capped by the source art

The atlas cannot invent detail the drawings do not contain, so what matters is
how many pixels of figure the source actually holds against how many the panel
asks for.

| Source | Figure height | Used for | Scale to the 650px bake |
|---|---|---|---|
| `Frames/*Walk/walk_0*.png` | 645–655px | the walk cycle | **1.0x — no resampling** |
| `Frames/*Turn/turn_*.png` | 662–678px | the turn and the stand | **1.0x — no resampling** |

This is the single largest quality gain of the rebuild, and it came for free.
The previous art was a set of sheets whose figures were 326px (`Walking.png`,
the walk) and 180px (`Walking2.png`, the turn), so the bake was upscaling them
2.0× and 3.6×. `Frames/` renders each pose at roughly the height it is baked at,
so the pipeline aligns with **integer pixel shifts only** and never resamples.

Superseded with it:

- **The Real-ESRGAN pass.** `tools/upscale_walk2.py` ran Real-ESRGAN x4 over the
  180px turn art to get it to 720px, because unsharp masking cannot help when the
  problem is missing information rather than softness — a ~30px head became a
  ~90px frame. It is no longer in the pipeline. Its one hard-won finding is worth
  keeping: use **x4plus (23-block)**, not `x4plus_anime_6B`, which erases fabric
  detail and redraws faces.
- **The photographic stand.** The stand was baked from the 959px `Avatar.jpg`
  cut-out because at 180px the `Walking2.png` pose had no eyes left in it. That
  bought sharpness at the cost of a render mismatch — the stand came from a
  different pass than everything around it, which is what the "transition between
  standing and moving is not smooth" complaint was actually about. The stand is
  now `Frames/RightTurn/turn_0`, from the **same render pass** as the walk and the
  turn, at full height. The mismatch is fixed at the root rather than blended over.
- **Breathing.** The old stand was a 12-frame idle with a subtle vertical rise,
  which read on a large panel as the character drifting up and down. The idle is a
  single authored frame and is perfectly still.

The remaining sharpness limit is now the source render itself, not the pipeline.
## Layout

```
Frames/RightWalk/walk_00…05.png    pinned source art — one step, right — do not modify
Frames/LeftWalk/walk_00…05.png     pinned source art — the same step, left
Frames/RightTurn/turn_0…2.png      pinned source art — front -> three-quarter -> profile
Frames/LeftTurn/turn_0…2.png       pinned source art — the same, to the other side
tools/frames_source.py             border-flood keying, component keeping, alignment
tools/build_frames_sprites.py      set alignment, stride measurement, atlas packing
tools/frame_interp.py              optical-flow in-betweening — REJECTED, kept as record
tools/verify_walk.mjs              headless proof of the smoothness claim
tools/smoke.mjs                    playability check: loads, walks, settles, holds fps
tools/capture_turn.mjs             per-render-tick canvas capture of a transition
tools/capture_walk.mjs             whole-cycle canvas capture of the walk, both ways, dpr 3
tools/capture_stop.mjs             per-tick capture of the closing step, legs cropped
tools/measure_stop.mjs             planted-foot slide while coming to a halt
tools/strip.py                     contact strips from a capture, for looking at
tools/shot.mjs                     full-screen grabs at 1x and 3x device pixel ratio
public/assets/hero/hero.png        2005x2924 atlas, 25 names over 17 images of 401x731
public/assets/hero/hero.json       TexturePacker JSON-Array atlas
public/assets/hero/rig-meta.json   runtime contract (stride, baseline, hitbox, reach)
src/config.js                      all gameplay tuning constants
src/Player.js                      distance-locked animation, direction frame sets, turn state
src/backdrop.js                    the character's contact shadow, and the parallax
                                   scenery layers used when SCENERY is on
src/scenes/                        Boot (load) and Game (world)
```

Superseded by the rebuild and no longer part of the pipeline: `Walking.png`,
`Walking2.png`, `Avatar.jpg`, `art/avatar-front.png`, `tools/walk_sheet.py`,
`tools/walk2_sheet.py`, `tools/walk2_analyse.py`, `tools/walk2_phase.py`,
`tools/upscale_walk2.py` and `tools/build_hero_sprites.py`. They are kept because
the measurements quoted above were taken with them.

Frames are named `walkRight_00…05`, `walkLeft_00…05`, `idleFront_00`,
`turnRight_00…02`, `turnLeft_00…02` and `stopRightA_00…02` with their left
counterparts — 25 names over 17 distinct images. The settle is the turn in
reverse under a second name, and frame `00` of both is the idle itself, so
settling ends on the idle frame pixel-for-pixel.

## Known scope

- **`LeftWalk` is a byte-exact horizontal mirror of `RightWalk`.** Walking left
  therefore puts his waistcoat emblem on the opposite chest, parts his hair the
  other way and leads with the other hand. The synthesised pipeline deliberately
  avoided this by baking each direction from its own profile art; the authored
  art does not offer the choice, because the two walk sets are the same pixels.
  The two *turn* sets are genuinely different renders, so this affects the walk
  only. Fixing it needs six more rendered poses.
- **Six poses per step, where the synthesised rig gave twelve.** Every pose is now
  a real render rather than a sample of a gait function, but there are half as
  many of them: the animation shows about 9 poses per second walking instead of
  18. Cadence and on-screen size are unchanged. In-betweening cannot close the
  gap — the measurements are under *In-betweening was tried three ways and
  rejected*.
- The far arm, far leg and far shoe are crushed almost to black by the source
  render. In profile that reads correctly, but the far shoe keeps bright
  speculars, so under heavy magnification against the black stage the far limb can
  read as a void with highlights in it. Lifting it was measured and rejected —
  see *The far limb is dark and stays dark*.
- Setting off holds the gait still for `TURN_SECONDS`, so his feet slide about
  27 world px while he accelerates away. This is inherent to turning on the spot
  in authored art and can only be removed by drawing turn frames that walk.
- Interrupting the settle part-way — releasing the key and pressing it again
  after he has begun coming round — rejoins the turn at the matching body angle
  and pins the gait to the entry pose, so the legs are correct but the body angle
  jumps by at most one drawing. Releasing and re-pressing immediately is exact.
- Whether the turn *feels* right at 0.15 s has not been judged by a human playing
  it, only measured and looked at frame by frame.
- No jumping, platforms, hazards or collectibles yet — movement first, by design.
