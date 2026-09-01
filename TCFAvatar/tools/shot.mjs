/**
 * Full-screen grabs of the running game, for looking at what the numbers cannot
 * show: how sharp and how tall the character reads, that the stage is black, and
 * that he reaches both edges of a screen that never scrolls.
 *
 * Runs twice. The second pass fakes a 4K panel via `deviceScaleFactor`, which is
 * the only way to exercise the supersampling path -- headless Chromium reports a
 * 1280x720 screen at dpr 1, so RENDER_SCALE would otherwise always be 1 and the
 * camera zoom would never be tested.
 */
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const OUT = new URL('../.build/', import.meta.url).pathname.replace(/^\//, '');
mkdirSync(OUT, { recursive: true });

const state = (page) =>
  page.evaluate(() => {
    const game = window.__game;
    const scene = game.scene.getScene('Game');
    const p = scene.player;
    return {
      renderScale: +(game.scale.gameSize.width / 1280).toFixed(3),
      canvas: `${game.canvas.width}x${game.canvas.height}`,
      zoom: scene.cameras.main.zoom,
      scrollX: Math.round(scene.cameras.main.scrollX),
      x: Math.round(p.x),
      left: Math.round(p.body.left),
      right: Math.round(p.body.right),
      charPx: Math.round(p.displayHeight * (650 / 742)),
      frame: p.frame.name
    };
  });

async function run(deviceScaleFactor, tag) {
  const browser = await chromium.launch();
  const page = await browser.newPage({
    viewport: { width: 1280, height: 720 },
    deviceScaleFactor
  });
  await page.goto('http://127.0.0.1:5173/', { waitUntil: 'networkidle' });
  await page.waitForFunction(() => window.__game?.scene?.getScene('Game')?.player);
  await page.waitForTimeout(800);

  const shot = async (name) => {
    await page.screenshot({ path: `${OUT}shot-${tag}-${name}.png` });
    console.log(`${tag} ${name}`.padEnd(20), JSON.stringify(await state(page)));
  };

  await shot('idle');
  await page.keyboard.down('ArrowRight');
  await page.waitForTimeout(1200);
  await shot('walking');
  await page.waitForTimeout(5000);
  await page.keyboard.up('ArrowRight');
  await page.waitForTimeout(1400);
  await shot('edge-right');
  await page.keyboard.down('ArrowLeft');
  await page.waitForTimeout(9000);
  await page.keyboard.up('ArrowLeft');
  await page.waitForTimeout(1400);
  await shot('edge-left');

  await browser.close();
}

await run(1, '1x');
await run(3, '3x');
