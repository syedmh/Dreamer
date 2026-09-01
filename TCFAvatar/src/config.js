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
 * character gives each of the 24 baked frames more distance and therefore more
 * headroom before a slow frame could skip one.
 *
 * The atlas is baked at 650px of character, above this, so the sprite is only
 * ever reduced on screen — the sharp direction to resample in.
 */
export const CHARACTER_DISPLAY_HEIGHT = 406;

/**
 * The profile rig strides 419.2 sprite px per cycle, which at the display
 * height above is ~262 world px.
 *
 * Showing all 24 baked frames requires the cycle to last at least 24 ticks, so
 * the binding constraint is `topSpeed <= strideWorldPx * fps / 24`, i.e. 327
 * px/s at 30 fps against a top speed of 273. At that speed the gait advances
 * 0.84 frames per tick at 30 fps, which is the design floor; at a normal 60 fps
 * refresh it is 0.42. Walking takes 1.30 s per cycle and running 0.96 s, both
 * natural cadences for a man of this height — the speeds are scaled with the
 * character so the cadence is unchanged by his size.
 */
export const WALK_SPEED = 202;
export const RUN_MULTIPLIER = 1.35;

/** How quickly the character reaches full speed / comes to rest (px/s^2). */
export const ACCELERATION = 4060;
export const FRICTION = 5000;

export const WALK_FRAME_COUNT = 24;
export const IDLE_FRAME_COUNT = 12;
export const IDLE_FPS = 9;

/**
 * Standing, the character faces the camera; walking, he is in profile.
 *
 * Setting off is not a separate event from walking. `turn<Dir>_i` morphs the
 * stand onto walk frame `WALK_START_FRAME + i`, so the sequence is played on the
 * same distance lock as the walk itself and `turn<Dir>_07` *is* walk frame 14 —
 * the baker asserts the two are pixel-identical. Nothing is gated, so no foot
 * slides; the frame index advances at the walk's own rate, so nothing is skipped.
 *
 * The first attempt at this ran a timed turn with the walk suspended underneath.
 * That forces a choice between a turn short enough not to slide his feet and a
 * turn long enough to show its own frames — there is no value that does both,
 * which is why frames went missing. Deriving the turn from distance removes the
 * choice.
 */
export const TURN_FRAME_COUNT = 8;

/**
 * Walk frame the character sets off from and returns to.
 *
 * At frame 7 the gait function has both ankles under the hips — the passing
 * pose, legs together — which is the one point in the cycle that resembles
 * standing. Frame 0 is the contact pose, feet at their widest.
 */
export const WALK_START_FRAME = 7;

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
 * nearest passing pose -- the one point in the cycle where both ankles are under
 * the hips -- before the settle begins, which is at most six frames away and
 * usually about three.  Fast enough not to feel like input lag, and below one
 * frame per tick even at 30 fps, so the same no-skip guarantee holds here.
 */
export const CLOSE_FRAME_RATE = 26;

/**
 * Walk frames the closing step aims for: the gait's two passing poses, where both
 * ankles are already under the hips.  `stop<Dir><V>_07` was baked *as* the walk
 * frame in `STOP_TARGETS[V]`, pixel for pixel, so handing over to the settle at
 * that point changes nothing on screen -- the same trick that makes setting off
 * seamless, run in reverse.
 */
export const STOP_TARGETS = [7, 19];
export const STOP_VARIANTS = ['A', 'B'];

/**
 * How far the gait may be wound *backwards* to reach a passing pose.  At rest a
 * frame or two back reads as the foot settling; more than that reads as the foot
 * sliding backwards, so beyond this the step is completed forwards instead.
 */
export const CLOSE_REWIND_MAX = 3;
