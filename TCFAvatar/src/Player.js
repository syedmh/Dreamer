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
  WALK_START_FRAME,
  STOP_SECONDS,
  CLOSE_FRAME_RATE,
  CLOSE_REWIND_MAX,
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
 * Standing, he faces the camera. Setting off runs on the *same* distance lock:
 * the turn frames were baked onto successive walk frames, so `walkPhase` alone
 * drives the stand, the turn and the walk as one continuous quantity. There is
 * no moment at which the walk is suspended, so there is no moment at which a
 * frame can go missing or a foot can slide.
 *
 * Settling is the one timed transition, because a stationary character has no
 * distance left to lock to.
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
     * Walk frames elapsed since he set off. Deliberately *not* wrapped: the turn
     * plays over the first `TURN_FRAME_COUNT - 1` of them and must not reappear
     * when the gait comes round again a cycle later.
     */
    this.startFrames = 0;
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
   * How far into setting off he is, in walk frames. Below `TURN_FRAME_COUNT - 1`
   * the turn is still playing; at or beyond it he is simply walking, and because
   * the last turn frame is the walk frame it targets, crossing that boundary
   * changes nothing on screen.
   */
  get startProgress() {
    return this.startFrames;
  }

  /** 0 = square to the camera, 1 = full profile. Drives the shadow's spread. */
  get turn() {
    if (this.settle > 0) return this.settle;
    return Math.min(1, this.startFrames / (TURN_FRAME_COUNT - 1));
  }

  /**
   * Maximum walk frames a single tick may advance. Staying below 1 is what
   * guarantees every baked frame is actually presented — of the walk *and* of
   * the turn, which advances on the same quantity.
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
          this.startFrames = TURN_FRAME_COUNT;
          this.walkPhase =
            STOP_TARGETS[STOP_VARIANTS.indexOf(this.stopVariant)] / WALK_FRAME_COUNT;
        } else {
          // Part-way round: rejoin the turn at the frame with the same body angle as
          // the one already on screen, rather than snapping back to the stand.
          this.startFrames = this.stopIndex;
          this.walkPhase = (START_PHASE + this.stopIndex / WALK_FRAME_COUNT) % 1;
        }
        this.settle = 0;
      }
      this.closing = 0;

      const advance = (speed * deltaSeconds) / this.strideWorldPx;
      const frames = advance * WALK_FRAME_COUNT;
      this.maxFramesPerTick = Math.max(this.maxFramesPerTick, frames);
      this.walkPhase = (this.walkPhase + advance) % 1;
      if (this.walkPhase < 0) this.walkPhase += 1;
      this.startFrames += frames;
      this.idleTime = 0;

      const turnIndex = Math.floor(this.startFrames);
      if (turnIndex < TURN_FRAME_COUNT) {
        // Still setting off. The last of these frames was baked as walk frame
        // WALK_START_FRAME + 7 exactly, so leaving this branch changes nothing on
        // screen -- the gait simply carries on.
        this.stopIndex = turnIndex;
        this.setFrame(turnFrameName(dir, turnIndex));
      } else {
        this.stopIndex = TURN_FRAME_COUNT - 1;
        this.setFrame(
          walkFrameName(dir, Math.floor(this.walkPhase * WALK_FRAME_COUNT) % WALK_FRAME_COUNT)
        );
      }
    } else if (this.closing !== 0 || (this.settle <= 0 && this.startFrames >= TURN_FRAME_COUNT)) {
      // He has stopped mid-stride. Carry the gait on to the nearer of the two passing
      // poses so the trailing foot comes in to meet the other, instead of the legs
      // shutting in a single frame the moment the key is released. The settle frames
      // for that pose were baked from the very walk frame he lands on, so handing over
      // to them changes nothing on screen -- exactly like setting off, in reverse.
      if (this.closing === 0) {
        // Prefer to finish the step forwards: rewinding the gait would read as the
        // foot sliding back. A rewind of a frame or two is allowed, because at rest
        // that reads as the foot settling rather than as travel.
        let best = null;
        STOP_TARGETS.forEach((target, i) => {
          const d = framesTo(this.walkPhase, target);
          const cost = d >= 0 ? d : -d <= CLOSE_REWIND_MAX ? -d : Infinity;
          if (best === null || cost < best.cost) best = { cost, d, i };
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
        this.startFrames = 0;
        this.walkPhase = START_PHASE;
        this.idleTime = 0;
        this.setFrame(standFrameName(0));
      } else {
        this.settle = Math.max(this.settle, this.stopIndex > 0 ? 1e-6 : 0);
        this.setFrame(stopFrameName(dir, this.stopVariant, this.stopIndex));
      }
    } else {
      this.startFrames = 0;
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
