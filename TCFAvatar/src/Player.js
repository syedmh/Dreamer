import Phaser from 'phaser';
import {
  CHARACTER_DISPLAY_HEIGHT,
  WALK_SPEED,
  RUN_MULTIPLIER,
  ACCELERATION,
  FRICTION,
  WALK_FRAME_COUNT,
  IDLE_FRAME_COUNT,
  IDLE_FPS,
  TURN_FRAME_COUNT,
  TURN_SECONDS,
  WALK_START_FRAME,
  STOP_SECONDS,
  CLOSE_FRAME_RATE,
  STOP_TARGETS,
  STOP_VARIANTS
} from './config.js';

const pad = (i) => String(i).padStart(2, '0');
const walkFrameName = (dir, i) => `walk${dir}_${pad(i)}`;
const turnFrameName = (dir, i) => `turn${dir}_${pad(i)}`;
const stopFrameName = (dir, variant, i) => `stop${dir}${variant}_${pad(i)}`;
const standFrameName = (i) => `idleFront_${pad(i)}`;

/** Speed under which the character counts as standing still. */
const STILL_SPEED = 6;

const START_PHASE = WALK_START_FRAME / WALK_FRAME_COUNT;

/** Signed distance in walk frames from `phase` to frame `target`, in [-12, 12). */
const framesTo = (phase, target) =>
  ((target - phase * WALK_FRAME_COUNT + WALK_FRAME_COUNT * 1.5) % WALK_FRAME_COUNT) -
  WALK_FRAME_COUNT / 2;

/**
 * The walk cycle is advanced by distance travelled, not by a timer.
 *
 * The rig was baked so that one full 24-frame cycle carries the character
 * exactly `strideLengthPx` forward. Driving the frame index from accumulated
 * distance therefore locks the drawn feet to the ground: the contact foot can
 * never slide, and because the phase is a continuous quantity that is only ever
 * incremented, no frame in the cycle can be skipped or repeated as long as a
 * single tick advances the phase by less than one frame.
 *
 * Both facings are baked from their own authored profile art, so turning around
 * swaps frame sets rather than mirroring the sprite. Mirroring would flip the
 * parting in his hair, the side his waistcoat buttons on and the hand he leads
 * with; swapping keeps every one of those correct.
 *
 * Standing, he faces the camera. Coming round to profile is the artist's three
 * drawings, and they are a rotation on the spot: both feet stay planted while
 * the body turns. So the turn runs on a short timer and the gait is *held* at
 * the frame it hands over to, rather than advancing underneath it. Advancing it
 * would walk his legs through a drawing in which they do not move.
 *
 * Settling back to the stand is timed for the same reason, plus the plainer one
 * that a stationary character has no distance left to lock to.
 */
export default class Player extends Phaser.Physics.Arcade.Sprite {
  constructor(scene, x, y, rig) {
    super(scene, x, y, 'hero', standFrameName(0));

    this.rig = rig;
    this.displayScale = CHARACTER_DISPLAY_HEIGHT / rig.characterHeightPx;

    /** World pixels covered by one complete walk cycle. */
    this.strideWorldPx = rig.walk.strideLengthPx * this.displayScale;

    this.walkPhase = START_PHASE;
    this.idleTime = 0;
    this.facing = 1;
    /**
     * Seconds spent coming round since he set off, capped at `TURN_SECONDS`.
     * Deliberately *not* wrapped: the turn plays once and must not reappear when
     * the gait comes round again a cycle later.
     */
    this.turnTime = 0;
    /** 1 while settling back to the stand, 0 once square to the camera. */
    this.settle = 0;
    /** Frames still to run to bring the trailing foot in, or 0. */
    this.closing = 0;
    /** Which settle family the closing step is aiming at. */
    this.stopVariant = STOP_VARIANTS[0];
    this.stopIndex = 0;
    this.maxFramesPerTick = 0;

    scene.add.existing(this);
    scene.physics.add.existing(this);

    this.setOrigin(rig.centerX / rig.frameWidth, rig.baselineY / rig.frameHeight);
    this.setScale(this.displayScale);

    this.body.setSize(rig.hitbox.width, rig.hitbox.height);
    this.body.setOffset(
      rig.centerX - rig.hitbox.width / 2,
      rig.baselineY - rig.hitbox.height
    );
    this.body.setMaxVelocity(WALK_SPEED * RUN_MULTIPLIER, 2400);
    this.body.setDragX(0);

    this.shadow = scene.add
      .image(x, y, 'shadow')
      .setOrigin(0.5, 0.5)
      .setDepth(this.depth - 1);
  }

  get topSpeed() {
    return WALK_SPEED * RUN_MULTIPLIER;
  }

  /** What the viewer actually sees, for the HUD and for verification. */
  get displayFrame() {
    return this.frame.name;
  }

  /**
   * How far into coming round he is, 0 to 1. At 1 the turn is finished and he is
   * simply walking, entering the gait at `WALK_START_FRAME` — the walk pose
   * closest to the profile the last turn drawing leaves him in.
   */
  get startProgress() {
    return Math.min(1, this.turnTime / TURN_SECONDS);
  }

  /** 0 = square to the camera, 1 = full profile. Drives the shadow's spread. */
  get turn() {
    if (this.settle > 0) return this.settle;
    return this.startProgress;
  }

  /**
   * Maximum walk frames a single tick may advance. Staying below 1 is what
   * guarantees every baked walk frame is actually presented. The turn is timed,
   * not distance-locked, so it is bounded separately: `TURN_SECONDS` must be at
   * least `TURN_FRAME_COUNT` render ticks long for every turn drawing to show.
   */
  framesPerTick(deltaSeconds) {
    return (this.topSpeed * deltaSeconds * WALK_FRAME_COUNT) / this.strideWorldPx;
  }

  update(deltaSeconds, input) {
    const body = this.body;
    const running = input.run;
    const target = (running ? WALK_SPEED * RUN_MULTIPLIER : WALK_SPEED) * input.axis;

    if (input.axis !== 0) {
      const delta = target - body.velocity.x;
      const step = ACCELERATION * deltaSeconds;
      body.velocity.x += Phaser.Math.Clamp(delta, -step, step);
      this.facing = input.axis > 0 ? 1 : -1;
    } else {
      const step = FRICTION * deltaSeconds;
      const v = body.velocity.x;
      body.velocity.x = Math.abs(v) <= step ? 0 : v - Math.sign(v) * step;
    }

    // Each direction has its own baked art, so facing is a frame-set choice.
    const dir = this.facing > 0 ? 'Right' : 'Left';

    const speed = Math.abs(body.velocity.x);
    const moving = input.axis !== 0 || speed > STILL_SPEED;

    if (moving) {
      if (this.settle > 0) {
        if (this.stopIndex >= TURN_FRAME_COUNT - 1) {
          // He had not begun coming round yet, and the frame on screen *is* a walk
          // frame, so rejoin the gait exactly there: nothing changes on screen at
          // all. This is the common case -- releasing and re-pressing within a tick
          // or two -- so it is the one worth making free.
          this.turnTime = TURN_SECONDS;
          this.walkPhase =
            STOP_TARGETS[STOP_VARIANTS.indexOf(this.stopVariant)] / WALK_FRAME_COUNT;
        } else {
          // Part-way round: rejoin the turn at the frame with the same body angle as
          // the one already on screen, rather than snapping back to the stand.
          this.turnTime = (this.stopIndex / TURN_FRAME_COUNT) * TURN_SECONDS;
          this.walkPhase = START_PHASE;
        }
        this.settle = 0;
      }
      this.closing = 0;

      this.idleTime = 0;

      if (this.turnTime < TURN_SECONDS) {
        // Still coming round. The gait is pinned to the frame the turn hands over
        // to, because these three drawings rotate him on the spot with both feet
        // planted -- running the walk underneath them would slide those feet.
        this.turnTime = Math.min(TURN_SECONDS, this.turnTime + deltaSeconds);
        this.walkPhase = START_PHASE;
        this.stopIndex = Math.min(
          TURN_FRAME_COUNT - 1,
          Math.floor((this.turnTime / TURN_SECONDS) * TURN_FRAME_COUNT)
        );
        this.setFrame(turnFrameName(dir, this.stopIndex));
      } else {
        const advance = (speed * deltaSeconds) / this.strideWorldPx;
        const frames = advance * WALK_FRAME_COUNT;
        this.maxFramesPerTick = Math.max(this.maxFramesPerTick, frames);
        this.walkPhase = (this.walkPhase + advance) % 1;
        if (this.walkPhase < 0) this.walkPhase += 1;
        this.stopIndex = TURN_FRAME_COUNT - 1;
        this.setFrame(
          walkFrameName(dir, Math.floor(this.walkPhase * WALK_FRAME_COUNT) % WALK_FRAME_COUNT)
        );
      }
    } else if (this.closing !== 0 || (this.settle <= 0 && this.turnTime >= TURN_SECONDS)) {
      // He has stopped mid-stride. Carry the gait on to the cycle's passing pose so
      // the trailing foot comes in to meet the other, instead of the legs shutting
      // in a single frame the moment the key is released. The settle frames
      // for that pose were baked from the very walk frame he lands on, so handing over
      // to them changes nothing on screen -- exactly like setting off, in reverse.
      if (this.closing === 0) {
        // The cycle passes through its one feet-together pose in the middle, so
        // the legs close whichever way round the gait is wound to reach it. Take
        // the shorter way and halve the worst-case travel.
        let best = null;
        STOP_TARGETS.forEach((target, i) => {
          const d = framesTo(this.walkPhase, target);
          if (best === null || Math.abs(d) < Math.abs(best.d)) best = { d, i };
        });
        this.closing = best.d;
        this.stopVariant = STOP_VARIANTS[best.i];
      }

      // Half a frame per tick, not one: the arrival hands over to the settle set,
      // and the bound has to cover that step too, at any frame rate.
      const step = Math.min(CLOSE_FRAME_RATE * deltaSeconds, 0.5);
      if (Math.abs(this.closing) <= step) {
        this.walkPhase = STOP_TARGETS[STOP_VARIANTS.indexOf(this.stopVariant)] / WALK_FRAME_COUNT;
        this.closing = 0;
        this.settle = 1;
        this.stopIndex = TURN_FRAME_COUNT - 1;
        this.setFrame(stopFrameName(dir, this.stopVariant, this.stopIndex));
      } else {
        const move = Math.sign(this.closing) * step;
        this.closing -= move;
        this.walkPhase = (this.walkPhase + move / WALK_FRAME_COUNT + 1) % 1;
        this.setFrame(
          walkFrameName(dir, Math.floor(this.walkPhase * WALK_FRAME_COUNT) % WALK_FRAME_COUNT)
        );
      }
    } else if (this.settle > 0 || this.turn > 0) {
      // He has come to rest facing away from the camera; bring him round. The
      // settle starts from however far round he actually got, so tapping the key
      // for two ticks brings him back from two frames out, not from full profile.
      if (this.settle <= 0) {
        this.settle = (this.stopIndex + 1) / TURN_FRAME_COUNT;
        // Reached here without a closing step, so he only ever got part-way into the
        // turn. Variant A is baked from the walk frame the turn sets off from, so it
        // is the one whose legs match what is on screen.
        this.stopVariant = STOP_VARIANTS[0];
      }
      this.settle = Math.max(0, this.settle - deltaSeconds / STOP_SECONDS);

      const want = Phaser.Math.Clamp(
        Math.ceil(this.settle * TURN_FRAME_COUNT) - 1,
        0,
        TURN_FRAME_COUNT - 1
      );
      // Never step more than one frame, whatever the frame rate: on a slow machine
      // the settle takes an extra tick or two rather than jumping.
      this.stopIndex += Math.sign(want - this.stopIndex);

      if (this.settle <= 0 && this.stopIndex <= 0) {
        this.settle = 0;
        this.turnTime = 0;
        this.walkPhase = START_PHASE;
        this.idleTime = 0;
        this.setFrame(standFrameName(0));
      } else {
        this.settle = Math.max(this.settle, this.stopIndex > 0 ? 1e-6 : 0);
        this.setFrame(stopFrameName(dir, this.stopVariant, this.stopIndex));
      }
    } else {
      this.turnTime = 0;
      this.walkPhase = START_PHASE;
      this.idleTime += deltaSeconds;
      this.setFrame(standFrameName(Math.floor(this.idleTime * IDLE_FPS) % IDLE_FRAME_COUNT));
    }

    // Square to the camera he is nearly twice as wide as he is side-on, and the
    // shadow has to agree with the silhouette above it.
    const spread = 1.35 - 0.2 * this.turn;
    const squash = 1 - Math.min(speed / this.topSpeed, 1) * 0.16;
    this.shadow.setPosition(this.x, this.y - 6);
    this.shadow.setScale(this.displayScale * spread * squash, this.displayScale * 0.62);
    this.shadow.setAlpha(0.9 * squash);
  }
}
