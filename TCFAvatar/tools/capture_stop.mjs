/**
 * Look at the closing step -- the handful of ticks between releasing the key and
 * the settle taking over.
 *
 * The gait is distance-locked while walking, so the planted foot cannot slide.
 * The close deliberately breaks that lock: it drives the phase to the cycle's
 * single passing pose on its own clock while the body coasts to a halt. With
 * only one passing pose in a one-step cycle, roughly half of all release phases
 * reach it by running the walk *backwards*, and `measure_stop.mjs` says that
 * costs no more in pixels than going forwards. Whether it *reads* backwards is
 * not something a pixel count can answer, so this renders it.
 *
 * Every tick is captured, not just the distinct frames, because a pose held for
 * five ticks and a pose held for one look nothing alike in motion. Cropped to
 * the legs, where the sliding would show, at dpr 3.
 */
import { chromium } from 'playwright';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';

const OUT = '.build/stopshots';
const URL = process.env.GAME_URL ?? 'http://127.0.0.1:5173/';

rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1280, height: 720 },
  deviceScaleFactor: 3
});
await page.goto(URL, { waitUntil: 'networkidle' });
await page.waitForFunction(() => window.__game?.scene?.getScene('Game')?.player);

/** Hold `key` until the gait is within a tick of `wantPhase`, then release and film. */
const capture = async (key, wantPhase) => {
  await page.keyboard.down(key);
  await page.waitForFunction(
    ([target]) => {
      const p = window.__game.scene.getScene('Game').player;
      const d = ((p.walkPhase * 6 - target) % 6 + 6) % 6;
      return d < 0.35 && p.frame.name.startsWith('walk');
    },
    [wantPhase],
    { timeout: 15000 }
  );
  const shot = page.evaluate(async () => {
    const game = window.__game;
    const scene = game.scene.getScene('Game');
    const p = scene.player;
    const src = game.canvas;
    const view = scene.cameras.main.worldView;
    const perWorld = src.width / view.width;

    // Legs only: from mid-thigh to just below the shoes.
    const w = Math.round(p.displayWidth * 1.9 * perWorld);
    const h = Math.round(p.displayHeight * 0.46 * perWorld);

    const tiles = [];
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    const ctx = c.getContext('2d');

    for (let i = 0; i < 26; i += 1) {
      await new Promise((r) => requestAnimationFrame(r));
      const v = scene.cameras.main.worldView;
      ctx.clearRect(0, 0, w, h);
      ctx.drawImage(
        src,
        ((p.x - v.x) / v.width) * src.width - w / 2,
        ((p.y - v.y) / v.height) * src.height - h * 0.97,
        w, h, 0, 0, w, h
      );
      tiles.push({ frame: p.frame.name, x: p.x, data: c.toDataURL('image/png') });
      if (!p.frame.name.startsWith('walk') && i > 2) break;
    }
    return { tiles, w, h };
  });
  await page.keyboard.up(key);
  return await shot;
};

/** Lay the ticks out left to right so the eye can read the sequence as motion. */
const sheet = async (tiles, w, h, path) => {
  const data = await page.evaluate(
    ([urls, tw, th]) => {
      const c = document.createElement('canvas');
      c.width = tw * urls.length;
      c.height = th;
      const ctx = c.getContext('2d');
      ctx.fillStyle = '#101010';
      ctx.fillRect(0, 0, c.width, c.height);
      return Promise.all(
        urls.map(
          (u, i) =>
            new Promise((res) => {
              const img = new Image();
              img.onload = () => {
                ctx.drawImage(img, i * tw, 0);
                res();
              };
              img.src = u;
            })
        )
      ).then(() => c.toDataURL('image/png'));
    },
    [tiles.map((t) => t.data), w, h]
  );
  writeFileSync(path, Buffer.from(data.split(',')[1], 'base64'));
};

for (const [label, phase] of [['rewind', 5.4], ['forward', 0.4]]) {
  // Walk to the far wall first so there is stage left to run across.
  await page.keyboard.down('ArrowLeft');
  await page.waitForTimeout(2600);
  await page.keyboard.up('ArrowLeft');
  await page.waitForTimeout(700);

  const { tiles, w, h } = await capture('ArrowRight', phase);
  await sheet(tiles, w, h, `${OUT}/${label}.png`);
  const travel = tiles[tiles.length - 1].x - tiles[0].x;
  console.log(
    `${label.padEnd(8)} ${tiles.length} ticks, travel ${travel.toFixed(1)}px  ` +
      tiles.map((t) => t.frame.replace(/^(walk|stop)Right/, '')).join(' ')
  );
}

await browser.close();
console.log(`\nwrote ${OUT}/rewind.png and ${OUT}/forward.png`);
