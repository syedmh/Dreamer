import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const projectRoot = fileURLToPath(new URL('..', import.meta.url));
const sourcePath = path.join(projectRoot, 'Avatar.jpg');
const implementationPath = fileURLToPath(new URL('./generate-avatar-assets.ps1', import.meta.url));
const outputDirectory = path.join(projectRoot, 'assets', 'avatar');
const manifestPath = path.join(outputDirectory, 'manifest.json');

const SOURCE = Object.freeze({
  file: 'Avatar.jpg',
  sha256: '04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF',
  width: 896,
  height: 1195,
  reflectionExcluded: true,
});

const LAYERS = Object.freeze([
  { role: 'leftLeg', parent: 'torsoHead', pivot: { x: 420, y: 806 }, zIndex: 10 },
  { role: 'rightLeg', parent: 'torsoHead', pivot: { x: 515, y: 812 }, zIndex: 20 },
  { role: 'torsoHead', parent: null, pivot: { x: 448, y: 785 }, zIndex: 30 },
  { role: 'leftArm', parent: 'torsoHead', pivot: { x: 337, y: 276 }, zIndex: 40 },
  { role: 'rightArm', parent: 'torsoHead', pivot: { x: 560, y: 278 }, zIndex: 50 },
]);

const OBSOLETE_PNG_NAMES = Object.freeze([
  'leftShoe.png',
  'rightShoe.png',
  'leftLowerLeg.png',
  'rightLowerLeg.png',
  'leftUpperLeg.png',
  'rightUpperLeg.png',
  'torso.png',
  'leftUpperArm.png',
  'rightUpperArm.png',
  'leftForearm.png',
  'rightForearm.png',
  'leftHand.png',
  'rightHand.png',
  'head.png',
]);

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex').toUpperCase();
}

function inspectJpegDimensions(buffer) {
  if (buffer[0] !== 0xff || buffer[1] !== 0xd8) {
    throw new Error('Avatar.jpg is not a JPEG');
  }

  let offset = 2;
  while (offset < buffer.length) {
    if (buffer[offset] !== 0xff) {
      throw new Error('Avatar.jpg contains an invalid JPEG segment');
    }
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
    if (segmentLength < 2 || payloadEnd > buffer.length) {
      throw new Error('Avatar.jpg contains a truncated JPEG segment');
    }

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

  throw new Error('Avatar.jpg has no JPEG start-of-frame segment');
}

function validateSource() {
  const source = readFileSync(sourcePath);
  const sourceHash = sha256(source);
  if (sourceHash !== SOURCE.sha256) {
    throw new Error(`Avatar.jpg SHA-256 mismatch: expected ${SOURCE.sha256}, received ${sourceHash}`);
  }

  const dimensions = inspectJpegDimensions(source);
  if (dimensions.width !== SOURCE.width || dimensions.height !== SOURCE.height) {
    throw new Error(
      `Avatar.jpg dimensions mismatch: expected ${SOURCE.width}x${SOURCE.height}, `
      + `received ${dimensions.width}x${dimensions.height}`,
    );
  }
}

function runPowerShell(writeFiles) {
  const args = [
    '-NoLogo',
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    implementationPath,
    '-SourcePath',
    sourcePath,
    '-OutputDirectory',
    outputDirectory,
  ];
  if (writeFiles) {
    args.push('-WriteFiles');
  }

  const result = spawnSync('powershell.exe', args, {
    cwd: projectRoot,
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
  });
  if (result.status !== 0) {
    throw new Error(
      `System.Drawing avatar compiler failed with exit code ${result.status}\n`
      + `stdout=${result.stdout}\nstderr=${result.stderr}`,
    );
  }

  const marker = 'AVATAR_RESULT_JSON=';
  const line = result.stdout
    .split(/\r?\n/)
    .find((candidate) => candidate.startsWith(marker));
  if (!line) {
    throw new Error(`System.Drawing avatar compiler returned no result\nstdout=${result.stdout}`);
  }
  return JSON.parse(line.slice(marker.length));
}

function createManifest(result) {
  const hashes = new Map(result.files.map((file) => [file.name, file.sha256.toUpperCase()]));
  const sourceRect = { x: 0, y: 0, width: SOURCE.width, height: SOURCE.height };

  return {
    version: 2,
    source: SOURCE,
    canvas: { width: SOURCE.width, height: SOURCE.height },
    coordinateSpace: 'Avatar.jpg source pixels',
    layeringNotes:
      'zIndex records the frozen five-layer painter order: whole legs paint behind torsoHead, '
      + 'while whole arms paint above it. White sleeve caps move with their whole arms; green '
      + 'waistcoat, collar, and qameez artwork remains static in torsoHead. Articulated layers '
      + 'overlap only along narrow root contours and within manifest-declared localized garment '
      + 'backing hidden beneath source-faithful whole-arm artwork.',
    backgroundRemoval: {
      method:
        'edge-connected adaptive matte segmentation protected by central artwork seeds, '
        + 'nearest-background color decontamination, a wide dark-edge feather, and a '
        + 'source-space floor/reflection polygon',
      preservesInteriorDarkDetail: true,
      darkEdgeFeatherRadius: 14,
      floorReflectionMask:
        'spatial source-space polygon preserving the illustrated right-shoe contour through y=1171',
    },
    idleFallback: {
      url: './assets/avatar/idle.png',
      sourceRect,
      sha256: hashes.get('idle.png'),
    },
    continuityBacking: [
      {
        name: 'right-waist-qameez-sweep',
        owner: 'torsoHead',
        occludedBy: 'rightArm',
        sweptRegion: {
          xMin: 555,
          xMax: 575,
          yMin: 650,
          yMax: 690,
        },
        maximumPixels: 400,
      },
    ],
    layers: LAYERS.map((layer) => {
      const fileName = `${layer.role}.png`;
      return {
        ...layer,
        url: `./assets/avatar/${fileName}`,
        sourceRect,
        sha256: hashes.get(fileName),
      };
    }),
  };
}

function stableManifestText(manifest) {
  return `${JSON.stringify(manifest, null, 2)}\n`;
}

function verifyFile(filePath, expectedHash) {
  if (!existsSync(filePath)) {
    throw new Error(`Missing generated asset: ${path.relative(projectRoot, filePath)}`);
  }
  const actualHash = sha256(readFileSync(filePath));
  if (actualHash !== expectedHash) {
    throw new Error(
      `${path.relative(projectRoot, filePath)} is stale: expected ${expectedHash}, received ${actualHash}`,
    );
  }
}

function verifyGenerated(result, expectedManifestText) {
  const expectedManifest = JSON.parse(expectedManifestText);
  const expectedPngNames = [
    path.basename(expectedManifest.idleFallback.url),
    ...expectedManifest.layers.map((layer) => path.basename(layer.url)),
  ].sort();
  const actualPngNames = readdirSync(outputDirectory, { withFileTypes: true })
    .filter((entry) => entry.isFile() && /\.png$/i.test(entry.name))
    .map((entry) => entry.name)
    .sort();

  if (result.files.length !== 6) {
    throw new Error(`Expected exactly 6 generated PNGs, received ${result.files.length}`);
  }
  if (JSON.stringify(actualPngNames) !== JSON.stringify(expectedPngNames)) {
    throw new Error(
      `assets/avatar PNG inventory is stale: expected ${expectedPngNames.join(', ')}, `
      + `received ${actualPngNames.join(', ')}`,
    );
  }

  for (const file of result.files) {
    verifyFile(path.join(outputDirectory, file.name), file.sha256.toUpperCase());
  }

  if (!existsSync(manifestPath)) {
    throw new Error('Missing generated asset: assets/avatar/manifest.json');
  }
  const actualManifestText = readFileSync(manifestPath, 'utf8');
  if (actualManifestText !== expectedManifestText) {
    throw new Error('assets/avatar/manifest.json is stale; run npm run build:avatar');
  }

  if (expectedManifest.version !== 2 || expectedManifest.layers.length !== 5) {
    throw new Error(
      `Expected private manifest v2 with exactly 5 articulated layers, received version `
      + `${expectedManifest.version} and ${expectedManifest.layers.length} layers`,
    );
  }
}

const verifyOnly = process.argv.includes('--verify');
const unknownArguments = process.argv.slice(2).filter((argument) => argument !== '--verify');
if (unknownArguments.length > 0) {
  throw new Error(`Unknown argument(s): ${unknownArguments.join(', ')}`);
}

validateSource();
if (!verifyOnly) {
  mkdirSync(outputDirectory, { recursive: true });
}
const result = runPowerShell(!verifyOnly);
const manifestText = stableManifestText(createManifest(result));

if (verifyOnly) {
  verifyGenerated(result, manifestText);
  console.log(`Verified 6 deterministic avatar PNGs and manifest from ${SOURCE.sha256}.`);
} else {
  for (const obsoleteName of OBSOLETE_PNG_NAMES) {
    const obsoletePath = path.join(outputDirectory, obsoleteName);
    if (existsSync(obsoletePath)) {
      rmSync(obsoletePath);
    }
  }
  writeFileSync(manifestPath, manifestText, 'utf8');
  verifyGenerated(result, manifestText);
  console.log(`Generated 6 deterministic avatar PNGs and manifest from ${SOURCE.sha256}.`);
}
