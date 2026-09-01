import {
  addRaisedAmount,
  clampAmount,
  deriveDemoRaised,
  deriveOperatorControlValues,
  deriveRaisedFromSliderValue,
  parseConfig,
  validateOperatorAmounts
} from "./config.mjs";
import { createCurrencyFormatter } from "./currency.mjs";
import { deriveProgress, exponentialStep } from "./model.mjs";
import { createScene } from "./scene.mjs";
import { createFundraiserView } from "./render.mjs";

const config = parseConfig(window.location.search);
const root = document.querySelector("#fundraiser");
const scene = createScene(config);
const view = createFundraiserView(root, scene, config);

const operatorPanel = document.querySelector("#operator-panel");
const operatorForm = document.querySelector("#operator-form");
const closeControls = document.querySelector("#close-controls");
const raisedInput = document.querySelector("#raised-input");
const goalInput = document.querySelector("#goal-input");
const raisedSlider = document.querySelector("#raised-slider");
const announcer = document.querySelector("#announcer");
const money = createCurrencyFormatter(config.locale, config.currency);

let goal = config.goal;
let targetRaised = config.raised;
let displayedRaised = targetRaised;
let demoActive = config.demo;
let demoStartedAt = performance.now();
let lastFrameAt = performance.now();
let celebrationUntil = 0;
let previousDisplayedRatio = displayedRaised / goal;
let framePending = false;
let focusRestoreTimeoutId = 0;
let continuousFireworksEnabled = false;
let continuousFireworksTimeoutId = 0;
let fireworksCleanupComplete = false;

const mediaReduced = window.matchMedia("(prefers-reduced-motion: reduce)");
const reducedMotion = () => config.motion === "reduce"
  || (config.motion === "auto" && mediaReduced.matches);
const focusRestoreDurationMs = 1400;

function synchronizeMotionState() {
  root.dataset.motion = reducedMotion() ? "reduce" : "full";
}

function clearFocusRestoreState() {
  if (focusRestoreTimeoutId) {
    clearTimeout(focusRestoreTimeoutId);
    focusRestoreTimeoutId = 0;
  }
  delete root.dataset.focusRestore;
}

function showFocusRestoreState() {
  clearFocusRestoreState();
  root.dataset.focusRestore = "pointer-close";
  focusRestoreTimeoutId = setTimeout(() => {
    delete root.dataset.focusRestore;
    focusRestoreTimeoutId = 0;
  }, focusRestoreDurationMs);
}

function synchronizeSliderText() {
  const sliderAmount = deriveRaisedFromSliderValue(raisedSlider.value, goal);
  const sliderPercent = goal > 0 ? (sliderAmount / goal) * 100 : 0;
  raisedSlider.setAttribute(
    "aria-valuetext",
    `${money.format(sliderAmount)} raised (${sliderPercent.toFixed(1)}% of goal)`
  );
}

function synchronizeControls() {
  const values = deriveOperatorControlValues(targetRaised, goal);
  raisedInput.value = String(money.round(Number(values.raised)));
  goalInput.value = String(money.round(Number(values.goal)));
  raisedSlider.max = String(money.round(Number(values.sliderMax)));
  raisedSlider.value = String(money.round(Number(values.sliderValue)));
  synchronizeSliderText();
}

function announce() {
  announcer.textContent = `${money.format(targetRaised)} raised toward a goal of ${money.format(goal)}.`;
}

function scheduleFrame() {
  if (framePending) return;
  framePending = true;
  requestAnimationFrame((now) => {
    framePending = false;
    frame(now);
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

function commitRaised(nextRaised, { speak = true } = {}) {
  targetRaised = clampAmount(nextRaised);
  demoActive = false;
  synchronizeControls();
  if (speak) announce();
  scheduleFrame();
}

function setControlsVisible(visible, { restoreFocusVisible = false } = {}) {
  const wasVisible = !operatorPanel.hidden;
  operatorPanel.hidden = !visible;
  if (visible) {
    clearFocusRestoreState();
    raisedInput.focus();
  } else if (wasVisible) {
    root.focus();
    if (restoreFocusVisible) {
      showFocusRestoreState();
    } else {
      clearFocusRestoreState();
    }
  }
}

raisedSlider.addEventListener("input", () => {
  raisedInput.value = String(money.round(
    deriveRaisedFromSliderValue(raisedSlider.value, goal)
  ));
  synchronizeSliderText();
});

operatorForm.addEventListener("submit", (event) => {
  event.preventDefault();
  const submission = validateOperatorAmounts(raisedInput.value, goalInput.value);
  if (!submission.valid) {
    announcer.textContent = submission.message;
    (submission.field === "raised" ? raisedInput : goalInput).focus();
    return;
  }

  goal = submission.goal;
  commitRaised(submission.raised);
});

closeControls.addEventListener("click", (event) => {
  setControlsVisible(false, {
    restoreFocusVisible: event?.detail > 0
  });
});

root.addEventListener("blur", clearFocusRestoreState);

function isEditingTarget(target) {
  const tagName = String(target?.tagName || "").toLowerCase();
  return ["input", "button", "select", "textarea"].includes(tagName)
    || target?.isContentEditable
    || Boolean(target?.closest?.(
      '[contenteditable]:not([contenteditable="false"]), [role="textbox"]'
    ))
    || document.designMode === "on";
}

function isSpaceKey(event) {
  return event.key === " "
    || event.key === "Spacebar"
    || event.code === "Space";
}

function isFireworkKey(event) {
  return String(event.key).toLowerCase() === "q";
}

function isContinuousFireworksKey(event) {
  return String(event.key).toLowerCase() === "w";
}

function isFullscreenKey(event) {
  return String(event.key).toLowerCase() === "f";
}

function formatKiteAnnouncement(count) {
  return `Kite added. ${count} ${count === 1 ? "kite" : "kites"} in the sky.`;
}

function toggleFullscreen() {
  const fullscreenAction = !document.fullscreenElement
    ? root.requestFullscreen?.()
    : document.exitFullscreen?.();
  fullscreenAction?.catch?.(() => {});
}

function randomContinuousDelay() {
  const [minimum, maximum] = reducedMotion()
    ? [900, 1700]
    : [350, 1100];
  return Math.round(minimum + Math.random() * (maximum - minimum));
}

function setContinuousFireworksState(state) {
  root.dataset.continuousFireworks = String(continuousFireworksEnabled);
  root.dataset.fireworksScheduler = state;
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

function startContinuousFireworks() {
  if (continuousFireworksEnabled) return false;
  continuousFireworksEnabled = true;
  scheduleContinuousFireworks();
  return true;
}

function stopContinuousFireworks() {
  const wasEnabled = continuousFireworksEnabled;
  continuousFireworksEnabled = false;
  cancelContinuousFireworksTimeout();
  setContinuousFireworksState("stopped");
  return wasEnabled;
}

function toggleContinuousFireworks() {
  if (continuousFireworksEnabled) {
    stopContinuousFireworks();
    return false;
  }
  startContinuousFireworks();
  return true;
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
  stopContinuousFireworks();
  view.clearFireworks();
}

document.addEventListener("keydown", (event) => {
  if (!isSpaceKey(event)) return;
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  event.preventDefault();
  const kiteCount = view.addKite();
  announcer.textContent = formatKiteAnnouncement(kiteCount);
}, true);

document.addEventListener("keydown", (event) => {
  if (!isFireworkKey(event)) return;
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  event.preventDefault();
  view.addFirework({
    reducedMotion: reducedMotion(),
    source: "manual"
  });
  announcer.textContent = "Firework launched.";
}, true);

document.addEventListener("keydown", (event) => {
  if (!isContinuousFireworksKey(event)) return;
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  event.preventDefault();
  const enabled = toggleContinuousFireworks();
  announcer.textContent = enabled
    ? "Continuous fireworks started."
    : "Continuous fireworks stopped.";
}, true);

document.addEventListener("keydown", (event) => {
  if (!isFullscreenKey(event)) return;
  if (event.ctrlKey || event.metaKey || event.altKey || event.repeat) return;
  event.preventDefault();
  toggleFullscreen();
}, true);

document.addEventListener("keydown", (event) => {
  if (event.ctrlKey || event.metaKey || event.altKey) return;
  const editing = isEditingTarget(event.target);
  if (isSpaceKey(event)) return;
  if (
    isFireworkKey(event)
    || isContinuousFireworksKey(event)
    || isFullscreenKey(event)
  ) return;
  if (editing && !["Escape", "Enter", "c", "C"].includes(event.key)) return;
  const stepFraction = event.shiftKey ? .05 : .01;

  switch (event.key.toLowerCase()) {
    case "c":
      event.preventDefault();
      setControlsVisible(operatorPanel.hidden);
      break;
    case "d":
      demoActive = !demoActive;
      demoStartedAt = performance.now();
      announcer.textContent = demoActive ? "Fundraiser demonstration started." : "Fundraiser demonstration paused.";
      scheduleFrame();
      break;
    case "arrowright":
    case "arrowup":
      event.preventDefault();
      commitRaised(addRaisedAmount(targetRaised, goal, stepFraction));
      break;
    case "arrowleft":
    case "arrowdown":
      event.preventDefault();
      commitRaised(addRaisedAmount(targetRaised, goal, -stepFraction));
      break;
    case "home":
      event.preventDefault();
      commitRaised(0);
      break;
    case "end":
      event.preventDefault();
      commitRaised(goal);
      break;
    case "escape":
      setControlsVisible(false);
      break;
  }
});

document.addEventListener("visibilitychange", handleVisibilityChange);
window.addEventListener("pagehide", cleanupFireworks, { once: true });
window.addEventListener("unload", cleanupFireworks, { once: true });

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
    synchronizeControls();
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

setControlsVisible(config.controls);
synchronizeControls();
synchronizeMotionState();
setContinuousFireworksState("stopped");
scheduleFrame();
