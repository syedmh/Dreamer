import { VIEW_WIDTH, VIEW_HEIGHT, GROUND_THICKNESS } from './config.js';

/**
 * The backdrop is generated at runtime instead of shipped as art, so the scene
 * stays lightweight and every layer is guaranteed to tile seamlessly: each hill
 * profile is a sum of sines whose periods divide the strip width exactly.
 */

const HILL_WIDTH = 1024;

function mulberry32(seed) {
  let a = seed >>> 0;
  return function random() {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function canvas(scene, key, width, height) {
  if (scene.textures.exists(key)) {
    scene.textures.remove(key);
  }
  const texture = scene.textures.createCanvas(key, width, height);
  return { texture, ctx: texture.getContext() };
}

function hillProfile(rng, harmonics) {
  const waves = [];
  for (let i = 0; i < harmonics; i += 1) {
    waves.push({
      freq: i + 1,
      amp: 1 / (i + 1.35),
      phase: rng() * Math.PI * 2
    });
  }
  const peak = waves.reduce((sum, w) => sum + w.amp, 0);
  return (x) => {
    let value = 0;
    for (const w of waves) {
      value += w.amp * Math.sin((2 * Math.PI * w.freq * x) / HILL_WIDTH + w.phase);
    }
    return value / peak;
  };
}

function drawHillStrip(scene, key, height, crest, amplitude, topColor, bottomColor, seed, harmonics, trees = 0, treeColor = '#1b3a26') {
  const { texture, ctx } = canvas(scene, key, HILL_WIDTH, height);

  const gradient = ctx.createLinearGradient(0, 0, 0, height);
  gradient.addColorStop(0, topColor);
  gradient.addColorStop(1, bottomColor);

  const rng = mulberry32(seed);
  const profile = hillProfile(rng, harmonics);
  const surface = (x) => crest + profile(x) * amplitude;

  ctx.beginPath();
  ctx.moveTo(0, height);
  for (let x = 0; x <= HILL_WIDTH; x += 1) {
    ctx.lineTo(x, surface(x));
  }
  ctx.lineTo(HILL_WIDTH, height);
  ctx.closePath();

  ctx.fillStyle = gradient;
  ctx.fill();

  ctx.fillStyle = treeColor;
  for (let i = 0; i < trees; i += 1) {
    const x = rng() * HILL_WIDTH;
    const h = 26 + rng() * 30;
    const w = h * (0.34 + rng() * 0.14);
    const baseY = surface(x) + 5;

    // Wrapped copy keeps trees that straddle the seam intact when tiled.
    for (const wrap of [0, x > HILL_WIDTH / 2 ? -HILL_WIDTH : HILL_WIDTH]) {
      const tx = x + wrap;
      ctx.beginPath();
      ctx.moveTo(tx, baseY - h);
      ctx.lineTo(tx + w / 2, baseY);
      ctx.lineTo(tx - w / 2, baseY);
      ctx.closePath();
      ctx.fill();
      ctx.fillRect(tx - 1.5, baseY - 2, 3, 9);
    }
  }

  texture.refresh();
}

function drawSky(scene) {
  const { texture, ctx } = canvas(scene, 'sky', VIEW_WIDTH, VIEW_HEIGHT);

  const gradient = ctx.createLinearGradient(0, 0, 0, VIEW_HEIGHT);
  gradient.addColorStop(0.0, '#1d4f8f');
  gradient.addColorStop(0.34, '#4f8ecb');
  gradient.addColorStop(0.62, '#8dc0e4');
  gradient.addColorStop(1.0, '#dceaf2');
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, VIEW_WIDTH, VIEW_HEIGHT);

  const sunX = VIEW_WIDTH * 0.78;
  const sunY = VIEW_HEIGHT * 0.2;
  const glow = ctx.createRadialGradient(sunX, sunY, 0, sunX, sunY, 260);
  glow.addColorStop(0, 'rgba(255, 246, 214, 0.95)');
  glow.addColorStop(0.18, 'rgba(255, 238, 189, 0.55)');
  glow.addColorStop(1, 'rgba(255, 236, 186, 0)');
  ctx.fillStyle = glow;
  ctx.fillRect(sunX - 280, sunY - 280, 560, 560);

  texture.refresh();
}

function drawClouds(scene) {
  const width = 1536;
  const height = 300;
  const { texture, ctx } = canvas(scene, 'clouds', width, height);
  const rng = mulberry32(9021);

  ctx.filter = 'blur(10px)';

  for (let i = 0; i < 9; i += 1) {
    const cx = rng() * width;
    const cy = 60 + rng() * (height - 150);
    const scale = 0.7 + rng() * 1.0;
    const puffs = 6 + Math.floor(rng() * 4);
    const alpha = 0.30 + rng() * 0.24;

    for (let p = 0; p < puffs; p += 1) {
      const t = p / (puffs - 1) - 0.5;
      const ox = t * 150 * scale + (rng() - 0.5) * 22;
      const oy = -Math.cos(t * Math.PI) * 20 * scale + (rng() - 0.5) * 12;
      const r = (26 + (1 - Math.abs(t) * 1.4) * 30 + rng() * 10) * scale;
      if (r <= 2) continue;

      // Each puff is drawn again one strip-width away so clouds crossing the
      // seam reappear on the opposite edge and the strip tiles cleanly.
      for (const wrap of [0, cx + ox > width / 2 ? -width : width]) {
        const gx = cx + ox + wrap;
        const grad = ctx.createRadialGradient(gx, cy + oy - r * 0.2, 0, gx, cy + oy, r);
        grad.addColorStop(0, `rgba(255,255,255,${alpha})`);
        grad.addColorStop(0.6, `rgba(248,252,255,${alpha * 0.7})`);
        grad.addColorStop(1, 'rgba(226,238,248,0)');
        ctx.fillStyle = grad;
        ctx.beginPath();
        ctx.arc(gx, cy + oy, r, 0, Math.PI * 2);
        ctx.fill();
      }
    }
  }

  ctx.filter = 'none';
  texture.refresh();
}

function drawGround(scene) {
  const width = 256;
  const height = GROUND_THICKNESS + 160;
  const { texture, ctx } = canvas(scene, 'ground', width, height);
  const rng = mulberry32(4477);

  const dirt = ctx.createLinearGradient(0, 0, 0, height);
  dirt.addColorStop(0, '#6d4a2f');
  dirt.addColorStop(0.35, '#5a3c26');
  dirt.addColorStop(1, '#3a271a');
  ctx.fillStyle = dirt;
  ctx.fillRect(0, 0, width, height);

  for (let i = 0; i < 130; i += 1) {
    const x = rng() * width;
    const y = 26 + rng() * (height - 30);
    const r = 1.2 + rng() * 3.4;
    ctx.fillStyle = rng() > 0.5 ? 'rgba(0, 0, 0, 0.18)' : 'rgba(255, 220, 180, 0.10)';
    ctx.beginPath();
    ctx.ellipse(x, y, r * 1.6, r, rng() * Math.PI, 0, Math.PI * 2);
    ctx.fill();
  }

  const grass = ctx.createLinearGradient(0, 0, 0, 26);
  grass.addColorStop(0, '#7cc453');
  grass.addColorStop(0.65, '#4f9a37');
  grass.addColorStop(1, '#356f28');
  ctx.fillStyle = grass;
  ctx.fillRect(0, 0, width, 22);

  ctx.strokeStyle = 'rgba(150, 214, 110, 0.85)';
  ctx.lineWidth = 2;
  for (let x = 2; x < width; x += 6) {
    const blade = 5 + rng() * 7;
    ctx.beginPath();
    ctx.moveTo(x, 22);
    ctx.lineTo(x + (rng() - 0.5) * 4, 22 - blade);
    ctx.stroke();
  }

  texture.refresh();
}

function drawShadow(scene) {
  const width = 192;
  const height = 96;
  const { texture, ctx } = canvas(scene, 'shadow', width, height);

  ctx.save();
  ctx.translate(width / 2, height / 2);
  ctx.scale(1, 0.42);
  const gradient = ctx.createRadialGradient(0, 0, 0, 0, 0, width / 2);
  gradient.addColorStop(0, 'rgba(16, 30, 12, 0.62)');
  gradient.addColorStop(0.45, 'rgba(16, 30, 12, 0.34)');
  gradient.addColorStop(0.78, 'rgba(16, 30, 12, 0.10)');
  gradient.addColorStop(1, 'rgba(16, 30, 12, 0)');
  ctx.fillStyle = gradient;
  ctx.beginPath();
  ctx.arc(0, 0, width / 2, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();

  texture.refresh();
}

/**
 * The scenery layers, which only exist when the game is drawn against a
 * landscape rather than plain black.
 */
export function generateBackdropTextures(scene) {
  drawSky(scene);
  drawClouds(scene);
  drawHillStrip(scene, 'hillsFar', 320, 150, 58, '#7fa8c8', '#a9c6dc', 1337, 5);
  drawHillStrip(scene, 'hillsMid', 360, 168, 74, '#4f7f66', '#3d6753', 2461, 6, 22, '#33604b');
  drawHillStrip(scene, 'hillsNear', 300, 150, 62, '#2f5c41', '#22452f', 7717, 7, 30, '#1b3a26');
  drawGround(scene);
}

/**
 * The character's contact shadow. This belongs to him, not to the scenery, so it
 * is generated whatever the stage looks like -- without it the sprite falls back
 * to Phaser's missing-texture placeholder and paints a green square at his feet.
 */
export function generateCharacterTextures(scene) {
  drawShadow(scene);
}
