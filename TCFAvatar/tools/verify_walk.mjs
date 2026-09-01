/**
 * Headless verification of the walk cycle, the stand, and the transitions
 * between them.
 *
 * Drives the real game in Chromium, holds a walk key, and samples what the
 * player is showing on every animation tick.
 *
 * Setting off is not a separate animation from walking: `turn<Dir>_i` was baked
 * onto walk frame `WALK_START_FRAME + i`, so the stand, the turn and the walk
 * are one continuous gait sequence driven by one distance-locked quantity. The
 * checks below therefore map both frame sets onto a single gait index and assert
 * continuity across the whole of it, rather than checking the walk and the turn
 * separately and leaving the seam between them untested. The seam was where the
 * frames were going missing.
 *
 * Fails on a missing atlas, any console error, any tick that advances the gait
 * by more than one frame anywhere from standing still to steady walking, any of
 * the 24 frames of either direction never being shown, a settle that skips or
 * reverses, a character that does not end up square to the camera, or a
 * character that is ever drawn at less than full opacity.
 */
import { chromium } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';

const URL = process.env.GAME_URL ?? 'http://127.0.0.1:5173/';
const OUT = '.build/verify';
const WALK_FRAMES = 24;
const STAND_FRAMES = 12;
const TURN_FRAMES = 8;
const STOP_FRAMES = 8;
const STOP_VARIANTS = ['A', 'B'];
const STOP_TARGETS = [7, 19];
const WALK_START_FRAME = 7;
const ATLAS_FRAMES =
  2 * WALK_FRAMES + STAND_FRAMES + 2 * TURN_FRAMES + 2 * STOP_VARIANTS.length * STOP_FRAMES;

const fail = (msg) => {
  console.error(`FAIL  ${msg}`);
  process.exitCode = 1;
};

/** Sample the player's frame on every rAF for `ms`. */
const sample = (page, ms) =>
  page.evaluate(
    async ([holdMs]) => {
      const scene = window.__game.scene.getScene('Game');
      const out = [];
      const started = performance.now();
      return await new Promise((resolve) => {
        const tick = () => {
          const p = scene.player;
          out.push({
            t: performance.now() - started,
            x: p.x,
            frame: p.frame.name,
            alpha: p.alpha,
            turn: p.turn,
            phase: p.walkPhase,
            v: p.body.velocity.x
          });
          if (performance.now() - started < holdMs) requestAnimationFrame(tick);
          else resolve(out);
        };
        requestAnimationFrame(tick);
      });
    },
    [ms]
  );

/**
 * Position in the gait cycle, whichever frame set is on screen.
 *
 * `turn<Dir>_i` is walk frame `WALK_START_FRAME + i` with the front-facing stand
 * morphed over it, so it occupies that slot in the cycle. Collapsing both sets
 * onto one index is what lets the seam between them be checked at all.
 */
const gaitIndex = (frame, dir) => {
  if (frame.startsWith(`walk${dir}_`)) {
    return Number(frame.slice(`walk${dir}_`.length));
  }
  if (frame.startsWith(`turn${dir}_`)) {
    return (WALK_START_FRAME + Number(frame.slice(`turn${dir}_`.length))) % WALK_FRAMES;
  }
  return null;
};

const analyse = (label, samples, dir) => {
  // Every sample from the moment he starts moving is part of the gait -- the
  // turn included. That is the point of the design, and the point of this check.
  const moving = samples.filter(
    (s) => gaitIndex(s.frame, dir) !== null && Math.abs(s.v) > 6
  );
  if (moving.length < 60) {
    fail(`${label}: only ${moving.length} moving samples captured`);
    return;
  }

  const idx = moving.map((s) => gaitIndex(s.frame, dir));
  const walkOnly = moving.filter((s) => s.frame.startsWith(`walk${dir}_`));
  const seen = new Set(walkOnly.map((s) => Number(s.frame.slice(`walk${dir}_`.length))));

  let maxStep = 0;
  let jumps = 0;
  for (let i = 1; i < idx.length; i += 1) {
    const step = (idx[i] - idx[i - 1] + WALK_FRAMES) % WALK_FRAMES;
    maxStep = Math.max(maxStep, step);
    if (step > 1) jumps += 1;
  }

  // The last turn frame was baked pixel-identical to walk frame
  // WALK_START_FRAME + TURN_FRAMES - 1, so crossing from one frame set to the
  // other is an ordinary one-frame step of the gait and nothing else.
  const handoff = moving.findIndex(
    (s, i) => i > 0 && s.frame.startsWith(`walk${dir}_`) && moving[i - 1].frame.startsWith(`turn${dir}_`)
  );
  const handoffStep =
    handoff > 0 ? (idx[handoff] - idx[handoff - 1] + WALK_FRAMES) % WALK_FRAMES : null;

  const turnIdx = moving
    .filter((s) => s.frame.startsWith(`turn${dir}_`))
    .map((s) => Number(s.frame.slice(`turn${dir}_`.length)));

  const worstAlpha = moving.reduce((worst, s) => Math.min(worst, s.alpha), 1);

  const travelled = moving[moving.length - 1].x - moving[0].x;
  const seconds = moving[moving.length - 1].t / 1000;
  const fps = moving.length / seconds;

  console.log(
    `${label}  samples=${moving.length} ${seconds.toFixed(2)}s (${fps.toFixed(1)} fps)  ` +
      `walkFrames=${seen.size}/${WALK_FRAMES} turnFrames=${new Set(turnIdx).size}/${TURN_FRAMES} ` +
      `maxStepPerTick=${maxStep} skips=${jumps} handoffStep=${handoffStep} ` +
      `minAlpha=${worstAlpha.toFixed(3)} travel=${travelled.toFixed(1)}px`
  );

  if (seen.size !== WALK_FRAMES) {
    fail(`${label}: only ${seen.size}/${WALK_FRAMES} walk frames were ever displayed`);
  }
  if (new Set(turnIdx).size !== TURN_FRAMES) {
    fail(
      `${label}: only ${new Set(turnIdx).size}/${TURN_FRAMES} turn frames were ever displayed ` +
        `(setting off skipped frames)`
    );
  }
  if (maxStep > 1) {
    fail(`${label}: gait advanced ${maxStep} frames in one tick (visible jerk)`);
  }
  if (handoffStep !== null && handoffStep > 1) {
    fail(`${label}: turn handed off to the walk ${handoffStep} frames out of step`);
  }
  if (worstAlpha < 1) {
    fail(`${label}: character was drawn at alpha ${worstAlpha} -- it must stay opaque`);
  }
  return moving;
};

/**
 * Settling is the one timed transition, because a stationary character has no
 * distance left to lock to. It must still step by at most one frame per tick,
 * must run monotonically down to the stand, and must finish square to the
 * camera.
 */
const analyseStop = (label, samples, dir) => {
  const first = samples.findIndex((s) => /^stop/.test(s.frame));
  if (first < 0) {
    fail(`${label}: never entered the settle`);
    return;
  }
  const variant = samples[first].frame.slice(`stop${dir}`.length, `stop${dir}`.length + 1);
  if (!STOP_VARIANTS.includes(variant)) {
    fail(`${label}: settled through unknown frame set ${samples[first].frame}`);
    return;
  }
  const prefix = `stop${dir}${variant}_`;
  const stopping = samples.filter((s) => s.frame.startsWith(prefix));
  const idx = stopping.map((s) => Number(s.frame.slice(prefix.length)));

  // Before the settle he brings his trailing foot in, still on walk frames. That
  // run has to be gap-free too -- it is part of the same transition -- and it has to
  // finish on the very walk frame the settle set was baked from, because that
  // identity is what makes the handover invisible.
  const walkPrefix = `walk${dir}_`;
  const closing = samples.slice(0, first).filter((s) => s.frame.startsWith(walkPrefix));
  const closeIdx = closing.map((s) => Number(s.frame.slice(walkPrefix.length)));
  const apart = (a, b) =>
    Math.min((a - b + WALK_FRAMES) % WALK_FRAMES, (b - a + WALK_FRAMES) % WALK_FRAMES);
  let closeStep = 0;
  for (let i = 1; i < closeIdx.length; i += 1) {
    closeStep = Math.max(closeStep, apart(closeIdx[i], closeIdx[i - 1]));
  }
  const target = STOP_TARGETS[STOP_VARIANTS.indexOf(variant)];
  const handoff = closeIdx.length ? apart(target, closeIdx[closeIdx.length - 1]) : 0;

  let maxStep = 0;
  let wrongWay = 0;
  for (let i = 1; i < idx.length; i += 1) {
    const delta = idx[i] - idx[i - 1];
    maxStep = Math.max(maxStep, Math.abs(delta));
    if (delta > 0) wrongWay += 1;
  }

  const worstAlpha = stopping.reduce((worst, s) => Math.min(worst, s.alpha), 1);
  const endpoint = samples[samples.length - 1];

  console.log(
    `${label}  close=${variant}->${target} ticks=${closing.length} step=${closeStep} ` +
      `handoff=${handoff} | settle ticks=${stopping.length} ` +
      `frames=${new Set(idx).size}/${STOP_FRAMES} from=${idx[0]} maxStep=${maxStep} ` +
      `wrongWay=${wrongWay} minAlpha=${worstAlpha.toFixed(3)} end=${endpoint.frame} ` +
      `turn=${endpoint.turn.toFixed(2)}`
  );

  if (closeStep > 1) {
    fail(`${label}: bringing the trailing foot in skipped ${closeStep} frames in one tick`);
  }
  if (handoff > 1) {
    fail(
      `${label}: closing step ended on walk frame ${closeIdx[closeIdx.length - 1]}, ` +
        `${handoff} frames from the ${target} the settle set was baked from`
    );
  }
  if (new Set(idx).size !== STOP_FRAMES) {
    fail(`${label}: only ${new Set(idx).size}/${STOP_FRAMES} settle frames were displayed`);
  }
  if (idx[0] !== STOP_FRAMES - 1) {
    fail(`${label}: settle began at frame ${idx[0]}, not ${STOP_FRAMES - 1}`);
  }
  if (maxStep > 1) {
    fail(`${label}: settle jumped ${maxStep} frames in one tick (missing frames)`);
  }
  if (wrongWay) fail(`${label}: settle ran backwards ${wrongWay} time(s)`);
  if (worstAlpha < 1) {
    fail(`${label}: character was drawn at alpha ${worstAlpha} -- it must stay opaque`);
  }
  if (!endpoint.frame.startsWith('idleFront_')) {
    fail(`${label}: ended on ${endpoint.frame}, not the stand`);
  }
  if (endpoint.turn > 0) fail(`${label}: never settled square to the camera`);
};

const run = async () => {
  mkdirSync(OUT, { recursive: true });

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });

  const errors = [];
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text()));
  page.on('pageerror', (e) => errors.push(String(e)));

  await page.goto(URL, { waitUntil: 'networkidle' });

  await page.waitForFunction(
    () => window.__game?.scene?.getScene('Game')?.player !== undefined,
    null,
    { timeout: 30000 }
  );

  const atlas = await page.evaluate(() => {
    const g = window.__game;
    const tex = g.textures.get('hero');
    const names = tex.getFrameNames();
    const count = (p) => names.filter((n) => n.startsWith(p)).length;
    const p = g.scene.getScene('Game').player;
    return {
      exists: g.textures.exists('hero'),
      frames: names.length,
      right: count('walkRight_'),
      left: count('walkLeft_'),
      stand: count('idleFront_'),
      turnRight: count('turnRight_'),
      turnLeft: count('turnLeft_'),
      stopRight: count('stopRight'),
      stopLeft: count('stopLeft'),
      current: p.frame.name,
      turn: p.turn,
      source: [tex.source[0].width, tex.source[0].height]
    };
  });

  console.log(
    `atlas   loaded=${atlas.exists} frames=${atlas.frames} ` +
      `(walkRight=${atlas.right} walkLeft=${atlas.left} stand=${atlas.stand} ` +
      `turnRight=${atlas.turnRight} turnLeft=${atlas.turnLeft} ` +
      `stopRight=${atlas.stopRight} stopLeft=${atlas.stopLeft}) sheet=${atlas.source.join('x')}`
  );
  if (!atlas.exists || atlas.frames !== ATLAS_FRAMES) {
    fail(`expected ${ATLAS_FRAMES} atlas frames, got ${atlas.frames}`);
  }
  if (atlas.right !== WALK_FRAMES || atlas.left !== WALK_FRAMES) {
    fail(`expected ${WALK_FRAMES} walk frames per direction`);
  }
  if (atlas.stand !== STAND_FRAMES) {
    fail(`expected ${STAND_FRAMES} front-facing stand frames, got ${atlas.stand}`);
  }
  if (atlas.turnRight !== TURN_FRAMES || atlas.turnLeft !== TURN_FRAMES) {
    fail(`expected ${TURN_FRAMES} turn frames per direction`);
  }
  if (atlas.stopRight !== STOP_VARIANTS.length * STOP_FRAMES ||
      atlas.stopLeft !== STOP_VARIANTS.length * STOP_FRAMES) {
    fail(`expected ${STOP_VARIANTS.length * STOP_FRAMES} settle frames per direction`);
  }
  if (!atlas.current.startsWith('idleFront_') || atlas.turn !== 0) {
    fail(`character starts on ${atlas.current} (turn=${atlas.turn}), not facing the camera`);
  }

  await page.screenshot({ path: `${OUT}/01-idle.png` });

  await page.keyboard.down('ArrowRight');
  const right = await sample(page, 4000);
  await page.screenshot({ path: `${OUT}/02-walking-right.png` });
  for (let i = 0; i < 6; i += 1) {
    await page.waitForTimeout(110);
    await page.screenshot({ path: `${OUT}/strip-${String(i).padStart(2, '0')}.png` });
  }
  await page.keyboard.up('ArrowRight');

  const settleRight = await sample(page, 900);
  await page.screenshot({ path: `${OUT}/03-back-to-stand.png` });

  await page.keyboard.down('ArrowLeft');
  const left = await sample(page, 4000);
  await page.screenshot({ path: `${OUT}/04-walking-left.png` });
  await page.keyboard.up('ArrowLeft');

  const settleLeft = await sample(page, 900);
  await page.screenshot({ path: `${OUT}/05-stand-front.png` });

  await browser.close();

  // ---- analysis -------------------------------------------------------------
  const rightWalk = analyse('right ', right, 'Right');
  const leftWalk = analyse('left  ', left, 'Left');
  analyseStop('stop R', settleRight, 'Right');
  analyseStop('stop L', settleLeft, 'Left');

  console.log(`errors  ${errors.length}`);
  if (errors.length) {
    errors.slice(0, 10).forEach((e) => console.error(`  console: ${e}`));
    fail(`${errors.length} console error(s)`);
  }

  writeFileSync(
    `${OUT}/samples.json`,
    JSON.stringify({ right: rightWalk ?? [], left: leftWalk ?? [] }, null, 1)
  );

  if (!process.exitCode) {
    console.log(
      '\nPASS  standing, setting off and walking are one gap-free distance-locked ' +
        'sequence in both directions, and the character settles back to face the camera.'
    );
  }
};

run().catch((e) => {
  console.error(e);
  process.exitCode = 1;
});
