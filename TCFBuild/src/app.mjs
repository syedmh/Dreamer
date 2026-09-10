import {
  addRaisedAmount,
  clampAmount,
  deriveDemoRaised,
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
const money = createCurrencyFormatter(config.locale, config.currency);

let state = {
  raised: config.raised,
  goal: config.goal,
  demoActive: false,
  nightMode: false,
  studentsClapping: false,
  thankYouVisible: true,
  continuousFireworks: false
};
let goal = state.goal;
let targetRaised = state.raised;
let displayedRaised = targetRaised;
let demoActive = state.demoActive;
let demoStartedAt = performance.now();
let lastDemoPublishAt = 0;
let demoPublishPending = false;
let lastFrameAt = performance.now();
let celebrationUntil = 0;
let previousDisplayedRatio = displayedRaised / goal;
let framePending = false;
let continuousFireworksEnabled = false;
let continuousFireworksTimeoutId = 0;
let fireworksCleanupComplete = false;
let nextActionSequence = 0;
const executedActionIds = new Set();
const MAX_EXECUTED_ACTION_IDS = 256;

const mediaReduced = window.matchMedia("(prefers-reduced-motion: reduce)");
const reducedMotion = () => config.motion === "reduce"
  || (config.motion === "auto" && mediaReduced.matches);

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

function applyState(nextState, { resetDemoClock = true } = {}) {
  const demoStarting = !demoActive && nextState.demoActive;
  state = { ...state, ...nextState };
  goal = state.goal;
  targetRaised = clampAmount(state.raised);
  demoActive = state.demoActive;
  view.setNightMode(state.nightMode);
  view.setStudentsClapping(state.studentsClapping);
  view.setThankYouVisible(state.thankYouVisible);
  synchronizeContinuousFireworks(state.continuousFireworks);
  if (demoStarting && resetDemoClock) {
    demoStartedAt = performance.now();
    lastDemoPublishAt = 0;
  }
  scheduleFrame();
}

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

async function mutateState(patch, successMessage) {
  applyState(patch);
  try {
    const authoritativeState = await requestJson("/api/state", {
      method: "PATCH",
      body: JSON.stringify(patch)
    });
    applyState(authoritativeState, { resetDemoClock: false });
    if (successMessage) announce(successMessage);
    return true;
  } catch {
    announce("Display updated locally; dashboard synchronization is temporarily unavailable.");
    return false;
  }
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
    case "firework.clear":
      view.clearFireworks();
      message = "All fireworks cleared.";
      break;
    case "school.add": {
      const previousCount = Number(root.dataset.distantSchools || 0);
      const count = view.addDistantSchool({ reducedMotion: reducedMotion() });
      if (count > previousCount) {
        view.addTcfFirework({ reducedMotion: reducedMotion() });
      }
      message = count === previousCount
        ? "All four distant schools are already present."
        : `Distant school ${count} added.`;
      break;
    }
    case "school.remove": {
      const previousCount = Number(root.dataset.distantSchools || 0);
      const count = view.removeLastDistantSchool();
      message = count === previousCount
        ? "There are no distant schools to remove."
        : `Distant school ${previousCount} removed.`;
      break;
    }
    default:
      return "";
  }
  rememberActionId(id);
  if (announceResult) announce(message);
  return message;
}

async function requestAction(type) {
  const action = { type, id: createActionId() };
  const message = executeAction(action);
  try {
    await requestJson("/api/actions", {
      method: "POST",
      body: JSON.stringify(action)
    });
  } catch {
    if (message) {
      announce(`${message} Control synchronization is temporarily unavailable.`);
    }
  }
}

function connectEvents() {
  if (typeof EventSource !== "function") {
    root.dataset.connection = "unavailable";
    announce("Live control connection is unavailable; keyboard controls remain active.");
    return;
  }
  const events = new EventSource("/events");
  events.addEventListener("open", () => {
    root.dataset.connection = "connected";
  });
  events.addEventListener("snapshot", (event) => {
    const snapshot = JSON.parse(event.data);
    applyState(snapshot.state);
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

  if (isSpace(event)) {
    event.preventDefault();
    keyboardHint.hidden = !keyboardHint.hidden;
    announce(keyboardHint.hidden ? "Keyboard legends hidden." : "Keyboard legends shown.");
    return;
  }

  const stepFraction = event.shiftKey ? .05 : .01;
  switch (event.key.toLowerCase()) {
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
      mutateState(
        { demoActive: !state.demoActive },
        state.demoActive
          ? "Fundraiser demonstration paused."
          : "Fundraiser demonstration started."
      );
      break;
    case "n":
      event.preventDefault();
      mutateState(
        { nightMode: !state.nightMode },
        state.nightMode ? "Day mode enabled." : "Night mode enabled."
      );
      break;
    case "o":
      event.preventDefault();
      mutateState(
        { studentsClapping: !state.studentsClapping },
        state.studentsClapping ? "Students stopped clapping." : "Students started clapping."
      );
      break;
    case "p":
      event.preventDefault();
      mutateState(
        { thankYouVisible: !state.thankYouVisible },
        state.thankYouVisible
          ? "Student thank-you messages hidden."
          : "Student thank-you messages enabled."
      );
      break;
    case "w":
      event.preventDefault();
      mutateState(
        { continuousFireworks: !state.continuousFireworks },
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
      {
        const raised = addRaisedAmount(targetRaised, goal, stepFraction);
        mutateState({
          raised,
          demoActive: false
        }, `${money.format(raised)} raised toward a goal of ${money.format(goal)}.`);
      }
      break;
    case "arrowleft":
    case "arrowdown":
      event.preventDefault();
      {
        const raised = addRaisedAmount(targetRaised, goal, -stepFraction);
        mutateState({
          raised,
          demoActive: false
        }, `${money.format(raised)} raised toward a goal of ${money.format(goal)}.`);
      }
      break;
    case "home":
      event.preventDefault();
      mutateState({
        raised: 0,
        demoActive: false
      }, `${money.format(0)} raised toward a goal of ${money.format(goal)}.`);
      break;
    case "end":
      event.preventDefault();
      mutateState({
        raised: goal,
        demoActive: false
      }, `${money.format(goal)} raised toward a goal of ${money.format(goal)}.`);
      break;
  }
});

document.addEventListener("visibilitychange", handleVisibilityChange);
window.addEventListener("pagehide", cleanupFireworks, { once: true });
window.addEventListener("unload", cleanupFireworks, { once: true });

async function publishDemoProgress(now) {
  if (demoPublishPending || now - lastDemoPublishAt < 250) return;
  demoPublishPending = true;
  lastDemoPublishAt = now;
  try {
    await requestJson("/api/state", {
      method: "PATCH",
      body: JSON.stringify({ raised: targetRaised })
    });
  } catch {
    root.dataset.connection = "reconnecting";
  } finally {
    demoPublishPending = false;
  }
}

function frame(now) {
  const delta = Math.min(50, Math.max(0, now - lastFrameAt));
  lastFrameAt = now;
  let demoCompleted = false;

  if (demoActive) {
    const elapsed = Math.max(0, now - demoStartedAt);
    demoCompleted = elapsed >= config.demoDurationMs;
    targetRaised = deriveDemoRaised(
      goal,
      demoCompleted ? 1 : elapsed / config.demoDurationMs
    );
    state.raised = targetRaised;
    publishDemoProgress(now);
  }

  displayedRaised = demoCompleted || reducedMotion()
    ? targetRaised
    : exponentialStep(displayedRaised, targetRaised, delta, config.animationTimeConstantMs);
  if (Math.abs(displayedRaised - targetRaised) < .01) displayedRaised = targetRaised;

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
  if (demoActive && demoCompleted && !renderResult.needsFrame) {
    demoStartedAt = now;
    lastDemoPublishAt = 0;
  }
  if (
    demoActive
    || displayedRaised !== targetRaised
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
