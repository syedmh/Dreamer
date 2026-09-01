/**
 * Grab consecutive rendered frames straight off the canvas, so a transition can be
 * inspected tick by tick instead of at Playwright screenshot latency (~60-165 ms,
 * far too coarse to see a 0.4 s transition).
 *
 * Writes a contact strip per transition: every render tick, in order, with the
 * frame name it was showing. This is the check that the numeric gates cannot make
 * -- an earlier cross-fade design passed every one of them while drawing the
 * character half-transparent.
 */
import { chromium } from 'playwright';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';

const OUT = '.build/turnshots';
// Wipe first: tiles are named after the frame they show, so leftovers from an
// earlier run mix into the strip and quietly misrepresent what happened.
rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

// The tile is derived from the character at runtime rather than hardcoded, so a
// change to CHARACTER_DISPLAY_HEIGHT cannot silently start cropping his head off
// and hide exactly the mismatch this strip exists to catch.
let W = 0;
let H = 0;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' });
await page.waitForFunction(() => window.__game?.scene?.getScene('Game')?.player);

[W, H] = await page.evaluate(() => {
  const p = window.__game.scene.getScene('Game').player;
  return [Math.round(p.displayWidth * 1.5), Math.round(p.displayHeight * 1.16)];
});

const grab = (ticks) =>
  page.evaluate(
    async ([n, w, h]) => {
      const game = window.__game;
      const scene = game.scene.getScene('Game');
      const p = scene.player;
      const out = [];
      const src = game.canvas;
      const c = document.createElement('canvas');
      c.width = w;
      c.height = h;
      const ctx = c.getContext('2d');
      for (let i = 0; i < n; i += 1) {
        await new Promise((r) => requestAnimationFrame(r));
        const cam = scene.cameras.main;
        const ratio = src.width / cam.width;
        const sx = (p.x - cam.scrollX) * ratio - w / 2;
        const sy = (p.y - cam.scrollY) * ratio - h * 0.93;
        ctx.clearRect(0, 0, w, h);
        ctx.drawImage(src, sx, sy, w, h, 0, 0, w, h);
        out.push({ turn: p.turn, frame: p.frame.name, data: c.toDataURL('image/png') });
      }
      return out;
    },
    [ticks, W, H]
  );

await page.keyboard.down('ArrowRight');
const start = await grab(22);
await page.waitForTimeout(700);
await page.keyboard.up('ArrowRight');
const stop = await grab(26);
await browser.close();

const write = (name, frames) => {
  frames.forEach((f, i) => {
    writeFileSync(
      `${OUT}/${name}-${String(i).padStart(2, '0')}-${f.frame}.png`,
      Buffer.from(f.data.split(',')[1], 'base64')
    );
  });
  console.log(`${name}: ${frames.map((f) => f.frame).join(' ')}`);
};
write('start', start);
write('stop', stop);
console.log(`tile size ${W}x${H}`);
