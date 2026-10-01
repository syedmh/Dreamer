import {
  clampAmount,
  DEFAULT_CONFIG,
  parseConfig
} from "./config.mjs";
import { createCurrencyFormatter } from "./currency.mjs";
import { deriveProgress, exponentialStep } from "./model.mjs";
import { createScene } from "./scene.mjs";
import { createFundraiserView } from "./render.mjs";

const config = parseConfig(window.location.search);
const root = document.querySelector("#fundraiser");
const scene = createScene(config);
const view = createFundraiserView(root, scene, config);
const announcer = document.querySelector("#announcer");
const keyboardHint = document.querySelector("#keyboard-hint");
const buildSummaryCard = document.querySelector("#build-summary-card");
const seattleSchoolsDisplay = document.querySelector("#seattle-schools-display");
const totalBox = document.querySelector("#total-box");
const totalBoxAmount = document.querySelector("#total-box-amount");
const schoolScene = root.querySelector("#school-scene");
const money = createCurrencyFormatter(config.locale, config.currency);

let state = {
  raised: DEFAULT_CONFIG.raised,
  goal: DEFAULT_CONFIG.goal,
  overrideRaised: DEFAULT_CONFIG.raised,
  seattleSchools: 0,
  operationCost: 0,
  demoActive: false,
  nightMode: false,
  studentsClapping: false,
  thankYouVisible: false,
  continuousFireworks: false,
  buildSummaryVisible: false,
  totalBoxVisible: false,
  keyboardLegendVisible: false,
  keypressEnabled: false,
  wideScreen: false,
  distantSchools: []
};
let goal = state.goal;
let targetRaised = state.raised;
let displayedRaised = targetRaised;
let targetOverrideRaised = state.overrideRaised;
let displayedOverrideRaised = targetOverrideRaised;
let lastFrameAt = performance.now();
let celebrationUntil = 0;
let previousDisplayedRatio = displayedRaised / goal;
let framePending = false;
let continuousFireworksEnabled = false;
let continuousFireworksTimeoutId = 0;
let fireworksCleanupComplete = false;
let nextActionSequence = 0;
let highestAuthoritativeRevision = null;
let initialUrlStateSent = false;
const executedActionIds = new Set();
const MAX_EXECUTED_ACTION_IDS = 256;

const mediaReduced = window.matchMedia("(prefers-reduced-motion: reduce)");
const reducedMotion = () => config.motion === "reduce"
  || (config.motion === "auto" && mediaReduced.matches);
const BASE_SCENE_WIDTH = 1600;
const BASE_SCENE_HEIGHT = 900;

function formatViewBoxNumber(value) {
  return String(Number(value.toFixed(3)));
}

function deriveDisplayViewBox(wideScreen) {
  if (!wideScreen) return "0 0 1600 900";
  const viewportWidth = Math.max(1, Number(window.innerWidth) || BASE_SCENE_WIDTH);
  const viewportHeight = Math.max(1, Number(window.innerHeight) || BASE_SCENE_HEIGHT);
  const viewportAspect = viewportWidth / viewportHeight;
  const sceneAspect = BASE_SCENE_WIDTH / BASE_SCENE_HEIGHT;

  if (viewportAspect >= sceneAspect) {
    const width = BASE_SCENE_HEIGHT * viewportAspect;
    const x = (BASE_SCENE_WIDTH - width) / 2;
    return `${formatViewBoxNumber(x)} 0 ${formatViewBoxNumber(width)} 900`;
  }

  const height = BASE_SCENE_WIDTH / viewportAspect;
  const y = BASE_SCENE_HEIGHT - height;
  return `0 ${formatViewBoxNumber(y)} 1600 ${formatViewBoxNumber(height)}`;
}

function synchronizeDisplayMode() {
  const wideScreen = Boolean(state.wideScreen);
  root.dataset.displayMode = wideScreen ? "wide" : "standard";
  schoolScene?.setAttribute("preserveAspectRatio", "xMidYMid meet");
  schoolScene?.setAttribute("viewBox", deriveDisplayViewBox(wideScreen));
}

function announce(message) {
  announcer.textContent = message;
}

function scheduleFrame() {
  if (framePending) return;
  framePending = true;
  requestAnimationFrame((now) => {
    framePending = false;
    frame(now);
  });
}

function synchronizeMotionState() {
  root.dataset.motion = reducedMotion() ? "reduce" : "full";
}

function randomContinuousDelay() {
  const [minimum, maximum] = reducedMotion()
    ? [900, 1700]
    : [350, 1100];
  return Math.round(minimum + Math.random() * (maximum - minimum));
}

function setContinuousFireworksState(stateName) {
  root.dataset.continuousFireworks = String(continuousFireworksEnabled);
  root.dataset.fireworksScheduler = stateName;
  root.dataset.fireworksReducedMotion = String(reducedMotion());
}

function cancelContinuousFireworksTimeout() {
  if (!continuousFireworksTimeoutId) return;
  clearTimeout(continuousFireworksTimeoutId);
  continuousFireworksTimeoutId = 0;
  delete root.dataset.nextFireworkDelayMs;
}

function scheduleContinuousFireworks() {
  if (!continuousFireworksEnabled) {
    setContinuousFireworksState("stopped");
    return;
  }
  if (document.hidden) {
    setContinuousFireworksState("paused-hidden");
    return;
  }
  if (continuousFireworksTimeoutId) return;

  const delay = randomContinuousDelay();
  root.dataset.nextFireworkDelayMs = String(delay);
  setContinuousFireworksState("scheduled");
  continuousFireworksTimeoutId = setTimeout(() => {
    continuousFireworksTimeoutId = 0;
    delete root.dataset.nextFireworkDelayMs;
    if (!continuousFireworksEnabled || document.hidden) {
      scheduleContinuousFireworks();
      return;
    }
    setContinuousFireworksState("launching");
    const launchCount = reducedMotion() || Math.random() >= .28 ? 1 : 2;
    for (let index = 0; index < launchCount; index += 1) {
      view.addFirework({
        reducedMotion: reducedMotion(),
        source: "continuous"
      });
    }
    scheduleContinuousFireworks();
  }, delay);
}

function synchronizeContinuousFireworks(enabled) {
  if (enabled === continuousFireworksEnabled) return;
  continuousFireworksEnabled = enabled;
  if (enabled) {
    scheduleContinuousFireworks();
  } else {
    cancelContinuousFireworksTimeout();
    setContinuousFireworksState("stopped");
  }
}

function applyState(nextState) {
  const revision = Number.isInteger(nextState?.revision)
    && nextState.revision >= 0
    ? nextState.revision
    : null;
  if (
    revision !== null
    && highestAuthoritativeRevision !== null
    && revision < highestAuthoritativeRevision
  ) {
    return false;
  }
  if (revision !== null) highestAuthoritativeRevision = revision;
  state = { ...state, ...nextState };
  goal = state.goal;
  targetRaised = clampAmount(state.raised);
  targetOverrideRaised = clampAmount(state.overrideRaised);
  view.setNightMode(state.nightMode);
  view.setStudentsClapping(state.studentsClapping);
  view.setThankYouVisible(state.thankYouVisible);
  synchronizeDisplayMode();
  keyboardHint.hidden = !state.keyboardLegendVisible;
  if (buildSummaryCard) {
    buildSummaryCard.classList.toggle(
      "is-hidden",
      !state.buildSummaryVisible
    );
    buildSummaryCard.setAttribute(
      "aria-hidden",
      String(!state.buildSummaryVisible)
    );
  }
  if (totalBox) {
    totalBox.classList.toggle("is-hidden", !state.totalBoxVisible);
    totalBox.setAttribute("aria-hidden", String(!state.totalBoxVisible));
  }
  if (seattleSchoolsDisplay) {
    seattleSchoolsDisplay.textContent = Number(state.seattleSchools).toLocaleString(
      config.locale
    );
  }
  view.reconcileDistantSchools(state.distantSchools, {
    now: Date.now(),
    reducedMotion: reducedMotion()
  });
  synchronizeContinuousFireworks(state.continuousFireworks);
  scheduleFrame();
  return true;
}

window.addEventListener("resize", () => {
  if (state.wideScreen) synchronizeDisplayMode();
});

async function requestJson(url, options) {
  const response = await fetch(url, {
    ...options,
    headers: {
      "Content-Type": "application/json",
      ...options?.headers
    }
  });
  const body = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(body.error || `Request failed with status ${response.status}.`);
  }
  return body;
}

async function refetchState() {
  const authoritativeState = await requestJson("/api/state");
  applyState(authoritativeState);
  return authoritativeState;
}

async function mutate(url, body, successMessage) {
  try {
    const authoritativeState = await requestJson(url, {
      method: url === "/api/state" ? "PATCH" : "POST",
      body: JSON.stringify(body)
    });
    const applied = applyState(authoritativeState);
    if (applied && successMessage) announce(successMessage);
    return true;
  } catch (error) {
    try {
      await refetchState();
    } catch {
      root.dataset.connection = "reconnecting";
    }
    announce(error.message);
    return false;
  }
}

function patchState(patch, successMessage) {
  return mutate("/api/state", patch, successMessage);
}

function sendCommand(command, successMessage) {
  return mutate("/api/commands", command, successMessage);
}

function rememberActionId(actionId) {
  if (!actionId) return;
  executedActionIds.add(actionId);
  if (executedActionIds.size <= MAX_EXECUTED_ACTION_IDS) return;
  executedActionIds.delete(executedActionIds.values().next().value);
}

function createActionId() {
  nextActionSequence += 1;
  return `${Date.now().toString(36)}-${nextActionSequence.toString(36)}`;
}

function executeAction(action, { announceResult = true } = {}) {
  const { type, id = "" } = typeof action === "string"
    ? { type: action }
    : action;
  if (id && executedActionIds.has(id)) return "";

  let message = "";
  switch (type) {
    case "kite.add": {
      const count = view.addKite();
      message = `Kite added. ${count} ${count === 1 ? "kite" : "kites"} in the sky.`;
      break;
    }
    case "kite.clear":
      view.clearKites();
      message = "All kites removed.";
      break;
    case "firework.launch":
      view.addFirework({ reducedMotion: reducedMotion(), source: "manual" });
      message = "Firework launched.";
      break;
    case "firework.tcf":
      view.addTcfFirework({ reducedMotion: reducedMotion() });
      message = "TCF celebration firework launched.";
      break;
    case "firework.clear":
      view.clearFireworks();
      message = "All fireworks cleared.";
      break;
    case "total.drop":
      if (totalBox) {
        totalBox.classList.remove("is-hidden", "is-dropping");
        totalBox.setAttribute("aria-hidden", "false");
        void totalBox.offsetWidth;
        totalBox.classList.add("is-dropping");
      }
      message = "Fundraising total dropped into view.";
      break;
    case "school.add": {
      view.addTcfFirework({ reducedMotion: reducedMotion() });
      message = "Distant school drop accepted.";
      break;
    }
    case "school.remove":
      message = "Distant school removal accepted.";
      break;
    default:
      return "";
  }
  rememberActionId(id);
  if (announceResult) announce(message);
  return message;
}

async function requestAction(type) {
  const action = { type, id: createActionId() };
  try {
    const result = await requestJson("/api/actions", {
      method: "POST",
      body: JSON.stringify(action)
    });
    if (result.state) applyState(result.state);
  } catch (error) {
    try {
      await refetchState();
    } catch {
      root.dataset.connection = "reconnecting";
    }
    announce(error.message);
  }
}

function connectEvents() {
  if (typeof EventSource !== "function") {
    root.dataset.connection = "unavailable";
    announce("Live control connection is unavailable; keyboard controls remain active.");
    return;
  }
  const role = new URLSearchParams(window.location.search).get("client") === "preview"
    ? "preview"
    : "presentation";
  const events = new EventSource(`/events?role=${role}`);
  events.addEventListener("open", () => {
    root.dataset.connection = "connected";
  });
  events.addEventListener("snapshot", (event) => {
    const snapshot = JSON.parse(event.data);
    applyState(snapshot.state);
    if (!initialUrlStateSent) {
      initialUrlStateSent = true;
      if (Object.keys(config.authoritativeInitialState).length > 0) {
        patchState(config.authoritativeInitialState);
      }
    }
  });
  events.addEventListener("state", (event) => {
    applyState(JSON.parse(event.data));
  });
  events.addEventListener("action", (event) => {
    executeAction(JSON.parse(event.data));
  });
  events.addEventListener("error", () => {
    root.dataset.connection = "reconnecting";
    announce("Live control connection lost. Reconnecting automatically; keyboard controls remain active.");
  });
}

function handleMotionPreferenceChange() {
  synchronizeMotionState();
  if (reducedMotion()) celebrationUntil = 0;
  if (continuousFireworksEnabled) {
    cancelContinuousFireworksTimeout();
    scheduleContinuousFireworks();
  }
  scheduleFrame();
}

if (config.motion === "auto") {
  if (typeof mediaReduced.addEventListener === "function") {
    mediaReduced.addEventListener("change", handleMotionPreferenceChange);
  } else {
    mediaReduced.addListener?.(handleMotionPreferenceChange);
  }
}

function handleVisibilityChange() {
  if (!continuousFireworksEnabled) return;
  if (document.hidden) {
    cancelContinuousFireworksTimeout();
    setContinuousFireworksState("paused-hidden");
  } else {
    scheduleContinuousFireworks();
  }
}

function cleanupFireworks() {
  if (fireworksCleanupComplete) return;
  fireworksCleanupComplete = true;
  continuousFireworksEnabled = false;
  cancelContinuousFireworksTimeout();
  view.clearFireworks();
}

function isEditingTarget(target) {
  const tagName = String(target?.tagName || "").toLowerCase();
  return ["input", "button", "select", "textarea"].includes(tagName)
    || target?.isContentEditable
    || Boolean(target?.closest?.(
      '[contenteditable]:not([contenteditable="false"]), [role="textbox"]'
    ))
    || document.designMode === "on";
}

function isPlainKey(event, key) {
  return String(event.key).toLowerCase() === key
    && !event.ctrlKey
    && !event.metaKey
    && !event.altKey
    && !event.repeat;
}

function isSpace(event) {
  return event.key === " " || event.key === "Spacebar" || event.code === "Space";
}

function toggleFullscreen() {
  const fullscreenAction = !document.fullscreenElement
    ? root.requestFullscreen?.()
    : document.exitFullscreen?.();
  fullscreenAction?.catch?.(() => {});
}

document.addEventListener("keydown", (event) => {
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  if (isEditingTarget(event.target)) return;
  if (!state.keypressEnabled) return;

  if (isSpace(event)) {
    event.preventDefault();
    sendCommand(
      { type: "state.toggle", field: "keyboardLegendVisible" },
      state.keyboardLegendVisible
        ? "Keyboard legends hidden."
        : "Keyboard legends shown."
    );
    return;
  }

  const stepFraction = event.shiftKey ? .05 : .01;
  switch (event.key.toLowerCase()) {
    case "y": {
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "buildSummaryVisible" },
        state.buildSummaryVisible
          ? "Seattle Schools count hidden."
          : "Seattle Schools count shown."
      );
      break;
    }
    case "s":
      event.preventDefault();
      requestAction("school.add");
      break;
    case "x":
      event.preventDefault();
      requestAction("school.remove");
      break;
    case "d":
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "demoActive" },
        state.demoActive
          ? "Fundraiser demonstration paused."
          : "Fundraiser demonstration started."
      );
      break;
    case "n":
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "nightMode" },
        state.nightMode ? "Day mode enabled." : "Night mode enabled."
      );
      break;
    case "o":
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "studentsClapping" },
        state.studentsClapping ? "Students stopped clapping." : "Students started clapping."
      );
      break;
    case "p":
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "thankYouVisible" },
        state.thankYouVisible
          ? "Student thank-you messages hidden."
          : "Student thank-you messages enabled."
      );
      break;
    case "w":
      event.preventDefault();
      sendCommand(
        { type: "state.toggle", field: "continuousFireworks" },
        state.continuousFireworks
          ? "Continuous fireworks stopped."
          : "Continuous fireworks started."
      );
      break;
    case "k":
      event.preventDefault();
      requestAction("kite.add");
      break;
    case "l":
      event.preventDefault();
      requestAction("kite.clear");
      break;
    case "q":
      event.preventDefault();
      requestAction("firework.launch");
      break;
    case "f":
      event.preventDefault();
      toggleFullscreen();
      break;
    case "arrowright":
    case "arrowup":
      event.preventDefault();
      sendCommand(
        { type: "raised.step", fraction: stepFraction },
        `Raised amount increased toward a goal of ${money.format(goal)}.`
      );
      break;
    case "arrowleft":
    case "arrowdown":
      event.preventDefault();
      sendCommand(
        { type: "raised.step", fraction: -stepFraction },
        `Raised amount decreased toward a goal of ${money.format(goal)}.`
      );
      break;
    case "home":
      event.preventDefault();
      sendCommand(
        { type: "raised.setRatio", ratio: 0 },
        `${money.format(0)} raised toward a goal of ${money.format(goal)}.`
      );
      break;
    case "end":
      event.preventDefault();
      sendCommand(
        { type: "raised.setRatio", ratio: 1 },
        `${money.format(goal)} raised toward a goal of ${money.format(goal)}.`
      );
      break;
  }
});

document.addEventListener("visibilitychange", handleVisibilityChange);
window.addEventListener("pagehide", cleanupFireworks, { once: true });
window.addEventListener("unload", cleanupFireworks, { once: true });

function frame(now) {
  const delta = Math.min(50, Math.max(0, now - lastFrameAt));
  lastFrameAt = now;

  displayedRaised = reducedMotion()
    ? targetRaised
    : exponentialStep(displayedRaised, targetRaised, delta, config.animationTimeConstantMs);
  if (Math.abs(displayedRaised - targetRaised) < .01) displayedRaised = targetRaised;
  displayedOverrideRaised = reducedMotion()
    ? targetOverrideRaised
    : exponentialStep(
      displayedOverrideRaised,
      targetOverrideRaised,
      delta,
      config.animationTimeConstantMs
    );
  if (Math.abs(displayedOverrideRaised - targetOverrideRaised) < .01) {
    displayedOverrideRaised = targetOverrideRaised;
  }
  if (totalBoxAmount) totalBoxAmount.textContent = money.format(displayedOverrideRaised);

  const progress = deriveProgress(displayedRaised, goal, config.overGoalRamp);
  if (previousDisplayedRatio < 1 && progress.donationRatio >= 1) {
    celebrationUntil = reducedMotion() ? now : now + 2200;
  }
  previousDisplayedRatio = progress.donationRatio;
  synchronizeMotionState();
  const renderResult = view.update({
    raised: displayedRaised,
    goal,
    ...progress,
    deltaMs: delta,
    reducedMotion: reducedMotion(),
    celebrationActive: now < celebrationUntil
  });
  if (
    displayedRaised !== targetRaised
    || displayedOverrideRaised !== targetOverrideRaised
    || now < celebrationUntil
    || renderResult.needsFrame
  ) {
    scheduleFrame();
  }
}

synchronizeMotionState();
view.setNightMode(state.nightMode);
view.setStudentsClapping(state.studentsClapping);
view.setThankYouVisible(state.thankYouVisible);
setContinuousFireworksState("stopped");
connectEvents();
scheduleFrame();
