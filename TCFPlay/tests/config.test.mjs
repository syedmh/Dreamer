import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { buildKeyMap, validateConfig } from "../src/config.js";

const v1ConfigUrl = new URL("../data/playground.v1.json", import.meta.url);
const v2ConfigUrl = new URL("../data/playground.v2.json", import.meta.url);

async function loadConfig(url = v2ConfigUrl) {
  return JSON.parse(await readFile(url, "utf8"));
}

test("shipped v2 JSON contains the turn scene and six unsequenced walk frames", async () => {
  const config = validateConfig(await loadConfig());
  const boy = config.characters.boy;
  assert.deepEqual(Object.keys(config.characters), ["boy"]);
  assert.deepEqual(boy.frames, [
    ...Array.from(
      { length: 9 },
      (_, index) => `./assets/boy-turn/turn_${String(index + 1).padStart(2, "0")}.png`
    ),
    ...Array.from(
      { length: 6 },
      (_, index) => `./assets/boy-walk/walk_${String(index + 1).padStart(2, "0")}.png`
    )
  ]);
  assert.deepEqual(boy.animations.front.frames, [0]);
  assert.deepEqual(boy.animations.turnRight.frames, [0, 1, 2, 3, 4, 5, 6, 7, 8]);
  assert.equal(boy.animations.walkRight, undefined);
  assert.equal(boy.frameAnchors.length, 9);
  assert.ok(boy.frameAnchors.every(({ x, y }) => x >= 0 && x <= 1 && y >= 0 && y <= 1));
  assert.equal(boy.frameScales.length, 9);
  assert.ok(boy.frameScales.every((scale) => scale > 0));
  assert.equal(config.scenes.length, 1);
  assert.equal(buildKeyMap(config).get("Digit1"), "boy-turn-right");
  assert.equal(config.scenes[0].autoPlay, undefined);
  assert.equal(
    config.scenes[0].prompt,
    "Start middle of screen front facing then turn right"
  );
  assert.deepEqual(config.scenes[0].actions, [
    { type: "spawn", actor: "boy", x: 0.5, y: 0.9, facing: "front", animation: "front" },
    { type: "turn", actor: "boy", facing: "right", durationMs: 1600, fps: 6 }
  ]);
});

test("v1 remains compatible with the same Boy turn", async () => {
  const legacy = validateConfig(await loadConfig(v1ConfigUrl));
  assert.equal(legacy.schemaVersion, 1);
  assert.equal(buildKeyMap(legacy).get("Digit1"), "boy-turn-right");
  assert.deepEqual(legacy.scenes[0].actions, [
    { type: "spawn", actor: "boy", x: 0.5, y: 0.9, facing: "front", animation: "front" },
    { type: "turn", actor: "boy", facing: "right", durationMs: 1600, fps: 6 }
  ]);
});

test("configuration rejects Girl, extra scenes, autoplay, and old assets", async () => {
  const girl = await loadConfig();
  girl.characters.girl = girl.characters.boy;
  delete girl.characters.boy;
  assert.throws(() => validateConfig(girl), /must contain only "boy"/);

  const extraScene = await loadConfig();
  extraScene.scenes.push({ ...extraScene.scenes[0], id: "another", key: "Digit2" });
  assert.throws(() => validateConfig(extraScene), /one keyboard scene/);

  const autoplay = await loadConfig();
  autoplay.scenes[0].autoPlay = true;
  assert.throws(() => validateConfig(autoplay), /must start only on keypress/);

  const oldAsset = await loadConfig();
  oldAsset.characters.boy.frames[9] = "./assets/walk_frames_1080p/walk_01.png";
  assert.throws(() => validateConfig(oldAsset), /assets\/boy-walk\/walk_01/);
});

test("v2 requires a prompt and forbids authored actions", async () => {
  const missingPrompt = await loadConfig();
  delete missingPrompt.scenes[0].prompt;
  assert.throws(() => validateConfig(missingPrompt), /requires a prompt/);

  const authoredActions = await loadConfig();
  authoredActions.scenes[0].actions = [];
  assert.throws(() => validateConfig(authoredActions), /must not author actions/);
});
