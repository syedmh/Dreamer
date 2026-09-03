/**
 * Central tuning constants. Values here are the only knobs gameplay code reads,
 * so the game stays consistent when the sprite rig is re-baked.
 */

export const VIEW_WIDTH = 1280;
export const VIEW_HEIGHT = 720;

/**
 * Supersampling factor: how many device pixels the game renders per world pixel.
 *
 * The world stays 1280x720 whatever this is — every constant below is in world
 * pixels, and `GameScene` zooms the camera by exactly this much to fill a canvas
 * that is this much bigger. Only the *resolution* changes.
 *
 * This is the single largest sharpness knob in the project. A 1280x720 canvas
 * stretched onto a 4K wall is a 3x upscale of the finished frame, and no amount
 * of care in the sprite bake survives it. Sized to the display instead, the
 * character is drawn at the panel's own pixel pitch.
 *
 * Capped at 4 so an oversized panel cannot ask for a canvas the GPU will not
 * allocate; a 200" 4K screen wants exactly 3.
 */
const MAX_RENDER_SCALE = 4;

function detectRenderScale() {
  if (typeof window === 'undefined' || !window.screen) return 1;
  const dpr = window.devicePixelRatio || 1;
  const needed = Math.max(
    (window.screen.width * dpr) / VIEW_WIDTH,
    (window.screen.height * dpr) / VIEW_HEIGHT
  );
  return Math.min(MAX_RENDER_SCALE, Math.max(1, needed));
}

export const RENDER_SCALE = detectRenderScale();

/**
 * The stage is exactly one screen wide, so the camera never moves and the
 * character walks to the visible edge and stops there. Set this wider than
 * `VIEW_WIDTH` and the camera resumes following him — `GameScene` decides
 * which it is from these two numbers alone.
 */
export const WORLD_WIDTH = VIEW_WIDTH;
export const GROUND_Y = 624;
export const GROUND_THICKNESS = 96;

/**
 * Draw the sky, hills and ground art, or present the character on plain black.
 *
 * Black is the honest backdrop for judging the walk: nothing scrolls, nothing
 * parallaxes, so any jitter in the animation is the animation's own.
 */
export const SCENERY = false;

/**
 * On-screen height of the character in world pixels.
 *
 * This is also an animation-safety knob: one walk cycle covers
 * `strideLengthPx * (height / characterHeightPx)` world pixels, so a taller
 * character gives each baked frame more distance and therefore more headroom
 * before a slow frame could skip one.
 *
 * The atlas is baked at 651px of character, above this, so the sprite is only
 * ever reduced on screen — the sharp direction to resample in.
 */
export const CHARACTER_DISPLAY_HEIGHT = 406;

/**
 * The authored walk covers 211.2 sprite px per cycle, which at the display
 * height above is ~132 world px. One cycle is one *step*, not a full stride:
 * the six drawings carry him from one foot's contact to the other's, and the
 * artist drew them to loop there.
 *
 * That is the same ground per step as the rig it replaces — which measured
 * 419.3px over a two-step cycle from entirely different art — so the cadence
 * the game has always had is unchanged: 1.5 steps/s walking, 2.1 running.
 *
 * Showing all 6 baked frames requires the cycle to last at least 6 ticks, so the
 * binding constraint is `topSpeed <= strideWorldPx * fps / 6`, i.e. 659 px/s at
 * 30 fps against a top speed of 273 — far more headroom than the 24-frame rig
 * had, because each frame now owns four times the distance.
 */
export const WALK_SPEED = 202;
export const RUN_MULTIPLIER = 1.35;

/** How quickly the character reaches full speed / comes to rest (px/s^2). */
export const ACCELERATION = 4060;
export const FRICTION = 5000;

/**
 * Six drawn poses per step, and no synthesised frames between them.
 *
 * In-betweening was tried first and rejected on measurement, not taste. Optical
 * flow registers the large-motion pairs no better than a third of the way —
 * DIS, Farneback and a coarse-to-fine pyramid all leave a residual of ~33 out of
 * ~70 — because a swinging leg uncovers kurta that exists in neither of its
 * neighbours. Blending two warped neighbours turns the legs translucent; warping
 * a single neighbour tears the shoes into the doubled smear that was already
 * rejected once. A drawn pose beats an invented one.
 */
export const WALK_FRAME_COUNT = 6;

/**
 * The stand is one drawing, held still.
 *
 * The previous idle breathed across twelve synthesised frames, which read as the
 * character drifting up and down while standing. He is now simply still.
 */
export const IDLE_FRAME_COUNT = 1;
export const IDLE_FPS = 1;

/**
 * Standing, the character faces the camera; walking, he is in profile.
 *
 * `turn<Dir>_00` is the idle frame itself and `turn<Dir>_01/02` are the artist's
 * three-quarter and profile drawings, so coming round is three real renders
 * rather than a morph. They play on the walk's own distance lock, so the frame
 * index advances at the gait's rate and nothing is gated or skipped.
 *
 * The turn no longer ends *on* a walk frame — authored art cannot be made to,
 * and faking it is what produced the bad turn frames before. It does not need
 * to: the profile stand measures 68.6 from walk frame 3, against 30–70 for the
 * walk's own frame-to-frame steps, so arriving there is a smaller change than
 * the gait routinely makes by itself.
 */
export const TURN_FRAME_COUNT = 3;

/**
 * Seconds to come round from facing the camera to full profile.
 *
 * The turn is timed rather than distance-locked, which is the opposite of what
 * the synthesised rig did — and the reason is the art. Those turn frames were
 * morphs of the stand onto successive *walk* frames, so his legs walked while
 * his body came round and the distance lock was exactly right. The artist's
 * three drawings are a rotation on the spot with both feet planted, so
 * advancing the gait underneath them would slide those planted feet across the
 * floor: at the gait's own rate the three frames would cover 66 world px, a
 * sixth of his height.
 *
 * Holding the gait still instead confines that slide to whatever he travels
 * while accelerating away, which this constant bounds to about 27 px.
 *
 * The old note that a timed turn cannot be both short enough to avoid sliding
 * and long enough to show its frames was written against eight frames, needing
 * 0.27 s at 30 fps. Three frames need 0.1 s, so the conflict is gone; at this
 * length every frame still lands on its own tick at 30 fps.
 */
export const TURN_SECONDS = 0.15;

/**
 * Walk frame the character sets off from and returns to.
 *
 * Frame 3 is the passing pose — the feet are 104px apart against 319 at contact,
 * the closest the cycle comes to standing — and it is also the walk frame
 * nearest the profile stand of every one of the six, by a clear margin.
 */
export const WALK_START_FRAME = 3;

/**
 * Seconds to settle back to facing the camera.
 *
 * Unlike setting off this is timed, not distance-locked, because by then he is
 * stationary and there is no distance to lock to. Long enough for all eight
 * `stop<Dir>_*` frames to land on their own render tick at 30 fps and above.
 */
export const STOP_SECONDS = 0.34;

/**
 * Walk frames per second while the trailing foot comes in to meet the other.
 *
 * Stopping mid-stride and cutting straight to a standing pose snaps the legs shut
 * in a single frame.  A person does not do that; he finishes bringing his back
 * foot up alongside the front one.  The gait is therefore carried on to the
 * passing pose -- the point in the cycle where both ankles are under the hips --
 * before the settle begins, which is at most three frames away.
 *
 * A frame of the authored cycle covers four times the ground a frame of the old
 * synthesised cycle did, so this is a quarter of the old rate: the trailing foot
 * closes at the same speed across the floor as it always has, which is what the
 * eye actually judges.  Still below one frame per tick at 30 fps, so the same
 * no-skip guarantee holds here.
 */
export const CLOSE_FRAME_RATE = 13;

/**
 * Walk frames the closing step aims for: the gait's passing pose, where both
 * ankles are already under the hips.  One cycle of the authored art is one step,
 * so it passes through that pose once rather than twice, and there is a single
 * settle family to aim at instead of two.
 */
export const STOP_TARGETS = [WALK_START_FRAME];
export const STOP_VARIANTS = ['A'];

/**
 * The closing step always takes the shorter way round to the passing pose, in
 * whichever direction that is.
 *
 * The synthesised rig would never wind the gait backwards by more than a frame
 * or so, on the reasoning that a reversed walk reads as the foot sliding back.
 * That reasoning does not survive contact with this art. One cycle here is a
 * single step, so the feet are apart at the ends and together in the middle --
 * measured spans of 319, 305, 104, 104, 137 and 234 sprite px across frames 0
 * to 5. The passing pose therefore sits in the middle of the cycle and is
 * reached by the legs *closing* from either side; going "backwards" through
 * 5 -> 4 -> 3 brings the trailing foot in exactly as 0 -> 1 -> 2 does.
 *
 * Confirmed both ways: `tools/measure_stop.mjs` puts the foot slide at 52.8 px
 * either side, and `tools/capture_stop.mjs` renders the two strips, in which
 * both show the legs coming together and neither reads as a walk in reverse.
 * Halving the worst-case travel is worth more than a direction preference, so
 * there is no rewind limit.
 */
