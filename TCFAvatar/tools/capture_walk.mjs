/**
 * Grab a whole walk cycle off the canvas, one tile per distinct frame, through the
 * real render path at panel resolution.
 *
 * `capture_turn.mjs` covers the transitions, which is where the frame *sequencing*
 * can go wrong.  It cannot cover the walk body: it releases the key after ~22 ticks
 * and only ever reaches walkRight_03..06.  But the walk body is where the depiction
 * defects have been -- a character on three shoes, and the far shoe reading as the
 * near one double-exposed -- and both were invisible to every numeric gate, because
 * a gate checks which frame index is showing, never what the pixels depict.
 *
 * The walk advances about one frame per 10 render ticks, so the 6-frame cycle needs
 * roughly 60.  Starting from the middle of the stage is not enough: the walk is
 * distance-locked, so once he reaches the world bound he stops travelling and the
 * animation stops advancing with him -- a centred run reaches only part of the
 * frames.  Each direction is therefore driven from the far wall, across the whole
 * 1048px stage, which is about 2.5 cycles.
 *
 * deviceScaleFactor 3 matches the 200" target, so what is inspected here is what
 * that panel will show.
 */
import { chromium } from 'playwright';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';

const OUT = '.build/walkshots';
const WALK_FRAMES = 6;
const URL = process.env.GAME_URL ?? 'http://127.0.0.1:5173/';
const TICKS = 150;

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1280, height: 720 },
  deviceScaleFactor: 3,
});
await page.goto(URL, { waitUntil: 'networkidle' });
await page.waitForFunction(() => window.__game?.scene?.getScene('Game')?.player);

// Derived from the character, not hardcoded, so a change to CHARACTER_DISPLAY_HEIGHT
// cannot silently start cropping his feet off and hide the very thing this looks for.
// Sizes are in *world* units here and converted to canvas pixels through the camera's
// worldView at capture time: `canvas.width / camera.width` is 1 at zoom 3, so the
// ratio capture_turn.mjs uses at dpr 1 silently mis-crops once either is scaled.
const [VW, VH] = await page.evaluate(() => {
  const p = window.__game.scene.getScene('Game').player;
  return [p.displayWidth * 1.9, p.displayHeight * 1.16];
});

// The tile is built through the camera's worldView, and the one failure that would
// make this tool worse than useless is a silent mis-crop -- a clean strip of the
// wrong part of the character. Sizing off `canvas.width / camera.width` (which is 1
// at zoom 3, not 3) did exactly that, cropping a chest-high band. The character
// stands 406 of the viewport's 720 CSS px, so a correctly scaled tile is around
// two thirds of the canvas height; a badly scaled one is a fifth.
const TILE = await page.evaluate(
  ([vw, vh]) => {
    const game = window.__game;
    const view = game.scene.getScene('Game').cameras.main.worldView;
    const perWorld = game.canvas.width / view.width;
    return {
      w: Math.round(vw * perWorld),
      h: Math.round(vh * perWorld),
      fill: (vh * perWorld) / game.canvas.height,
    };
  },
  [VW, VH]
);

const run = async (key, ticks) => {
  await page.keyboard.down(key);
  const frames = await page.evaluate(
    async ([n, vw, vh]) => {
      const game = window.__game;
      const scene = game.scene.getScene('Game');
      const p = scene.player;
      const src = game.canvas;
      const view = scene.cameras.main.worldView;
      const perWorld = src.width / view.width;
      const w = Math.round(vw * perWorld);
      const h = Math.round(vh * perWorld);
      const c = document.createElement('canvas');
      c.width = w;
      c.height = h;
      const ctx = c.getContext('2d');
      const out = [];
      let last = null;
      for (let i = 0; i < n; i += 1) {
        await new Promise((r) => requestAnimationFrame(r));
        const name = p.frame.name;
        if (name === last || !name.startsWith('walk')) {
          last = name;
          continue;
        }
        last = name;
        const v = scene.cameras.main.worldView;
        ctx.clearRect(0, 0, w, h);
        ctx.drawImage(
          src,
          ((p.x - v.x) / v.width) * src.width - w / 2,
          ((p.y - v.y) / v.height) * src.height - h * 0.93,
          w, h, 0, 0, w, h
        );
        out.push({ frame: name, data: c.toDataURL('image/png') });
      }
      return out;
    },
    [ticks, VW, VH]
  );
  await page.keyboard.up(key);
  return frames;
};

// Each direction is driven from the far wall so the whole stage is available; a run
// started mid-stage stalls against the world bound with a quarter of the cycle unseen.
const walkTo = async (key) => {
  await page.keyboard.down(key);
  await page.waitForTimeout(2600);
  await page.keyboard.up(key);
  await page.waitForTimeout(500);
};

await walkTo('ArrowLeft');
const right = await run('ArrowRight', TICKS);
await page.waitForTimeout(500);
const left = await run('ArrowLeft', TICKS);
await browser.close();

let bad = 0;
for (const [name, frames] of [['right', right], ['left', left]]) {
  frames.forEach((f, i) =>
    writeFileSync(
      `${OUT}/${name}-${String(i).padStart(2, '0')}-${f.frame}.png`,
      Buffer.from(f.data.split(',')[1], 'base64')
    )
  );
  const seen = new Set(frames.map((f) => f.frame));
  console.log(`${name.padEnd(6)} ${frames.length} tiles, ${seen.size} distinct frames`);
  if (seen.size < WALK_FRAMES) {
    console.log(`       only ${seen.size}/${WALK_FRAMES} of the cycle reached -- raise TICKS`);
    bad += 1;
  }
}
console.log(`-> ${OUT}/  (${TILE.w}x${TILE.h} canvas px at dpr 3)`);
// A mis-crop is the one failure that would make this tool worse than useless, so
// check the tile really is character-sized before anyone judges the walk from it.
if (TILE.fill < 0.45) {
  console.log(`       tile is only ${(TILE.fill * 100) | 0}% of the canvas height -- the crop is wrong`);
  bad += 1;
}
console.log(
  bad
    ? 'INCOMPLETE  the strip does not show a whole cycle of the character; do not judge the walk from it.'
    : 'PASS  a whole cycle in both directions is on disk. Now look at it.'
);
process.exit(bad ? 1 : 0);
