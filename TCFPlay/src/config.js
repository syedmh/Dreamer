import { compilePrompt } from "./prompt-compiler.js";

const ACTION_TYPES = new Set(["spawn", "turn", "wait", "moveTo"]);
const FACINGS = new Set(["front", "right"]);
const EXPECTED_FRAME_PATHS = [
  ...Array.from(
    { length: 9 },
    (_, index) => `./assets/boy-turn/turn_${String(index + 1).padStart(2, "0")}.png`
  ),
  ...Array.from(
    { length: 6 },
    (_, index) => `./assets/boy-walk/walk_${String(index + 1).padStart(2, "0")}.png`
  )
];

function assert(condition, message) {
  if (!condition) {
    throw new Error(`Configuration error: ${message}`);
  }
}

function finiteNumber(value) {
  return typeof value === "number" && Number.isFinite(value);
}

function validateAction(action, sceneId, index, characterIds) {
  const at = `scene "${sceneId}" action ${index + 1}`;
  assert(action && typeof action === "object", `${at} must be an object`);
  assert(ACTION_TYPES.has(action.type), `${at} has unsupported type "${action.type}"`);
  if (action.type !== "wait") {
    assert(
      typeof action.actor === "string" && characterIds.has(action.actor),
      `${at} references an unknown actor`
    );
  }
  if (action.type === "spawn" || action.type === "moveTo") {
    assert(finiteNumber(action.x) && finiteNumber(action.y), `${at} requires finite x and y`);
  }
  if (action.type === "spawn" || action.type === "turn") {
    assert(FACINGS.has(action.facing), `${at} requires facing front or right`);
  }
  if (action.type === "turn" || action.type === "wait" || action.type === "moveTo") {
    assert(
      finiteNumber(action.durationMs) && action.durationMs >= 0,
      `${at} requires a non-negative durationMs`
    );
  }
  if (action.type === "moveTo") {
    assert(finiteNumber(action.walkFps) && action.walkFps > 0, `${at} requires positive walkFps`);
  }
}

export function validateConfig(config) {
  assert(config && typeof config === "object", "root must be an object");
  assert(config.schemaVersion === 1 || config.schemaVersion === 2, `unsupported schemaVersion "${config.schemaVersion}"`);
  assert(config.stage && finiteNumber(config.stage.width) && config.stage.width > 0, "stage.width must be positive");
  assert(config.stage && finiteNumber(config.stage.height) && config.stage.height > 0, "stage.height must be positive");
  assert(finiteNumber(config.stage.maxBackingWidth) && config.stage.maxBackingWidth > 0, "stage.maxBackingWidth must be positive");
  assert(finiteNumber(config.stage.maxBackingHeight) && config.stage.maxBackingHeight > 0, "stage.maxBackingHeight must be positive");
  assert(config.characters && typeof config.characters === "object", "characters must be an object");

  const characterIds = new Set(Object.keys(config.characters));
  assert(characterIds.size === 1 && characterIds.has("boy"), 'characters must contain only "boy"');
  for (const [id, character] of Object.entries(config.characters)) {
    assert(Array.isArray(character.frames), `character "${id}" requires frames`);
    assert(character.name === "Boy", `character "${id}" name must be "Boy"`);
    assert(character.frames.length === 15, `character "${id}" must contain nine turn frames and six review walk frames`);
    assert(character.frames.every((frame) => typeof frame === "string" && frame.endsWith(".png")), `character "${id}" frame paths must be PNG files`);
    EXPECTED_FRAME_PATHS.forEach((expectedPath, index) => {
      assert(
        character.frames[index] === expectedPath,
        `character "${id}" frame ${index + 1} must be "${expectedPath}"`
      );
    });
    assert(finiteNumber(character.renderHeight) && character.renderHeight > 0, `character "${id}" requires renderHeight`);
    assert(character.anchor && finiteNumber(character.anchor.x) && finiteNumber(character.anchor.y), `character "${id}" requires an anchor`);
    assert(
      Array.isArray(character.frameAnchors) &&
      character.frameAnchors.length === 9 &&
      character.frameAnchors.every(
        (anchor) =>
          anchor &&
          finiteNumber(anchor.x) &&
          finiteNumber(anchor.y) &&
          anchor.x >= 0 &&
          anchor.x <= 1 &&
          anchor.y >= 0 &&
          anchor.y <= 1
      ),
      `character "${id}" requires one normalized frame anchor per turn frame`
    );
    assert(
      Array.isArray(character.frameScales) &&
      character.frameScales.length === 9 &&
      character.frameScales.every((scale) => finiteNumber(scale) && scale > 0),
      `character "${id}" requires one positive frame scale per turn frame`
    );
    assert(character.animations && character.animations.front && character.animations.turnRight, `character "${id}" requires front and turnRight animations`);
    assert(!character.animations.walkRight, `character "${id}" must not define a walk sequence before review`);
    assert(
      JSON.stringify(character.animations.front.frames) === JSON.stringify([0]),
      `character "${id}" front must use frame 0`
    );
    assert(
      JSON.stringify(character.animations.turnRight.frames) === JSON.stringify([0, 1, 2, 3, 4, 5, 6, 7, 8]),
      `character "${id}" turnRight must use frames 0 through 8 in order`
    );
  }

  assert(Array.isArray(config.scenes) && config.scenes.length === 1, "one keyboard scene is required");
  const scenes = config.scenes.map((scene) => {
    if (config.schemaVersion === 1) {
      return scene;
    }
    assert(typeof scene.prompt === "string" && scene.prompt.length > 0, `scene "${scene.id}" requires a prompt`);
    assert(!Object.hasOwn(scene, "actions"), `scene "${scene.id}" must not author actions in schemaVersion 2`);
    return { ...scene, actions: compilePrompt(scene.prompt, config.characters) };
  });
  const normalizedConfig = config.schemaVersion === 2 ? { ...config, scenes } : config;
  const sceneIds = new Set();
  const keys = new Set();
  for (const scene of normalizedConfig.scenes) {
    assert(typeof scene.id === "string" && scene.id.length > 0, "scene requires an id");
    assert(!sceneIds.has(scene.id), `duplicate scene id "${scene.id}"`);
    sceneIds.add(scene.id);
    assert(typeof scene.title === "string" && scene.title.length > 0, `scene "${scene.id}" requires a title`);
    assert(typeof scene.key === "string" && !keys.has(scene.key), `scene "${scene.id}" requires a unique key`);
    keys.add(scene.key);
    assert(Array.isArray(scene.actions) && scene.actions.length > 0, `scene "${scene.id}" requires actions`);
    scene.actions.forEach((action, index) => validateAction(action, scene.id, index, characterIds));
  }
  assert(buildKeyMap(normalizedConfig).has("Digit1"), "Digit1 scene is required");
  assert(normalizedConfig.scenes[0].key === "Digit1", "the only scene must use Digit1");
  assert(normalizedConfig.scenes.every((scene) => scene.autoPlay !== true), "the boy scene must start only on keypress");
  return normalizedConfig;
}

export function buildKeyMap(config) {
  return new Map(config.scenes.map((scene) => [scene.key, scene.id]));
}
