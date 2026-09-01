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
*depict*. Six visual defects have passed a fully green run — translucent frames,
a head that never turned, a double-exposed face, washed-out colour, a missing
texture placeholder, and a character walking on three shoes. After any sprite
change, also run `node tools/capture_turn.mjs` and look at the strip.

## Tech

| Layer | Choice | Why |
|---|---|---|
| Engine | Phaser 3 (WebGL) | Best-in-class 2D platformer runtime — batched WebGL, arcade physics, atlas animation, texture filtering |
| Bundler | Vite | Instant HMR while iterating on feel; single-command production build |
| Sprite pipeline | Python + Pillow / NumPy / SciPy | The source art is a handful of still poses, so the frames are generated, not drawn |
| Verification | Playwright (headless Chromium) | The smoothness claim is proved against the real running game, not asserted on paper |

## The source art

`Walking.png` is the character sheet: a **WALK RIGHT** row and a **WALK LEFT**
row of side-profile figures on black.

It is worth being straight about what it is and is not:

- **It is not a walk cycle.** Each row is ~14 variations on a single mid-stride
  pose. The foot spread stays at 100–110 px in nearly every figure, there is no
  passing pose, and the same leg leads throughout — a real cycle oscillates
  through zero spread twice. Played back, it would read as a twitch. The printed
  `01…16` labels are decorative too: their pitch shrinks from 104 px to 72 px and
  drifts off the figures, and the rows actually hold 14 and 15 of them.
- **It is exactly the art that was missing.** Every figure is a genuine 90°
  profile — real ear, real nose contour, the waistcoat buttoning on the correct
  side, one shoe in front of the other. No amount of geometry applied to a
  front-on photograph produces that.

So the sheet is used as **rig art, not as animation**: one clean neutral figure
per direction (legs together, arm hanging, one clear shoe silhouette) is lifted
out and driven by a continuous gait function.

`Walking2.png` is a second authored sheet, and it supplies the pose the profile
sheet cannot: the character **turning**. Alongside its own (unusable — see below)
walk rows it holds two `walk -> idle (facing front)` transitions, and those carry
genuine **three-quarter** views, one per direction. Those intermediate angles are
the whole reason it is here.

`Avatar.jpg` and its cut-out `art/avatar-front.png` are the original front-on
photograph, and `art/avatar-front.png` **is** the standing pose. The stand was
taken from `Walking2.png` for a while, so that the stand, the three-quarter views
and the profile would all be one render pass. At 180px that pose simply had no
eyes left in it, which is why the stand is now baked from the 959px photograph
instead — see *The standing pose comes from the photograph* below for the
measurements, and for the footwear that trade costs.

## How the walk cycle is made

### 1. `tools/walk_sheet.py` — lift the figures off the sheet

- **Keying is exact, not thresholded.** The art is composited over pure black:
  background luminance is ≤ 1 and interior luminance is ≥ 24, so
  `alpha = clip(lum / 20, 0, 1)` is a true soft key. Rim colour is taken from the
  nearest fully-interior pixel rather than by dividing a near-zero alpha, which
  is what would otherwise produce a dark fringe.
- **Figures overlap at the shoes**, so column gaps cannot separate them. Torsos
  never overlap, so components are seeded in the top 45 % of each row and every
  ink pixel is then assigned to its nearest seed with a distance transform. The
  seam lands midway between two torsos, well clear of either figure's feet.
- **Landmarks are measured, not typed in.** The waistcoat is the only strongly
  green thing on the figure, the hand the only bare skin below the chest, the
  shoes the only dark mass below the kurta — so shoulder, waist, hem, ankle,
  ground, body axis, sleeve boundary and hand position all fall out of colour and
  silhouette-width analysis.

### 2. `tools/build_hero_sprites.py` — rig it and bake it

Each direction is cut into a 7-part rig (torso, near/far arm, near/far leg,
near/far foot) and posed. **Every cut follows an edge the art already has:**

- The **sleeve** is whatever white lies behind the green waistcoat — a real seam,
  not a hand-traced curve. Below the waistcoat the kurta behind the arm is the
  same white as the sleeve and no cut could be honest, so only the bare **hand**
  is taken; by then the sleeve has already ended at the cuff.
- Because the arm is `_largest(sleeve | hand)`, that mask is a **connectivity
  cliff, not a dial**. Widening the green by three pixels to chase an
  anti-aliased seam looks like a rounding change and is not: it reconnects the
  components, `_largest` picks a different one, and the arm layer jumps from
  4,583px to 26,140px — the whole sleeve starts swinging, the torso slot opens
  behind it and the fill smear that the slot rule is designed to hide comes into
  view. Rig geometry stays *identical* while this happens, so nothing numeric
  catches it. Change this mask only with the layer pixel count in front of you.
- The **near shoe** is split from the far one at a single measured column
  (`FOOT_CUT`), because the two shoes touch in both source figures.
- The **thigh** is the only invented part — it is hidden under the kurta in the
  source. It is extruded *upward* from the trouser it continues, and **tapers**
  towards the hip: a full-width rectangle swings its top corner out past the hem
  at full stride and reads as a paper flag stuck to the hip.
- Removing the sleeve leaves a slot down the back. Filling it from each row's own
  trailing pixel produces horizontal stripes, because that pixel flips between
  waistcoat and collar from row to row; sampling several pixels in and smoothing
  down the column carries the garment cleanly round the back. The hand leaves an
  *interior* hole instead, which no edge rule reaches, so whatever is still empty
  afterwards is closed by a small isotropic bleed.
- The kurta widens over its lowest rows (`HEM_FLARE`) so a swinging thigh stays
  covered.

The gait:

- Limbs are posed with **exact single-segment IK**: the forward matrix is
  `A = R(α)·(I + (s−1)·u·uᵀ)`, which stretches strictly *along* the limb axis, so
  a lengthening leg keeps its width and the ankle lands exactly on target.
- Legs split at the ankle so the shoe rides **rigidly** on the leg instead of
  being squashed by the leg's stretch.
- **The trouser and the shoe layers are disjoint.** Both bands used to reach past
  the ankle by `FOOT_OVERLAP` so no gap could open between them, but that made
  every pixel where they met get drawn *twice* under two different transforms —
  the shoe rigid on the ankle, the trouser stretched along its own axis by the
  IK. The moment the leg changed length the trouser's copy of the shoe slid out
  from behind the real one and the character walked on **doubled feet**: three
  shoes on screen instead of two. The shoe now wins the shared band outright,
  which costs nothing visible because it is composited after the leg and its copy
  was always the one actually on screen. Nothing needs to be bled downward to
  replace the trouser's copy: the leg's new bottom edge is scaled with the limb,
  so under the worst compression in the cycle (−25%) it rides only
  `0.75·FOOT_OVERLAP` up, still inside the shoe's own rigid reach. Measured over
  both directions and every phase, the thinnest the trouser/shoe seal ever gets
  is 1102px of overlap, and the layers now share **0** pixels.
- **The rim light is taken off the waistcoat's trailing edge.** `Walking.png` is
  lit with a cold kicker from behind, and where it grazes the green waistcoat it
  turns the outermost pixels cyan — measured at `(77,132,141)`, blue actually
  *ahead* of green, against `(44,106,72)` for the cloth. At sheet resolution it
  reads as a highlight; blown up to 650px against a black stage it is a hard blue
  line down the trailing edge of the sleeve, and because the arm swings across it
  while the waistcoat stays put, it reads as a line drawn *on* the character
  rather than as light. Since the costume is white, green, skin and near-black,
  cyan is a hue it never legitimately uses, so those pixels are identified by hue
  alone: green and blue both more than `CYAN_CHROMA` clear of red, with green
  less than `CYAN_BALANCE` ahead of blue. Measured on the rig, that cut separates
  238 rim pixels from 3354 waistcoat pixels with nothing in between. Each one
  keeps its own brightness — the rim stays a highlight — and is retinted with the
  hue of the nearest non-cyan pixel, which along this edge is the waistcoat.
  Alpha is never touched, so the silhouette is unchanged.
- **The far limb's shadow is neutral.** It used to lift blue 8% and green 1% to
  "keep the shadow cool rather than muddy", but on a white sleeve that is a hue
  shift rather than a shadow: at `FAR_LIMB_TINT` it turned white into
  `(178,180,193)`, a distinctly cyan grey, on exactly the limbs that move.
- The stance foot travels **exactly linearly**, so a planted foot moves backwards
  at precisely the body's forward speed and cannot skate. The swing is a cubic
  Hermite whose end slopes match the stance rate, making the whole foot path C¹
  continuous — no jerk at either handover.
- Stride is **derived, not guessed**: the foot covers `2·STEP_MAX` while planted
  for `STANCE_FRACTION` of the cycle, so one cycle must carry the body
  `2·STEP_MAX / STANCE_FRACTION`. `STEP_MAX` is 0.72 of leg length, which puts
  the feet 0.39 × body height apart at contact — life-size.
- **Both directions are baked from their own authored art. Nothing is mirrored.**
  Mirroring would flip the parting in his hair, the side the waistcoat buttons on
  and the hand he leads with. Both rigs are normalised to one height and one
  baseline first, so turning around cannot pop.

Re-bake with:

```powershell
cd tools
python build_hero_sprites.py     # needs pillow + numpy + scipy
```

### 3. The stand, and the turn that reaches it

Standing still, the character faces the camera — a profile statue reads as a man
waiting for a bus, not as the hero of the scene. The stand is `idleFront_2` from
`Walking2.png`, normalised onto the **same** rig height, ground line and body axis
as the two profile rigs, so he cannot change size when he turns. It breathes: a
whole-body settle of ~1.7 display px over 12 frames. There is no rig behind a
drawing, so nothing is articulated — only enough movement to read as alive.

The turn between the stand and the profile is **not** a single set played both
ways: setting off and settling have opposite requirements, so there are two
families, 8 frames each.

`turn<Dir>_i` morphs the front stand onto **walk frame `7 + i`**. Its targets
advance with the gait, so his legs are already walking while his body comes
round — which is what setting off actually looks like. Frame 7 is the *passing*
pose, where both ankles are under the hips: the one point in the cycle that
resembles standing. The last frame, `turn<Dir>_07`, therefore **is**
`walk<Dir>_14`, pixel for pixel; the bake asserts it and refuses to write a sheet
where it is not true. Leaving the turn set is an ordinary one-frame step of the
walk, not a handover.

`stop<Dir>A_i` and `stop<Dir>B_i` morph the front stand onto walk frames **7 and
19** — the two passing poses, half a cycle apart. Their targets are fixed,
because by the time he is settling he is stationary and legs that kept walking
would slide. Played backwards, `stop<Dir><V>_07 → 00` runs from a real walk
frame to the front stand. The same identity holds at that end too, so the gait
hands over to the settle without a seam.

Both families are built by the same morph, and the rotation itself is carried by
**real drawings**. The earlier version interpolated straight from the front stand
to the profile, so every angle in between was invented — a warped front figure,
not a man seen from that side. `Walking2.png` supplies the missing angles, so the
turn now changes over through them in order: front stand → `turn<Dir>_8` →
`turn<Dir>_7` → `turn<Dir>_6` → the profile walk frame. Each changeover is narrow
and they do not overlap, so no frame is a half-and-half average of two views —
it is one drawing, or a brief bridge between two that are already only a few
degrees apart.

Between drawings, for every scanline the current figure's leading and trailing
edges are mapped onto the profile's, interpolated by the turn's progress. A
uniform squash was tried first and was wrong — seen from the front the head sits
over the body axis, in profile it projects forward, so the two never lined up.

The colour transition is a **shape morph, not a cross-fade**. Averaging two
alphas leaves anything only one figure covers at partial opacity: the daylight
between the walking legs came out at ~80/255 and the background showed through
him. Instead each image is converted to a signed distance field, the fields are
interpolated, and the alpha is read back off the result — so every frame is
fully opaque. Colour is weighted by each source's *own* coverage.

Three bands change over at three different moments — **legs at 0.26, body at
0.50, head at 0.79**. Feet commit before shoulders do when a man sets off, so
that ordering is what a turn actually looks like; it also removes an artefact,
because the three-quarter drawings stand with the feet together while the walk
frame they hand over to already has them apart, and morphing one silhouette into
the other conjured a shoe-shaped ghost in the gap. Switching the legs early means
the frames where that gap is widest show real walk art instead of a blend.

The head keeps two small extra mechanisms — it sweeps toward the leading edge,
and its far side is drawn in behind the nose (anchored at the leading edge;
anchoring at the centre reads as a squash). Both are now turned down to roughly a
third of their former strength, because the three-quarter drawings do that
rotation for real and doubling it makes the head visibly overshoot.

Everything above is zero at both ends of the morph, which is what keeps the
endpoints exact and the handoffs identities.

### Why the walk is not taken from `Walking2.png` too

Because it is not in there. `tools/walk2_phase.py` measures the foot spread of
every figure in the `walkRight` row:

```
spread: 52 56 58 58 58 58 58 61 62 46 46 46 0 0
```

A gait cycle oscillates — contact, passing, contact. This is nine near-identical
contact poses followed by the legs closing, which is a *stop*, not a cycle. The
sheet is variations on a pose, not animation. `walk2_analyse.py` confirms it from
the other side: the 13 → 0 wrap is 2.3–3.2× the mean interior step, so the row
does not even close into a loop. The gait therefore stays synthesised, where it
is periodic by construction; only the turn comes from the authored art.

## Why the animation cannot jerk or drop a frame

Two independent guarantees.

**1. The cycle itself is seamless.** Every frame is sampled from one continuous
periodic function, so frame 23 wraps into frame 00 by construction. The baker
prints the frame-to-frame pixel delta including the wrap:

```
walk right: n=24 mean=10.419 min=8.599 max=11.516 wrap=8.988 wrap/mean=0.863
walk left:  n=24 mean=9.977 min=7.848 max=11.047 wrap=8.445 wrap/mean=0.846
```

The wrap delta sits inside the ordinary interior range, so the loop seam is not
merely small — it is indistinguishable from a normal frame step. That is a
consequence of the gait function being C¹, not something tuned by hand.

**2. Playback is driven by distance, not by a timer.** One cycle is baked to
carry the character exactly `strideLengthPx` forward, so the frame index is
`floor((distance / stride) · 24)`. The drawn contact foot therefore cannot slide,
at any speed, under any acceleration.

Showing all 24 frames requires the cycle to last at least 24 render ticks, so the
binding constraint is `topSpeed ≤ strideWorldPx · fps / 24`. A cycle covers
261.9 world px, giving **0.42 frames per tick at 60 Hz** and 0.84 at 30 Hz — the
design floor. Walking takes 1.30 s per cycle and running 0.96 s. The walk and run
speeds are scaled with the character, so his size never changes his cadence.

**3. The transitions are inside the same sequence, not bolted onto it.** Setting
off was originally a timed 0.08 s turn that had to finish before the walk began.
That is an impossible brief: short enough not to slide his planted feet, or long
enough to show its own 8 frames — no value does both. At 0.08 s and 60 Hz the
index ran 0 → 1 → 3 → 4 → 6 → 7, which is exactly the "missing frames in
between" it looked like. Deriving the turn from distance removes the choice, and
baking its last frame *as* a walk frame removes the seam.

Stopping is the same idea run backwards. Releasing the key mid-stride used to
shut his legs in a single frame, so the gait first runs on to the nearer of the
two passing poses — at most 6 frames, typically 3, at 26 frames/sec, so under
0.23 s — and only then hands over to the settle set baked from that very frame.
The rate is capped at half a frame per tick so the handover is covered by the
same no-skip bound at any frame rate.

`tools/verify_walk.mjs` proves this against the real running game:

```powershell
npm run dev                    # in one shell
node tools/verify_walk.mjs     # in another
```

It drives Chromium, holds each walk key in turn, samples the displayed frame on
every render tick, and fails on a missing atlas, any console error, any tick that
advances the cycle by more than one frame, or any of the 24 frames of either
direction never being shown. Crucially it collapses the turn and walk sets onto a
**single gait index**, so the seam between them is checked rather than skipped
over. It also checks the stand: the character must face the camera on load; the
closing step must be gap-free and must land on the frame its settle set was baked
from; and the settle must run through all 8 frames in descending order, ending on
the front-facing stand, opaque throughout.

```
atlas   loaded=true frames=108 (walkRight=24 walkLeft=24 stand=12 turnRight=8 turnLeft=8 stopRight=16 stopLeft=16) sheet=6000x5936
right   samples=241 4.00s (60.2 fps)  walkFrames=24/24 turnFrames=8/8 maxStepPerTick=1 skips=0 handoffStep=1 minAlpha=1.000 travel=521.3px
left    samples=241 4.00s (60.2 fps)  walkFrames=24/24 turnFrames=8/8 maxStepPerTick=1 skips=0 handoffStep=1 minAlpha=1.000 travel=-804.6px
stop R  close=A->7 ticks=18 step=1 handoff=1 | settle ticks=21 frames=8/8 from=7 maxStep=1 wrongWay=0 minAlpha=1.000 end=idleFront_02 turn=0.00
stop L  close=B->19 ticks=22 step=1 handoff=1 | settle ticks=21 frames=8/8 from=7 maxStep=1 wrongWay=0 minAlpha=1.000 end=idleFront_01 turn=0.00
errors  0

PASS  standing, setting off and walking are one gap-free distance-locked sequence
      in both directions, and the character settles back to face the camera.
```

**These gates are necessary, not sufficient.** Five separate visual defects —
translucent frames, a head that did not turn, a double-exposed face, washed-out
colour, and a green missing-texture placeholder at his feet — passed every number
above. They were found by capturing the canvas on every animation frame and
looking at the result:

```powershell
node tools/capture_turn.mjs    # one tile per render tick
python tools/strip.py          # contact strips of what actually reached the screen
```

The capture tile is sized from the character at runtime, so raising
`CHARACTER_DISPLAY_HEIGHT` cannot quietly start cropping his head out of the
strip — which is precisely where a mismatch shows first.

Anything that changes the morph or the transitions should finish there.

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

The atlas cannot invent detail the drawings do not contain. Measured figure
heights in the pinned art:

| Source | Figure height | Used for | Scale to 650px bake |
|---|---|---|---|
| `art/avatar-front.png` | 959px | the standing pose | **0.7x — a downscale** |
| `Walking.png` | 326px | the walk cycle and profile | 2.0x |
| `Walking2.png` | 180px | the three-quarter turn frames | 3.6x — *now super-resolved, below* |

Both sheet parsers find their cells by measuring the art rather than assuming a
grid, so those numbers are what the files actually hold, not an extraction
artefact.

This ranking is exactly what the rendered frames look like. The stand is crisp —
eyes with catchlights, individual beard hairs, a legible crest badge. The walk is
good. The eight turn frames were visibly softer than either, because ~30px of head
in the source becomes ~90px in the frame.

Two things that look like they should help, and measurably do not:

- *Resampling in linear light instead of gamma.* Correct in principle, worth
  0.13/255 mean difference here — the art is smooth enough that the gamma error
  never accumulates.
- *More unsharp on the upscaled frames.* Rendering one turn frame at 85%, 40% and
  0% intermediate sharpening produced three indistinguishable images. The
  softness is missing information, not lost contrast.

`FRAME_SHARPEN` is set to `(2.0, 55, 2)`. The radius tracks one *source* pixel
measured in frame pixels — 650/326 is very nearly 2 — so it restores detail the
art actually contains. At radius 1 it instead sharpened the gaps between real
detail and rang the sleeve and collar edges.

That unsharp pass runs on colour that has first been flood-extended past the
silhouette to its nearest opaque neighbour, and the original alpha is put back
untouched. Run naively over straight-alpha art it would read the transparent
black outside the figure as shadow and cut a dark rim around him.

### The turn frames are super-resolved

Unsharp masking cannot help the turn, because the problem is missing information
rather than lost contrast. A learned upscaler can, so `tools/upscale_walk2.py`
runs **Real-ESRGAN x4** over `Walking2.png` once and writes `art/Walking2-x4.png`
(6144x4096). `walk2_sheet.cutout()` keys each figure out of that sheet when it is
present, so the turn is baked from a 720px figure instead of a 180px one — a 0.9x
downscale rather than a 3.6x upscale. The pinned source is never touched, and the
bake still works without the file.

Two findings cost real time and are worth not repeating:

- **The anime model is the wrong model.** `RealESRGAN_x4plus_anime_6B` is trained
  on cel and line art. On this stylised 3D render it flattened the face to a
  cartoon, moved the moustache, and erased both the damask weave and the crest
  badge. Edge-energy metrics went *up* while the image got worse. The general
  `RealESRGAN_x4plus` (23 blocks) keeps identity and fabric, and roughly doubles
  real edge detail — mean `|dx|` 7.8 → 14.0 on a turn figure.
- **Full strength still ruins the face.** ~30px of head means ~2-3px eyes, so the
  model does not restore a face, it invents one, and the invention — hollow dark
  sockets, a smeared lip — reads worse than blur. Head weights of 0.00, 0.35, 0.60
  and 1.00 were rendered and compared side by side.

`HEAD_SR_WEIGHT = 0.35` over `HEAD_SR_BAND = (0.13, 0.23)` of the figure is the
result: the head gets a graded 35% of the super-resolved image, ramping to full
strength below the shoulders. Hair, beard edge and collar sharpen; the face still
reads as a face. Clothing has no such limit, and the gain there is large — the
ringing halos and stair-stepping that unsharp used to leave along the waistcoat
placket are gone, and the buttons are legible.

`basicsr` is deliberately not a dependency: its import chain breaks against
current torchvision, so `RRDBNet` is written out inline in the tool, and the
block count is inferred from the checkpoint's `body.N.` keys. The pass needs
`torch` and takes about nine minutes on CPU. It is skipped when the output
already exists — delete `art/Walking2-x4.png` to rebuild.

The remaining gap is the face during the first two turn frames. It is better, not
solved: **re-rendering `Walking2.png` at higher resolution is still the only
complete fix.**

### The standing pose comes from the photograph

The stand was sourced from `Walking2.png` so that it would match the turn frames
exactly. At 180px that pose had no eyes left in it — a featureless smear beside a
walk frame that showed buttons and beard hairs.

It is now baked from `art/avatar-front.png` at 959px, which is a *downscale* and
therefore the sharpest frame in the atlas. The figure is a closer identity match
than it first appears: it carries the same damask waistcoat and gold buttons as
`Walking.png`, where `Walking2.png` has plain green ones. The known cost is
footwear — the photograph wears strapped sandals and the walk art wears closed
shoes — which is spent during the first few frames of the turn, where both read
as dark leather with a tan sole. Measured head height holds at 0.160 of the figure
in the stand against 0.145 in the walk, and head luminance ramps smoothly from 119
to 99 across the turn rather than stepping.

`_normalise()` skips its intermediate unsharp below `FRONT_SHARPEN_MIN_SCALE`, so
the photograph — which is being reduced, not enlarged — is sharpened once at
output resolution instead of twice.

### Breathing does not lift him off the floor

The idle used to raise the whole body, feet included, by `int(round(lift))` — a
pixel-quantised levitation that read as a bob. It is now a Gaussian horizontal
swell across the chest band only (`FRONT_BREATH_SWELL`, `BREATH_CENTRE`,
`BREATH_SPREAD`), which is sub-pixel smooth and leaves the feet planted.
Frame-to-frame delta across the stand fell from 9.824 to 2.691.

## Layout

```
Walking.png                    pinned source art (profile walk) — do not modify
Walking2.png                   pinned source art (three-quarter turn frames)
Avatar.jpg                     original front-on photo; reference only, not baked
art/avatar-front.png           pinned cut-out of it; baked as the standing pose
tools/walk_sheet.py            sheet keying, figure segmentation, landmark measurement
tools/walk2_sheet.py           Walking2 segmentation and cut-outs, used by the bake
tools/upscale_walk2.py         Real-ESRGAN x4 pass over Walking2 -> art/Walking2-x4.png
tools/walk2_analyse.py         proves which Walking2 sequences actually join up
tools/walk2_phase.py           measures gait phase; proves the walk rows are not a cycle
tools/build_hero_sprites.py    rig segmentation, gait baking, turn morph, atlas packing
tools/verify_walk.mjs          headless proof of the smoothness claim
tools/smoke.mjs                playability check: loads, walks, settles, holds fps
tools/capture_turn.mjs         per-render-tick canvas capture of a transition
tools/strip.py                 contact strips from that capture, for looking at
tools/shot.mjs                 full-screen grabs at 1x and 3x device pixel ratio
public/assets/hero/hero.png    6000x5936 atlas, 108 frames of 400x742
public/assets/hero/hero.json   TexturePacker JSON-Array atlas
public/assets/hero/rig-meta.json  runtime contract (stride, baseline, hitbox, reach)
src/config.js                  all gameplay tuning constants
src/Player.js                  distance-locked animation, direction frame sets, turn state
src/backdrop.js                the character's contact shadow, and the parallax
                               scenery layers used when SCENERY is on
src/scenes/                    Boot (load) and Game (world)
```

Frames are named `walkRight_00…23`, `walkLeft_00…23`, `idleFront_00…11`,
`turnRight_00…07`, `turnLeft_00…07`, and `stopRightA/B_00…07` with their left
counterparts. In every morph set frame `00` is the front stand and frame `07` is
a real walk frame — `walk_14` for the turn sets, `walk_07` for the `A` settle
sets and `walk_19` for the `B` ones.

## Known scope

- The far arm and far leg are tinted copies of the near ones. In a true profile
  the far limb is almost entirely occluded, so this reads correctly; it would not
  survive a three-quarter camera.
- The morph's middle frames are a warped photograph, not drawn art. In motion
  they read as a turn; paused and studied, the warping is visible.
- Interrupting the settle part-way — releasing the key and pressing it again
  within about a third of a second, after he has begun coming round — rejoins the
  turn at the matching body angle, but the legs can be up to a few frames out.
  Releasing and re-pressing immediately is exact; the partial case is not. Fixing
  it properly needs a two-dimensional frame family (entry phase × morph step),
  roughly 190 frames per direction, which was not judged worth it.
- The turn-in takes about 0.4 s of travel. It is gap-free and locked to the
  ground, but whether that is the right *feel* has not been judged by a human
  playing it.
- No jumping, platforms, hazards or collectibles yet — movement first, by design.
