import { buildKeyMap, validateConfig } from "./config.js";
import { commandForKeyboardEvent } from "./controls.js";
import { createCampusBackground } from "./background.js";
import { Renderer } from "./renderer.js";
import { loadCharacterSprites } from "./sprites.js";
import { advanceTimeline, createSceneState, dismissScene } from "./timeline.js";

const canvas = document.querySelector("#stage");
const legend = document.querySelector("#legend");
const status = document.querySelector("#status");
const errorPanel = document.querySelector("#error");

let config;
let keyMap;
let renderer;
let activeState = null;
let previousTime = performance.now();

function showError(error) {
  console.error(error);
  errorPanel.hidden = false;
  errorPanel.textContent = error instanceof Error ? error.message : String(error);
  status.textContent = "Playground unavailable";
}

function sceneById(sceneId) {
  return config.scenes.find((scene) => scene.id === sceneId);
}

function startScene(sceneId) {
  const scene = sceneById(sceneId);
  if (!scene) {
    showError(new Error(`Runtime error: unknown scene "${sceneId}"`));
    return;
  }
  activeState = createSceneState(scene);
  advanceTimeline(activeState, 0);
  status.textContent = `Playing: ${scene.title}`;
}

function dismissActiveScene() {
  if (!activeState || activeState.dismissed) {
    return;
  }
  dismissScene(activeState);
  status.textContent = "Scene dismissed";
}

function labelForCode(code) {
  return code.startsWith("Digit") ? code.slice(5) : code;
}

function buildLegend() {
  legend.replaceChildren();
  for (const scene of config.scenes) {
    const key = document.createElement("kbd");
    key.textContent = labelForCode(scene.key);
    legend.append(key, document.createTextNode(scene.title), document.createTextNode("  "));
  }
  const escapeKey = document.createElement("kbd");
  escapeKey.textContent = "Esc";
  legend.append(escapeKey, document.createTextNode("Dismiss"));
}

function onKeyDown(event) {
  const command = commandForKeyboardEvent(event, keyMap);
  if (!command) {
    return;
  }
  event.preventDefault();
  if (command.type === "dismiss") {
    dismissActiveScene();
  } else {
    startScene(command.sceneId);
  }
}

function frame(now) {
  const deltaMs = Math.min(100, Math.max(0, now - previousTime));
  previousTime = now;
  if (activeState) {
    advanceTimeline(activeState, deltaMs);
    if (activeState.complete && activeState.dismissed) {
      status.textContent = "Ready - press a scene key";
    }
  }
  renderer.draw(activeState, now);
  requestAnimationFrame(frame);
}

async function initialize() {
  const response = await fetch("./data/playground.v2.json", { cache: "no-store" });
  if (!response.ok) {
    throw new Error(`Configuration error: request failed with HTTP ${response.status}`);
  }
  let rawConfig;
  try {
    rawConfig = await response.json();
  } catch (error) {
    throw new Error(`Configuration error: invalid JSON (${error.message})`);
  }
  config = validateConfig(rawConfig);
  keyMap = buildKeyMap(config);
  const sprites = new Map();
  for (const [id, character] of Object.entries(config.characters)) {
    sprites.set(id, await loadCharacterSprites(character));
  }
  const background = createCampusBackground(config.stage.width, config.stage.height);
  renderer = new Renderer(canvas, config, sprites, background);
  buildLegend();
  window.addEventListener("resize", () => renderer.resize());
  window.addEventListener("keydown", onKeyDown);
  const initialScene = config.scenes.find((scene) => scene.autoPlay);
  status.textContent = "Ready";
  if (initialScene) {
    startScene(initialScene.id);
  }
  requestAnimationFrame(frame);
}

initialize().catch(showError);
