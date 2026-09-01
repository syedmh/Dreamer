/**
 * Runtime smoke test: is the game actually playable?
 *
 * The other gates check the baked atlas and the animation maths.  This one only
 * asks the questions a person pressing the arrow keys would ask -- does it load
 * without errors, does he walk both ways, does he stop facing the camera, and
 * does it hold frame rate -- so a broken build cannot look green.
 */
import { chromium } from 'playwright';

const URL = process.env.GAME_URL ?? 'http://127.0.0.1:5173/';
const problems = [];
const note = (m) => problems.push(m);

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });

const consoleErrors = [];
page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(m.text()); });
page.on('pageerror', (e) => consoleErrors.push(String(e)));
page.on('requestfailed', (r) => consoleErrors.push(`${r.url()} ${r.failure()?.errorText}`));

await page.goto(URL, { waitUntil: 'networkidle' });
await page.waitForFunction(
  () => window.__game?.scene?.getScene('Game')?.player !== undefined,
  null, { timeout: 20000 });

const state = () => page.evaluate(() => {
  const s = window.__game.scene.getScene('Game');
  const p = s.player;
  return {
    x: p.x,
    frame: p.frame.name,
    // displayHeight is the padded frame box; the character inside it is what the
    // 200" screen actually shows, so assert that instead.
    charPx: Math.round(p.displayScale * p.rig.characterHeightPx),
    fps: Math.round(window.__game.loop.actualFps),
  };
});

const hold = async (key, ms) => {
  await page.keyboard.down(key);
  await page.waitForTimeout(ms);
  await page.keyboard.up(key);
};

const before = await state();
console.log('loaded      ', JSON.stringify(before));

await hold('ArrowRight', 1200);
await page.waitForTimeout(900);
const right = await state();
console.log('after right ', JSON.stringify(right));
if (right.x <= before.x + 20) note(`right arrow did not move him (${before.x} -> ${right.x})`);
if (!/^idleFront/.test(right.frame)) note(`did not settle facing camera, ended on ${right.frame}`);

await hold('ArrowLeft', 1600);
await page.waitForTimeout(900);
const left = await state();
console.log('after left  ', JSON.stringify(left));
if (left.x >= right.x - 20) note(`left arrow did not move him (${right.x} -> ${left.x})`);
if (!/^idleFront/.test(left.frame)) note(`did not settle facing camera, ended on ${left.frame}`);

// Frame rate while actually walking, which is when the atlas is being churned.
await page.keyboard.down('ArrowRight');
await page.waitForTimeout(600);
const samples = [];
for (let i = 0; i < 20; i++) { await page.waitForTimeout(100); samples.push((await state()).fps); }
await page.keyboard.up('ArrowRight');
const minFps = Math.min(...samples);
console.log('fps walking ', `min=${minFps} median=${samples.sort((a, b) => a - b)[10]}`);
if (minFps < 50) note(`frame rate dipped to ${minFps}`);

if (left.charPx !== before.charPx) note(`character height changed: ${before.charPx} -> ${left.charPx}`);
if (before.charPx !== 406) note(`character is ${before.charPx}px, expected 406`);
if (consoleErrors.length) note(`${consoleErrors.length} console/network error(s): ${consoleErrors.slice(0, 3).join(' | ')}`);

console.log('errors      ', problems.length);
for (const p of problems) console.log('  -', p);
console.log(problems.length
  ? '\nFAIL  the game does not play cleanly.'
  : '\nPASS  loads clean, walks both ways, settles facing the camera, holds frame rate.');

await browser.close();
process.exit(problems.length ? 1 : 0);
