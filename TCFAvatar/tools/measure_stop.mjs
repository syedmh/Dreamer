/**
 * How far the planted foot slides while he comes to a stop.
 *
 * The walk is distance-locked, so a foot stays planted exactly while the gait
 * phase advances in step with the body: `dx == dPhase * strideWorldPx`. The
 * closing step breaks that lock deliberately -- it drives the phase to a chosen
 * pose on its own clock while the body coasts to a halt -- so the residual
 *
 *     |dx - dPhase * strideWorldPx|
 *
 * is the skate, in world pixels, and it is what the eye reads as the foot
 * sliding on the floor. Reported against his on-screen height so the number
 * means something.
 *
 * Run at several release phases, because the cost depends entirely on where in
 * the cycle the key happens to come up.
 */
import { chromium } from 'playwright';

const URL = process.env.GAME_URL ?? 'http://127.0.0.1:5173/';

const run = async (page, holdMs) => {
  // Settle to a clean stand first.
  await page.waitForTimeout(900);

  await page.keyboard.down('ArrowRight');
  await page.waitForTimeout(holdMs);
  const releasePhase = await page.evaluate(
    () => window.__game.scene.getScene('Game').player.walkPhase
  );
  const sampling = page.evaluate(async () => {
    const p = window.__game.scene.getScene('Game').player;
    const out = [];
    await new Promise((resolve) => {
      const t0 = performance.now();
      const tick = () => {
        out.push({ t: performance.now() - t0, x: p.x, phase: p.walkPhase, frame: p.frame.name });
        if (performance.now() - t0 < 900) requestAnimationFrame(tick);
        else resolve();
      };
      requestAnimationFrame(tick);
    });
    return { stride: p.strideWorldPx, height: p.displayHeight, out };
  });
  await page.keyboard.up('ArrowRight');
  return { releasePhase, ...(await sampling) };
};

const wrap = (d) => ((d + 0.5) % 1) - 0.5;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
await page.goto(URL, { waitUntil: 'networkidle' });
await page.waitForFunction(() => window.__game?.scene?.getScene('Game')?.player, null, {
  timeout: 20000
});

let worst = 0;
for (const hold of [700, 760, 820, 880, 940, 1000]) {
  const { stride, releasePhase, out, height } = await run(page, hold);

  // Only the closing step: from release until the settle art takes over.
  const close = [];
  for (const s of out) {
    close.push(s);
    if (!s.frame.startsWith('walk')) break;
  }

  let skate = 0;
  let rewind = 0;
  for (let i = 1; i < close.length; i += 1) {
    const dx = close[i].x - close[i - 1].x;
    const dPhase = wrap(close[i].phase - close[i - 1].phase);
    skate += Math.abs(dx - dPhase * stride);
    if (dPhase < 0) rewind += -dPhase;
  }

  worst = Math.max(worst, skate);
  const frames = close.map((s) => s.frame.replace(/^(walk|stop)Right/, '')).join(' ');
  console.log(
    `release phase ${(releasePhase * 6).toFixed(2)}  ticks=${close.length} ` +
      `rewind=${(rewind * 6).toFixed(2)}f  skate=${skate.toFixed(1)}px ` +
      `(${((skate / height) * 100).toFixed(1)}% of height)  ${frames}`
  );
}

console.log(`\nworst skate ${worst.toFixed(1)}px`);
await browser.close();
