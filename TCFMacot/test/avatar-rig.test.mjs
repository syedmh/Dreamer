import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  existsSync,
  readFileSync,
  readdirSync,
  statSync,
} from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { after, before, describe, it } from 'node:test';
import { fileURLToPath } from 'node:url';
import { inflateSync } from 'node:zlib';

import { AVATAR_LAYERS } from '../src/avatar-rig.js';
import { computePoseKinematics } from '../src/render.js';
import { createStaticServer } from '../server.mjs';

const projectRoot = fileURLToPath(new URL('..', import.meta.url));
const avatarSourcePath = path.join(projectRoot, 'Avatar.jpg');
const assetsRoot = path.join(projectRoot, 'assets');
const sourceRoot = path.join(projectRoot, 'src');
const stylesPath = path.join(projectRoot, 'styles.css');
const serverPath = path.join(projectRoot, 'server.mjs');
const SOURCE_SHA256 = '04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF';
const SOURCE_WIDTH = 896;
const SOURCE_HEIGHT = 1195;
const FIVE_LAYER_CONTRACT = Object.freeze([
  Object.freeze({
    role: 'leftLeg',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 420, y: 806 }),
    zIndex: 10,
    url: './assets/avatar/leftLeg.png',
  }),
  Object.freeze({
    role: 'rightLeg',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 515, y: 812 }),
    zIndex: 20,
    url: './assets/avatar/rightLeg.png',
  }),
  Object.freeze({
    role: 'torsoHead',
    parent: null,
    pivot: Object.freeze({ x: 448, y: 785 }),
    zIndex: 30,
    url: './assets/avatar/torsoHead.png',
  }),
  Object.freeze({
    role: 'leftArm',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 337, y: 276 }),
    zIndex: 40,
    url: './assets/avatar/leftArm.png',
  }),
  Object.freeze({
    role: 'rightArm',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 560, y: 278 }),
    zIndex: 50,
    url: './assets/avatar/rightArm.png',
  }),
]);
const FIVE_LAYER_BY_ROLE = new Map(
  FIVE_LAYER_CONTRACT.map((layer) => [layer.role, layer]),
);
const FIVE_LAYER_PNG_NAMES = Object.freeze([
  'idle.png',
  ...FIVE_LAYER_CONTRACT.map(({ role }) => `${role}.png`),
].sort());
const OBSOLETE_LAYER_ROLES = Object.freeze([
  'leftShoe',
  'rightShoe',
  'leftLowerLeg',
  'rightLowerLeg',
  'leftUpperLeg',
  'rightUpperLeg',
  'torso',
  'leftUpperArm',
  'rightUpperArm',
  'leftForearm',
  'rightForearm',
  'leftHand',
  'rightHand',
  'head',
]);
const CANONICAL_ROLE_ALIASES = Object.freeze({
  torsoHead: Object.freeze(['torsoHead', 'torso', 'head']),
  leftArm: Object.freeze(['leftArm', 'leftUpperArm', 'leftForearm', 'leftHand']),
  rightArm: Object.freeze(['rightArm', 'rightUpperArm', 'rightForearm', 'rightHand']),
  leftLeg: Object.freeze(['leftLeg', 'leftUpperLeg', 'leftLowerLeg', 'leftShoe']),
  rightLeg: Object.freeze(['rightLeg', 'rightUpperLeg', 'rightLowerLeg', 'rightShoe']),
});
const MANIFEST_PATH = path.join(assetsRoot, 'avatar', 'manifest.json');
const GENERATOR_CANDIDATES = [
  path.join(projectRoot, 'scripts', 'generate-avatar-assets.mjs'),
  path.join(projectRoot, 'scripts', 'generate-avatar-rig.mjs'),
  path.join(projectRoot, 'tools', 'generate-avatar-assets.mjs'),
  path.join(projectRoot, 'tools', 'generate-avatar-rig.mjs'),
];
const PNG_SIGNATURE = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
const ALLOWED_PNG_CHUNKS = new Set(['IHDR', 'IDAT', 'IEND']);
const PAINTER_ORDER = Object.freeze(FIVE_LAYER_CONTRACT.map(({ role }) => role));
const STATIC_TUNIC_OWNERSHIP_PROBES = Object.freeze([
  Object.freeze({
    role: 'leftLeg',
    rectangle: Object.freeze({ xMin: 390, xMax: 446, yMin: 806, yMax: 828 }),
    minimumBrightPixels: 1_300,
    pose: 'leftGaitMaximum',
  }),
  Object.freeze({
    role: 'rightLeg',
    rectangle: Object.freeze({ xMin: 520, xMax: 565, yMin: 815, yMax: 845 }),
    minimumBrightPixels: 1_400,
    pose: 'rightGaitMaximum',
  }),
  Object.freeze({
    role: 'leftArm',
    rectangle: Object.freeze({ xMin: 345, xMax: 355, yMin: 640, yMax: 697 }),
    minimumBrightPixels: 600,
    pose: 'clapMaximum',
  }),
  Object.freeze({
    role: 'rightArm',
    rectangle: Object.freeze({ xMin: 566, xMax: 594, yMin: 695, yMax: 717 }),
    minimumBrightPixels: 600,
    pose: 'clapMaximum',
  }),
]);
const SHOULDER_OWNERSHIP_PROBES = Object.freeze([
  Object.freeze({
    role: 'leftArm',
    rectangle: Object.freeze({ xMin: 306, xMax: 358, yMin: 232, yMax: 315 }),
    minimumGreenPixels: 1_800,
    minimumWhitePixels: 250,
  }),
  Object.freeze({
    role: 'rightArm',
    rectangle: Object.freeze({ xMin: 545, xMax: 608, yMin: 232, yMax: 315 }),
    minimumGreenPixels: 1_800,
    minimumWhitePixels: 1_200,
  }),
]);
const PRODUCTION_POSES = Object.freeze({
  leftGaitMaximum: computePoseKinematics({
    gaitPhase: 0.25,
    speedNormalized: 1,
    velocityX: 1,
  }).cssVariables,
  rightGaitMaximum: computePoseKinematics({
    gaitPhase: 0.75,
    speedNormalized: 1,
    velocityX: 1,
  }).cssVariables,
  clapMaximum: computePoseKinematics({
    clapping: true,
    clapProgress: 0.1,
  }).cssVariables,
});
const LIMB_SUBTREES = Object.freeze([
  Object.freeze({
    name: 'leftArm',
    roles: Object.freeze(['leftArm']),
  }),
  Object.freeze({
    name: 'rightArm',
    roles: Object.freeze(['rightArm']),
  }),
  Object.freeze({
    name: 'leftLeg',
    roles: Object.freeze(['leftLeg']),
  }),
  Object.freeze({
    name: 'rightLeg',
    roles: Object.freeze(['rightLeg']),
  }),
]);
const POSED_RASTER_ALPHA_THRESHOLDS = Object.freeze([1, 32]);
const DENSE_GAIT_PHASE_SAMPLES = 240;
const DENSE_CLAP_PROGRESS_SAMPLES = 240;
const BRIGHT_MATTE_BACKGROUNDS = Object.freeze([
  Object.freeze({ name: 'white', rgb: Object.freeze([255, 255, 255]) }),
  Object.freeze({ name: 'cyan', rgb: Object.freeze([0, 255, 255]) }),
  Object.freeze({ name: 'magenta', rgb: Object.freeze([255, 0, 255]) }),
]);
const QA_1080P_SOURCE_TO_SCREEN_SCALE = (1080 * 0.67) / SOURCE_HEIGHT;
const QA_MAX_BACKGROUND_NOTCH_SCREEN_PIXELS = 96;
const QA_MAX_BACKGROUND_NOTCH_WIDTH_PX = 2;
const QA_MAX_STATIC_SHOULDER_SLIVER_SCREEN_PIXELS = 8;
const QA_PROTECTED_NOTCH_PROBES = Object.freeze([
  Object.freeze({
    name: 'right-underarm-waistcoat',
    rectangle: Object.freeze({ xMin: 505, xMax: 570, yMin: 296, yMax: 650 }),
  }),
  Object.freeze({
    name: 'right-waist-qameez',
    rectangle: Object.freeze({ xMin: 548, xMax: 610, yMin: 660, yMax: 875 }),
  }),
]);
const SOURCE_INTERIOR_GEOMETRY = Object.freeze([
  Object.freeze({
    name: 'waistcoat-core',
    rectangle: Object.freeze({ xMin: 400, xMax: 500, yMin: 350, yMax: 650 }),
  }),
]);
const QA_SEAM_POSES = Object.freeze([
  Object.freeze({
    label: 'walk-lift',
    pose: computePoseKinematics({
      gaitPhase: 0.25,
      speedNormalized: 1,
      velocityX: 1,
    }).cssVariables,
  }),
  Object.freeze({
    label: 'walk-passing',
    pose: computePoseKinematics({
      gaitPhase: 0.5,
      speedNormalized: 1,
      velocityX: 1,
    }).cssVariables,
  }),
  Object.freeze({
    label: 'clap-open',
    pose: computePoseKinematics({
      clapping: true,
      clapProgress: 0,
    }).cssVariables,
  }),
  Object.freeze({
    label: 'clap-contact',
    pose: computePoseKinematics({
      clapping: true,
      clapProgress: 0.1,
    }).cssVariables,
  }),
]);
// The unchanged assets measure 4,240-5,012 dark ring pixels, 2,906-3,420
// pixels at least four pixels deep, and components up to 1,992 pixels. These
// limits require a decontaminated edge while retaining substantial headroom for
// legitimate dark outlines and antialiasing. A clean 896x1195 silhouette also
// needs at least roughly one partial-alpha pixel around its multi-thousand-pixel
// perimeter, rather than the current 2,625 partial-alpha pixels.
const DARK_MATTE_RING_MAX_PIXELS = 1_500;
const DARK_MATTE_RING_MAX_BROAD_PIXELS = 500;
const DARK_MATTE_RING_MAX_COMPONENT_PIXELS = 1_000;
const DARK_MATTE_RING_RADIUS = 16;
const DARK_MATTE_TRUSTED_DILATION = 2;
const MINIMUM_IDLE_PARTIAL_ALPHA_PIXELS = 4_000;
// Current dense-pose underarm scans expose 4,461-5,853 transparent pixels with
// 44-61 pixel maximum widths. Allowing 2,500 pixels and 30 pixels preserves
// natural negative space while rejecting the broad wedges seen in review.
const UNDERARM_MAX_GAP_PIXELS = 2_500;
const UNDERARM_MAX_GAP_WIDTH = 30;
const TORSO_ARM_JOINT_OVERLAP_RADIUS = 36;
const TORSO_ARM_DUPLICATE_MAX_PIXELS = 256;
const TORSO_ARM_DUPLICATE_MAX_FRACTION = 0.08;
const TORSO_ARM_STATIC_EXPOSURE_DILATION = 2;
const TORSO_ARM_STATIC_EXPOSURE_MAX_PIXELS = 256;
const ARM_BACKING_MAX_TORSO_DISTANCE = 36;
const ARM_BACKING_MAX_VISIBLE_PIXELS = 2_000;
const ARM_BACKING_MIN_CONNECTED_FRACTION = 0.97;
const ARM_BACKING_MAX_DETACHED_COMPONENT_PIXELS = 16;
const SHOULDER_WHITE_MOVING_MIN_FRACTION = 0.95;
const SHOULDER_GREEN_TORSO_MIN_FRACTION = 0.99;
const NEAR_OPAQUE_ALPHA_MINIMUM = 250;
const SOURCE_INTERIOR_MIN_EDGE_DISTANCE = 2;
const SOURCE_FIDELITY_GEOMETRY = Object.freeze([
  Object.freeze({
    name: 'left-shoulder',
    rectangle: Object.freeze({ xMin: 306, xMax: 358, yMin: 232, yMax: 315 }),
  }),
  Object.freeze({
    name: 'right-shoulder',
    rectangle: Object.freeze({ xMin: 545, xMax: 608, yMin: 232, yMax: 315 }),
  }),
  Object.freeze({
    name: 'right-waist-qameez',
    rectangle: Object.freeze({ xMin: 548, xMax: 610, yMin: 650, yMax: 875 }),
  }),
]);
const RIGHT_HAND_KNOWN_RECOLOR_PIXEL = Object.freeze({ x: 566, y: 623 });
const RIGHT_HAND_SOURCE_GEOMETRY = Object.freeze({
  xMin: 552,
  xMax: 625,
  yMin: 620,
  yMax: 718,
});
const RIGHT_HAND_INTERIOR_SOURCE_PROBE = Object.freeze({
  xMin: 556,
  xMax: 608,
  yMin: 660,
  yMax: 688,
});
const PROTECTED_FIXED_GARMENT_PROBES = Object.freeze([
  Object.freeze({
    name: 'left-underarm-waistcoat',
    rectangle: Object.freeze({ xMin: 330, xMax: 390, yMin: 296, yMax: 620 }),
    pixelKind: 'green',
    movingRoles: Object.freeze(['leftArm']),
    minimumProtectedPixels: 2_000,
  }),
  Object.freeze({
    name: 'right-underarm-waistcoat',
    rectangle: Object.freeze({ xMin: 505, xMax: 570, yMin: 296, yMax: 650 }),
    pixelKind: 'green',
    movingRoles: Object.freeze(['rightArm']),
    minimumProtectedPixels: 2_000,
  }),
  Object.freeze({
    name: 'right-waist-qameez',
    rectangle: Object.freeze({ xMin: 548, xMax: 610, yMin: 660, yMax: 875 }),
    pixelKind: 'garment',
    movingRoles: Object.freeze(['rightArm', 'rightLeg']),
    minimumProtectedPixels: 3_000,
  }),
]);
// Keep generous headroom below the observed fixed-asset minima while requiring
// contact areas far wider than a one-pixel/diagonal tether. The left shoulder
// currently bottoms out at 287 gait pixels and 127 clap pixels at both alpha
// thresholds, so its 160/64 floors preserve roughly 44-50% regression margin.
const DENSE_BRIDGE_PIXEL_FLOORS = Object.freeze({
  gait: Object.freeze({
    'leftArm->torsoHead': 160,
    'rightArm->torsoHead': 256,
    'leftLeg->torsoHead': 192,
    'rightLeg->torsoHead': 96,
  }),
  clap: Object.freeze({
    'leftArm->torsoHead': 64,
    'rightArm->torsoHead': 256,
    'leftLeg->torsoHead': 192,
    'rightLeg->torsoHead': 96,
  }),
});
const RASTER_ROLE_BITS = new Map([
  ['torsoHead', 1],
  ...LIMB_SUBTREES.map(({ name }, index) => [name, 2 ** (index + 1)]),
]);
const SUBTREE_ROLE_BITS = new Map(LIMB_SUBTREES.map((subtree) => [
  subtree.name,
  RASTER_ROLE_BITS.get(subtree.name),
]));
let server;
let origin;

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex').toUpperCase();
}

function fileSnapshot(filePath) {
  const stats = statSync(filePath, { bigint: true });
  return {
    mtimeNs: stats.mtimeNs,
    sha256: sha256(readFileSync(filePath)),
    size: stats.size,
  };
}

function inspectJpegDimensions(buffer) {
  assert.deepEqual(buffer.subarray(0, 2), Buffer.from([0xff, 0xd8]));
  let offset = 2;

  while (offset < buffer.length) {
    assert.equal(buffer[offset], 0xff, 'JPEG segment must start with a marker');
    while (buffer[offset] === 0xff) {
      offset += 1;
    }

    const marker = buffer[offset];
    offset += 1;
    if (marker === 0xd9 || marker === 0xda) {
      break;
    }
    if (marker >= 0xd0 && marker <= 0xd7) {
      continue;
    }

    const segmentLength = buffer.readUInt16BE(offset);
    const payloadStart = offset + 2;
    const payloadEnd = offset + segmentLength;
    assert.ok(segmentLength >= 2 && payloadEnd <= buffer.length);

    if (
      marker === 0xc0
      || marker === 0xc1
      || marker === 0xc2
      || marker === 0xc3
      || marker === 0xc5
      || marker === 0xc6
      || marker === 0xc7
      || marker === 0xc9
      || marker === 0xca
      || marker === 0xcb
      || marker === 0xcd
      || marker === 0xce
      || marker === 0xcf
    ) {
      return {
        height: buffer.readUInt16BE(payloadStart + 1),
        width: buffer.readUInt16BE(payloadStart + 3),
      };
    }

    offset = payloadEnd;
  }

  assert.fail('Avatar.jpg must contain a JPEG start-of-frame segment');
}

function firstExisting(candidates) {
  return candidates.find((candidate) => existsSync(candidate)) ?? null;
}

function discoverNamedModule(directory, pattern) {
  if (!existsSync(directory)) {
    return [];
  }

  return readdirSync(directory, { withFileTypes: true })
    .flatMap((entry) => {
      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        return discoverNamedModule(entryPath, pattern);
      }
      return pattern.test(entry.name) ? [entryPath] : [];
    });
}

function findGeneratorPath() {
  return firstExisting(GENERATOR_CANDIDATES)
    ?? discoverNamedModule(
      projectRoot,
      /^(?=.*(?:avatar|rig))(?=.*(?:generate|build)).*\.mjs$/i,
    ).find((candidate) => !candidate.includes(`${path.sep}test${path.sep}`))
    ?? null;
}

function findManifestPath() {
  return existsSync(MANIFEST_PATH) ? MANIFEST_PATH : null;
}

function loadManifest() {
  const manifestPath = findManifestPath();
  assert.ok(
    manifestPath,
    'the private generated avatar manifest must exist at assets/avatar/manifest.json',
  );

  try {
    return {
      manifest: JSON.parse(readFileSync(manifestPath, 'utf8')),
      manifestPath,
    };
  } catch (error) {
    assert.fail(`avatar rig manifest must be valid JSON: ${error.message}`);
  }
}

function normalizedRole(value) {
  return String(value ?? '').replace(/[^a-z0-9]/gi, '').toLowerCase();
}

function layerRole(layer) {
  return normalizedRole(layer.role ?? layer.id ?? layer.name);
}

function canonicalRole(value) {
  const normalized = normalizedRole(value);
  for (const [role, aliases] of Object.entries(CANONICAL_ROLE_ALIASES)) {
    if (aliases.some((alias) => normalizedRole(alias) === normalized)) {
      return role;
    }
  }
  return null;
}

function layersForCanonicalRole(layers, requiredRole) {
  return layers.filter((layer) => canonicalRole(layer.role) === requiredRole);
}

function findRoleLayer(layers, requiredRole) {
  const exact = layers.find((layer) => normalizedRole(layer.role) === normalizedRole(requiredRole));
  if (exact) {
    return exact;
  }

  return layersForCanonicalRole(layers, requiredRole)[0] ?? null;
}

function rootLayer(layers) {
  return findRoleLayer(layers, 'torsoHead');
}

function manifestSource(manifest) {
  const source = manifest.source ?? manifest.sourceImage ?? {};
  const dimensions = source.dimensions ?? manifest.sourceDimensions ?? manifest.canvas ?? {};
  return {
    file: source.file ?? source.path ?? source.name,
    sha256: source.sha256 ?? source.hash,
    width: source.width ?? dimensions.width,
    height: source.height ?? dimensions.height,
    reflectionExcluded: source.reflectionExcluded
      ?? manifest.reflectionExcluded
      ?? manifest.excludedContent?.includes?.('reflection'),
  };
}

function layerBounds(layer) {
  const bounds = layer.sourceRect ?? layer.bounds ?? layer.rect ?? {};
  return {
    x: bounds.x ?? layer.x ?? 0,
    y: bounds.y ?? layer.y ?? 0,
    width: bounds.width ?? layer.width,
    height: bounds.height ?? layer.height,
  };
}

function layerPivot(layer) {
  const pivot = layer.pivot ?? layer.anchor ?? layer.transformOrigin;
  if (Array.isArray(pivot)) {
    return { x: pivot[0], y: pivot[1] };
  }
  return pivot ?? {};
}

function layerUrl(layer) {
  return layer.url ?? layer.src ?? layer.path;
}

function localAssetPath(url) {
  assert.equal(typeof url, 'string', 'each layer must register a URL');
  assert.doesNotMatch(url, /^(?:[a-z]+:)?\/\//i, `${url} must be local`);
  assert.doesNotMatch(url, /(?:^|\/)\.\.(?:\/|$)/, `${url} must not traverse`);
  assert.match(url, /\.png$/i, `${url} must be a PNG`);

  const pathname = url.replace(/[?#].*$/, '').replace(/^\.?\//, '');
  const filePath = path.resolve(projectRoot, pathname);
  const relative = path.relative(assetsRoot, filePath);
  assert.ok(
    relative !== '' && !relative.startsWith('..') && !path.isAbsolute(relative),
    `${url} must resolve inside the assets directory`,
  );
  return filePath;
}

function inspectPng(buffer) {
  assert.deepEqual(buffer.subarray(0, 8), PNG_SIGNATURE, 'runtime layer must be a PNG');
  const chunks = [];
  const idat = [];
  let width;
  let height;
  let bitDepth;
  let colorType;
  let endOffset;
  let offset = 8;

  while (offset < buffer.length) {
    const length = buffer.readUInt32BE(offset);
    const type = buffer.toString('ascii', offset + 4, offset + 8);
    const dataStart = offset + 8;
    const dataEnd = dataStart + length;
    assert.ok(dataEnd + 4 <= buffer.length, `${type} chunk must fit inside the PNG`);
    const data = buffer.subarray(dataStart, dataEnd);
    chunks.push(type);

    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      assert.equal(data[10], 0, 'PNG compression method must be deflate');
      assert.equal(data[11], 0, 'PNG filter method must be standard');
      assert.equal(data[12], 0, 'interlaced runtime layers are not supported');
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      endOffset = dataEnd + 4;
      break;
    }

    offset = dataEnd + 4;
  }

  assert.ok(Number.isInteger(width) && width > 0);
  assert.ok(Number.isInteger(height) && height > 0);
  assert.equal(bitDepth, 8, 'runtime layers must use deterministic 8-bit channels');
  assert.equal(colorType, 6, 'runtime layers must use deterministic RGBA pixels');
  assert.ok(idat.length > 0, 'runtime layer must contain image data');
  assert.equal(endOffset, buffer.length, 'runtime PNG must end immediately after IEND');
  return { bitDepth, chunks, colorType, height, idat, width };
}

function paethPredictor(left, above, upperLeft) {
  const prediction = left + above - upperLeft;
  const leftDistance = Math.abs(prediction - left);
  const aboveDistance = Math.abs(prediction - above);
  const upperLeftDistance = Math.abs(prediction - upperLeft);
  if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance) {
    return left;
  }
  return aboveDistance <= upperLeftDistance ? above : upperLeft;
}

function decodePngPixels(png) {
  const channels = png.colorType === 6 ? 4 : 2;
  const stride = png.width * channels;
  const inflated = inflateSync(Buffer.concat(png.idat));
  assert.equal(inflated.length, (stride + 1) * png.height);
  const pixels = Buffer.alloc(stride * png.height);

  for (let y = 0; y < png.height; y += 1) {
    const filter = inflated[y * (stride + 1)];
    const rowStart = y * stride;
    const encodedStart = y * (stride + 1) + 1;

    for (let x = 0; x < stride; x += 1) {
      const encoded = inflated[encodedStart + x];
      const left = x >= channels ? pixels[rowStart + x - channels] : 0;
      const above = y > 0 ? pixels[rowStart + x - stride] : 0;
      const upperLeft = y > 0 && x >= channels
        ? pixels[rowStart + x - stride - channels]
        : 0;
      let value;

      switch (filter) {
        case 0:
          value = encoded;
          break;
        case 1:
          value = encoded + left;
          break;
        case 2:
          value = encoded + above;
          break;
        case 3:
          value = encoded + Math.floor((left + above) / 2);
          break;
        case 4:
          value = encoded + paethPredictor(left, above, upperLeft);
          break;
        default:
          assert.fail(`unsupported PNG row filter ${filter}`);
      }

      pixels[rowStart + x] = value & 0xff;
    }
  }

  return { channels, pixels };
}

function pixelStats(png) {
  const { channels, pixels } = decodePngPixels(png);
  let minimumAlpha = 255;
  let maximumAlpha = 0;
  let darkOpaquePixels = 0;

  for (let offset = 0; offset < pixels.length; offset += channels) {
    const red = pixels[offset];
    const green = png.colorType === 6 ? pixels[offset + 1] : red;
    const blue = png.colorType === 6 ? pixels[offset + 2] : red;
    const alpha = pixels[offset + channels - 1];
    minimumAlpha = Math.min(minimumAlpha, alpha);
    maximumAlpha = Math.max(maximumAlpha, alpha);
    const luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
    if (alpha >= 180 && luminance <= 70) {
      darkOpaquePixels += 1;
    }
  }

  return { darkOpaquePixels, maximumAlpha, minimumAlpha, channels, pixels };
}

function assetPixel(stats, x, y) {
  const offset = ((y * SOURCE_WIDTH) + x) * stats.channels;
  return {
    red: stats.pixels[offset],
    green: stats.pixels[offset + 1],
    blue: stats.pixels[offset + 2],
    alpha: stats.pixels[offset + 3],
  };
}

function isBrightCharacterPixel(pixel) {
  const luminance = (pixel.red * 0.2126) + (pixel.green * 0.7152) + (pixel.blue * 0.0722);
  return pixel.alpha >= 32 && luminance >= 110;
}

function isHandSkinPixel(pixel) {
  return pixel.alpha >= 32
    && pixel.red >= 70
    && pixel.red >= pixel.green + 8
    && pixel.green >= pixel.blue + 4
    && pixel.red >= pixel.blue + 18;
}

function isRightHandArtworkPixel(stats, x, y) {
  if (
    x < RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMin
    || x > RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMax
    || y < RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMin
    || y > RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMax
  ) {
    return false;
  }
  if (isHandSkinPixel(assetPixel(stats, x, y))) {
    return true;
  }

  for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
    for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
      if (offsetX === 0 && offsetY === 0) {
        continue;
      }
      if (isHandSkinPixel(assetPixel(stats, x + offsetX, y + offsetY))) {
        return true;
      }
    }
  }
  return false;
}

function rectangleContains(rectangle, x, y) {
  return x >= rectangle.xMin
    && x <= rectangle.xMax
    && y >= rectangle.yMin
    && y <= rectangle.yMax;
}

function isSourceArtworkSeed(pixel) {
  const maximum = Math.max(pixel.red, pixel.green, pixel.blue);
  const minimum = Math.min(pixel.red, pixel.green, pixel.blue);
  const luminance = Math.floor(
    ((pixel.red * 54) + (pixel.green * 183) + (pixel.blue * 19)) / 256,
  );
  const chroma = maximum - minimum;
  return luminance >= 64 || (chroma >= 15 && maximum >= 28);
}

function sourceVisibleInteriorPopulation(
  source,
  regions,
  predicate = isSourceArtworkSeed,
  minimumEdgeDistance = SOURCE_INTERIOR_MIN_EDGE_DISTANCE,
) {
  const candidateMask = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
  let candidatePixels = 0;

  for (const { rectangle } of regions) {
    for (let y = rectangle.yMin; y <= rectangle.yMax; y += 1) {
      for (let x = rectangle.xMin; x <= rectangle.xMax; x += 1) {
        const index = (y * SOURCE_WIDTH) + x;
        if (candidateMask[index] === 0 && predicate(sourcePixel(source, x, y), x, y)) {
          candidateMask[index] = 1;
          candidatePixels += 1;
        }
      }
    }
  }

  const exteriorMask = new Uint8Array(candidateMask.length);
  for (let index = 0; index < candidateMask.length; index += 1) {
    exteriorMask[index] = candidateMask[index] === 0 ? 1 : 0;
  }
  const edgeDistance = distanceFromMask(
    exteriorMask,
    minimumEdgeDistance + 1,
  );
  const indices = [];
  for (let index = 0; index < candidateMask.length; index += 1) {
    if (
      candidateMask[index] !== 0
      && edgeDistance[index] >= minimumEdgeDistance
    ) {
      indices.push(index);
    }
  }

  return {
    candidatePixels,
    edgeDistance,
    indices: Uint32Array.from(indices),
    minimumEdgeDistance,
  };
}

function sourceFidelityGeometryByIndex() {
  const geometry = new Map();
  for (const { name, rectangle } of SOURCE_FIDELITY_GEOMETRY) {
    for (let y = rectangle.yMin; y <= rectangle.yMax; y += 1) {
      for (let x = rectangle.xMin; x <= rectangle.xMax; x += 1) {
        geometry.set((y * SOURCE_WIDTH) + x, name);
      }
    }
  }
  return geometry;
}

function sourceInteriorRasterMetrics(source, stats, population) {
  let missingPixels = 0;
  let recoloredPixels = 0;
  let translucentPixels = 0;
  const sampleFailures = [];

  for (const index of population.indices) {
    const x = index % SOURCE_WIDTH;
    const y = Math.floor(index / SOURCE_WIDTH);
    const originalPixel = sourcePixel(source, x, y);
    const generatedPixel = assetPixel(stats, x, y);
    const missing = generatedPixel.alpha === 0;
    const translucent = generatedPixel.alpha > 0 && generatedPixel.alpha !== 255;
    const recolored = !sameRgb(generatedPixel, originalPixel);
    if (missing) {
      missingPixels += 1;
    }
    if (translucent) {
      translucentPixels += 1;
    }
    if (recolored) {
      recoloredPixels += 1;
    }
    if ((missing || translucent || recolored) && sampleFailures.length < 8) {
      sampleFailures.push({
        generated: generatedPixel,
        source: originalPixel,
        sourceEdgeDistance: population.edgeDistance[index],
        x,
        y,
      });
    }
  }

  return {
    candidatePixels: population.candidatePixels,
    minimumSourceEdgeDistance: population.minimumEdgeDistance,
    missingPixels,
    populationPixels: population.indices.length,
    recoloredPixels,
    sampleFailures,
    translucentPixels,
  };
}

function sourceDefinedRightHandPopulation(source) {
  return sourceVisibleInteriorPopulation(
    source,
    [{
      name: 'right-hand',
      rectangle: RIGHT_HAND_SOURCE_GEOMETRY,
    }],
    (pixel) => isHandSkinPixel({ ...pixel, alpha: 255 }),
  );
}

function rightHandRasterFidelityMetrics(source, stats, population) {
  const metrics = sourceInteriorRasterMetrics(source, stats, population);
  const composite = {
    black: { mismatchedPixels: 0, maximumChannelDelta: 0, totalChannelDelta: 0 },
    white: { mismatchedPixels: 0, maximumChannelDelta: 0, totalChannelDelta: 0 },
  };

  for (const index of population.indices) {
    const x = index % SOURCE_WIDTH;
    const y = Math.floor(index / SOURCE_WIDTH);
    const originalPixel = sourcePixel(source, x, y);
    const generatedPixel = assetPixel(stats, x, y);
    for (const [backgroundName, backgroundChannel] of [
      ['black', 0],
      ['white', 255],
    ]) {
      const channelDeltas = [
        ['red', originalPixel.red],
        ['green', originalPixel.green],
        ['blue', originalPixel.blue],
      ].map(([channel, sourceChannel]) => {
        const compositeChannel = Math.round(
          ((generatedPixel[channel] * generatedPixel.alpha)
            + (backgroundChannel * (255 - generatedPixel.alpha)))
            / 255,
        );
        return Math.abs(compositeChannel - sourceChannel);
      });
      const pixelMaximumDelta = Math.max(...channelDeltas);
      const compositeMetric = composite[backgroundName];
      if (pixelMaximumDelta > 0) {
        compositeMetric.mismatchedPixels += 1;
      }
      compositeMetric.maximumChannelDelta = Math.max(
        compositeMetric.maximumChannelDelta,
        pixelMaximumDelta,
      );
      compositeMetric.totalChannelDelta += channelDeltas.reduce(
        (sum, delta) => sum + delta,
        0,
      );
    }
  }

  return {
    ...metrics,
    composite: Object.fromEntries(
      Object.entries(composite).map(([backgroundName, metric]) => [
        backgroundName,
        {
          averageChannelDelta: population.indices.length === 0
            ? 0
            : Number(
              (metric.totalChannelDelta / (population.indices.length * 3)).toFixed(2),
            ),
          maximumChannelDelta: metric.maximumChannelDelta,
          mismatchedPixels: metric.mismatchedPixels,
        },
      ]),
    ),
  };
}

function isGreenGarmentPixel(pixel) {
  return pixel.alpha >= 180
    && pixel.green >= 45
    && pixel.green >= pixel.red + 12
    && pixel.green >= pixel.blue + 8;
}

function isWhiteGarmentPixel(pixel) {
  return pixel.alpha >= 180
    && pixel.red >= 105
    && pixel.green >= 105
    && pixel.blue >= 105
    && Math.max(pixel.red, pixel.green, pixel.blue)
      - Math.min(pixel.red, pixel.green, pixel.blue) <= 85;
}

function isProtectedGarmentPixel(pixel, pixelKind) {
  if (pixelKind === 'green') {
    return isGreenGarmentPixel(pixel);
  }
  return isGreenGarmentPixel(pixel) || isWhiteGarmentPixel(pixel);
}

function countPixelsInRect(stats, rectangle, predicate) {
  let count = 0;
  for (let y = rectangle.yMin; y <= rectangle.yMax; y += 1) {
    for (let x = rectangle.xMin; x <= rectangle.xMax; x += 1) {
      if (predicate(assetPixel(stats, x, y), x, y)) {
        count += 1;
      }
    }
  }
  return count;
}

function rotatePoint(point, origin, angleDeg) {
  const angle = (angleDeg * Math.PI) / 180;
  const cosine = Math.cos(angle);
  const sine = Math.sin(angle);
  const deltaX = point.x - origin.x;
  const deltaY = point.y - origin.y;

  return {
    x: origin.x + (deltaX * cosine) - (deltaY * sine),
    y: origin.y + (deltaX * sine) + (deltaY * cosine),
  };
}

function articulatedPoint(role, point, pose) {
  return transformPointThroughChain(
    point,
    roleRotationChain(role, pose, { includeRoot: false }),
  );
}

function colorPopulation(stats) {
  let green = 0;
  let white = 0;
  let visible = 0;

  for (let offset = 0; offset < stats.pixels.length; offset += stats.channels) {
    const red = stats.pixels[offset];
    const greenChannel = stats.pixels[offset + 1];
    const blue = stats.pixels[offset + 2];
    const alpha = stats.pixels[offset + 3];
    if (alpha < 32) {
      continue;
    }

    visible += 1;
    if (greenChannel >= 45 && greenChannel >= red + 12 && greenChannel >= blue + 8) {
      green += 1;
    }
    if (
      alpha >= 180
      && red >= 105
      && greenChannel >= 105
      && blue >= 105
      && Math.max(red, greenChannel, blue) - Math.min(red, greenChannel, blue) <= 85
    ) {
      white += 1;
    }
  }

  return { green, visible, white };
}

function sourceHasNearbyGarmentColor(source, generatedPixel, x, y) {
  const generatedOpaqueColor = { ...generatedPixel, alpha: 255 };
  if (isHandSkinPixel(generatedOpaqueColor)) {
    return false;
  }

  const xMin = Math.max(0, x - ARM_BACKING_MAX_TORSO_DISTANCE);
  const xMax = Math.min(SOURCE_WIDTH - 1, x + ARM_BACKING_MAX_TORSO_DISTANCE);
  const yMin = Math.max(0, y - ARM_BACKING_MAX_TORSO_DISTANCE);
  const yMax = Math.min(SOURCE_HEIGHT - 1, y + ARM_BACKING_MAX_TORSO_DISTANCE);

  for (let sourceY = yMin; sourceY <= yMax; sourceY += 1) {
    for (let sourceX = xMin; sourceX <= xMax; sourceX += 1) {
      const candidate = sourcePixel(source, sourceX, sourceY);
      const candidateOpaqueColor = { ...candidate, alpha: 255 };
      if (
        !isHandSkinPixel(candidateOpaqueColor)
        && isProtectedGarmentPixel(candidateOpaqueColor, 'garment')
        && sameRgb(candidate, generatedPixel)
      ) {
        return true;
      }
    }
  }
  return false;
}

function hiddenTorsoBackingObservationFailures(observation) {
  const failures = [];
  if (observation.generatedRole !== 'torsoHead') {
    failures.push('backing is not torsoHead-owned');
  }
  if (!observation.declaredRegion) {
    failures.push('backing is outside a manifest-declared swept region');
  }
  if (!['leftArm', 'rightArm'].includes(observation.occludingRole)) {
    failures.push('occluder is not a manifest-declared whole arm');
  }
  if (!observation.occluderOpaque || !observation.occluderSourceFaithful) {
    failures.push('source coordinate is not hidden by opaque source-faithful arm artwork');
  }
  if (observation.torsoDistance > ARM_BACKING_MAX_TORSO_DISTANCE) {
    failures.push('backing is not localized to source-faithful torso artwork');
  }
  if (!observation.sourceDerivedGarmentColor) {
    failures.push('backing color is not derived from nearby source garment artwork');
  }
  if (!observation.differsFromSource) {
    failures.push('pixel is source-faithful overlap rather than the hidden-backing exception');
  }
  return failures;
}

function manifestContinuityBackingDeclarations(manifest) {
  const declarations = manifest.continuityBacking ?? [];
  assert.ok(
    Array.isArray(declarations),
    'manifest continuityBacking must be an array when hidden garment backing is declared',
  );
  const names = new Set();

  return declarations.map((declaration) => {
    const {
      name,
      owner,
      occludedBy,
      sweptRegion,
      maximumPixels,
    } = declaration;
    assert.equal(typeof name, 'string', 'each continuity backing declaration needs a name');
    assert.ok(name.length > 0, 'continuity backing names must not be empty');
    assert.equal(names.has(name), false, `duplicate continuity backing declaration ${name}`);
    names.add(name);
    assert.equal(owner, 'torsoHead', `${name} backing must be torsoHead-owned`);
    assert.ok(
      ['leftArm', 'rightArm'].includes(occludedBy),
      `${name} must be occluded by a whole-arm layer`,
    );
    assert.ok(
      findRoleLayer(manifest.layers, occludedBy),
      `${name} occluder ${occludedBy} must be registered in manifest.layers`,
    );
    assert.ok(sweptRegion && typeof sweptRegion === 'object', `${name} needs sweptRegion bounds`);
    for (const coordinate of ['xMin', 'xMax', 'yMin', 'yMax']) {
      assert.ok(
        Number.isInteger(sweptRegion[coordinate]),
        `${name} sweptRegion.${coordinate} must be an integer`,
      );
    }
    assert.ok(
      sweptRegion.xMin >= 0
        && sweptRegion.xMin <= sweptRegion.xMax
        && sweptRegion.xMax < SOURCE_WIDTH
        && sweptRegion.yMin >= 0
        && sweptRegion.yMin <= sweptRegion.yMax
        && sweptRegion.yMax < SOURCE_HEIGHT,
      `${name} sweptRegion must stay inside the source canvas`,
    );
    assert.ok(
      Number.isInteger(maximumPixels)
        && maximumPixels > 0
        && maximumPixels <= ARM_BACKING_MAX_VISIBLE_PIXELS,
      `${name} maximumPixels must be 1..${ARM_BACKING_MAX_VISIBLE_PIXELS}`,
    );
    const sweptArea = (sweptRegion.xMax - sweptRegion.xMin + 1)
      * (sweptRegion.yMax - sweptRegion.yMin + 1);
    assert.ok(
      sweptArea <= maximumPixels * 4,
      `${name} sweptRegion area must stay within four times its pixel budget`,
    );

    return {
      maximumPixels,
      name,
      occludedBy,
      owner,
      sweptRegion,
    };
  });
}

function createHiddenTorsoBackingContext(manifest, decoded, source) {
  const torso = decoded.get('torsoHead');
  const sourceFaithfulTorso = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
  for (let y = 0; y < SOURCE_HEIGHT; y += 1) {
    for (let x = 0; x < SOURCE_WIDTH; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      const torsoPixel = assetPixel(torso, x, y);
      if (
        torsoPixel.alpha >= 32
        && sameRgb(torsoPixel, sourcePixel(source, x, y))
      ) {
        sourceFaithfulTorso[index] = 1;
      }
    }
  }

  return {
    backingDeclarations: manifestContinuityBackingDeclarations(manifest),
    decoded,
    source,
    torsoDistance: distanceFromMask(
      sourceFaithfulTorso,
      ARM_BACKING_MAX_TORSO_DISTANCE + 1,
    ),
  };
}

function continuityBackingDeclarationAt(context, occludingRole, x, y) {
  return context.backingDeclarations.find((declaration) => {
    const { occludedBy, sweptRegion } = declaration;
    return occludedBy === occludingRole
      && x >= sweptRegion.xMin
      && x <= sweptRegion.xMax
      && y >= sweptRegion.yMin
      && y <= sweptRegion.yMax;
  }) ?? null;
}

function hiddenTorsoBackingDeclaration(context, generatedPixel, x, y) {
  const index = (y * SOURCE_WIDTH) + x;
  const originalPixel = sourcePixel(context.source, x, y);
  for (const declaration of context.backingDeclarations) {
    const {
      occludedBy,
      sweptRegion,
    } = declaration;
    const declaredRegion = x >= sweptRegion.xMin
      && x <= sweptRegion.xMax
      && y >= sweptRegion.yMin
      && y <= sweptRegion.yMax;
    const occluderPixel = assetPixel(context.decoded.get(occludedBy), x, y);
    const observation = {
      declaredRegion,
      differsFromSource: !sameRgb(generatedPixel, originalPixel),
      generatedRole: 'torsoHead',
      occluderOpaque: occluderPixel.alpha === 255,
      occluderSourceFaithful: sameRgb(occluderPixel, originalPixel),
      occludingRole: occludedBy,
      sourceDerivedGarmentColor: sourceHasNearbyGarmentColor(
        context.source,
        generatedPixel,
        x,
        y,
      ),
      torsoDistance: context.torsoDistance[index],
    };
    if (hiddenTorsoBackingObservationFailures(observation).length === 0) {
      return declaration;
    }
  }
  return null;
}

function eightConnectedComponentSizes(indices) {
  const remaining = new Set(indices);
  const sizes = [];

  while (remaining.size > 0) {
    const [start] = remaining;
    const queue = [start];
    remaining.delete(start);
    let size = 0;

    while (queue.length > 0) {
      const index = queue.pop();
      size += 1;
      const x = index % SOURCE_WIDTH;
      const y = Math.floor(index / SOURCE_WIDTH);
      for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
        const neighborY = y + offsetY;
        if (neighborY < 0 || neighborY >= SOURCE_HEIGHT) {
          continue;
        }
        for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
          if (offsetX === 0 && offsetY === 0) {
            continue;
          }
          const neighborX = x + offsetX;
          if (neighborX < 0 || neighborX >= SOURCE_WIDTH) {
            continue;
          }
          const neighborIndex = (neighborY * SOURCE_WIDTH) + neighborX;
          if (remaining.delete(neighborIndex)) {
            queue.push(neighborIndex);
          }
        }
      }
    }
    sizes.push(size);
  }

  return sizes.sort((left, right) => right - left);
}

function hiddenBackingTopologyMetrics(declaration, indices) {
  const componentSizes = eightConnectedComponentSizes(indices);
  const pixels = indices.size;
  const connectedPixels = componentSizes[0] ?? 0;
  return {
    connectedFraction: pixels === 0 ? 1 : connectedPixels / pixels,
    largestDetachedComponentPixels: componentSizes[1] ?? 0,
    maximumPixels: declaration.maximumPixels,
    name: declaration.name,
    occludedBy: declaration.occludedBy,
    pixels,
  };
}

function hiddenBackingTopologyFailures(metrics) {
  return metrics.filter((metric) => (
    metric.pixels > metric.maximumPixels
    || metric.connectedFraction < ARM_BACKING_MIN_CONNECTED_FRACTION
    || metric.largestDetachedComponentPixels
      > ARM_BACKING_MAX_DETACHED_COMPONENT_PIXELS
  ));
}

function shoulderOwnershipMetrics(manifest, decoded, idle) {
  const torso = decoded.get('torsoHead');
  return SHOULDER_OWNERSHIP_PROBES.map((probe) => {
    const shoulder = FIVE_LAYER_BY_ROLE.get(probe.role).pivot;
    const movingLayers = layersForCanonicalRole(manifest.layers, probe.role)
      .map((layer) => decoded.get(layer.role));
    const metric = {
      greenMovingOwnedPixels: 0,
      greenTorsoOwnedPixels: 0,
      role: probe.role,
      sourceGreenPixels: 0,
      sourceWhitePixels: 0,
      whiteMovingOwnedPixels: 0,
      whiteOverlapOutsideRootPixels: 0,
      whiteTorsoOnlyPixels: 0,
      whiteUnownedPixels: 0,
    };

    for (let y = probe.rectangle.yMin; y <= probe.rectangle.yMax; y += 1) {
      for (let x = probe.rectangle.xMin; x <= probe.rectangle.xMax; x += 1) {
        const sourcePixelValue = assetPixel(idle, x, y);
        const movingOwned = movingLayers.some((moving) => (
          assetPixel(moving, x, y).alpha >= 32
        ));
        const torsoOwned = assetPixel(torso, x, y).alpha >= 32;

        if (isWhiteGarmentPixel(sourcePixelValue)) {
          metric.sourceWhitePixels += 1;
          if (movingOwned) {
            metric.whiteMovingOwnedPixels += 1;
          }
          if (!movingOwned && torsoOwned) {
            metric.whiteTorsoOnlyPixels += 1;
          }
          if (!movingOwned && !torsoOwned) {
            metric.whiteUnownedPixels += 1;
          }
          if (
            movingOwned
            && torsoOwned
            && Math.hypot(x - shoulder.x, y - shoulder.y)
              > TORSO_ARM_JOINT_OVERLAP_RADIUS
          ) {
            metric.whiteOverlapOutsideRootPixels += 1;
          }
        }

        if (isGreenGarmentPixel(sourcePixelValue)) {
          metric.sourceGreenPixels += 1;
          if (movingOwned) {
            metric.greenMovingOwnedPixels += 1;
          }
          if (torsoOwned) {
            metric.greenTorsoOwnedPixels += 1;
          }
        }
      }
    }

    return {
      ...metric,
      allowedGreenMovingPixels: Math.max(
        16,
        Math.floor(metric.sourceGreenPixels * 0.005),
      ),
      allowedWhiteOwnershipTrivia: Math.max(
        16,
        Math.floor(metric.sourceWhitePixels * 0.005),
      ),
      greenTorsoOwnedFraction: metric.sourceGreenPixels === 0
        ? 0
        : metric.greenTorsoOwnedPixels / metric.sourceGreenPixels,
      minimumGreenPixels: probe.minimumGreenPixels,
      minimumWhitePixels: probe.minimumWhitePixels,
      whiteMovingOwnedFraction: metric.sourceWhitePixels === 0
        ? 0
        : metric.whiteMovingOwnedPixels / metric.sourceWhitePixels,
    };
  });
}

function shoulderOwnershipFailures(metrics) {
  return metrics.flatMap((metric) => {
    const failures = [];
    if (metric.sourceWhitePixels < metric.minimumWhitePixels) {
      failures.push({ ...metric, reason: 'white sleeve-cap source population is not meaningful' });
    }
    if (metric.sourceGreenPixels < metric.minimumGreenPixels) {
      failures.push({ ...metric, reason: 'green static-shoulder source population is not meaningful' });
    }
    if (metric.whiteMovingOwnedFraction < SHOULDER_WHITE_MOVING_MIN_FRACTION) {
      failures.push({ ...metric, reason: 'white illustrated sleeve cap is not whole-arm owned' });
    }
    if (
      metric.whiteTorsoOnlyPixels > metric.allowedWhiteOwnershipTrivia
      || metric.whiteUnownedPixels > metric.allowedWhiteOwnershipTrivia
    ) {
      failures.push({ ...metric, reason: 'white sleeve-cap artwork remains static or unowned' });
    }
    if (metric.greenTorsoOwnedFraction < SHOULDER_GREEN_TORSO_MIN_FRACTION) {
      failures.push({ ...metric, reason: 'green waistcoat/collar shoulder art is not torsoHead-owned' });
    }
    if (metric.greenMovingOwnedPixels > metric.allowedGreenMovingPixels) {
      failures.push({ ...metric, reason: 'green waistcoat/collar shoulder art rotates with the arm' });
    }
    if (
      metric.whiteOverlapOutsideRootPixels
      > metric.allowedWhiteOwnershipTrivia
    ) {
      failures.push({ ...metric, reason: 'white sleeve overlap extends beyond the narrow root joint' });
    }
    return failures;
  });
}

function decodeAsset(filePath) {
  const png = inspectPng(readFileSync(filePath));
  return { ...pixelStats(png), png };
}

let decodedAvatarSource;

function decodeAvatarSource() {
  if (decodedAvatarSource) {
    return decodedAvatarSource;
  }

  const escapedSourcePath = avatarSourcePath.replaceAll('\'', '\'\'');
  const script = `
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$loaded = New-Object System.Drawing.Bitmap('${escapedSourcePath}')
$source = New-Object System.Drawing.Bitmap(
  ${SOURCE_WIDTH},
  ${SOURCE_HEIGHT},
  [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
)
try {
  $graphics = [System.Drawing.Graphics]::FromImage($source)
  try {
    $graphics.Clear([System.Drawing.Color]::Black)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
    $graphics.DrawImage(
      $loaded,
      (New-Object System.Drawing.Rectangle(0, 0, ${SOURCE_WIDTH}, ${SOURCE_HEIGHT}))
    )
  }
  finally {
    $graphics.Dispose()
  }

  $rectangle = New-Object System.Drawing.Rectangle(0, 0, ${SOURCE_WIDTH}, ${SOURCE_HEIGHT})
  $data = $source.LockBits(
    $rectangle,
    [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
  )
  try {
    if ($data.Stride -ne ${SOURCE_WIDTH * 4}) {
      throw "Unexpected Avatar.jpg bitmap stride: $($data.Stride)"
    }
    $pixels = New-Object byte[] (${SOURCE_WIDTH * SOURCE_HEIGHT * 4})
    [System.Runtime.InteropServices.Marshal]::Copy(
      $data.Scan0,
      $pixels,
      0,
      $pixels.Length
    )
    [Console]::Out.Write([Convert]::ToBase64String($pixels))
  }
  finally {
    $source.UnlockBits($data)
  }
}
finally {
  $source.Dispose()
  $loaded.Dispose()
}
`;
  const result = spawnSync(
    'powershell.exe',
    [
      '-NoLogo',
      '-NoProfile',
      '-NonInteractive',
      '-EncodedCommand',
      Buffer.from(script, 'utf16le').toString('base64'),
    ],
    {
      cwd: projectRoot,
      encoding: 'utf8',
      maxBuffer: 16 * 1024 * 1024,
    },
  );
  assert.equal(
    result.status,
    0,
    `Avatar.jpg must decode through the same System.Drawing pipeline as the generator\n`
      + `stdout=${result.stdout}\nstderr=${result.stderr}`,
  );
  const pixels = Buffer.from(result.stdout.trim(), 'base64');
  assert.equal(
    pixels.length,
    SOURCE_WIDTH * SOURCE_HEIGHT * 4,
    'decoded Avatar.jpg must contain one BGRA pixel per source coordinate',
  );
  decodedAvatarSource = { channels: 4, pixels };
  return decodedAvatarSource;
}

function sourcePixel(source, x, y) {
  const offset = ((y * SOURCE_WIDTH) + x) * source.channels;
  return {
    red: source.pixels[offset + 2],
    green: source.pixels[offset + 1],
    blue: source.pixels[offset],
    alpha: source.pixels[offset + 3],
  };
}

function sameRgb(left, right) {
  return left.red === right.red
    && left.green === right.green
    && left.blue === right.blue;
}

function sameRgba(left, right) {
  return sameRgb(left, right) && left.alpha === right.alpha;
}

function alphaPlaneFromRgba(rgba) {
  const alpha = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
  for (let index = 0; index < alpha.length; index += 1) {
    alpha[index] = rgba[(index * 4) + 3];
  }
  return alpha;
}

function blendSourceOver(rgba, targetIndex, red, green, blue, alpha) {
  if (alpha === 0) {
    return;
  }

  const targetOffset = targetIndex * 4;
  const sourceAlpha = alpha / 255;
  const destinationAlpha = rgba[targetOffset + 3] / 255;
  const outputAlpha = sourceAlpha + (destinationAlpha * (1 - sourceAlpha));

  if (outputAlpha === 0) {
    return;
  }

  rgba[targetOffset] = Math.round(
    ((red * sourceAlpha)
      + (rgba[targetOffset] * destinationAlpha * (1 - sourceAlpha)))
      / outputAlpha,
  );
  rgba[targetOffset + 1] = Math.round(
    ((green * sourceAlpha)
      + (rgba[targetOffset + 1] * destinationAlpha * (1 - sourceAlpha)))
      / outputAlpha,
  );
  rgba[targetOffset + 2] = Math.round(
    ((blue * sourceAlpha)
      + (rgba[targetOffset + 2] * destinationAlpha * (1 - sourceAlpha)))
      / outputAlpha,
  );
  rgba[targetOffset + 3] = Math.round(outputAlpha * 255);
}

function roleRotationChain(role, pose, { includeRoot = true } = {}) {
  const rotations = [];
  let layer = AVATAR_LAYERS.find((candidate) => candidate.role === role);

  while (layer) {
    let rotationProperty;
    if (FIVE_LAYER_BY_ROLE.has(layer.role)) {
      if (layer.role === 'torsoHead' && !includeRoot) {
        break;
      }
      rotationProperty = `${layer.role}RotateDeg`;
    } else if (layer.role === 'torso') {
      break;
    } else {
      rotationProperty = layer.role === 'head'
        ? 'headTiltDeg'
        : `${layer.role}RotateDeg`;
    }

    const angleDeg = pose[rotationProperty];
    assert.ok(
      Number.isFinite(angleDeg),
      `production pose must provide ${rotationProperty}`,
    );
    rotations.push({
      angleDeg,
      origin: layer.pivot,
    });

    if (layer.parent === null) {
      break;
    }
    layer = AVATAR_LAYERS.find((candidate) => candidate.role === layer.parent);
  }

  return rotations;
}

function transformPointThroughChain(point, rotations) {
  return rotations.reduce(
    (transformed, rotation) => rotatePoint(
      transformed,
      rotation.origin,
      rotation.angleDeg,
    ),
    point,
  );
}

function sourceAlphaIndices(stats) {
  const indices = [];
  for (let index = 0; index < SOURCE_WIDTH * SOURCE_HEIGHT; index += 1) {
    if (stats.pixels[(index * stats.channels) + stats.channels - 1] > 0) {
      indices.push(index);
    }
  }
  return Uint32Array.from(indices);
}

function rasterizePosedComposite(decoded, visibleIndices, pose) {
  const pixelCount = SOURCE_WIDTH * SOURCE_HEIGHT;
  const alpha = new Uint8Array(pixelCount);
  const anyRoleBits = new Uint16Array(pixelCount);
  const strongRoleBits = new Uint16Array(pixelCount);
  let xMin = SOURCE_WIDTH;
  let xMax = -1;
  let yMin = SOURCE_HEIGHT;
  let yMax = -1;

  for (const layer of AVATAR_LAYERS) {
    const stats = decoded.get(layer.role);
    const rotations = roleRotationChain(layer.role, pose, { includeRoot: false });
    const roleBit = RASTER_ROLE_BITS.get(canonicalRole(layer.role)) ?? 0;

    for (const sourceIndex of visibleIndices.get(layer.role)) {
      const sourceX = sourceIndex % SOURCE_WIDTH;
      const sourceY = Math.floor(sourceIndex / SOURCE_WIDTH);
      const transformed = transformPointThroughChain(
        { x: sourceX + 0.5, y: sourceY + 0.5 },
        rotations,
      );
      const targetX = Math.floor(transformed.x);
      const targetY = Math.floor(transformed.y);
      if (
        targetX < 0
        || targetX >= SOURCE_WIDTH
        || targetY < 0
        || targetY >= SOURCE_HEIGHT
      ) {
        continue;
      }

      const targetIndex = (targetY * SOURCE_WIDTH) + targetX;
      const sourceAlpha = stats.pixels[
        (sourceIndex * stats.channels) + stats.channels - 1
      ];
      alpha[targetIndex] = Math.max(alpha[targetIndex], sourceAlpha);
      if (roleBit !== 0) {
        anyRoleBits[targetIndex] |= roleBit;
        if (sourceAlpha >= 32) {
          strongRoleBits[targetIndex] |= roleBit;
        }
      }
      xMin = Math.min(xMin, targetX);
      xMax = Math.max(xMax, targetX);
      yMin = Math.min(yMin, targetY);
      yMax = Math.max(yMax, targetY);
    }
  }

  assert.ok(xMax >= xMin && yMax >= yMin, 'posed composite must contain visible alpha');
  return {
    alpha,
    anyRoleBits,
    strongRoleBits,
    xMax,
    xMin,
    yMax,
    yMin,
  };
}

function rasterizePosedRgbaComposite(decoded, visibleIndices, pose) {
  const rgba = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT * 4);

  for (const layer of AVATAR_LAYERS) {
    const stats = decoded.get(layer.role);
    const rotations = roleRotationChain(layer.role, pose);

    for (const sourceIndex of visibleIndices.get(layer.role)) {
      const sourceX = sourceIndex % SOURCE_WIDTH;
      const sourceY = Math.floor(sourceIndex / SOURCE_WIDTH);
      const transformed = transformPointThroughChain(
        { x: sourceX + 0.5, y: sourceY + 0.5 },
        rotations,
      );
      const targetX = Math.floor(transformed.x);
      const targetY = Math.floor(transformed.y);
      if (
        targetX < 0
        || targetX >= SOURCE_WIDTH
        || targetY < 0
        || targetY >= SOURCE_HEIGHT
      ) {
        continue;
      }

      const sourceOffset = sourceIndex * stats.channels;
      blendSourceOver(
        rgba,
        (targetY * SOURCE_WIDTH) + targetX,
        stats.pixels[sourceOffset],
        stats.pixels[sourceOffset + 1],
        stats.pixels[sourceOffset + 2],
        stats.pixels[sourceOffset + 3],
      );
    }
  }

  return rgba;
}

function rgbaPixel(rgba, x, y) {
  const offset = ((y * SOURCE_WIDTH) + x) * 4;
  return {
    red: rgba[offset],
    green: rgba[offset + 1],
    blue: rgba[offset + 2],
    alpha: rgba[offset + 3],
  };
}

function backgroundConnectedTransparency(alpha, threshold = 8) {
  const exterior = new Uint8Array(alpha.length);
  const queue = new Int32Array(alpha.length);
  let queueHead = 0;
  let queueTail = 0;

  function enqueue(x, y) {
    const index = (y * SOURCE_WIDTH) + x;
    if (exterior[index] !== 0 || alpha[index] >= threshold) {
      return;
    }
    exterior[index] = 1;
    queue[queueTail] = index;
    queueTail += 1;
  }

  for (let x = 0; x < SOURCE_WIDTH; x += 1) {
    enqueue(x, 0);
    enqueue(x, SOURCE_HEIGHT - 1);
  }
  for (let y = 0; y < SOURCE_HEIGHT; y += 1) {
    enqueue(0, y);
    enqueue(SOURCE_WIDTH - 1, y);
  }

  while (queueHead < queueTail) {
    const index = queue[queueHead];
    queueHead += 1;
    const x = index % SOURCE_WIDTH;
    const y = Math.floor(index / SOURCE_WIDTH);

    for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
      const nextY = y + offsetY;
      if (nextY < 0 || nextY >= SOURCE_HEIGHT) {
        continue;
      }
      for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
        if (offsetX === 0 && offsetY === 0) {
          continue;
        }
        const nextX = x + offsetX;
        if (nextX < 0 || nextX >= SOURCE_WIDTH) {
          continue;
        }
        enqueue(nextX, nextY);
      }
    }
  }

  return exterior;
}

function backgroundConnectedSeamNotchMetrics(composite, side) {
  const armBitMask = RASTER_ROLE_BITS.get(`${side}Arm`);
  const torsoBit = RASTER_ROLE_BITS.get('torsoHead');
  const exterior = backgroundConnectedTransparency(composite.alpha, 32);
  let backgroundPixels = 0;
  let gapRows = 0;
  let maximumWidth = 0;

  for (let y = 240; y <= 720; y += 1) {
    let armInnerEdge = side === 'left' ? -1 : SOURCE_WIDTH;
    let torsoOuterEdge = side === 'left' ? SOURCE_WIDTH : -1;
    const xMin = side === 'left' ? 240 : 448;
    const xMax = side === 'left' ? 448 : 656;

    for (let x = xMin; x <= xMax; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      if (composite.alpha[index] < 32) {
        continue;
      }
      const bits = composite.strongRoleBits[index];
      if ((bits & armBitMask) !== 0) {
        armInnerEdge = side === 'left'
          ? Math.max(armInnerEdge, x)
          : Math.min(armInnerEdge, x);
      }
      if ((bits & torsoBit) !== 0) {
        torsoOuterEdge = side === 'left'
          ? Math.min(torsoOuterEdge, x)
          : Math.max(torsoOuterEdge, x);
      }
    }

    const hasArmEdge = side === 'left'
      ? armInnerEdge >= 0
      : armInnerEdge < SOURCE_WIDTH;
    const hasTorsoEdge = side === 'left'
      ? torsoOuterEdge < SOURCE_WIDTH
      : torsoOuterEdge >= 0;
    if (!hasArmEdge || !hasTorsoEdge) {
      continue;
    }

    const gapStart = side === 'left' ? armInnerEdge + 1 : torsoOuterEdge + 1;
    const gapEnd = side === 'left' ? torsoOuterEdge - 1 : armInnerEdge - 1;
    if (gapStart > gapEnd) {
      continue;
    }

    let rowBackgroundPixels = 0;
    for (let x = gapStart; x <= gapEnd; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      if (composite.alpha[index] < 32 && exterior[index] !== 0) {
        rowBackgroundPixels += 1;
      }
    }
    if (rowBackgroundPixels === 0) {
      continue;
    }

    backgroundPixels += rowBackgroundPixels;
    gapRows += 1;
    maximumWidth = Math.max(maximumWidth, rowBackgroundPixels);
  }

  return {
    backgroundPixels,
    backgroundScreenPixels: Number(
      (backgroundPixels * (QA_1080P_SOURCE_TO_SCREEN_SCALE ** 2)).toFixed(3),
    ),
    gapRows,
    maximumWidth,
    maximumWidthPx: Number(
      (maximumWidth * QA_1080P_SOURCE_TO_SCREEN_SCALE).toFixed(3),
    ),
    side,
  };
}

function backgroundConnectedProtectedNotchMetrics(composite, probe) {
  const exterior = backgroundConnectedTransparency(composite.alpha, 32);
  let backgroundPixels = 0;
  let gapRows = 0;
  let maximumWidth = 0;

  for (
    let y = probe.rectangle.yMin;
    y <= probe.rectangle.yMax;
    y += 1
  ) {
    let leftOccupied = -1;
    let rightOccupied = -1;
    for (
      let x = probe.rectangle.xMin;
      x <= probe.rectangle.xMax;
      x += 1
    ) {
      if (composite.alpha[(y * SOURCE_WIDTH) + x] < 32) {
        continue;
      }
      if (leftOccupied === -1) {
        leftOccupied = x;
      }
      rightOccupied = x;
    }
    if (leftOccupied === -1 || rightOccupied <= leftOccupied) {
      continue;
    }

    let rowBackgroundPixels = 0;
    let currentRun = 0;
    let longestRun = 0;
    for (let x = leftOccupied + 1; x < rightOccupied; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      if (composite.alpha[index] < 32 && exterior[index] !== 0) {
        rowBackgroundPixels += 1;
        currentRun += 1;
        longestRun = Math.max(longestRun, currentRun);
      } else {
        currentRun = 0;
      }
    }
    if (rowBackgroundPixels === 0) {
      continue;
    }

    backgroundPixels += rowBackgroundPixels;
    gapRows += 1;
    maximumWidth = Math.max(maximumWidth, longestRun);
  }

  return {
    backgroundPixels,
    backgroundScreenPixels: Number(
      (backgroundPixels * (QA_1080P_SOURCE_TO_SCREEN_SCALE ** 2)).toFixed(3),
    ),
    gapRows,
    maximumWidth,
    maximumWidthPx: Number(
      (maximumWidth * QA_1080P_SOURCE_TO_SCREEN_SCALE).toFixed(3),
    ),
    name: probe.name,
  };
}

function staticShoulderSliverMetrics({
  composite,
  decoded,
  poseRgba,
  side,
}) {
  const role = `${side}Arm`;
  const probe = SHOULDER_OWNERSHIP_PROBES.find((candidate) => candidate.role === role);
  const torso = decoded.get('torsoHead');
  const armBit = RASTER_ROLE_BITS.get(role);
  const torsoBit = RASTER_ROLE_BITS.get('torsoHead');
  const exterior = backgroundConnectedTransparency(composite.alpha, 32);
  const exteriorDistance = distanceFromMask(exterior, 9);
  const candidateMask = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
  let sliverPixels = 0;
  let chromaticBackgroundVisiblePixels = 0;

  for (let y = probe.rectangle.yMin; y <= probe.rectangle.yMax; y += 1) {
    for (let x = probe.rectangle.xMin; x <= probe.rectangle.xMax; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      const finalPixel = rgbaPixel(poseRgba, x, y);
      const bits = composite.strongRoleBits[index];
      if (
        exteriorDistance[index] < 1
        || exteriorDistance[index] > 8
        || (bits & torsoBit) === 0
        || (bits & armBit) !== 0
        || !isWhiteGarmentPixel(assetPixel(torso, x, y))
        || !isWhiteGarmentPixel(finalPixel)
      ) {
        continue;
      }

      sliverPixels += 1;
      candidateMask[index] = 1;
      const visibleOnChromaticBackgrounds = BRIGHT_MATTE_BACKGROUNDS
        .filter(({ name }) => name !== 'white')
        .every(({ rgb }) => {
          const alpha = finalPixel.alpha / 255;
          const compositeRgb = [
            (finalPixel.red * alpha) + (rgb[0] * (1 - alpha)),
            (finalPixel.green * alpha) + (rgb[1] * (1 - alpha)),
            (finalPixel.blue * alpha) + (rgb[2] * (1 - alpha)),
          ];
          return Math.hypot(
            compositeRgb[0] - rgb[0],
            compositeRgb[1] - rgb[1],
            compositeRgb[2] - rgb[2],
          ) >= 120;
        });
      if (visibleOnChromaticBackgrounds) {
        chromaticBackgroundVisiblePixels += 1;
      }
    }
  }

  return {
    chromaticBackgroundVisiblePixels,
    largestComponentPixels: largestMaskComponent(candidateMask),
    role,
    screenPixels: Number(
      (
        chromaticBackgroundVisiblePixels
        * (QA_1080P_SOURCE_TO_SCREEN_SCALE ** 2)
      ).toFixed(3),
    ),
    side,
    sliverPixels,
  };
}

function seamLayerIntegrityMetrics(decoded) {
  const torso = decoded.get('torsoHead');
  return ['left', 'right'].map((side) => {
    const role = `${side}Arm`;
    const arm = decoded.get(role);
    const shoulder = FIVE_LAYER_BY_ROLE.get(role).pivot;
    const population = colorPopulation(arm);
    let duplicatePixelsOutsideJoint = 0;

    for (let y = 0; y < SOURCE_HEIGHT; y += 1) {
      for (let x = 0; x < SOURCE_WIDTH; x += 1) {
        if (
          assetPixel(arm, x, y).alpha < 32
          || Math.hypot(x - shoulder.x, y - shoulder.y)
            <= TORSO_ARM_JOINT_OVERLAP_RADIUS
        ) {
          continue;
        }
        if (assetPixel(torso, x, y).alpha >= 32) {
          duplicatePixelsOutsideJoint += 1;
        }
      }
    }

    return {
      duplicateFraction: Number(
        (duplicatePixelsOutsideJoint / Math.max(1, population.visible)).toFixed(6),
      ),
      duplicatePixelsOutsideJoint,
      role,
      visiblePixels: population.visible,
      whiteGarmentPixels: population.white,
    };
  });
}

function seamLayerIntegrityFailures(metrics) {
  return metrics.flatMap((metric) => {
    const failures = [];
    if (metric.visiblePixels < 5_000) {
      failures.push({ ...metric, reason: 'articulated garment was erased' });
    }
    if (metric.whiteGarmentPixels < 3_000) {
      failures.push({ ...metric, reason: 'white sleeve garment was erased' });
    }
    if (metric.duplicateFraction > TORSO_ARM_DUPLICATE_MAX_FRACTION) {
      failures.push({ ...metric, reason: 'broad static arm duplicate hides seam motion' });
    }
    return failures;
  });
}

function cloneDecodedAssets(decoded) {
  return new Map([...decoded].map(([role, stats]) => [
    role,
    {
      ...stats,
      pixels: Buffer.from(stats.pixels),
    },
  ]));
}

function copyVisibleAssetPixels(source, target) {
  for (let index = 0; index < SOURCE_WIDTH * SOURCE_HEIGHT; index += 1) {
    const sourceOffset = index * source.channels;
    if (source.pixels[sourceOffset + 3] < 32) {
      continue;
    }
    const targetOffset = index * target.channels;
    for (let channel = 0; channel < 4; channel += 1) {
      target.pixels[targetOffset + channel] = source.pixels[sourceOffset + channel];
    }
  }
}

function distanceFromMask(mask, maximumDistance = 0xffff) {
  const distance = new Uint16Array(mask.length);
  distance.fill(0xffff);
  const queue = new Int32Array(mask.length);
  let queueHead = 0;
  let queueTail = 0;

  for (let index = 0; index < mask.length; index += 1) {
    if (mask[index] !== 0) {
      distance[index] = 0;
      queue[queueTail] = index;
      queueTail += 1;
    }
  }

  while (queueHead < queueTail) {
    const index = queue[queueHead];
    queueHead += 1;
    const nextDistance = distance[index] + 1;
    if (nextDistance > maximumDistance) {
      continue;
    }

    const x = index % SOURCE_WIDTH;
    const y = Math.floor(index / SOURCE_WIDTH);
    for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
      const nextY = y + offsetY;
      if (nextY < 0 || nextY >= SOURCE_HEIGHT) {
        continue;
      }
      for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
        if (offsetX === 0 && offsetY === 0) {
          continue;
        }
        const nextX = x + offsetX;
        if (nextX < 0 || nextX >= SOURCE_WIDTH) {
          continue;
        }
        const nextIndex = (nextY * SOURCE_WIDTH) + nextX;
        if (distance[nextIndex] <= nextDistance) {
          continue;
        }
        distance[nextIndex] = nextDistance;
        queue[queueTail] = nextIndex;
        queueTail += 1;
      }
    }
  }

  return distance;
}

function staticTorsoOverlapExposureMetrics(staticIndices, movingMask) {
  const movingDistance = distanceFromMask(
    movingMask,
    TORSO_ARM_STATIC_EXPOSURE_DILATION + 1,
  );
  let uncoveredStaticOverlapPixels = 0;
  let exposedStaticOverlapPixels = 0;

  for (const index of staticIndices) {
    if (movingDistance[index] > 0) {
      uncoveredStaticOverlapPixels += 1;
    }
    if (movingDistance[index] > TORSO_ARM_STATIC_EXPOSURE_DILATION) {
      exposedStaticOverlapPixels += 1;
    }
  }

  return {
    exposedStaticOverlapPixels,
    uncoveredStaticOverlapPixels,
  };
}

function torsoArmStaticExposureMetrics(metric, movingMask) {
  return staticTorsoOverlapExposureMetrics(
    [
      ...metric.backingIndices,
      ...metric.duplicateIndices,
    ],
    movingMask,
  );
}

function largestMaskComponent(mask) {
  const visited = new Uint8Array(mask.length);
  const queue = new Int32Array(mask.length);
  let largest = 0;

  for (let startIndex = 0; startIndex < mask.length; startIndex += 1) {
    if (mask[startIndex] === 0 || visited[startIndex] !== 0) {
      continue;
    }

    let size = 0;
    let queueHead = 0;
    let queueTail = 1;
    queue[0] = startIndex;
    visited[startIndex] = 1;

    while (queueHead < queueTail) {
      const index = queue[queueHead];
      queueHead += 1;
      size += 1;
      const x = index % SOURCE_WIDTH;
      const y = Math.floor(index / SOURCE_WIDTH);

      for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
        const nextY = y + offsetY;
        if (nextY < 0 || nextY >= SOURCE_HEIGHT) {
          continue;
        }
        for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
          if (offsetX === 0 && offsetY === 0) {
            continue;
          }
          const nextX = x + offsetX;
          if (nextX < 0 || nextX >= SOURCE_WIDTH) {
            continue;
          }
          const nextIndex = (nextY * SOURCE_WIDTH) + nextX;
          if (mask[nextIndex] === 0 || visited[nextIndex] !== 0) {
            continue;
          }
          visited[nextIndex] = 1;
          queue[queueTail] = nextIndex;
          queueTail += 1;
        }
      }
    }

    largest = Math.max(largest, size);
  }

  return largest;
}

function darkMatteRingMetrics(rgba) {
  const alpha = alphaPlaneFromRgba(rgba);
  const exterior = backgroundConnectedTransparency(alpha);
  const exteriorDistance = distanceFromMask(exterior, DARK_MATTE_RING_RADIUS);
  const trustedForeground = new Uint8Array(alpha.length);
  let partialAlphaPixels = 0;
  let visiblePixels = 0;

  for (let index = 0; index < alpha.length; index += 1) {
    const offset = index * 4;
    const red = rgba[offset];
    const green = rgba[offset + 1];
    const blue = rgba[offset + 2];
    const pixelAlpha = alpha[index];
    if (pixelAlpha > 0) {
      visiblePixels += 1;
    }
    if (pixelAlpha > 0 && pixelAlpha < 255) {
      partialAlphaPixels += 1;
    }

    const luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
    const chroma = Math.max(red, green, blue) - Math.min(red, green, blue);
    if (
      pixelAlpha >= 180
      && (luminance >= 120 || chroma >= 45)
    ) {
      trustedForeground[index] = 1;
    }
  }

  const trustedDistance = distanceFromMask(
    trustedForeground,
    DARK_MATTE_TRUSTED_DILATION + 1,
  );
  const results = BRIGHT_MATTE_BACKGROUNDS.map(({ name, rgb }) => ({
    background: name,
    broadPixels: 0,
    candidateMask: new Uint8Array(alpha.length),
    ringPixels: 0,
  }));

  for (let index = 0; index < alpha.length; index += 1) {
    const pixelAlpha = alpha[index];
    const distanceToExterior = exteriorDistance[index];
    if (
      pixelAlpha < 180
      || distanceToExterior < 1
      || distanceToExterior > DARK_MATTE_RING_RADIUS
      || trustedDistance[index] <= DARK_MATTE_TRUSTED_DILATION
    ) {
      continue;
    }

    const offset = index * 4;
    const red = rgba[offset];
    const green = rgba[offset + 1];
    const blue = rgba[offset + 2];
    const luminance = (red * 0.2126) + (green * 0.7152) + (blue * 0.0722);
    if (luminance > 115) {
      continue;
    }

    const normalizedAlpha = pixelAlpha / 255;
    for (const [resultIndex, { rgb }] of BRIGHT_MATTE_BACKGROUNDS.entries()) {
      const composite = [
        (red * normalizedAlpha) + (rgb[0] * (1 - normalizedAlpha)),
        (green * normalizedAlpha) + (rgb[1] * (1 - normalizedAlpha)),
        (blue * normalizedAlpha) + (rgb[2] * (1 - normalizedAlpha)),
      ];
      const contrast = Math.hypot(
        composite[0] - rgb[0],
        composite[1] - rgb[1],
        composite[2] - rgb[2],
      );
      if (contrast < 120) {
        continue;
      }

      const result = results[resultIndex];
      result.ringPixels += 1;
      result.candidateMask[index] = 1;
      if (distanceToExterior >= 4) {
        result.broadPixels += 1;
      }
    }
  }

  return {
    backgrounds: results.map(({ background, broadPixels, candidateMask, ringPixels }) => ({
      background,
      broadPixels,
      largestComponentPixels: largestMaskComponent(candidateMask),
      ringPixels,
    })),
    partialAlphaPixels,
    visiblePixels,
  };
}

function underarmWedgeMetrics(composite, side) {
  const armBitMask = RASTER_ROLE_BITS.get(`${side}Arm`);
  const torsoBit = RASTER_ROLE_BITS.get('torsoHead');
  const rows = [];
  let gapPixels = 0;
  let gapRows = 0;
  let missingEdgeRows = 0;
  let maximumGapWidth = 0;

  for (let y = 240; y <= 520; y += 1) {
    let armInnerEdge = side === 'left' ? -1 : SOURCE_WIDTH;
    let torsoOuterEdge = side === 'left' ? SOURCE_WIDTH : -1;

    const xMin = side === 'left' ? 260 : 448;
    const xMax = side === 'left' ? 448 : 640;
    for (let x = xMin; x <= xMax; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      if (composite.alpha[index] < 32) {
        continue;
      }
      const bits = composite.strongRoleBits[index];
      if ((bits & armBitMask) !== 0) {
        armInnerEdge = side === 'left'
          ? Math.max(armInnerEdge, x)
          : Math.min(armInnerEdge, x);
      }
      if ((bits & torsoBit) !== 0) {
        torsoOuterEdge = side === 'left'
          ? Math.min(torsoOuterEdge, x)
          : Math.max(torsoOuterEdge, x);
      }
    }

    const hasArmEdge = side === 'left'
      ? armInnerEdge >= 0
      : armInnerEdge < SOURCE_WIDTH;
    const hasTorsoEdge = side === 'left'
      ? torsoOuterEdge < SOURCE_WIDTH
      : torsoOuterEdge >= 0;
    rows.push({
      armInnerEdge,
      hasArmEdge,
      hasTorsoEdge,
      torsoOuterEdge,
      xMax,
      xMin,
      y,
    });
  }

  const fullyBoundedRows = rows
    .map((row, index) => ({ index, row }))
    .filter(({ row }) => row.hasArmEdge && row.hasTorsoEdge);
  if (fullyBoundedRows.length === 0) {
    return {
      gapPixels: SOURCE_WIDTH * SOURCE_HEIGHT,
      gapRows: rows.length,
      maximumGapWidth: SOURCE_WIDTH,
      missingEdgeRows: rows.length,
    };
  }

  const firstBoundedIndex = fullyBoundedRows[0].index;
  const lastBoundedIndex = fullyBoundedRows.at(-1).index;
  for (const row of rows.slice(firstBoundedIndex, lastBoundedIndex + 1)) {
    const {
      armInnerEdge,
      hasArmEdge,
      hasTorsoEdge,
      torsoOuterEdge,
      xMax,
      xMin,
      y,
    } = row;
    if (!hasArmEdge || !hasTorsoEdge) {
      missingEdgeRows += 1;
      const missingWidth = xMax - xMin + 1;
      gapPixels += missingWidth;
      gapRows += 1;
      maximumGapWidth = Math.max(maximumGapWidth, missingWidth);
      continue;
    }

    const gapStart = side === 'left' ? armInnerEdge + 1 : torsoOuterEdge + 1;
    const gapEnd = side === 'left' ? torsoOuterEdge - 1 : armInnerEdge - 1;
    if (gapStart > gapEnd) {
      continue;
    }

    let transparentInGap = 0;
    for (let x = gapStart; x <= gapEnd; x += 1) {
      if (composite.alpha[(y * SOURCE_WIDTH) + x] < 32) {
        transparentInGap += 1;
      }
    }
    if (transparentInGap === 0) {
      continue;
    }

    gapPixels += transparentInGap;
    gapRows += 1;
    maximumGapWidth = Math.max(maximumGapWidth, transparentInGap);
  }

  return {
    gapPixels,
    gapRows,
    maximumGapWidth,
    missingEdgeRows,
  };
}

function countRoleBridgePixels(composite, childBit, parentBit, alphaThreshold) {
  const roleBits = alphaThreshold === 1
    ? composite.anyRoleBits
    : composite.strongRoleBits;
  let bridgePixels = 0;

  for (let y = composite.yMin; y <= composite.yMax; y += 1) {
    for (let x = composite.xMin; x <= composite.xMax; x += 1) {
      const index = (y * SOURCE_WIDTH) + x;
      if ((roleBits[index] & childBit) === 0) {
        continue;
      }

      let touchesParent = false;
      for (let offsetY = -1; offsetY <= 1 && !touchesParent; offsetY += 1) {
        const neighborY = y + offsetY;
        if (neighborY < 0 || neighborY >= SOURCE_HEIGHT) {
          continue;
        }
        for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
          const neighborX = x + offsetX;
          if (neighborX < 0 || neighborX >= SOURCE_WIDTH) {
            continue;
          }
          const neighborIndex = (neighborY * SOURCE_WIDTH) + neighborX;
          if ((roleBits[neighborIndex] & parentBit) !== 0) {
            touchesParent = true;
            break;
          }
        }
      }
      if (touchesParent) {
        bridgePixels += 1;
      }
    }
  }

  return bridgePixels;
}

function bridgeStrengthFailures(minimums) {
  return minimums
    .map((minimum) => {
      const edge = `${minimum.childRole}->${minimum.parentRole}`;
      const minimumBridgePixels = DENSE_BRIDGE_PIXEL_FLOORS[minimum.scenario]?.[edge];
      assert.ok(
        Number.isInteger(minimumBridgePixels) && minimumBridgePixels > 1,
        `missing resolution-resistant bridge floor for ${minimum.scenario} ${edge}`,
      );
      return {
        ...minimum,
        edge,
        minimumBridgePixels,
      };
    })
    .filter(({ bridgePixels, minimumBridgePixels }) => (
      bridgePixels < minimumBridgePixels
    ));
}

function createSinglePixelShoulderTetherComposite() {
  const pixelCount = SOURCE_WIDTH * SOURCE_HEIGHT;
  const alpha = new Uint8Array(pixelCount);
  const anyRoleBits = new Uint16Array(pixelCount);
  const strongRoleBits = new Uint16Array(pixelCount);

  function fillRoleRectangle(role, xMin, yMin, xMax, yMax) {
    const roleBit = RASTER_ROLE_BITS.get(role);
    for (let y = yMin; y <= yMax; y += 1) {
      for (let x = xMin; x <= xMax; x += 1) {
        const index = (y * SOURCE_WIDTH) + x;
        alpha[index] = 255;
        anyRoleBits[index] |= roleBit;
        strongRoleBits[index] |= roleBit;
      }
    }
  }

  fillRoleRectangle('torsoHead', 100, 100, 103, 103);
  fillRoleRectangle('leftArm', 104, 104, 107, 107);

  return {
    alpha,
    anyRoleBits,
    strongRoleBits,
    xMax: 107,
    xMin: 100,
    yMax: 107,
    yMin: 100,
  };
}

function createOversizedUnderarmGapComposite() {
  const pixelCount = SOURCE_WIDTH * SOURCE_HEIGHT;
  const alpha = new Uint8Array(pixelCount);
  const anyRoleBits = new Uint16Array(pixelCount);
  const strongRoleBits = new Uint16Array(pixelCount);

  function paint(role, xMin, yMin, xMax, yMax) {
    const roleBit = RASTER_ROLE_BITS.get(role);
    for (let y = yMin; y <= yMax; y += 1) {
      for (let x = xMin; x <= xMax; x += 1) {
        const index = (y * SOURCE_WIDTH) + x;
        alpha[index] = 255;
        anyRoleBits[index] |= roleBit;
        strongRoleBits[index] |= roleBit;
      }
    }
  }

  paint('torsoHead', 420, 240, 520, 520);
  paint('leftArm', 250, 240, 330, 520);
  paint('leftArm', 331, 240, 420, 250);

  return {
    alpha,
    anyRoleBits,
    strongRoleBits,
    xMax: 520,
    xMin: 250,
    yMax: 520,
    yMin: 240,
  };
}

function createFragmentedLimbComposite() {
  const pixelCount = SOURCE_WIDTH * SOURCE_HEIGHT;
  const alpha = new Uint8Array(pixelCount);
  const anyRoleBits = new Uint16Array(pixelCount);
  const strongRoleBits = new Uint16Array(pixelCount);

  function paint(role, xMin, yMin, xMax, yMax) {
    const roleBit = RASTER_ROLE_BITS.get(role);
    for (let y = yMin; y <= yMax; y += 1) {
      for (let x = xMin; x <= xMax; x += 1) {
        const index = (y * SOURCE_WIDTH) + x;
        alpha[index] = 255;
        anyRoleBits[index] |= roleBit;
        strongRoleBits[index] |= roleBit;
      }
    }
  }

  paint('torsoHead', 100, 100, 199, 199);
  paint('leftArm', 200, 100, 208, 199);
  for (let fragment = 0; fragment < 10; fragment += 1) {
    const x = 300 + ((fragment % 5) * 20);
    const y = 100 + (Math.floor(fragment / 5) * 30);
    paint('leftArm', x, y, x + 9, y + 9);
  }

  return {
    alpha,
    anyRoleBits,
    strongRoleBits,
    xMax: 389,
    xMin: 100,
    yMax: 199,
    yMin: 100,
  };
}

function posedComponentMetrics(composite, alphaThreshold) {
  const roleBits = alphaThreshold === 1
    ? composite.anyRoleBits
    : composite.strongRoleBits;
  const visited = new Uint8Array(composite.alpha.length);
  const queue = new Int32Array(composite.alpha.length);
  const components = [];
  const torsoBit = RASTER_ROLE_BITS.get('torsoHead');

  function isOccupied(index) {
    return composite.alpha[index] >= alphaThreshold;
  }

  for (let y = composite.yMin; y <= composite.yMax; y += 1) {
    for (let x = composite.xMin; x <= composite.xMax; x += 1) {
      const startIndex = (y * SOURCE_WIDTH) + x;
      if (visited[startIndex] !== 0 || !isOccupied(startIndex)) {
        continue;
      }

      const component = {
        size: 0,
        subtreePixels: Object.fromEntries(
          LIMB_SUBTREES.map(({ name }) => [name, 0]),
        ),
        torsoPixels: 0,
      };
      let queueHead = 0;
      let queueTail = 1;
      queue[0] = startIndex;
      visited[startIndex] = 1;

      while (queueHead < queueTail) {
        const index = queue[queueHead];
        queueHead += 1;
        const currentX = index % SOURCE_WIDTH;
        const currentY = Math.floor(index / SOURCE_WIDTH);
        const bits = roleBits[index];
        component.size += 1;
        if ((bits & torsoBit) !== 0) {
          component.torsoPixels += 1;
        }
        for (const subtree of LIMB_SUBTREES) {
          const subtreeBitMask = SUBTREE_ROLE_BITS.get(subtree.name);
          if ((bits & subtreeBitMask) !== 0) {
            component.subtreePixels[subtree.name] += 1;
          }
        }

        for (let offsetY = -1; offsetY <= 1; offsetY += 1) {
          const neighborY = currentY + offsetY;
          if (neighborY < 0 || neighborY >= SOURCE_HEIGHT) {
            continue;
          }
          for (let offsetX = -1; offsetX <= 1; offsetX += 1) {
            if (offsetX === 0 && offsetY === 0) {
              continue;
            }
            const neighborX = currentX + offsetX;
            if (neighborX < 0 || neighborX >= SOURCE_WIDTH) {
              continue;
            }
            const neighborIndex = (neighborY * SOURCE_WIDTH) + neighborX;
            if (visited[neighborIndex] !== 0 || !isOccupied(neighborIndex)) {
              continue;
            }
            visited[neighborIndex] = 1;
            queue[queueTail] = neighborIndex;
            queueTail += 1;
          }
        }
      }

      components.push(component);
    }
  }

  const mainComponent = components.reduce(
    (main, component) => (
      !main || component.torsoPixels > main.torsoPixels
        ? component
        : main
    ),
    null,
  );
  assert.ok(mainComponent?.torsoPixels > 0, 'posed raster must have a torso component');

  return Object.fromEntries(LIMB_SUBTREES.map((subtree) => {
    const totalSubtreePixels = components.reduce(
      (sum, component) => sum + component.subtreePixels[subtree.name],
      0,
    );
    const connectedSubtreePixels = mainComponent.subtreePixels[subtree.name];
    const detachedSubtreePixels = totalSubtreePixels - connectedSubtreePixels;
    const largestDetachedComponentPixels = Math.max(
      0,
      ...components
        .filter((component) => component !== mainComponent)
        .map((component) => component.subtreePixels[subtree.name]),
    );

    return [subtree.name, {
      connectedFraction: totalSubtreePixels === 0
        ? 0
        : connectedSubtreePixels / totalSubtreePixels,
      connectedSubtreePixels,
      detachedSubtreePixels,
      largestDetachedComponentPixels,
      mainComponentPixels: mainComponent.size,
      totalSubtreePixels,
    }];
  }));
}

function poseSignature(pose) {
  const properties = [
    ...FIVE_LAYER_CONTRACT.map(({ role }) => `${role}RotateDeg`),
    'bodyLeanDeg',
    'headTiltDeg',
    ...OBSOLETE_LAYER_ROLES.map((role) => `${role}RotateDeg`),
  ];
  return properties
    .filter((property) => Number.isFinite(pose[property]))
    .map((property) => `${property}:${Number(pose[property]).toFixed(9)}`)
    .join('|');
}

function denseProductionPoseSamples() {
  const allSamples = [];

  for (let index = 0; index < DENSE_GAIT_PHASE_SAMPLES; index += 1) {
    const gaitPhase = index / DENSE_GAIT_PHASE_SAMPLES;
    allSamples.push({
      label: `gait:${gaitPhase.toFixed(6)}`,
      pose: computePoseKinematics({
        gaitPhase,
        speedNormalized: 1,
        velocityX: 1,
      }).cssVariables,
      scenario: 'gait',
    });
  }

  for (let index = 0; index <= DENSE_CLAP_PROGRESS_SAMPLES; index += 1) {
    const clapProgress = index / DENSE_CLAP_PROGRESS_SAMPLES;
    allSamples.push({
      label: `clap:${clapProgress.toFixed(6)}`,
      pose: computePoseKinematics({
        clapping: true,
        clapProgress,
      }).cssVariables,
      scenario: 'clap',
    });
  }

  const uniqueSamples = new Map();
  for (const sample of allSamples) {
    const key = `${sample.scenario}|${poseSignature(sample.pose)}`;
    if (!uniqueSamples.has(key)) {
      uniqueSamples.set(key, sample);
    }
  }
  return {
    allSampleCount: allSamples.length,
    samples: [...uniqueSamples.values()],
  };
}

function request(pathname) {
  return new Promise((resolve, reject) => {
    const target = new URL(pathname, origin);
    const requestInstance = http.get(target, (response) => {
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => resolve({
        statusCode: response.statusCode,
        headers: response.headers,
        body: Buffer.concat(chunks),
      }));
    });
    requestInstance.on('error', reject);
  });
}

before(async () => {
  server = createStaticServer({ root: projectRoot });
  server.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  if (server?.listening) {
    server.close();
    await new Promise((resolve) => server.once('close', resolve));
  }
});

describe('illustrated avatar source and generator contracts', () => {
  it('pins Avatar.jpg as the sole 896x1195 illustrated source', () => {
    const sourceBuffer = readFileSync(avatarSourcePath);
    assert.equal(sha256(sourceBuffer), SOURCE_SHA256);
    assert.deepEqual(inspectJpegDimensions(sourceBuffer), {
      width: SOURCE_WIDTH,
      height: SOURCE_HEIGHT,
    });
  });

  it('five-layer: generator verifies six deterministic PNGs without writing', () => {
    const generatorPath = findGeneratorPath();
    assert.ok(generatorPath, 'an avatar asset generator .mjs file must exist under scripts or tools');
    const generatorSource = readFileSync(generatorPath, 'utf8');
    assert.match(generatorSource, /Avatar\.jpg/, 'generator must use Avatar.jpg as its source');
    assert.doesNotMatch(
      generatorSource,
      /Me\.jpg|avatar-face\.jpg/i,
      'generator must not use the private portrait or obsolete face derivative as character art',
    );
    assert.match(generatorSource, /--verify\b/, 'generator must implement an explicit --verify mode');

    const before = new Map(
      discoverNamedModule(path.join(assetsRoot, 'avatar'), /\.(?:json|png)$/i)
        .map((filePath) => [filePath, fileSnapshot(filePath)]),
    );
    const result = spawnSync(process.execPath, [generatorPath, '--verify'], {
      cwd: projectRoot,
      encoding: 'utf8',
    });
    assert.equal(
      result.status,
      0,
      `generator verify mode must succeed without rewriting assets\nstdout=${result.stdout}\nstderr=${result.stderr}`,
    );
    const after = new Map(
      discoverNamedModule(path.join(assetsRoot, 'avatar'), /\.(?:json|png)$/i)
        .map((filePath) => [filePath, fileSnapshot(filePath)]),
    );
    assert.deepEqual(
      after,
      before,
      '--verify must not create, rewrite, touch, or remove generated PNGs or private manifest v2',
    );
    assert.match(
      result.stdout,
      /Verified 6 deterministic avatar PNGs and manifest/,
      `five-layer verification must report idle plus five rigid layers; stdout=${result.stdout}`,
    );
  });
});

describe('registered raster rig contracts', () => {
  it('five-layer: manifest and runtime expose the exact frozen rigid rig contract', () => {
    const { manifest } = loadManifest();
    const source = manifestSource(manifest);
    const layers = manifest.layers;

    assert.equal(manifest.version, 2, 'the private avatar manifest must use schema version 2');
    assert.equal(normalizedRole(source.file), normalizedRole('Avatar.jpg'));
    assert.equal(String(source.sha256).toUpperCase(), SOURCE_SHA256);
    assert.equal(source.width, SOURCE_WIDTH);
    assert.equal(source.height, SOURCE_HEIGHT);
    assert.deepEqual(manifest.canvas, { width: SOURCE_WIDTH, height: SOURCE_HEIGHT });
    assert.equal(manifest.idleFallback?.url, './assets/avatar/idle.png');
    assert.ok(Array.isArray(layers), 'private avatar manifest v2 must contain a layers array');

    const manifestContract = layers.map((layer) => ({
      role: layer.role,
      parent: layer.parent,
      pivot: layer.pivot,
      zIndex: layer.zIndex,
      url: layer.url,
    }));
    assert.deepEqual(
      manifestContract,
      FIVE_LAYER_CONTRACT,
      'manifest v2 must register exactly the frozen five rigid layers in painter order',
    );

    for (const layer of layers) {
      const bounds = layerBounds(layer);
      const pivot = layerPivot(layer);
      assert.deepEqual(
        bounds,
        { x: 0, y: 0, width: SOURCE_WIDTH, height: SOURCE_HEIGHT },
        `${layer.role} must stay registered to the full source canvas`,
      );
      assert.ok(Number.isFinite(bounds.x) && bounds.x >= 0);
      assert.ok(Number.isFinite(bounds.y) && bounds.y >= 0);
      assert.ok(Number.isInteger(bounds.width) && bounds.width > 0);
      assert.ok(Number.isInteger(bounds.height) && bounds.height > 0);
      assert.ok(bounds.x + bounds.width <= SOURCE_WIDTH);
      assert.ok(bounds.y + bounds.height <= SOURCE_HEIGHT);
      assert.ok(Number.isFinite(pivot.x) && pivot.x >= 0 && pivot.x <= SOURCE_WIDTH);
      assert.ok(Number.isFinite(pivot.y) && pivot.y >= 0 && pivot.y <= SOURCE_HEIGHT);
      assert.ok(Number.isFinite(layer.zIndex ?? layer.order), `${layer.role ?? layer.id} must pin layer order`);
      localAssetPath(layerUrl(layer));
    }

    const runtimeContract = AVATAR_LAYERS.map((layer) => ({
      role: layer.role,
      parent: layer.parent,
      pivot: layer.pivot,
      zIndex: layer.zIndex,
      url: layer.url,
    }));
    assert.deepEqual(
      manifestContract,
      runtimeContract,
      'private manifest v2 and public src/avatar-rig.js must expose the same five-layer contract',
    );
    assert.deepEqual(
      layers.map((layer) => layer.role),
      PAINTER_ORDER,
      'manifest order must mirror the fixed nested DOM painter order',
    );
    assert.deepEqual(
      [...layers].sort((left, right) => left.zIndex - right.zIndex).map((layer) => layer.role),
      PAINTER_ORDER,
      'increasing zIndex must preserve the frozen five-layer painter order',
    );
  });

  it('five-layer: asset directory contains exactly idle plus the five registered PNGs', () => {
    const { manifest } = loadManifest();
    const generatedPngNames = readdirSync(path.join(assetsRoot, 'avatar'), { withFileTypes: true })
      .filter((entry) => entry.isFile() && /\.png$/i.test(entry.name))
      .map((entry) => entry.name)
      .sort();
    const manifestPngNames = [
      path.basename(manifest.idleFallback?.url ?? ''),
      ...(manifest.layers ?? []).map((layer) => path.basename(layerUrl(layer))),
    ].sort();

    assert.deepEqual(
      generatedPngNames,
      FIVE_LAYER_PNG_NAMES,
      'obsolete 14-part PNGs must be deleted rather than left beside the six frozen assets',
    );
    assert.deepEqual(
      manifestPngNames,
      FIVE_LAYER_PNG_NAMES,
      'manifest v2 must name exactly idle plus the five rigid PNGs',
    );
  });

  it('five-layer: pins deterministic transparent metadata-free PNGs and meaningful dark details', () => {
    const { manifest } = loadManifest();
    const layers = manifest.layers;
    assert.ok(Array.isArray(layers));
    const decoded = new Map();
    const assetEntries = [
      {
        role: 'idle',
        url: manifest.idleFallback?.url,
        sha256: manifest.idleFallback?.sha256,
        sourceRect: manifest.idleFallback?.sourceRect,
      },
      ...layers,
    ];

    for (const layer of assetEntries) {
      const filePath = localAssetPath(layerUrl(layer));
      assert.ok(existsSync(filePath), `${path.relative(projectRoot, filePath)} must be generated`);
      const buffer = readFileSync(filePath);
      assert.match(String(layer.sha256 ?? layer.hash ?? ''), /^[a-f0-9]{64}$/i);
      assert.equal(sha256(buffer), String(layer.sha256 ?? layer.hash).toUpperCase());

      const png = inspectPng(buffer);
      const bounds = layerBounds(layer);
      assert.equal(png.width, bounds.width, `${layer.role ?? layer.id} width must match registration`);
      assert.equal(png.height, bounds.height, `${layer.role ?? layer.id} height must match registration`);
      assert.equal(png.chunks[0], 'IHDR', `${layer.role ?? layer.id} must begin with IHDR`);
      assert.equal(png.chunks.at(-1), 'IEND', `${layer.role ?? layer.id} must end with IEND`);
      assert.equal(
        png.chunks.filter((chunk) => chunk === 'IHDR').length,
        1,
        `${layer.role ?? layer.id} must contain exactly one IHDR`,
      );
      assert.equal(
        png.chunks.filter((chunk) => chunk === 'IEND').length,
        1,
        `${layer.role ?? layer.id} must contain exactly one IEND`,
      );
      for (const chunk of png.chunks) {
        assert.ok(
          ALLOWED_PNG_CHUNKS.has(chunk),
          `${layer.role ?? layer.id} must contain only IHDR/IDAT/IEND; received ${png.chunks.join(',')}`,
        );
      }

      const stats = pixelStats(png);
      assert.equal(stats.minimumAlpha, 0, `${layer.role ?? layer.id} must contain transparent pixels`);
      assert.ok(stats.maximumAlpha >= 200, `${layer.role ?? layer.id} must contain visible artwork`);
      decoded.set(layer, { ...stats, png });
    }

    for (const requiredRole of ['torsoHead', 'leftLeg', 'rightLeg']) {
      const roleLayers = layersForCanonicalRole(layers, requiredRole);
      const darkOpaquePixels = roleLayers.reduce(
        (sum, layer) => sum + decoded.get(layer).darkOpaquePixels,
        0,
      );
      assert.ok(
        darkOpaquePixels >= 20,
        `${requiredRole} must preserve dark illustrated details instead of globally deleting dark pixels`,
      );
    }
  });

  it('five-layer: preserves source RGB for every fully opaque generated pixel', () => {
    const source = decodeAvatarSource();
    const { manifest } = loadManifest();
    const generated = [
      {
        label: 'idle',
        layer: manifest.idleFallback,
        stats: decodeAsset(localAssetPath(manifest.idleFallback.url)),
      },
      ...(manifest.layers ?? []).map((layer) => ({
        label: layer.role,
        layer,
        stats: decodeAsset(
          localAssetPath(layerUrl(layer)),
        ),
      })),
    ];
    const decoded = new Map(
      generated
        .filter(({ label }) => label !== 'idle')
        .map(({ label, stats }) => [label, stats]),
    );
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    const permittedBackingIndices = new Map(
      backingContext.backingDeclarations.map((declaration) => [
        declaration.name,
        new Set(),
      ]),
    );
    const metrics = generated.map(({ label, layer, stats }) => {
      const bounds = layerBounds(layer);
      let fullyOpaquePixels = 0;
      let changedPixels = 0;
      let permittedHiddenBackingPixels = 0;
      let sourceDifferences = 0;
      let totalBlueDelta = 0;
      let maximumBlueDelta = 0;

      for (let localY = 0; localY < bounds.height; localY += 1) {
        for (let localX = 0; localX < bounds.width; localX += 1) {
          const generatedPixel = assetPixel(stats, localX, localY);
          if (generatedPixel.alpha !== 255) {
            continue;
          }

          fullyOpaquePixels += 1;
          const originalPixel = sourcePixel(
            source,
            bounds.x + localX,
            bounds.y + localY,
          );
          if (sameRgb(generatedPixel, originalPixel)) {
            continue;
          }

          sourceDifferences += 1;
          if (label === 'torsoHead') {
            const backingDeclaration = hiddenTorsoBackingDeclaration(
              backingContext,
              generatedPixel,
              bounds.x + localX,
              bounds.y + localY,
            );
            if (backingDeclaration) {
              permittedHiddenBackingPixels += 1;
              permittedBackingIndices.get(backingDeclaration.name).add(
                ((bounds.y + localY) * SOURCE_WIDTH) + bounds.x + localX,
              );
              continue;
            }
          }

          changedPixels += 1;
          const blueDelta = Math.abs(generatedPixel.blue - originalPixel.blue);
          totalBlueDelta += blueDelta;
          maximumBlueDelta = Math.max(maximumBlueDelta, blueDelta);
        }
      }

      return {
        averageBlueDelta: changedPixels === 0
          ? 0
          : Number((totalBlueDelta / changedPixels).toFixed(2)),
        changedPixels,
        fullyOpaquePixels,
        label,
        maximumBlueDelta,
        permittedHiddenBackingPixels,
        sourceDifferences,
        sourceRect: bounds,
      };
    });
    const backingTopology = backingContext.backingDeclarations.map((declaration) => (
      hiddenBackingTopologyMetrics(
        declaration,
        permittedBackingIndices.get(declaration.name),
      )
    ));
    const failures = [
      ...metrics.filter((metric) => (
        metric.fullyOpaquePixels === 0 || metric.changedPixels !== 0
      )),
      ...hiddenBackingTopologyFailures(backingTopology),
    ];

    console.log(`FULL_ALPHA_SOURCE_FIDELITY_METRICS=${JSON.stringify({
      backingTopology,
      comparison:
        'fully opaque generated pixels only; partial-alpha decontamination edge excluded; '
        + 'torsoHead may differ only for localized source-derived garment backing hidden beneath '
        + 'an opaque manifest-registered whole arm',
      knownPixel: RIGHT_HAND_KNOWN_RECOLOR_PIXEL,
      rasters: metrics,
    })}`);
    assert.deepEqual(
      failures,
      [],
      'fully opaque visible artwork must retain Avatar.jpg RGB exactly; the sole exception is '
        + 'torsoHead-owned garment backing hidden beneath source-faithful opaque artwork from a '
        + 'manifest-registered whole arm, using an exact nearby source garment color within 36px, '
        + 'at most 2000 pixels per arm, at least 97% connected, and with no detached component '
        + 'larger than 16 pixels; '
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: rejects undeclared near-opaque source-divergent seam patches', () => {
    const source = decodeAvatarSource();
    const { manifest } = loadManifest();
    const generated = [
      {
        label: 'idle',
        stats: decodeAsset(localAssetPath(manifest.idleFallback.url)),
      },
      ...(manifest.layers ?? []).map((layer) => ({
        label: layer.role,
        stats: decodeAsset(localAssetPath(layer.url)),
      })),
    ];
    const decoded = new Map(
      generated
        .filter(({ label }) => label !== 'idle')
        .map(({ label, stats }) => [label, stats]),
    );
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    const permittedBackingIndices = new Map(
      backingContext.backingDeclarations.map((declaration) => [
        declaration.name,
        new Set(),
      ]),
    );
    const sourceInterior = sourceVisibleInteriorPopulation(
      source,
      SOURCE_INTERIOR_GEOMETRY,
    );
    const sourceInteriorMetrics = sourceInteriorRasterMetrics(
      source,
      generated.find(({ label }) => label === 'idle').stats,
      sourceInterior,
    );
    const geometryByIndex = sourceFidelityGeometryByIndex();
    const rasterMetrics = generated.map(({ label, stats }) => {
      const failureBounds = {
        xMax: Number.NEGATIVE_INFINITY,
        xMin: Number.POSITIVE_INFINITY,
        yMax: Number.NEGATIVE_INFINITY,
        yMin: Number.POSITIVE_INFINITY,
      };
      const failureAlphaValues = new Set();
      const regionFailures = Object.fromEntries(
        SOURCE_FIDELITY_GEOMETRY.map(({ name }) => [name, 0]),
      );
      let maximumChannelDelta = 0;
      let permittedHiddenBackingPixels = 0;
      let sourceDivergentNearOpaquePixels = 0;
      let undeclaredSourceDivergentPixels = 0;
      const sampleFailures = [];

      for (const [index, region] of geometryByIndex) {
        const x = index % SOURCE_WIDTH;
        const y = Math.floor(index / SOURCE_WIDTH);
        const generatedPixel = assetPixel(stats, x, y);
        const originalPixel = sourcePixel(source, x, y);
        if (
          generatedPixel.alpha < NEAR_OPAQUE_ALPHA_MINIMUM
          || sameRgb(generatedPixel, originalPixel)
        ) {
          continue;
        }

        sourceDivergentNearOpaquePixels += 1;
        if (label === 'torsoHead') {
          const declaration = hiddenTorsoBackingDeclaration(
            backingContext,
            generatedPixel,
            x,
            y,
          );
          if (declaration) {
            permittedHiddenBackingPixels += 1;
            permittedBackingIndices.get(declaration.name).add(index);
            continue;
          }
        }

        undeclaredSourceDivergentPixels += 1;
        regionFailures[region] += 1;
        failureAlphaValues.add(generatedPixel.alpha);
        failureBounds.xMin = Math.min(failureBounds.xMin, x);
        failureBounds.xMax = Math.max(failureBounds.xMax, x);
        failureBounds.yMin = Math.min(failureBounds.yMin, y);
        failureBounds.yMax = Math.max(failureBounds.yMax, y);
        maximumChannelDelta = Math.max(
          maximumChannelDelta,
          Math.abs(generatedPixel.red - originalPixel.red),
          Math.abs(generatedPixel.green - originalPixel.green),
          Math.abs(generatedPixel.blue - originalPixel.blue),
        );
        if (sampleFailures.length < 8) {
          sampleFailures.push({
            generated: generatedPixel,
            region,
            source: originalPixel,
            x,
            y,
          });
        }
      }

      return {
        failureAlphaValues: [...failureAlphaValues].sort((left, right) => left - right),
        failureBounds: undeclaredSourceDivergentPixels === 0 ? null : failureBounds,
        label,
        maximumChannelDelta,
        permittedHiddenBackingPixels,
        regionFailures,
        sampleFailures,
        sourceDivergentNearOpaquePixels,
        undeclaredSourceDivergentPixels,
      };
    });
    const backingTopology = backingContext.backingDeclarations.map((declaration) => (
      hiddenBackingTopologyMetrics(
        declaration,
        permittedBackingIndices.get(declaration.name),
      )
    ));
    const backingFailures = [
      ...backingTopology.filter(({ pixels }) => pixels === 0),
      ...hiddenBackingTopologyFailures(backingTopology),
    ];
    const failures = [
      ...(sourceInteriorMetrics.missingPixels === 0
        && sourceInteriorMetrics.recoloredPixels === 0
        && sourceInteriorMetrics.translucentPixels === 0
        ? []
        : [{ label: 'idle-source-interior', ...sourceInteriorMetrics }]),
      ...rasterMetrics.filter(
        ({ undeclaredSourceDivergentPixels }) => undeclaredSourceDivergentPixels !== 0,
      ),
      ...backingFailures.map((metric) => ({
        label: 'invalid-hidden-backing-population',
        ...metric,
      })),
    ];
    const metrics = {
      backingTopology,
      detector:
        `source-defined interiors require exact coverage/RGB/opacity at distance >=`
        + `${SOURCE_INTERIOR_MIN_EDGE_DISTANCE}; independently, generated alpha >=`
        + `${NEAR_OPAQUE_ALPHA_MINIMUM} may not diverge from Avatar.jpg`,
      rasters: rasterMetrics,
      sourceInterior: sourceInteriorMetrics,
    };

    console.log(`SOURCE_DEFINED_FIDELITY_METRICS=${JSON.stringify(metrics)}`);
    assert.ok(
      sourceInteriorMetrics.populationPixels >= 10_000,
      `source-defined fidelity population must remain meaningful; metrics=${JSON.stringify(metrics)}`,
    );
    assert.deepEqual(
      failures,
      [],
      'source-visible interiors defined from Avatar.jpg, semantic geometry, and source-edge '
        + 'distance must remain covered, fully opaque, and RGB-exact; independently, near-opaque '
        + 'source-divergent generated pixels are fidelity failures unless torsoHead owns a '
        + 'manifest-declared population hidden by an opaque source-faithful moving arm, localized '
        + 'to connected nearby source-derived non-hand garment artwork; '
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: rejects broad, synthetic, exposed, or non-torso backing exceptions', () => {
    const manifestLayers = FIVE_LAYER_CONTRACT.map((layer) => ({ ...layer }));
    const source = decodeAvatarSource();
    const garmentProbe = PROTECTED_FIXED_GARMENT_PROBES.find(
      ({ name }) => name === 'right-underarm-waistcoat',
    );
    const sourceGarmentPixels = [];
    for (let y = garmentProbe.rectangle.yMin; y <= garmentProbe.rectangle.yMax; y += 1) {
      for (let x = garmentProbe.rectangle.xMin; x <= garmentProbe.rectangle.xMax; x += 1) {
        const pixel = { ...sourcePixel(source, x, y), alpha: 255 };
        if (
          !isHandSkinPixel(pixel)
          && isProtectedGarmentPixel(pixel, 'garment')
        ) {
          sourceGarmentPixels.push({ pixel, x, y });
        }
      }
    }
    assert.ok(
      sourceGarmentPixels.length >= 1_000,
      'the source-derived garment-color positive control must retain a meaningful population',
    );
    const sourceGarmentPixel = sourceGarmentPixels[0];
    assert.equal(
      sourceHasNearbyGarmentColor(
        source,
        sourceGarmentPixel.pixel,
        sourceGarmentPixel.x,
        sourceGarmentPixel.y,
      ),
      true,
      'a non-hand source garment color must remain eligible for localized backing',
    );
    const ambiguousHandWhitePixels = [];
    for (
      let y = RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMin;
      y <= RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMax;
      y += 1
    ) {
      for (
        let x = RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMin;
        x <= RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMax;
        x += 1
      ) {
        const pixel = { ...sourcePixel(source, x, y), alpha: 255 };
        if (isHandSkinPixel(pixel) && isWhiteGarmentPixel(pixel)) {
          ambiguousHandWhitePixels.push({ pixel, x, y });
        }
      }
    }
    assert.ok(
      ambiguousHandWhitePixels.length >= 100,
      'the source hand/white-color ambiguity control must retain a meaningful pixel population',
    );
    const ambiguousHandPixel = ambiguousHandWhitePixels[0];
    assert.equal(
      sourceHasNearbyGarmentColor(
        source,
        ambiguousHandPixel.pixel,
        ambiguousHandPixel.x,
        ambiguousHandPixel.y,
      ),
      false,
      'skin-colored source pixels must not qualify as nearby white garment backing',
    );
    assert.deepEqual(
      manifestContinuityBackingDeclarations({ layers: manifestLayers }),
      [],
      'no hidden-backing exception exists unless the manifest declares one',
    );
    const validDeclaration = {
      maximumPixels: 200,
      name: 'left-axilla-sweep',
      occludedBy: 'leftArm',
      owner: 'torsoHead',
      sweptRegion: { xMin: 320, xMax: 359, yMin: 280, yMax: 299 },
    };
    assert.deepEqual(
      manifestContinuityBackingDeclarations({
        continuityBacking: [validDeclaration],
        layers: manifestLayers,
      }),
      [validDeclaration],
      'a bounded manifest declaration must retain its exact ownership and swept-region contract',
    );
    const declaredBackingContext = {
      backingDeclarations: manifestContinuityBackingDeclarations({
        continuityBacking: [validDeclaration],
        layers: manifestLayers,
      }),
    };
    assert.deepEqual(
      continuityBackingDeclarationAt(declaredBackingContext, 'leftArm', 330, 290),
      validDeclaration,
      'source-faithful continuity overlap is backing only inside its declared arm swept region',
    );
    assert.equal(
      continuityBackingDeclarationAt({ backingDeclarations: [] }, 'leftArm', 330, 290),
      null,
      'undeclared source-faithful torso/arm overlap must remain duplicate trivia, not backing',
    );
    assert.equal(
      continuityBackingDeclarationAt(declaredBackingContext, 'leftArm', 400, 290),
      null,
      'source-faithful torso/arm overlap outside the declared swept region must not become backing',
    );
    assert.equal(
      continuityBackingDeclarationAt(declaredBackingContext, 'rightArm', 330, 290),
      null,
      'a swept region declared for one arm must not authorize backing beneath the other arm',
    );
    assert.throws(
      () => manifestContinuityBackingDeclarations({
        continuityBacking: [{
          ...validDeclaration,
          sweptRegion: { xMin: 0, xMax: SOURCE_WIDTH, yMin: 0, yMax: 0 },
        }],
        layers: manifestLayers,
      }),
      /sweptRegion must stay inside the source canvas/,
      'an unbounded backing region must be rejected',
    );
    assert.throws(
      () => manifestContinuityBackingDeclarations({
        continuityBacking: [{
          ...validDeclaration,
          sweptRegion: { xMin: 300, xMax: 399, yMin: 250, yMax: 349 },
        }],
        layers: manifestLayers,
      }),
      /sweptRegion area must stay within four times its pixel budget/,
      'an in-canvas swept region must not become a broad unlimited exception',
    );
    assert.throws(
      () => manifestContinuityBackingDeclarations({
        continuityBacking: [{
          ...validDeclaration,
          maximumPixels: ARM_BACKING_MAX_VISIBLE_PIXELS + 1,
        }],
        layers: manifestLayers,
      }),
      /maximumPixels/,
      'a manifest declaration must not exceed the frozen 2000-pixel area cap',
    );
    assert.throws(
      () => manifestContinuityBackingDeclarations({
        continuityBacking: [{
          ...validDeclaration,
          owner: 'leftArm',
        }],
        layers: manifestLayers,
      }),
      /backing must be torsoHead-owned/,
      'a moving-layer declaration must not authorize source-divergent backing',
    );
    assert.throws(
      () => manifestContinuityBackingDeclarations({
        continuityBacking: [{
          ...validDeclaration,
          occludedBy: 'leftLeg',
        }],
        layers: manifestLayers,
      }),
      /must be occluded by a whole-arm layer/,
      'a non-arm occluder must not authorize hidden torso backing',
    );

    const valid = {
      declaredRegion: true,
      differsFromSource: true,
      generatedRole: 'torsoHead',
      occluderOpaque: true,
      occluderSourceFaithful: true,
      occludingRole: 'leftArm',
      sourceDerivedGarmentColor: true,
      torsoDistance: ARM_BACKING_MAX_TORSO_DISTANCE,
    };
    assert.deepEqual(
      hiddenTorsoBackingObservationFailures(valid),
      [],
      'negative controls require a valid bounded hidden-backing observation',
    );

    for (const [property, value, reason] of [
      ['declaredRegion', false, 'backing is outside a manifest-declared swept region'],
      ['generatedRole', 'leftArm', 'backing is not torsoHead-owned'],
      ['occludingRole', 'leftLeg', 'occluder is not a manifest-declared whole arm'],
      ['occluderOpaque', false, 'source coordinate is not hidden by opaque source-faithful arm artwork'],
      ['occluderSourceFaithful', false, 'source coordinate is not hidden by opaque source-faithful arm artwork'],
      ['torsoDistance', ARM_BACKING_MAX_TORSO_DISTANCE + 1, 'backing is not localized to source-faithful torso artwork'],
      ['sourceDerivedGarmentColor', false, 'backing color is not derived from nearby source garment artwork'],
      ['differsFromSource', false, 'pixel is source-faithful overlap rather than the hidden-backing exception'],
    ]) {
      const failures = hiddenTorsoBackingObservationFailures({
        ...valid,
        [property]: value,
      });
      assert.ok(
        failures.includes(reason),
        `${property} negative control must be rejected; failures=${JSON.stringify(failures)}`,
      );
    }

    for (const metric of [
      {
        connectedFraction: 1,
        largestDetachedComponentPixels: 0,
        maximumPixels: ARM_BACKING_MAX_VISIBLE_PIXELS,
        pixels: ARM_BACKING_MAX_VISIBLE_PIXELS + 1,
        role: 'leftArm',
      },
      {
        connectedFraction: ARM_BACKING_MIN_CONNECTED_FRACTION - 0.01,
        largestDetachedComponentPixels: 0,
        maximumPixels: ARM_BACKING_MAX_VISIBLE_PIXELS,
        pixels: 100,
        role: 'leftArm',
      },
      {
        connectedFraction: 0.99,
        largestDetachedComponentPixels:
          ARM_BACKING_MAX_DETACHED_COMPONENT_PIXELS + 1,
        maximumPixels: ARM_BACKING_MAX_VISIBLE_PIXELS,
        pixels: 1_000,
        role: 'leftArm',
      },
    ]) {
      assert.deepEqual(
        hiddenBackingTopologyFailures([metric]),
        [metric],
        `backing topology negative control must be rejected; metric=${JSON.stringify(metric)}`,
      );
    }

    const exposedBackingDeclaration = {
      maximumPixels: 300,
      name: 'left-axilla-exposure-control',
      occludedBy: 'leftArm',
      owner: 'torsoHead',
      sweptRegion: { xMin: 320, xMax: 339, yMin: 280, yMax: 294 },
    };
    const [validatedExposedBacking] = manifestContinuityBackingDeclarations({
      continuityBacking: [exposedBackingDeclaration],
      layers: manifestLayers,
    });
    const exposedBackingIndices = new Set();
    const hiddenArmMask = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
    const displacedArmMask = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
    for (
      let y = exposedBackingDeclaration.sweptRegion.yMin;
      y <= exposedBackingDeclaration.sweptRegion.yMax;
      y += 1
    ) {
      for (
        let x = exposedBackingDeclaration.sweptRegion.xMin;
        x <= exposedBackingDeclaration.sweptRegion.xMax;
        x += 1
      ) {
        const index = (y * SOURCE_WIDTH) + x;
        exposedBackingIndices.add(index);
        hiddenArmMask[index] = 1;
        displacedArmMask[(y * SOURCE_WIDTH) + x + 40] = 1;
      }
    }
    assert.deepEqual(
      hiddenBackingTopologyFailures([
        hiddenBackingTopologyMetrics(
          validatedExposedBacking,
          exposedBackingIndices,
        ),
      ]),
      [],
      'the exposed-remnant negative fixture must otherwise satisfy the backing topology contract',
    );
    const declaredBackingMetric = {
      backingIndices: Uint32Array.from(exposedBackingIndices),
      duplicateIndices: new Uint32Array(),
    };
    assert.deepEqual(
      torsoArmStaticExposureMetrics(declaredBackingMetric, hiddenArmMask),
      {
        exposedStaticOverlapPixels: 0,
        uncoveredStaticOverlapPixels: 0,
      },
      'declared backing fully hidden by its arm must not be reported as an exposed remnant',
    );
    const exposedBackingMetrics = torsoArmStaticExposureMetrics(
      declaredBackingMetric,
      displacedArmMask,
    );
    assert.deepEqual(
      exposedBackingMetrics,
      {
        exposedStaticOverlapPixels: 300,
        uncoveredStaticOverlapPixels: 300,
      },
      'the dense exposure integration must retain all 300 declared backing pixels after displacement',
    );
    assert.ok(
      exposedBackingMetrics.exposedStaticOverlapPixels
        > TORSO_ARM_STATIC_EXPOSURE_MAX_PIXELS,
      'otherwise-valid declared backing exposed by arm articulation must exceed the frozen remnant cap',
    );
  });

  it('five-layer: keeps source-opaque right-hand interiors opaque and source-faithful', () => {
    const source = decodeAvatarSource();
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const rightArmLayer = manifest.layers.find(({ role }) => role === 'rightArm');
    const torsoHeadLayer = manifest.layers.find(({ role }) => role === 'torsoHead');
    assert.ok(rightArmLayer, 'manifest must contain rightArm');
    assert.ok(torsoHeadLayer, 'manifest must contain torsoHead');
    const rightArm = decodeAsset(localAssetPath(rightArmLayer.url));
    const torsoHead = decodeAsset(localAssetPath(torsoHeadLayer.url));
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        layer.role === 'rightArm'
          ? rightArm
          : layer.role === 'torsoHead'
            ? torsoHead
            : decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    let protectedPixels = 0;
    let idleOpacityFailures = 0;
    let rightArmOpacityFailures = 0;
    let permittedTorsoGarmentBackingPixels = 0;
    let torsoHandDuplicationPixels = 0;
    let invalidTorsoBackingPixels = 0;

    for (
      let y = RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMin;
      y <= RIGHT_HAND_INTERIOR_SOURCE_PROBE.yMax;
      y += 1
    ) {
      for (
        let x = RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMin;
        x <= RIGHT_HAND_INTERIOR_SOURCE_PROBE.xMax;
        x += 1
      ) {
        const originalPixel = sourcePixel(source, x, y);
        const idlePixel = assetPixel(idle, x, y);
        const rightArmPixel = assetPixel(rightArm, x, y);
        if (
          originalPixel.alpha !== 255
          || idlePixel.alpha === 0
          || rightArmPixel.alpha === 0
          || !sameRgb(rightArmPixel, originalPixel)
          || !sameRgb(idlePixel, originalPixel)
        ) {
          continue;
        }

        protectedPixels += 1;
        if (idlePixel.alpha !== 255) {
          idleOpacityFailures += 1;
        }
        if (rightArmPixel.alpha !== 255) {
          rightArmOpacityFailures += 1;
        }
        const torsoHeadPixel = assetPixel(torsoHead, x, y);
        if (torsoHeadPixel.alpha > 0) {
          if (isHandSkinPixel(torsoHeadPixel)) {
            torsoHandDuplicationPixels += 1;
          } else if (
            hiddenTorsoBackingDeclaration(
              backingContext,
              torsoHeadPixel,
              x,
              y,
            )?.occludedBy === 'rightArm'
          ) {
            permittedTorsoGarmentBackingPixels += 1;
          } else {
            invalidTorsoBackingPixels += 1;
          }
        }
      }
    }

    const metrics = {
      idleOpacityFailures,
      invalidTorsoBackingPixels,
      permittedTorsoGarmentBackingPixels,
      protectedPixels,
      region: RIGHT_HAND_INTERIOR_SOURCE_PROBE,
      rightArmOpacityFailures,
      torsoHandDuplicationPixels,
    };
    console.log(`RIGHT_HAND_LEGACY_PREFILTER_METRICS=${JSON.stringify(metrics)}`);
    assert.deepEqual(
      {
        idleOpacityFailures,
        invalidTorsoBackingPixels,
        rightArmOpacityFailures,
        torsoHandDuplicationPixels,
      },
      {
        idleOpacityFailures: 0,
        invalidTorsoBackingPixels: 0,
        rightArmOpacityFailures: 0,
        torsoHandDuplicationPixels: 0,
      },
      'the historical generated-success-prefiltered right-hand contract must remain green while '
        + 'the independent source-first regression demonstrates its missing coverage; '
        + `metrics=${JSON.stringify(metrics)}`,
    );
  });

  it('five-layer: requires source-defined right-hand completeness before generated inspection', () => {
    const source = decodeAvatarSource();
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const rightArmLayer = manifest.layers.find(({ role }) => role === 'rightArm');
    const torsoHeadLayer = manifest.layers.find(({ role }) => role === 'torsoHead');
    assert.ok(rightArmLayer, 'manifest must contain rightArm');
    assert.ok(torsoHeadLayer, 'manifest must contain torsoHead');
    const rightArm = decodeAsset(localAssetPath(rightArmLayer.url));
    const torsoHead = decodeAsset(localAssetPath(torsoHeadLayer.url));
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        layer.role === 'rightArm'
          ? rightArm
          : layer.role === 'torsoHead'
            ? torsoHead
            : decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    const sourceHand = sourceDefinedRightHandPopulation(source);
    const rasterMetrics = {
      idle: rightHandRasterFidelityMetrics(source, idle, sourceHand),
      rightArm: rightHandRasterFidelityMetrics(source, rightArm, sourceHand),
    };
    let permittedTorsoGarmentBackingPixels = 0;
    let torsoHandDuplicationPixels = 0;
    let invalidTorsoBackingPixels = 0;

    for (const index of sourceHand.indices) {
      const x = index % SOURCE_WIDTH;
      const y = Math.floor(index / SOURCE_WIDTH);
      const torsoHeadPixel = assetPixel(torsoHead, x, y);
      if (torsoHeadPixel.alpha === 0) {
        continue;
      }
      if (isHandSkinPixel(torsoHeadPixel)) {
        torsoHandDuplicationPixels += 1;
      } else if (
        hiddenTorsoBackingDeclaration(
          backingContext,
          torsoHeadPixel,
          x,
          y,
        )?.occludedBy === 'rightArm'
      ) {
        permittedTorsoGarmentBackingPixels += 1;
      } else {
        invalidTorsoBackingPixels += 1;
      }
    }

    const metrics = {
      permittedTorsoGarmentBackingPixels,
      rasters: rasterMetrics,
      sourceCandidatePixels: sourceHand.candidatePixels,
      sourceInteriorPixels: sourceHand.indices.length,
      sourceMinimumEdgeDistance: sourceHand.minimumEdgeDistance,
      sourceRegion: RIGHT_HAND_SOURCE_GEOMETRY,
      torsoHandDuplicationPixels,
      invalidTorsoBackingPixels,
    };

    console.log(`RIGHT_HAND_INTERIOR_FIDELITY_METRICS=${JSON.stringify(metrics)}`);
    assert.ok(
      sourceHand.indices.length >= 1_000,
      `source-defined right-hand interior must remain meaningful; metrics=${JSON.stringify(metrics)}`,
    );
    assert.deepEqual(
      {
        idleBlackCompositeMismatches:
          rasterMetrics.idle.composite.black.mismatchedPixels,
        idleMissingPixels: rasterMetrics.idle.missingPixels,
        idleRecoloredPixels: rasterMetrics.idle.recoloredPixels,
        idleTranslucentPixels: rasterMetrics.idle.translucentPixels,
        idleWhiteCompositeMismatches:
          rasterMetrics.idle.composite.white.mismatchedPixels,
        rightArmBlackCompositeMismatches:
          rasterMetrics.rightArm.composite.black.mismatchedPixels,
        rightArmMissingPixels: rasterMetrics.rightArm.missingPixels,
        rightArmRecoloredPixels: rasterMetrics.rightArm.recoloredPixels,
        rightArmTranslucentPixels: rasterMetrics.rightArm.translucentPixels,
        rightArmWhiteCompositeMismatches:
          rasterMetrics.rightArm.composite.white.mismatchedPixels,
        torsoHandDuplicationPixels,
        invalidTorsoBackingPixels,
      },
      {
        idleBlackCompositeMismatches: 0,
        idleMissingPixels: 0,
        idleRecoloredPixels: 0,
        idleTranslucentPixels: 0,
        idleWhiteCompositeMismatches: 0,
        rightArmBlackCompositeMismatches: 0,
        rightArmMissingPixels: 0,
        rightArmRecoloredPixels: 0,
        rightArmTranslucentPixels: 0,
        rightArmWhiteCompositeMismatches: 0,
        torsoHandDuplicationPixels: 0,
        invalidTorsoBackingPixels: 0,
      },
      'source-defined right-hand interior artwork must be selected from Avatar.jpg and geometry '
        + 'before inspecting generated pixels: every selected pixel must exist, stay alpha 255, '
        + 'retain exact source RGB, and reproduce Avatar.jpg over black and white, with no '
        + 'duplicated hand skin in torsoHead; only validated hidden garment backing may exist '
        + 'beneath it; '
        + `metrics=${JSON.stringify(metrics)}`,
    );
  });

  it('five-layer: source-defined hand fidelity catches missing, recolored, and translucent pixels', () => {
    const source = decodeAvatarSource();
    const { manifest } = loadManifest();
    const rightArmLayer = manifest.layers.find(({ role }) => role === 'rightArm');
    assert.ok(rightArmLayer, 'manifest must contain rightArm');
    const rightArm = decodeAsset(localAssetPath(rightArmLayer.url));
    const sourceHand = sourceDefinedRightHandPopulation(source);
    assert.ok(
      sourceHand.indices.length >= 3,
      'right-hand negative controls require three source-defined interior pixels',
    );
    const cleanControlIndices = [...sourceHand.indices]
      .filter((index) => {
        const x = index % SOURCE_WIDTH;
        const y = Math.floor(index / SOURCE_WIDTH);
        const generatedPixel = assetPixel(rightArm, x, y);
        return generatedPixel.alpha === 255
          && sameRgb(generatedPixel, sourcePixel(source, x, y));
      })
      .slice(0, 3);
    assert.equal(
      cleanControlIndices.length,
      3,
      'right-hand negative controls require three initially faithful source-defined pixels',
    );
    const controlPopulation = {
      ...sourceHand,
      indices: Uint32Array.from(cleanControlIndices),
    };
    const mutatedRightArm = {
      ...rightArm,
      pixels: Buffer.from(rightArm.pixels),
    };
    const [missingIndex, recoloredIndex, translucentIndex] = controlPopulation.indices;
    const missingOffset = missingIndex * mutatedRightArm.channels;
    const recoloredOffset = recoloredIndex * mutatedRightArm.channels;
    const translucentOffset = translucentIndex * mutatedRightArm.channels;
    mutatedRightArm.pixels[missingOffset + 3] = 0;
    mutatedRightArm.pixels[recoloredOffset] = (
      mutatedRightArm.pixels[recoloredOffset] + 1
    ) % 256;
    mutatedRightArm.pixels[translucentOffset + 3] = 200;

    const metrics = rightHandRasterFidelityMetrics(
      source,
      mutatedRightArm,
      controlPopulation,
    );

    assert.deepEqual(
      {
        missingPixels: metrics.missingPixels,
        recoloredPixels: metrics.recoloredPixels,
        translucentPixels: metrics.translucentPixels,
      },
      {
        missingPixels: 1,
        recoloredPixels: 1,
        translucentPixels: 1,
      },
      'source-first hand coverage must retain coordinates after generated coverage, RGB, or alpha '
        + `regresses; metrics=${JSON.stringify(metrics)}`,
    );
    assert.equal(
      metrics.sampleFailures.length,
      3,
      `each independent hand mutation must remain observable; metrics=${JSON.stringify(metrics)}`,
    );
  });

  it('five-layer: reconstructs idle exactly from localized rigid layers', () => {
    const { manifest } = loadManifest();
    const idlePath = localAssetPath(manifest.idleFallback.url);
    const idle = decodeAsset(idlePath);
    const decodedLayers = manifest.layers.map((layer) => ({
      role: layer.role,
      stats: decodeAsset(localAssetPath(layer.url)),
    }));
    let idleVisiblePixels = 0;
    let missingFromLayerUnion = 0;
    let pixelsOutsideIdle = 0;
    let neutralColorMismatches = 0;
    let overlapPixels = 0;
    let brightCompositeDifferences = 0;
    let brightCompositeMaximumDelta = 0;
    let brightCompositeTotalDelta = 0;
    const brightBackground = [255, 240, 32];

    for (let pixelIndex = 0; pixelIndex < SOURCE_WIDTH * SOURCE_HEIGHT; pixelIndex += 1) {
      const offset = pixelIndex * 4;
      const idleAlpha = idle.pixels[offset + 3];
      let topmost = null;
      let coveringLayers = 0;
      let coveredAtAnyAlpha = false;
      let compositeRed = brightBackground[0];
      let compositeGreen = brightBackground[1];
      let compositeBlue = brightBackground[2];

      for (const { stats } of decodedLayers) {
        const alpha = stats.pixels[offset + 3];
        if (alpha >= 32) {
          coveringLayers += 1;
        }
        if (alpha > 0) {
          coveredAtAnyAlpha = true;
          topmost = {
            red: stats.pixels[offset],
            green: stats.pixels[offset + 1],
            blue: stats.pixels[offset + 2],
            alpha,
          };
          const normalizedAlpha = alpha / 255;
          const remainingAlpha = 1 - normalizedAlpha;
          compositeRed = (stats.pixels[offset] * normalizedAlpha) + (compositeRed * remainingAlpha);
          compositeGreen = (stats.pixels[offset + 1] * normalizedAlpha)
            + (compositeGreen * remainingAlpha);
          compositeBlue = (stats.pixels[offset + 2] * normalizedAlpha)
            + (compositeBlue * remainingAlpha);
        }
      }

      if (idleAlpha >= 32) {
        idleVisiblePixels += 1;
      }
      if (idleAlpha > 0 && !coveredAtAnyAlpha) {
        missingFromLayerUnion += 1;
      } else if (idleAlpha === 0 && coveredAtAnyAlpha) {
        pixelsOutsideIdle += 1;
      }
      if (coveringLayers > 1) {
        overlapPixels += 1;
      }
      if (
        idleAlpha > 0
        && topmost
        && (
          topmost.red !== idle.pixels[offset]
          || topmost.green !== idle.pixels[offset + 1]
          || topmost.blue !== idle.pixels[offset + 2]
        )
      ) {
        neutralColorMismatches += 1;
      }

      const idleNormalizedAlpha = idleAlpha / 255;
      const expectedRed = (idle.pixels[offset] * idleNormalizedAlpha)
        + (brightBackground[0] * (1 - idleNormalizedAlpha));
      const expectedGreen = (idle.pixels[offset + 1] * idleNormalizedAlpha)
        + (brightBackground[1] * (1 - idleNormalizedAlpha));
      const expectedBlue = (idle.pixels[offset + 2] * idleNormalizedAlpha)
        + (brightBackground[2] * (1 - idleNormalizedAlpha));
      const channelDeltas = [
        Math.abs(compositeRed - expectedRed),
        Math.abs(compositeGreen - expectedGreen),
        Math.abs(compositeBlue - expectedBlue),
      ];
      const pixelMaximumDelta = Math.max(...channelDeltas);
      if (pixelMaximumDelta > 1) {
        brightCompositeDifferences += 1;
      }
      brightCompositeMaximumDelta = Math.max(brightCompositeMaximumDelta, pixelMaximumDelta);
      brightCompositeTotalDelta += channelDeltas.reduce((sum, delta) => sum + delta, 0);
    }

    assert.ok(
      idleVisiblePixels >= 237_000,
      `complete source-pinned foreground must retain at least 237000 visible pixels; received ${idleVisiblePixels}`,
    );
    assert.equal(
      missingFromLayerUnion,
      0,
      'every visible idle foreground pixel must be represented by at least one neutral rig layer',
    );
    assert.equal(pixelsOutsideIdle, 0, 'rig layers must never invent pixels outside the cleaned idle foreground');
    assert.equal(
      neutralColorMismatches,
      0,
      'the topmost neutral layer must reconstruct the exact cleaned-foreground RGB at every pixel',
    );
    assert.ok(
      overlapPixels < idleVisiblePixels * 0.12,
      `joint overlaps must stay localized; ${overlapPixels} of ${idleVisiblePixels} visible pixels overlap`,
    );
    assert.ok(
      brightCompositeDifferences <= 500,
      `neutral rig must visually match idle on a bright background; differing pixels=${brightCompositeDifferences}`,
    );
    assert.ok(
      brightCompositeMaximumDelta <= 64,
      `neutral rig bright-background channel delta must stay bounded; maximum=${brightCompositeMaximumDelta}`,
    );
    assert.ok(
      brightCompositeTotalDelta / (SOURCE_WIDTH * SOURCE_HEIGHT * 3) <= 0.01,
      'neutral rig must have negligible mean bright-background compositing error',
    );

    const rightSleeveBright = countPixelsInRect(
      idle,
      { xMin: 610, xMax: 629, yMin: 407, yMax: 630 },
      isBrightCharacterPixel,
    );
    const rightArmLayers = decodedLayers
      .filter(({ role }) => canonicalRole(role) === 'rightArm')
      .map(({ stats }) => stats);
    const rightSleeveBrightInArmSubtree = countPixelsInRect(
      idle,
      { xMin: 610, xMax: 629, yMin: 407, yMax: 630 },
      (pixel, x, y) => {
        if (!isBrightCharacterPixel(pixel)) {
          return false;
        }
        return rightArmLayers.some((stats) => assetPixel(stats, x, y).alpha >= 32);
      },
    );
    const outerTailBright = countPixelsInRect(
      idle,
      { xMin: 591, xMax: 610, yMin: 712, yMax: 820 },
      isBrightCharacterPixel,
    );
    const innerTailBright = countPixelsInRect(
      idle,
      { xMin: 581, xMax: 593, yMin: 713, yMax: 803 },
      isBrightCharacterPixel,
    );
    const torso = decodedLayers.find(({ role }) => canonicalRole(role) === 'torsoHead').stats;
    const outerTailBrightInTorso = countPixelsInRect(
      idle,
      { xMin: 591, xMax: 610, yMin: 712, yMax: 820 },
      (pixel, x, y) => isBrightCharacterPixel(pixel) && assetPixel(torso, x, y).alpha >= 32,
    );
    const innerTailBrightInTorso = countPixelsInRect(
      idle,
      { xMin: 581, xMax: 593, yMin: 713, yMax: 803 },
      (pixel, x, y) => isBrightCharacterPixel(pixel) && assetPixel(torso, x, y).alpha >= 32,
    );
    assert.ok(
      rightSleeveBright >= 2_000,
      `complete idle must retain the source-pinned outer right sleeve; bright pixels=${rightSleeveBright}`,
    );
    assert.ok(
      rightSleeveBrightInArmSubtree >= rightSleeveBright * 0.99,
      'the complete outer right sleeve must move with the right-arm subtree instead of staying in torso',
    );
    assert.ok(
      outerTailBright >= 950,
      `complete idle must retain the source-pinned outer right qameez tail; bright pixels=${outerTailBright}`,
    );
    assert.ok(
      innerTailBright >= 470,
      `complete idle must retain the source-pinned inner right qameez tail; bright pixels=${innerTailBright}`,
    );
    assert.ok(
      outerTailBrightInTorso >= outerTailBright * 0.95,
      'the outer right qameez tail must remain in the static torso/remainder layer',
    );
    assert.ok(
      innerTailBrightInTorso >= innerTailBright * 0.95,
      'the inner right qameez tail must remain in the static torso/remainder layer',
    );
  });

  it('five-layer: keeps bright-background composites free of a broad dark matte ring', () => {
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const rasters = [
      {
        label: 'idle',
        rgba: Uint8Array.from(idle.pixels),
      },
      {
        label: 'walk-lift',
        rgba: rasterizePosedRgbaComposite(
          decoded,
          visibleIndices,
          computePoseKinematics({
            gaitPhase: 0.25,
            speedNormalized: 1,
            velocityX: 1,
          }).cssVariables,
        ),
      },
      {
        label: 'clap-contact',
        rgba: rasterizePosedRgbaComposite(
          decoded,
          visibleIndices,
          computePoseKinematics({
            clapping: true,
            clapProgress: 0.1,
          }).cssVariables,
        ),
      },
    ];
    const metrics = rasters.map(({ label, rgba }) => ({
      label,
      ...darkMatteRingMetrics(rgba),
    }));
    const failures = metrics.flatMap((metric) => metric.backgrounds
      .filter((background) => (
        background.ringPixels > DARK_MATTE_RING_MAX_PIXELS
        || background.broadPixels > DARK_MATTE_RING_MAX_BROAD_PIXELS
        || background.largestComponentPixels > DARK_MATTE_RING_MAX_COMPONENT_PIXELS
      ))
      .map((background) => ({
        ...background,
        label: metric.label,
      })));
    if (metrics[0].partialAlphaPixels < MINIMUM_IDLE_PARTIAL_ALPHA_PIXELS) {
      failures.push({
        idlePartialAlphaPixels: metrics[0].partialAlphaPixels,
        minimumIdlePartialAlphaPixels: MINIMUM_IDLE_PARTIAL_ALPHA_PIXELS,
      });
    }

    console.log(`DARK_MATTE_RING_METRICS=${JSON.stringify({
      thresholds: {
        maximumBroadPixels: DARK_MATTE_RING_MAX_BROAD_PIXELS,
        maximumLargestComponentPixels: DARK_MATTE_RING_MAX_COMPONENT_PIXELS,
        maximumRingPixels: DARK_MATTE_RING_MAX_PIXELS,
        minimumIdlePartialAlphaPixels: MINIMUM_IDLE_PARTIAL_ALPHA_PIXELS,
        ringRadius: DARK_MATTE_RING_RADIUS,
        trustedForegroundDilation: DARK_MATTE_TRUSTED_DILATION,
      },
      rasters: metrics,
    })}`);
    assert.deepEqual(
      failures,
      [],
      'idle and representative posed layers must composite over white, cyan, and magenta '
        + 'without an opaque near-black ring outside a conservatively dilated colorful/bright '
        + `foreground silhouette; failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: seam guards reject broad static duplicates and erased garment controls', () => {
    const { manifest } = loadManifest();
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const actualMetrics = seamLayerIntegrityMetrics(decoded);
    assert.deepEqual(
      seamLayerIntegrityFailures(actualMetrics),
      [],
      `negative controls require a meaningful unmodified garment baseline; metrics=${JSON.stringify(
        actualMetrics,
      )}`,
    );

    const duplicated = cloneDecodedAssets(decoded);
    copyVisibleAssetPixels(duplicated.get('leftArm'), duplicated.get('torsoHead'));
    const duplicatedFailures = seamLayerIntegrityFailures(
      seamLayerIntegrityMetrics(duplicated),
    );
    assert.ok(
      duplicatedFailures.some(({ reason, role }) => (
        role === 'leftArm'
        && reason === 'broad static arm duplicate hides seam motion'
      )),
      `broad static arm duplicate must be rejected; failures=${JSON.stringify(
        duplicatedFailures,
      )}`,
    );

    const erased = cloneDecodedAssets(decoded);
    erased.get('rightArm').pixels.fill(0);
    const erasedFailures = seamLayerIntegrityFailures(
      seamLayerIntegrityMetrics(erased),
    );
    assert.ok(
      erasedFailures.some(({ reason, role }) => (
        role === 'rightArm'
        && reason === 'articulated garment was erased'
      )),
      `erased articulated garment must be rejected; failures=${JSON.stringify(
        erasedFailures,
      )}`,
    );
    assert.ok(
      erasedFailures.some(({ reason, role }) => (
        role === 'rightArm'
        && reason === 'white sleeve garment was erased'
      )),
      `erased white sleeve must be rejected; failures=${JSON.stringify(erasedFailures)}`,
    );
  });

  it('five-layer: bright QA poses expose neither static shoulder slivers nor background garment notches', () => {
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const poseMetrics = QA_SEAM_POSES.map(({ label, pose }) => {
      const torsoLocalPose = {
        ...pose,
        torsoHeadRotateDeg: 0,
      };
      const composite = rasterizePosedComposite(
        decoded,
        visibleIndices,
        torsoLocalPose,
      );
      const poseRgba = rasterizePosedRgbaComposite(
        decoded,
        visibleIndices,
        torsoLocalPose,
      );
      return {
        factoredCommonRootRotationDeg: pose.torsoHeadRotateDeg,
        label,
        protectedNotches: QA_PROTECTED_NOTCH_PROBES.map((probe) => (
          backgroundConnectedProtectedNotchMetrics(composite, probe)
        )),
        roleNotches: ['left', 'right'].map((side) => (
          backgroundConnectedSeamNotchMetrics(composite, side)
        )),
        shoulderSlivers: ['left', 'right'].map((side) => (
          staticShoulderSliverMetrics({
            composite,
            decoded,
            poseRgba,
            side,
          })
        )),
      };
    });
    const integrityMetrics = seamLayerIntegrityMetrics(decoded);
    const ownershipMetrics = shoulderOwnershipMetrics(manifest, decoded, idle);
    const failures = [
      ...seamLayerIntegrityFailures(integrityMetrics),
      ...shoulderOwnershipFailures(ownershipMetrics),
      ...poseMetrics.flatMap(({ label, protectedNotches, roleNotches }) => [
        ...protectedNotches,
        ...roleNotches,
      ].flatMap((metric) => {
        const exceedsArea = metric.backgroundScreenPixels
          > QA_MAX_BACKGROUND_NOTCH_SCREEN_PIXELS;
        const exceedsWidth = metric.maximumWidthPx
          > QA_MAX_BACKGROUND_NOTCH_WIDTH_PX;
        return exceedsArea || exceedsWidth
          ? [{
            ...metric,
            label,
            maximumBackgroundNotchScreenPixels:
              QA_MAX_BACKGROUND_NOTCH_SCREEN_PIXELS,
            maximumBackgroundNotchWidthPx: QA_MAX_BACKGROUND_NOTCH_WIDTH_PX,
            reason: 'background-connected protected waist/underarm garment notch',
          }]
          : [];
      })),
      ...poseMetrics.flatMap(({ label, shoulderSlivers }) => (
        shoulderSlivers.flatMap((metric) => (
          metric.screenPixels > QA_MAX_STATIC_SHOULDER_SLIVER_SCREEN_PIXELS
            ? [{
              ...metric,
              label,
              maximumStaticShoulderSliverScreenPixels:
                QA_MAX_STATIC_SHOULDER_SLIVER_SCREEN_PIXELS,
              reason: 'bright torso-static shoulder sliver remains exposed',
            }]
            : []
        ))
      )),
    ];

    console.log(`BRIGHT_QA_SEAM_METRICS=${JSON.stringify({
      backgrounds: BRIGHT_MATTE_BACKGROUNDS.map(({ name }) => name),
      integrityMetrics,
      ownershipMetrics,
      poseMetrics,
      sourceMapping: {
        avatarCssHeightPx: 1080 * 0.67,
        sourceHeight: SOURCE_HEIGHT,
        sourceToScreenScale: Number(QA_1080P_SOURCE_TO_SCREEN_SCALE.toFixed(6)),
      },
      thresholds: {
        maximumBackgroundNotchScreenPixels:
          QA_MAX_BACKGROUND_NOTCH_SCREEN_PIXELS,
        maximumBackgroundNotchWidthPx: QA_MAX_BACKGROUND_NOTCH_WIDTH_PX,
        maximumStaticShoulderSliverScreenPixels:
          QA_MAX_STATIC_SHOULDER_SLIVER_SCREEN_PIXELS,
      },
    })}`);
    assert.deepEqual(
      failures,
      [],
      'the actual five-layer painter order and hierarchical production transforms must composite '
        + 'over white/cyan/magenta with white sleeve caps owned by their whole-arm layers, green '
        + 'waistcoat/collar shoulder art retained by torsoHead, only narrow root overlap, no '
        + 'exposed torso-static white shoulder spikes, and no background-connected '
        + 'waist/underarm holes; '
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: evaluates seam contracts in torso-local coordinates', () => {
    const { manifest } = loadManifest();
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const neutralPose = computePoseKinematics({
      gaitPhase: 0,
      speedNormalized: 0,
      velocityX: 0,
    }).cssVariables;
    const tiltedRootPose = {
      ...neutralPose,
      torsoHeadRotateDeg: 2.6,
    };
    const signatures = [neutralPose, tiltedRootPose].map((pose) => {
      const composite = rasterizePosedComposite(decoded, visibleIndices, pose);
      return {
        alpha: sha256(Buffer.from(composite.alpha.buffer)),
        anyRoleBits: sha256(Buffer.from(composite.anyRoleBits.buffer)),
        bounds: {
          xMax: composite.xMax,
          xMin: composite.xMin,
          yMax: composite.yMax,
          yMin: composite.yMin,
        },
        strongRoleBits: sha256(Buffer.from(composite.strongRoleBits.buffer)),
      };
    });

    assert.deepEqual(
      signatures[1],
      signatures[0],
      'a common torsoHead rotation must not create false garment gaps, weak bridges, '
        + 'underarm wedges, or static remnants in torso-local seam analysis',
    );
  });

  it('five-layer: keeps protected garment pixels torsoHead-owned through dense poses', () => {
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const torsoLayer = rootLayer(manifest.layers);
    assert.ok(torsoLayer, 'five-layer rig must provide torsoHead');
    const torso = decoded.get(torsoLayer.role);
    const source = decodeAvatarSource();
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const neutralVisible = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
    for (let index = 0; index < neutralVisible.length; index += 1) {
      neutralVisible[index] = idle.pixels[(index * idle.channels) + 3] >= 32 ? 1 : 0;
    }
    const neutralDistance = distanceFromMask(neutralVisible, 4);
    const records = PROTECTED_FIXED_GARMENT_PROBES.map((probe) => {
      let excludedAdjacentHandPixels = 0;
      let excludedDirectSkinPixels = 0;
      let excludedRightArmOwnedPixels = 0;
      let excludedRightLegOwnedPixels = 0;
      let excludedPermittedTorsoBackingPixels = 0;
      let excludedTorsoHandDuplicationPixels = 0;
      let excludedInvalidTorsoBackingPixels = 0;
      const protectedIndices = [];
      const movingOwnership = [];
      const movingOwnedIndices = new Set();
      let torsoOwnedPixels = 0;

      for (let y = probe.rectangle.yMin; y <= probe.rectangle.yMax; y += 1) {
        for (let x = probe.rectangle.xMin; x <= probe.rectangle.xMax; x += 1) {
          const index = (y * SOURCE_WIDTH) + x;
          const idlePixel = assetPixel(idle, x, y);
          if (!isProtectedGarmentPixel(idlePixel, probe.pixelKind)) {
            continue;
          }
          if (
            probe.name === 'right-waist-qameez'
            && isRightHandArtworkPixel(idle, x, y)
          ) {
            if (isHandSkinPixel(idlePixel)) {
              excludedDirectSkinPixels += 1;
            } else {
              excludedAdjacentHandPixels += 1;
            }
            const torsoPixel = assetPixel(torso, x, y);
            if (torsoPixel.alpha >= 32) {
              if (isHandSkinPixel(torsoPixel)) {
                excludedTorsoHandDuplicationPixels += 1;
              } else if (
                hiddenTorsoBackingDeclaration(
                  backingContext,
                  torsoPixel,
                  x,
                  y,
                )?.occludedBy === 'rightArm'
              ) {
                excludedPermittedTorsoBackingPixels += 1;
              } else {
                excludedInvalidTorsoBackingPixels += 1;
              }
            }
            for (const canonicalMovingRole of probe.movingRoles) {
              const owned = layersForCanonicalRole(
                manifest.layers,
                canonicalMovingRole,
              ).some((layer) => (
                assetPixel(decoded.get(layer.role), x, y).alpha >= 32
              ));
              if (canonicalMovingRole === 'rightArm' && owned) {
                excludedRightArmOwnedPixels += 1;
              }
              if (canonicalMovingRole === 'rightLeg' && owned) {
                excludedRightLegOwnedPixels += 1;
              }
            }
            continue;
          }

          protectedIndices.push(index);
          if (assetPixel(torso, x, y).alpha >= 32) {
            torsoOwnedPixels += 1;
          }
          for (const canonicalMovingRole of probe.movingRoles) {
            for (const layer of layersForCanonicalRole(
              manifest.layers,
              canonicalMovingRole,
            )) {
              const stats = decoded.get(layer.role);
              if (stats.pixels[(index * stats.channels) + 3] >= 32) {
                movingOwnership.push({ index, role: layer.role });
                movingOwnedIndices.add(index);
              }
            }
          }
        }
      }

      return {
        allowedGapPixels: 0,
        allowedMovingOwnedPixels: 0,
        allowedProtrusionPixels: 0,
        excludedAdjacentHandPixels,
        excludedDirectSkinPixels,
        excludedInvalidTorsoBackingPixels,
        excludedPermittedTorsoBackingPixels,
        excludedRightArmOwnedPixels,
        excludedRightLegOwnedPixels,
        excludedTorsoHandDuplicationPixels,
        maximumDisplacement: 0,
        maximumGap: { label: null, pixels: 0 },
        maximumProtrusion: { label: null, pixels: 0 },
        movingOwnedPixels: movingOwnedIndices.size,
        movingOwnership,
        name: probe.name,
        probe,
        protectedIndices,
        protectedPixels: protectedIndices.length,
        torsoOwnedPixels,
      };
    });
    const rightWaistQameez = records.find(({ name }) => name === 'right-waist-qameez');
    const excludedHandPixels = rightWaistQameez.excludedDirectSkinPixels
      + rightWaistQameez.excludedAdjacentHandPixels;
    assert.ok(
      rightWaistQameez.excludedDirectSkinPixels > 0,
      'right-waist-qameez hand exclusion must contain direct right-hand skin pixels',
    );
    assert.ok(
      rightWaistQameez.excludedDirectSkinPixels
        > rightWaistQameez.excludedAdjacentHandPixels,
      'right-waist-qameez hand exclusion must be predominantly direct skin, not a broad halo',
    );
    assert.equal(
      rightWaistQameez.excludedRightArmOwnedPixels,
      excludedHandPixels,
      'every excluded right-hand artwork pixel must be owned by rightArm',
    );
    assert.equal(
      rightWaistQameez.excludedRightLegOwnedPixels,
      0,
      'rightLeg-owned qameez pixels must remain in the protected garment population',
    );
    assert.equal(
      rightWaistQameez.excludedTorsoHandDuplicationPixels,
      0,
      'right-hand artwork must never be duplicated into torsoHead',
    );
    assert.equal(
      rightWaistQameez.excludedInvalidTorsoBackingPixels,
      0,
      'torso pixels beneath the right hand must be transparent or validated source-derived garment backing',
    );
    const { allSampleCount, samples } = denseProductionPoseSamples();

    for (const sample of samples) {
      const composite = rasterizePosedComposite(decoded, visibleIndices, sample.pose);

      for (const record of records) {
        let gapPixels = 0;
        let protrusionPixels = 0;

        for (const index of record.protectedIndices) {
          if (composite.alpha[index] < 32) {
            gapPixels += 1;
          }
        }

        for (const { index, role } of record.movingOwnership) {
          const sourceX = index % SOURCE_WIDTH;
          const sourceY = Math.floor(index / SOURCE_WIDTH);
          const transformed = transformPointThroughChain(
            { x: sourceX + 0.5, y: sourceY + 0.5 },
            roleRotationChain(role, sample.pose, { includeRoot: false }),
          );
          const displacement = Math.hypot(
            transformed.x - (sourceX + 0.5),
            transformed.y - (sourceY + 0.5),
          );
          record.maximumDisplacement = Math.max(record.maximumDisplacement, displacement);
          if (displacement < 3) {
            continue;
          }

          const targetX = Math.floor(transformed.x);
          const targetY = Math.floor(transformed.y);
          if (
            targetX < 0
            || targetX >= SOURCE_WIDTH
            || targetY < 0
            || targetY >= SOURCE_HEIGHT
          ) {
            continue;
          }
          const targetIndex = (targetY * SOURCE_WIDTH) + targetX;
          if (
            neutralDistance[targetIndex] > 3
            && (
              composite.strongRoleBits[targetIndex]
              & RASTER_ROLE_BITS.get(canonicalRole(role))
            ) !== 0
          ) {
            protrusionPixels += 1;
          }
        }

        if (gapPixels > record.maximumGap.pixels) {
          record.maximumGap = { label: sample.label, pixels: gapPixels };
        }
        if (protrusionPixels > record.maximumProtrusion.pixels) {
          record.maximumProtrusion = { label: sample.label, pixels: protrusionPixels };
        }
      }
    }

    const metrics = records.map((record) => {
      // Fixed garment pixels should be entirely torso-owned. The 0.5%/16-pixel
      // allowance ignores raster edge trivia; current failures are 235-671
      // moving-owned pixels and 132-286 displaced protrusion pixels.
      const allowedPixelTrivia = Math.max(
        16,
        Math.floor(record.protectedPixels * 0.005),
      );
      return {
        allowedGapPixels: allowedPixelTrivia,
        allowedMovingOwnedPixels: allowedPixelTrivia,
        allowedProtrusionPixels: allowedPixelTrivia,
        excludedAdjacentHandPixels: record.excludedAdjacentHandPixels,
        excludedDirectSkinPixels: record.excludedDirectSkinPixels,
        excludedInvalidTorsoBackingPixels: record.excludedInvalidTorsoBackingPixels,
        excludedPermittedTorsoBackingPixels: record.excludedPermittedTorsoBackingPixels,
        excludedRightArmOwnedPixels: record.excludedRightArmOwnedPixels,
        excludedRightLegOwnedPixels: record.excludedRightLegOwnedPixels,
        excludedTorsoHandDuplicationPixels: record.excludedTorsoHandDuplicationPixels,
        maximumDisplacement: Number(record.maximumDisplacement.toFixed(2)),
        maximumGap: record.maximumGap,
        maximumProtrusion: record.maximumProtrusion,
        movingOwnedPixels: record.movingOwnedPixels,
        name: record.name,
        protectedPixels: record.protectedPixels,
        torsoOwnedFraction: record.protectedPixels === 0
          ? 0
          : Number((record.torsoOwnedPixels / record.protectedPixels).toFixed(6)),
        torsoOwnedPixels: record.torsoOwnedPixels,
      };
    });
    const failures = metrics.filter((metric) => {
      const probe = PROTECTED_FIXED_GARMENT_PROBES.find(({ name }) => name === metric.name);
      return metric.protectedPixels < probe.minimumProtectedPixels
        || metric.torsoOwnedFraction < 0.99
        || metric.movingOwnedPixels > metric.allowedMovingOwnedPixels
        || metric.maximumGap.pixels > metric.allowedGapPixels
        || metric.maximumProtrusion.pixels > metric.allowedProtrusionPixels;
    });

    console.log(`FIXED_GARMENT_POSE_METRICS=${JSON.stringify({
      allDenseSamples: allSampleCount,
      clapSamples: DENSE_CLAP_PROGRESS_SAMPLES + 1,
      gaitSamples: DENSE_GAIT_PHASE_SAMPLES,
      probes: metrics,
      uniqueRasterPoses: samples.length,
    })}`);
    assert.deepEqual(
      failures,
      [],
      'protected green waistcoat panels and qameez waist pixels must remain at least 99% '
        + 'torso-owned through dense gait/clap sampling, with no more than 0.5% or 16 pixels '
        + 'of moving ownership, visible gaps, or displaced protrusions; right-hand exclusions '
        + 'permit only validated hidden garment backing and never duplicated hand artwork; '
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: rejects an attached arm with an oversized underarm opening', () => {
    const composite = createOversizedUnderarmGapComposite();
    const metrics = underarmWedgeMetrics(composite, 'left');

    assert.ok(
      metrics.maximumGapWidth > 80,
      `negative control must expose a gap wider than 80px; metrics=${JSON.stringify(metrics)}`,
    );
    assert.ok(
      metrics.maximumGapWidth > UNDERARM_MAX_GAP_WIDTH,
      'the frozen 30px underarm width limit must reject the oversized opening',
    );
    assert.ok(
      metrics.gapPixels > UNDERARM_MAX_GAP_PIXELS,
      'the oversized opening must also exceed the aggregate gap-pixel budget',
    );
  });

  it('five-layer: avoids broad transparent underarm wedges through dense walk and clap poses', () => {
    const { manifest } = loadManifest();
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const { allSampleCount, samples } = denseProductionPoseSamples();
    const failures = [];
    const worstGapPixels = new Map();
    const worstGapWidths = new Map();

    for (const sample of samples) {
      const composite = rasterizePosedComposite(decoded, visibleIndices, sample.pose);
      for (const side of ['left', 'right']) {
        const metrics = underarmWedgeMetrics(composite, side);
        const key = `${sample.scenario}:${side}`;
        const candidate = {
          ...metrics,
          label: sample.label,
          scenario: sample.scenario,
          side,
        };
        if (
          metrics.gapPixels > UNDERARM_MAX_GAP_PIXELS
          || metrics.maximumGapWidth > UNDERARM_MAX_GAP_WIDTH
        ) {
          failures.push(candidate);
        }
        const previousPixels = worstGapPixels.get(key);
        if (!previousPixels || metrics.gapPixels > previousPixels.gapPixels) {
          worstGapPixels.set(key, candidate);
        }
        const previousWidth = worstGapWidths.get(key);
        if (
          !previousWidth
          || metrics.maximumGapWidth > previousWidth.maximumGapWidth
        ) {
          worstGapWidths.set(key, {
            ...metrics,
            label: sample.label,
            scenario: sample.scenario,
            side,
          });
        }
      }
    }

    console.log(`UNDERARM_WEDGE_METRICS=${JSON.stringify({
      allDenseSamples: allSampleCount,
      maximumGapPixels: UNDERARM_MAX_GAP_PIXELS,
      maximumGapWidth: UNDERARM_MAX_GAP_WIDTH,
      uniqueRasterPoses: samples.length,
      worstGapPixels: [...worstGapPixels.values()],
      worstGapWidths: [...worstGapWidths.values()],
    })}`);
    assert.deepEqual(
      failures,
      [],
      'the torso-to-arm silhouette must not expose a broad background-colored wedge: dense '
        + `walk/clap poses allow at most ${UNDERARM_MAX_GAP_PIXELS} transparent scanline pixels `
        + `in aggregate and a maximum ${UNDERARM_MAX_GAP_WIDTH}-pixel seam; `
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: localizes arm backing and rejects broad torso duplication or static remnants', () => {
    const { manifest } = loadManifest();
    const source = decodeAvatarSource();
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const torsoLayer = rootLayer(manifest.layers);
    assert.ok(torsoLayer, 'five-layer rig must provide torsoHead');
    const torso = decoded.get(torsoLayer.role);
    const backingContext = createHiddenTorsoBackingContext(
      manifest,
      decoded,
      source,
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const sideMetrics = new Map();

    for (const side of ['left', 'right']) {
      const canonicalArmRole = `${side}Arm`;
      const shoulder = FIVE_LAYER_BY_ROLE.get(canonicalArmRole).pivot;
      const movingLayers = layersForCanonicalRole(manifest.layers, canonicalArmRole);
      for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
        const backingCandidates = new Set();
        const hiddenBackingCandidates = new Map(
          backingContext.backingDeclarations
            .filter(({ occludedBy }) => occludedBy === canonicalArmRole)
            .map((declaration) => [declaration.name, new Set()]),
        );
        const invalidDuplicateIndices = new Set();
        const duplicateIndices = new Set();
        const sourceFaithfulBackingCandidates = new Set();
        const visibleIndicesOutsideJoint = new Set();

        for (const layer of movingLayers) {
          const moving = decoded.get(layer.role);
          for (let y = 0; y < SOURCE_HEIGHT; y += 1) {
            for (let x = 0; x < SOURCE_WIDTH; x += 1) {
              const movingPixel = assetPixel(moving, x, y);
              if (movingPixel.alpha < alphaThreshold) {
                continue;
              }
              if (
                Math.hypot(x - shoulder.x, y - shoulder.y)
                <= TORSO_ARM_JOINT_OVERLAP_RADIUS
              ) {
                continue;
              }

              const index = (y * SOURCE_WIDTH) + x;
              visibleIndicesOutsideJoint.add(index);
              const torsoPixel = assetPixel(torso, x, y);
              if (torsoPixel.alpha < alphaThreshold) {
                continue;
              }

              duplicateIndices.add(index);
              const bodyFacing = side === 'left' ? x >= shoulder.x : x <= shoulder.x;
              const originalPixel = sourcePixel(source, x, y);
              const sourceFaithful = sameRgb(movingPixel, originalPixel);
              const generatedRgbaMatches = sameRgba(movingPixel, torsoPixel);
              const sourceGarment = isProtectedGarmentPixel(
                { ...originalPixel, alpha: 255 },
                'garment',
              );
              const sourceFaithfulOverlap = sourceFaithful
                && generatedRgbaMatches
                && sourceGarment;
              const hiddenBackingDeclaration = sourceFaithful
                ? hiddenTorsoBackingDeclaration(
                  backingContext,
                  torsoPixel,
                  x,
                  y,
                )
                : null;
              const sourceFaithfulBackingDeclaration = sourceFaithfulOverlap
                ? continuityBackingDeclarationAt(
                  backingContext,
                  canonicalArmRole,
                  x,
                  y,
                )
                : null;
              const backingDeclaration = hiddenBackingDeclaration
                ?? sourceFaithfulBackingDeclaration;
              const permittedTorsoBacking = backingDeclaration?.occludedBy
                === canonicalArmRole;
              if (
                bodyFacing
                && permittedTorsoBacking
              ) {
                backingCandidates.add(index);
                if (hiddenBackingDeclaration) {
                  hiddenBackingCandidates
                    .get(backingDeclaration.name)
                    .add(index);
                } else {
                  sourceFaithfulBackingCandidates.add(index);
                }
              } else {
                invalidDuplicateIndices.add(index);
              }
            }
          }
        }

        const nonBackingTorso = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
        for (let index = 0; index < nonBackingTorso.length; index += 1) {
          const torsoAlpha = torso.pixels[(index * torso.channels) + 3];
          if (torsoAlpha >= alphaThreshold && !backingCandidates.has(index)) {
            nonBackingTorso[index] = 1;
          }
        }
        const torsoDistance = distanceFromMask(
          nonBackingTorso,
          ARM_BACKING_MAX_TORSO_DISTANCE + 1,
        );
        const backingIndices = new Set();
        let maximumBackingTorsoDistance = 0;
        for (const index of backingCandidates) {
          const distance = torsoDistance[index];
          maximumBackingTorsoDistance = Math.max(maximumBackingTorsoDistance, distance);
          if (distance <= ARM_BACKING_MAX_TORSO_DISTANCE) {
            backingIndices.add(index);
          } else {
            invalidDuplicateIndices.add(index);
          }
        }
        const backingComponentSizes = eightConnectedComponentSizes(backingIndices);
        const backingPixels = backingIndices.size;
        const connectedBackingPixels = backingComponentSizes[0] ?? 0;
        const hiddenBackingContracts = backingContext.backingDeclarations
          .filter(({ occludedBy }) => occludedBy === canonicalArmRole)
          .map((declaration) => {
            const acceptedIndices = new Set(
              [...hiddenBackingCandidates.get(declaration.name)]
                .filter((index) => backingIndices.has(index)),
            );
            return hiddenBackingTopologyMetrics(declaration, acceptedIndices);
          });

        const visiblePixelsOutsideJoint = visibleIndicesOutsideJoint.size;
        const duplicateFraction = visiblePixelsOutsideJoint === 0
          ? 0
          : invalidDuplicateIndices.size / visiblePixelsOutsideJoint;
        sideMetrics.set(`${side}:alpha>=${alphaThreshold}`, {
          alphaThreshold,
          backingIndices: Uint32Array.from(backingIndices),
          duplicateIndices: Uint32Array.from(invalidDuplicateIndices),
          metrics: {
            allowedBackingPixels: ARM_BACKING_MAX_VISIBLE_PIXELS,
            allowedDuplicateFraction: TORSO_ARM_DUPLICATE_MAX_FRACTION,
            allowedDuplicatePixels: TORSO_ARM_DUPLICATE_MAX_PIXELS,
            alphaThreshold,
            backingConnectedFraction: backingPixels === 0
              ? 1
              : connectedBackingPixels / backingPixels,
            backingPixels,
            duplicateFraction: Number(duplicateFraction.toFixed(6)),
            duplicatePixelsOutsideJointAndBacking: invalidDuplicateIndices.size,
            hiddenBackingContracts,
            hiddenBackingPixels: hiddenBackingContracts.reduce(
              (sum, contract) => sum + contract.pixels,
              0,
            ),
            largestDetachedBackingComponentPixels:
              backingComponentSizes[1] ?? 0,
            maximumBackingTorsoDistance,
            side,
            sourceFaithfulOverlapPixels: [...backingIndices].filter((index) => (
              sourceFaithfulBackingCandidates.has(index)
            )).length,
            totalOverlapPixelsOutsideJoint: duplicateIndices.size,
            visiblePixelsOutsideJoint,
          },
        });
      }
    }

    const { allSampleCount, samples } = denseProductionPoseSamples();
    const worstExposure = new Map();
    const clapMaximumExposure = [];
    const sideRoleBits = new Map([
      ['left', RASTER_ROLE_BITS.get('leftArm')],
      ['right', RASTER_ROLE_BITS.get('rightArm')],
    ]);

    for (const sample of samples) {
      const composite = rasterizePosedComposite(decoded, visibleIndices, sample.pose);
      for (const side of ['left', 'right']) {
        for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
          const movingMask = new Uint8Array(SOURCE_WIDTH * SOURCE_HEIGHT);
          const roleBits = sideRoleBits.get(side);
          const compositeRoleBits = alphaThreshold === 1
            ? composite.anyRoleBits
            : composite.strongRoleBits;
          for (let index = 0; index < movingMask.length; index += 1) {
            movingMask[index] = (compositeRoleBits[index] & roleBits) !== 0 ? 1 : 0;
          }
          const metric = sideMetrics.get(`${side}:alpha>=${alphaThreshold}`);
          const exposureMetrics = torsoArmStaticExposureMetrics(
            metric,
            movingMask,
          );

          if (sample.label === 'clap:0.100000') {
            clapMaximumExposure.push({
              alphaThreshold,
              exposedAfterDilation: exposureMetrics.exposedStaticOverlapPixels,
              side,
              uncoveredStaticOverlapPixels:
                exposureMetrics.uncoveredStaticOverlapPixels,
            });
          }
          const key = `${sample.scenario}:${side}:alpha>=${alphaThreshold}`;
          const previous = worstExposure.get(key);
          if (
            !previous
            || exposureMetrics.exposedStaticOverlapPixels
              > previous.exposedStaticOverlapPixels
          ) {
            worstExposure.set(key, {
              alphaThreshold,
              exposedStaticOverlapPixels:
                exposureMetrics.exposedStaticOverlapPixels,
              label: sample.label,
              scenario: sample.scenario,
              side,
            });
          }
        }
      }
    }

    const sourceOverlapMetrics = [...sideMetrics.values()].map(({ metrics }) => metrics);
    const poseExposureMetrics = [...worstExposure.values()];
    const failures = [
      ...sourceOverlapMetrics.flatMap((metric) => (
        hiddenBackingTopologyFailures(metric.hiddenBackingContracts)
      )),
      ...sourceOverlapMetrics.filter((metric) => (
        metric.backingPixels > ARM_BACKING_MAX_VISIBLE_PIXELS
        || metric.backingConnectedFraction < ARM_BACKING_MIN_CONNECTED_FRACTION
        || metric.largestDetachedBackingComponentPixels
          > ARM_BACKING_MAX_DETACHED_COMPONENT_PIXELS
        || metric.maximumBackingTorsoDistance > ARM_BACKING_MAX_TORSO_DISTANCE
        || metric.duplicatePixelsOutsideJointAndBacking > TORSO_ARM_DUPLICATE_MAX_PIXELS
        || metric.duplicateFraction > TORSO_ARM_DUPLICATE_MAX_FRACTION
      )),
      ...poseExposureMetrics.filter((metric) => (
        metric.exposedStaticOverlapPixels > TORSO_ARM_STATIC_EXPOSURE_MAX_PIXELS
      )),
    ];

    console.log(`TORSO_ARM_DUPLICATION_METRICS=${JSON.stringify({
      allDenseSamples: allSampleCount,
      allowedJointRadius: TORSO_ARM_JOINT_OVERLAP_RADIUS,
      backing: {
        maximumDetachedComponentPixels:
          ARM_BACKING_MAX_DETACHED_COMPONENT_PIXELS,
        maximumTorsoDistance: ARM_BACKING_MAX_TORSO_DISTANCE,
        maximumVisiblePixels: ARM_BACKING_MAX_VISIBLE_PIXELS,
        minimumConnectedFraction: ARM_BACKING_MIN_CONNECTED_FRACTION,
        region:
          'manifest-registered whole-arm source overlap and hidden torso garment backing',
      },
      maximumDuplicateFraction: TORSO_ARM_DUPLICATE_MAX_FRACTION,
      maximumDuplicatePixels: TORSO_ARM_DUPLICATE_MAX_PIXELS,
      maximumExposedAfterDilation: TORSO_ARM_STATIC_EXPOSURE_MAX_PIXELS,
      movingArtworkDilation: TORSO_ARM_STATIC_EXPOSURE_DILATION,
      clapMaximumExposure,
      poseExposure: poseExposureMetrics,
      sourceOverlap: sourceOverlapMetrics,
      uniqueRasterPoses: samples.length,
    })}`);
    assert.deepEqual(
      failures,
      [],
      'torsoHead overlap with each rigid arm must stay inside the bounded shoulder joint or '
        + 'a body-facing backing region bounded by manifest-registered whole-arm artwork, within '
        + '36px of non-backing torsoHead art, at most 2000 visible pixels per side, at least 97% '
        + 'connected, with no detached backing component larger than 16 pixels; backing must be '
        + 'source-faithful overlap or validated torso-only source-derived garment hidden beneath '
        + `the arm; outside those regions each arm may duplicate `
        + `at most ${TORSO_ARM_DUPLICATE_MAX_PIXELS} pixels or `
        + `${TORSO_ARM_DUPLICATE_MAX_FRACTION * 100}% of its visible art, and dense gait/clap `
        + `poses may leave at most ${TORSO_ARM_STATIC_EXPOSURE_MAX_PIXELS} static backing or duplicate `
        + `pixels uncovered after ${TORSO_ARM_STATIC_EXPOSURE_DILATION}px dilation; `
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('five-layer: keeps both rigid arm layers localized to sleeves instead of waistcoat wedges', () => {
    const { manifest } = loadManifest();
    for (const role of ['leftArm', 'rightArm']) {
      const population = layersForCanonicalRole(manifest.layers, role)
        .map((layer) => colorPopulation(decodeAsset(localAssetPath(layer.url))))
        .reduce(
          (total, current) => ({
            green: total.green + current.green,
            visible: total.visible + current.visible,
            white: total.white + current.white,
          }),
          { green: 0, visible: 0, white: 0 },
        );
      assert.ok(population.visible >= 5_000, `${role} must retain a substantial articulated sleeve`);
      assert.ok(population.white >= 3_000, `${role} must retain its white garment artwork`);
      assert.ok(
        population.green <= Math.max(300, population.white * 0.08),
        `${role} must not rotate a waistcoat wedge; green=${population.green}, white=${population.white}`,
      );
    }
  });

  it('five-layer: shoulder ownership rejects static white caps and moving green torso art', () => {
    const valid = {
      allowedGreenMovingPixels: 16,
      allowedWhiteOwnershipTrivia: 16,
      greenMovingOwnedPixels: 10,
      greenTorsoOwnedFraction: 0.995,
      minimumGreenPixels: 1_800,
      minimumWhitePixels: 250,
      role: 'leftArm',
      sourceGreenPixels: 2_000,
      sourceWhitePixels: 1_000,
      whiteMovingOwnedFraction: 0.98,
      whiteOverlapOutsideRootPixels: 0,
      whiteTorsoOnlyPixels: 10,
      whiteUnownedPixels: 0,
    };
    assert.deepEqual(
      shoulderOwnershipFailures([valid]),
      [],
      'negative controls require a valid semantic shoulder ownership baseline',
    );

    for (const [property, value, reason] of [
      ['whiteMovingOwnedFraction', 0.94, 'white illustrated sleeve cap is not whole-arm owned'],
      ['whiteTorsoOnlyPixels', 17, 'white sleeve-cap artwork remains static or unowned'],
      ['whiteUnownedPixels', 17, 'white sleeve-cap artwork remains static or unowned'],
      ['greenTorsoOwnedFraction', 0.98, 'green waistcoat/collar shoulder art is not torsoHead-owned'],
      ['greenMovingOwnedPixels', 17, 'green waistcoat/collar shoulder art rotates with the arm'],
      ['whiteOverlapOutsideRootPixels', 17, 'white sleeve overlap extends beyond the narrow root joint'],
    ]) {
      const failures = shoulderOwnershipFailures([{
        ...valid,
        [property]: value,
      }]);
      assert.ok(
        failures.some((failure) => failure.reason === reason),
        `${property} negative control must be rejected; failures=${JSON.stringify(failures)}`,
      );
    }
  });

  it('five-layer: rejects an eight-neighbor root connection narrowed to one bridge pixel', () => {
    const composite = createSinglePixelShoulderTetherComposite();
    const bridgeMinimums = ['gait', 'clap'].flatMap((scenario) => (
      POSED_RASTER_ALPHA_THRESHOLDS.map((alphaThreshold) => ({
        alphaThreshold,
        bridgePixels: countRoleBridgePixels(
          composite,
          RASTER_ROLE_BITS.get('leftArm'),
          RASTER_ROLE_BITS.get('torsoHead'),
          alphaThreshold,
        ),
        childRole: 'leftArm',
        label: 'synthetic:single-pixel-diagonal-shoulder',
        parentRole: 'torsoHead',
        scenario,
        subtree: 'leftArm',
      }))
    ));
    const bridgeFailures = bridgeStrengthFailures(bridgeMinimums);

    for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
      const components = posedComponentMetrics(composite, alphaThreshold).leftArm;
      assert.equal(components.connectedFraction, 1);
      assert.equal(components.largestDetachedComponentPixels, 0);
    }
    assert.deepEqual(
      bridgeMinimums.map(({ bridgePixels }) => bridgePixels),
      [1, 1, 1, 1],
      'fixture must have exactly one fully opaque diagonal bridge pixel',
    );
    assert.equal(
      bridgeFailures.length,
      bridgeMinimums.length,
      'the bridge-strength contract must reject the tether in every dense scenario and alpha threshold',
    );
    assert.deepEqual(
      bridgeFailures.map(({ minimumBridgePixels }) => minimumBridgePixels),
      [160, 160, 64, 64],
      'the negative control must exercise the scenario-aware shoulder floors',
    );
    console.log(`POSED_RASTER_NEGATIVE_CONTROL=${JSON.stringify({
      bridgeFailures: bridgeFailures.map(({
        alphaThreshold,
        bridgePixels,
        edge,
        minimumBridgePixels,
        scenario,
      }) => ({
        alphaThreshold,
        bridgePixels,
        edge,
        minimumBridgePixels,
        scenario,
      })),
      connectedFraction: 1,
      largestDetachedComponentPixels: 0,
    })}`);
  });

  it('five-layer: rejects sub-97% connectivity even when detached fragments stay below 256px', () => {
    const composite = createFragmentedLimbComposite();

    for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
      const metrics = posedComponentMetrics(composite, alphaThreshold).leftArm;
      assert.ok(
        metrics.connectedFraction < 0.97,
        `negative control must fall below 97% connectivity; metrics=${JSON.stringify(metrics)}`,
      );
      assert.ok(
        metrics.largestDetachedComponentPixels <= 256,
        'negative control must isolate the connected-fraction threshold rather than component size',
      );
    }
  });

  it('five-layer: keeps every rigid limb alpha-connected to torsoHead through dense poses', () => {
    const { manifest } = loadManifest();
    const decoded = new Map(
      manifest.layers.map((layer) => [
        layer.role,
        decodeAsset(localAssetPath(layer.url)),
      ]),
    );
    const visibleIndices = new Map(
      [...decoded].map(([role, stats]) => [role, sourceAlphaIndices(stats)]),
    );
    const { allSampleCount, samples } = denseProductionPoseSamples();
    const minimums = new Map();
    const weakestConnectedFractions = new Map();
    const largestDetachedComponents = new Map();
    const componentFailures = [];

    for (const sample of samples) {
      const composite = rasterizePosedComposite(decoded, visibleIndices, sample.pose);

      for (const subtree of LIMB_SUBTREES) {
        const childRole = subtree.name;
        const parentRole = 'torsoHead';

        for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
          const bridgePixels = countRoleBridgePixels(
            composite,
            RASTER_ROLE_BITS.get(childRole),
            RASTER_ROLE_BITS.get(parentRole),
            alphaThreshold,
          );
          const key = [
            sample.scenario,
            subtree.name,
            `${childRole}->${parentRole}`,
            `alpha>=${alphaThreshold}`,
          ].join('|');
          const previous = minimums.get(key);
          if (!previous || bridgePixels < previous.bridgePixels) {
            minimums.set(key, {
              alphaThreshold,
              bridgePixels,
              childRole,
              label: sample.label,
              parentRole,
              pose: sample.pose,
              scenario: sample.scenario,
              subtree: subtree.name,
            });
          }
        }
      }

      for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
        const components = posedComponentMetrics(composite, alphaThreshold);
        for (const subtree of LIMB_SUBTREES) {
          const key = [
            sample.scenario,
            subtree.name,
            `alpha>=${alphaThreshold}`,
          ].join('|');
          const candidate = {
            ...components[subtree.name],
            alphaThreshold,
            label: sample.label,
            scenario: sample.scenario,
            subtree: subtree.name,
          };
          const previousFraction = weakestConnectedFractions.get(key);
          if (
            !previousFraction
            || candidate.connectedFraction < previousFraction.connectedFraction
          ) {
            weakestConnectedFractions.set(key, candidate);
          }
          const previousDetached = largestDetachedComponents.get(key);
          if (
            !previousDetached
            || candidate.largestDetachedComponentPixels
              > previousDetached.largestDetachedComponentPixels
          ) {
            largestDetachedComponents.set(key, candidate);
          }
          if (
            candidate.connectedFraction < 0.97
            || candidate.largestDetachedComponentPixels > 256
          ) {
            componentFailures.push(candidate);
          }
        }
      }
    }

    const minimumJointBridges = [];
    for (const scenario of ['gait', 'clap']) {
      for (const subtree of LIMB_SUBTREES) {
        for (const alphaThreshold of POSED_RASTER_ALPHA_THRESHOLDS) {
          const weakest = [...minimums.values()]
            .filter((minimum) => (
              minimum.scenario === scenario
              && minimum.subtree === subtree.name
              && minimum.alphaThreshold === alphaThreshold
            ))
            .reduce(
              (current, candidate) => (
                !current || candidate.bridgePixels < current.bridgePixels
                  ? candidate
                  : current
              ),
              null,
            );
          minimumJointBridges.push({
            alphaThreshold: weakest.alphaThreshold,
            bridgePixels: weakest.bridgePixels,
            edge: `${weakest.childRole}->${weakest.parentRole}`,
            label: weakest.label,
            minimumBridgePixels: DENSE_BRIDGE_PIXEL_FLOORS[
              weakest.scenario
            ][`${weakest.childRole}->${weakest.parentRole}`],
            scenario: weakest.scenario,
            subtree: weakest.subtree,
          });
        }
      }
    }

    const continuityMetrics = {
      allDenseSamples: allSampleCount,
      clapSamples: DENSE_CLAP_PROGRESS_SAMPLES + 1,
      gaitSamples: DENSE_GAIT_PHASE_SAMPLES,
      minimumJointBridges,
      uniqueRasterPoses: samples.length,
      largestDetachedComponents: [...largestDetachedComponents.values()],
      weakestConnectedFractions: [...weakestConnectedFractions.values()],
    };
    console.log(`POSED_RASTER_CONTINUITY=${JSON.stringify(continuityMetrics)}`);

    const understrengthBridges = bridgeStrengthFailures([...minimums.values()]);
    assert.deepEqual(
      understrengthBridges,
      [],
      'every registered limb joint must meet its resolution-resistant bridge-pixel floor '
        + 'at alpha>0 and alpha>=32 through dense production poses; '
        + `failures=${JSON.stringify(understrengthBridges.map(
          ({
            alphaThreshold,
            bridgePixels,
            edge,
            label,
            minimumBridgePixels,
            subtree,
          }) => ({
            alphaThreshold,
            bridgePixels,
            edge,
            label,
            minimumBridgePixels,
            subtree,
          }),
        ))}`,
    );
    assert.deepEqual(
      componentFailures,
      [],
      'each major limb subtree must remain in the torso-connected alpha component '
        + 'without a detached component larger than 256 raster pixels and with at least '
        + `97% connected coverage at every dense sample; failures=${JSON.stringify(
          componentFailures,
        )}`,
    );
  });

  it('five-layer: keeps source-pinned qameez regions out of rigid limbs', () => {
    const { manifest } = loadManifest();
    const idle = decodeAsset(localAssetPath(manifest.idleFallback.url));
    const torso = decodeAsset(localAssetPath(rootLayer(manifest.layers).url));

    for (const probe of STATIC_TUNIC_OWNERSHIP_PROBES) {
      const movingLayers = layersForCanonicalRole(manifest.layers, probe.role)
        .map((layer) => ({
          role: layer.role,
          stats: decodeAsset(localAssetPath(layer.url)),
        }));
      const pose = PRODUCTION_POSES[probe.pose];
      let sourceBrightPixels = 0;
      let movingOwnedPixels = 0;
      let torsoOwnedPixels = 0;
      let displacedStaticPixels = 0;
      let maximumDisplacement = 0;

      for (let y = probe.rectangle.yMin; y <= probe.rectangle.yMax; y += 1) {
        for (let x = probe.rectangle.xMin; x <= probe.rectangle.xMax; x += 1) {
          if (!isBrightCharacterPixel(assetPixel(idle, x, y))) {
            continue;
          }

          sourceBrightPixels += 1;
          if (assetPixel(torso, x, y).alpha >= 32) {
            torsoOwnedPixels += 1;
          }
          const owners = movingLayers.filter(({ stats }) => (
            assetPixel(stats, x, y).alpha >= 32
          ));
          if (owners.length === 0) {
            continue;
          }

          movingOwnedPixels += 1;
          const displacements = owners.map(({ role }) => {
            const transformed = articulatedPoint(role, { x, y }, pose);
            return Math.hypot(transformed.x - x, transformed.y - y);
          });
          maximumDisplacement = Math.max(maximumDisplacement, ...displacements);
          if (displacements.some((displacement) => displacement >= 1)) {
            displacedStaticPixels += 1;
          }
        }
      }

      assert.ok(
        sourceBrightPixels >= probe.minimumBrightPixels,
        `${probe.role} source-pinned tunic probe must remain meaningful; `
          + `bright pixels=${sourceBrightPixels}`,
      );
      assert.equal(
        movingOwnedPixels,
        0,
        `${probe.role} must not own static qameez pixels; moving=${movingOwnedPixels}, `
          + `maximum production-pose displacement=${maximumDisplacement.toFixed(2)}px`,
      );
      assert.equal(
        displacedStaticPixels,
        0,
        `${probe.role} must not displace static qameez pixels at its production rotation`,
      );
      assert.ok(
        torsoOwnedPixels >= sourceBrightPixels * 0.99,
        `${probe.role} static qameez probe must remain torso-owned; `
          + `torso=${torsoOwnedPixels}, source=${sourceBrightPixels}`,
      );
    }

    const rightArmLayers = layersForCanonicalRole(manifest.layers, 'rightArm')
      .map((layer) => decodeAsset(localAssetPath(layer.url)));
    for (const skinProbe of [
      { xMin: 556, xMax: 565, yMin: 654, yMax: 669, minimumSkinPixels: 120 },
      { xMin: 563, xMax: 568, yMin: 678, yMax: 690, minimumSkinPixels: 50 },
    ]) {
      const sourceSkinPixels = countPixelsInRect(idle, skinProbe, isHandSkinPixel);
      const movingSkinPixels = countPixelsInRect(
        idle,
        skinProbe,
        (pixel, x, y) => (
          isHandSkinPixel(pixel)
          && rightArmLayers.some((rightArm) => assetPixel(rightArm, x, y).alpha >= 32)
        ),
      );
      assert.ok(
        sourceSkinPixels >= skinProbe.minimumSkinPixels,
        `right-hand source skin probe must remain meaningful; skin=${sourceSkinPixels}`,
      );
      assert.equal(
        movingSkinPixels,
        sourceSkinPixels,
        `rightArm must retain all source-pinned finger artwork; `
          + `moving=${movingSkinPixels}, source=${sourceSkinPixels}`,
      );
    }
  });

  it('five-layer: removes the floor line and reflected duplicate from generated alpha', () => {
    const { manifest } = loadManifest();
    const source = manifestSource(manifest);
    const layers = manifest.layers;
    assert.equal(source.reflectionExcluded, true, 'manifest must document reflection exclusion');
    const rowCoverage = new Uint16Array(SOURCE_HEIGHT);

    for (const layer of layers) {
      const filePath = localAssetPath(layerUrl(layer));
      const png = inspectPng(readFileSync(filePath));
      const bounds = layerBounds(layer);
      const { channels, pixels } = decodePngPixels(png);

      for (let localY = 0; localY < png.height; localY += 1) {
        let opaqueInRow = 0;
        for (let localX = 0; localX < png.width; localX += 1) {
          const alpha = pixels[((localY * png.width) + localX) * channels + channels - 1];
          if (alpha >= 32) {
            opaqueInRow += 1;
          }
        }
        rowCoverage[bounds.y + localY] += opaqueInRow;
      }
    }

    const widestRow = Math.max(...rowCoverage);
    assert.ok(
      widestRow < SOURCE_WIDTH * 0.6,
      `no generated row may retain the source floor line; widest alpha row=${widestRow}`,
    );
    const finalRowsCoverage = rowCoverage
      .slice(Math.floor(SOURCE_HEIGHT * 0.985))
      .reduce((sum, value) => sum + value, 0);
    assert.equal(finalRowsCoverage, 0, 'the bottom reflected duplicate must be fully transparent');
  });
});

describe('runtime raster renderer and privacy contracts', () => {
  it('five-layer: serves idle and every registered rigid PNG through the explicit allowlist', async () => {
    const { manifest } = loadManifest();
    const assets = [manifest.idleFallback, ...(manifest.layers ?? [])];
    for (const layer of assets) {
      const url = layerUrl(layer);
      localAssetPath(url);
      const response = await request(url);
      assert.equal(response.statusCode, 200, `${url} must be in the public allowlist`);
      assert.equal(response.headers['content-type'], 'image/png');
      assert.deepEqual(response.body, readFileSync(localAssetPath(url)));
    }
    assert.deepEqual(
      assets.map((layer) => path.basename(layerUrl(layer))).sort(),
      FIVE_LAYER_PNG_NAMES,
    );
  });

  it('does not retain obsolete face assets, references, or inline synthetic SVG geometry', () => {
    assert.equal(
      existsSync(path.join(assetsRoot, 'avatar-face.jpg')),
      false,
      'assets/avatar-face.jpg must be removed',
    );

    const runtimeFiles = [
      path.join(projectRoot, 'index.html'),
      stylesPath,
      serverPath,
      ...discoverNamedModule(sourceRoot, /\.(?:js|mjs)$/i),
    ];
    for (const filePath of runtimeFiles) {
      const source = readFileSync(filePath, 'utf8');
      assert.doesNotMatch(
        source,
        /avatar-face\.jpg|face-photo/i,
        `${path.relative(projectRoot, filePath)} must not reference the obsolete photographic face`,
      );
    }

    const renderSource = readFileSync(path.join(sourceRoot, 'render.js'), 'utf8');
    assert.doesNotMatch(
      renderSource,
      /<(?:svg|path|ellipse|circle)\b/i,
      'render.js must not embed synthetic SVG body geometry',
    );
    assert.match(renderSource, /\.png\b/i, 'render.js must create raster layer images');
  });

  it('sizes the illustrated avatar at 64-70vh without a 580px cap', () => {
    const styles = readFileSync(stylesPath, 'utf8');
    const trackRules = [...styles.matchAll(/\.avatar-track(?:[^{]*)\{([^}]*)\}/gs)]
      .map(([, declarations]) => declarations);
    assert.ok(trackRules.length > 0, 'styles.css must define .avatar-track');

    const heightValues = trackRules.flatMap((declarations) => [
      ...declarations.matchAll(/\bheight\s*:\s*([^;]+);/gi),
    ].map(([, value]) => value.trim()));
    assert.ok(heightValues.length > 0, '.avatar-track must declare height');
    assert.ok(
      heightValues.some((value) => {
        const vhValues = [...value.matchAll(/(\d+(?:\.\d+)?)vh\b/gi)]
          .map(([, number]) => Number(number));
        return vhValues.some((number) => number >= 64 && number <= 70);
      }),
      `.avatar-track height must use 64-70vh; received ${heightValues.join(', ')}`,
    );
    assert.equal(
      heightValues.some((value) => /580px\b/i.test(value)),
      false,
      'avatar height must not retain the obsolete 580px cap',
    );
  });
});
