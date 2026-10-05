import {
  MAX_RAISED,
  deriveOperatorControlValues,
  deriveRaisedFromSliderValue,
  validateOperatorAmounts
} from "./config.mjs";
import { createCurrencyFormatter } from "./currency.mjs";

const form = document.querySelector("#progress-form");
const overrideForm = document.querySelector("#override-form");
const buildSummaryForm = document.querySelector("#build-summary-form");
const seattleSchoolsInput = document.querySelector("#seattle-schools-input");
const raisedInput = document.querySelector("#raised-input");
const goalInput = document.querySelector("#goal-input");
const overrideRaisedInput = document.querySelector("#override-raised-input");
const raisedSlider = document.querySelector("#raised-slider");
const serverIndicator = document.querySelector("#server-indicator");
const displayIndicator = document.querySelector("#display-indicator");
const operationStatus = document.querySelector("#operation-status");
const displayPreview = document.querySelector("#display-preview");
const displayPreviewFrame = document.querySelector("#display-preview-frame");
const celebrationToggle = document.querySelector("#celebration-toggle");
const amountEditToggle = document.querySelector("#edit-amounts-toggle");
const overrideEditToggle = document.querySelector("#edit-override-toggle");
const applyOverrideButton = document.querySelector("#apply-override-button");
const wideScreenToggle = document.querySelector("#wide-screen-toggle");
const money = createCurrencyFormatter("en-US", "USD");

const toggles = {
  demoActive: document.querySelector("#demo-toggle"),
  nightMode: document.querySelector("#night-toggle"),
  studentsClapping: document.querySelector("#clapping-toggle"),
  thankYouVisible: document.querySelector("#thank-you-toggle"),
  continuousFireworks: document.querySelector("#continuous-toggle")
  ,
  buildSummaryVisible: document.querySelector("#build-summary-toggle"),
  totalBoxVisible: document.querySelector("#total-box-toggle"),
  keyboardLegendVisible: document.querySelector("#keyboard-legend-toggle"),
  keypressEnabled: document.querySelector("#keypress-toggle")
};

let state = null;
let editingProgress = false;
let editingOverride = false;
let editingBuildSummary = false;
let highestAuthoritativeRevision = null;

function setStatus(message, error = false) {
  operationStatus.textContent = message;
  operationStatus.dataset.error = String(error);
}

function setIndicator(element, online, onlineText, offlineText) {
  element.dataset.state = online ? "online" : "offline";
  element.textContent = online ? onlineText : offlineText;
}

function synchronizeProgressControls() {
  if (!state || editingProgress) return;
  const values = deriveOperatorControlValues(state.raised, state.goal);
  raisedInput.value = String(money.round(Number(values.raised)));
  goalInput.value = String(money.round(Number(values.goal)));
  raisedSlider.max = values.sliderMax;
  raisedSlider.value = values.sliderValue;
  synchronizeSliderText();
}

function synchronizeOverrideControls() {
  if (!state || editingOverride) return;
  overrideRaisedInput.value = String(money.round(Number(state.overrideRaised)));
}

function synchronizeBuildSummaryControls() {
  if (!state || editingBuildSummary || !buildSummaryForm) return;
  seattleSchoolsInput.value = String(state.seattleSchools);
}

function synchronizeSliderText(goal = Number(goalInput.value)) {
  const sliderAmount = deriveRaisedFromSliderValue(raisedSlider.value, goal);
  const sliderPercent = goal > 0 ? (sliderAmount / goal) * 100 : 0;
  raisedSlider.setAttribute(
    "aria-valuetext",
    `${money.format(sliderAmount)} raised (${sliderPercent.toFixed(1)}% of goal)`
  );
}

function synchronizeSliderFromDraft() {
  const goalValidation = validateOperatorAmounts("0", goalInput.value);
  if (!goalValidation.valid) return;
  const values = deriveOperatorControlValues(
    Number(raisedInput.value),
    goalValidation.goal
  );
  raisedSlider.max = values.sliderMax;
  raisedSlider.value = values.sliderValue;
  synchronizeSliderText(goalValidation.goal);
}

function synchronizeAmountEditState() {
  const editable = Boolean(amountEditToggle?.checked);
  raisedInput.readOnly = !editable;
  goalInput.readOnly = !editable;
}

function synchronizeOverrideEditState() {
  const editable = Boolean(overrideEditToggle?.checked);
  overrideRaisedInput.readOnly = !editable;
  if (applyOverrideButton) applyOverrideButton.disabled = !editable;
}

function synchronizePreviewMode(wideScreen) {
  if (!displayPreviewFrame) return;
  displayPreviewFrame.dataset.displayMode = wideScreen ? "wide" : "standard";
}

function synchronizeToggles() {
  if (!state) return;
  const wideScreen = Boolean(state.wideScreen);
  if (wideScreenToggle) wideScreenToggle.checked = wideScreen;
  synchronizePreviewMode(wideScreen);
  const labels = {
    demoActive: state.demoActive ? "Pause demo" : "Start demo",
    nightMode: state.nightMode ? "Switch to day" : "Switch to night",
    studentsClapping: state.studentsClapping ? "Stop clapping" : "Start clapping",
    thankYouVisible: state.thankYouVisible
      ? "Hide thank-you bubbles"
      : "Show thank-you bubbles",
    continuousFireworks: state.continuousFireworks
      ? "Stop continuous fireworks"
      : "Start continuous fireworks",
    buildSummaryVisible: state.buildSummaryVisible
      ? "Hide School Counts"
      : "Show School Counts",
    totalBoxVisible: state.totalBoxVisible
      ? "Hide Total Box"
      : "Show Total Box",
    keyboardLegendVisible: state.keyboardLegendVisible
      ? "Hide key legend"
      : "Show key legend",
    keypressEnabled: state.keypressEnabled
      ? "Disable keyboard controls"
      : "Enable keyboard controls"
  };
  for (const [field, button] of Object.entries(toggles)) {
    if (!button) continue;
    button.setAttribute("aria-pressed", String(state[field]));
    button.textContent = labels[field];
  }
  if (celebrationToggle) {
    const active = state.nightMode
      && state.continuousFireworks
      && state.thankYouVisible;
    celebrationToggle.setAttribute("aria-pressed", String(active));
    celebrationToggle.textContent = active
      ? "Stop celebration"
      : "Start celebration";
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
  synchronizeProgressControls();
  synchronizeOverrideControls();
  synchronizeBuildSummaryControls();
  synchronizeToggles();
  return true;
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

async function connectPreview() {
  if (!displayPreview) return;
  try {
    const runtime = await requestJson("/api/runtime");
    const previewUrl = new URL(window.location.href);
    previewUrl.port = String(runtime.displayPort);
    previewUrl.pathname = "/";
    previewUrl.search = "?client=preview";
    previewUrl.hash = "";
    displayPreview.src = previewUrl.href;
  } catch (error) {
    setStatus(`Preview unavailable: ${error.message}`, true);
  }
}

async function refetchState() {
  const authoritativeState = await requestJson("/api/state");
  applyState(authoritativeState);
  return authoritativeState;
}

async function mutate(url, body, message) {
  try {
    const nextState = await requestJson(url, {
      method: url === "/api/state" ? "PATCH" : "POST",
      body: JSON.stringify(body)
    });
    if (!applyState(nextState)) return null;
    setStatus(message);
    return nextState;
  } catch (error) {
    try {
      await refetchState();
    } catch {
      setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
    }
    setStatus(error.message, true);
    return null;
  }
}

function sendCommand(command, message) {
  return mutate("/api/commands", command, message);
}

if (buildSummaryForm) {
  seattleSchoolsInput.addEventListener("input", () => {
    editingBuildSummary = true;
  });

  buildSummaryForm.addEventListener("focusout", (event) => {
    if (
      event.relatedTarget
      && buildSummaryForm.contains(event.relatedTarget)
    ) return;
    editingBuildSummary = false;
    synchronizeBuildSummaryControls();
  });

  buildSummaryForm.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    editingBuildSummary = false;
    synchronizeBuildSummaryControls();
  });

  buildSummaryForm.addEventListener("submit", async (event) => {
    event.preventDefault();
    const seattleSchools = Number(seattleSchoolsInput.value);
    if (!Number.isSafeInteger(seattleSchools) || seattleSchools < 0) {
      setStatus("Seattle Schools must be a non-negative whole number.", true);
      seattleSchoolsInput.focus();
      return;
    }
    try {
      const nextState = await requestJson("/api/state", {
        method: "PATCH",
        body: JSON.stringify({ seattleSchools })
      });
      editingBuildSummary = false;
      if (applyState(nextState)) {
        setStatus("Seattle Schools updated.");
      } else {
        synchronizeBuildSummaryControls();
      }
    } catch (error) {
      editingBuildSummary = false;
      try {
        await refetchState();
      } catch {
        setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
      } finally {
        synchronizeBuildSummaryControls();
      }
      setStatus(error.message, true);
    }
  });
}

raisedSlider.addEventListener("input", () => {
  editingProgress = true;
  const goalValidation = validateOperatorAmounts("0", goalInput.value);
  if (!goalValidation.valid) return;
  raisedInput.value = String(money.round(
    deriveRaisedFromSliderValue(raisedSlider.value, goalValidation.goal)
  ));
  synchronizeSliderText(goalValidation.goal);
});

amountEditToggle?.addEventListener("change", () => {
  synchronizeAmountEditState();
  if (amountEditToggle.checked) {
    setStatus("Raised and Goal can now be edited.");
    raisedInput.focus();
    return;
  }
  if (editingProgress) {
    form.requestSubmit();
  } else {
    synchronizeProgressControls();
    setStatus("Raised and Goal are read-only.");
  }
});

for (const input of [raisedInput, goalInput]) {
  input.addEventListener("input", () => {
    editingProgress = true;
    synchronizeSliderFromDraft();
  });
}

form.addEventListener("focusout", (event) => {
  if (event.relatedTarget && form.contains(event.relatedTarget)) return;
  editingProgress = false;
  synchronizeProgressControls();
});

form.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  event.preventDefault();
  editingProgress = false;
  synchronizeProgressControls();
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  const submission = validateOperatorAmounts(raisedInput.value, goalInput.value);
  if (!submission.valid) {
    if (amountEditToggle) amountEditToggle.checked = true;
    synchronizeAmountEditState();
    setStatus(submission.message, true);
    (submission.field === "raised" ? raisedInput : goalInput).focus();
    return;
  }
  try {
    const nextState = await requestJson("/api/state", {
      method: "PATCH",
      body: JSON.stringify({
        raised: submission.raised,
        goal: submission.goal,
        demoActive: false
      })
    });
    editingProgress = false;
    if (amountEditToggle) amountEditToggle.checked = false;
    synchronizeAmountEditState();
    if (applyState(nextState)) {
      setStatus("Raised amount and goal applied.");
    } else {
      synchronizeProgressControls();
    }
  } catch (error) {
    editingProgress = false;
    if (amountEditToggle) amountEditToggle.checked = false;
    synchronizeAmountEditState();
    try {
      await refetchState();
    } catch {
      setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
    } finally {
      synchronizeProgressControls();
    }
    setStatus(error.message, true);
  }
});

synchronizeAmountEditState();

overrideRaisedInput.addEventListener("input", () => {
  editingOverride = true;
});

overrideEditToggle?.addEventListener("change", () => {
  synchronizeOverrideEditState();
  if (overrideEditToggle.checked) {
    setStatus("Override Raised Amount can now be edited.");
    overrideRaisedInput.focus();
    return;
  }
  editingOverride = false;
  synchronizeOverrideControls();
  setStatus("Unsaved override changes canceled. Override Raised Amount is read-only.");
});

overrideForm?.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  event.preventDefault();
  editingOverride = false;
  if (overrideEditToggle) overrideEditToggle.checked = false;
  synchronizeOverrideEditState();
  synchronizeOverrideControls();
});

overrideForm?.addEventListener("submit", async (event) => {
  event.preventDefault();
  const overrideRaised = Number(String(overrideRaisedInput.value).trim());
  if (
    !Number.isFinite(overrideRaised)
    || overrideRaised < 0
    || overrideRaised > MAX_RAISED
  ) {
    if (overrideEditToggle) overrideEditToggle.checked = true;
    synchronizeOverrideEditState();
    setStatus("Enter a valid Override Raised Amount within the allowed range.", true);
    overrideRaisedInput.focus();
    return;
  }
  try {
    const nextState = await requestJson("/api/state", {
      method: "PATCH",
      body: JSON.stringify({ overrideRaised })
    });
    editingOverride = true;
    if (overrideEditToggle) overrideEditToggle.checked = true;
    synchronizeOverrideEditState();
    if (applyState(nextState)) {
      setStatus("Ultimate total override applied.");
    } else {
      synchronizeOverrideControls();
    }
  } catch (error) {
    editingOverride = true;
    if (overrideEditToggle) overrideEditToggle.checked = true;
    synchronizeOverrideEditState();
    try {
      await refetchState();
    } catch {
      setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
    } finally {
      synchronizeOverrideControls();
    }
    setStatus(error.message, true);
  }
});

synchronizeOverrideEditState();

for (const [field, button] of Object.entries(toggles)) {
  if (!button) continue;
  button.addEventListener("click", () => {
    if (!state) {
      setStatus("Wait for the initial live state before changing controls.", true);
      return;
    }
    sendCommand(
      { type: "state.toggle", field },
      `${button.textContent} requested.`
    );
  });
}

wideScreenToggle?.addEventListener("change", () => {
  if (!state) {
    wideScreenToggle.checked = false;
    synchronizePreviewMode(false);
    setStatus("Wait for the initial live state before changing display mode.", true);
    return;
  }
  synchronizePreviewMode(Boolean(wideScreenToggle.checked));
  mutate(
    "/api/state",
    { wideScreen: Boolean(wideScreenToggle.checked) },
    wideScreenToggle.checked
      ? "Wide screen mode enabled."
      : "Fitted 16:9 mode enabled."
  );
});

celebrationToggle?.addEventListener("click", async () => {
  if (!state) {
    setStatus("Wait for the initial live state before starting celebration.", true);
    return;
  }
  const active = state.nightMode
    && state.continuousFireworks
    && state.thankYouVisible;
  try {
    const nextState = await sendCommand(
      { type: "celebration.toggle" },
      !active ? "Celebration started." : "Celebration stopped."
    );
    if (!nextState) return;
    if (!active) {
      await requestJson("/api/actions", {
        method: "POST",
        body: JSON.stringify({ type: "firework.tcf" })
      });
      setStatus("Celebration started with TCF firework.");
    } else {
      setStatus("Celebration stopped.");
    }
  } catch (error) {
    try {
      await refetchState();
    } catch {
      setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
    }
    setStatus(error.message, true);
  }
});

for (const button of document.querySelectorAll("[data-progress-set]")) {
  button.addEventListener("click", () => {
    if (!state) {
      setStatus("Wait for the initial live state before changing progress.", true);
      return;
    }
    const ratio = Number(button.dataset.progressSet);
    sendCommand(
      { type: "raised.setRatio", ratio },
      `Progress set to ${money.format(state.goal * ratio)}.`
    );
  });
}

for (const button of document.querySelectorAll("[data-action]")) {
  button.addEventListener("click", async () => {
    try {
      const result = await requestJson("/api/actions", {
        method: "POST",
        body: JSON.stringify({ type: button.dataset.action })
      });
      if (!result.state || applyState(result.state)) {
        setStatus(`${button.textContent} sent to the display.`);
      }
    } catch (error) {
      try {
        await refetchState();
      } catch {
        setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
      }
      setStatus(error.message, true);
    }
  });
}

function connectEvents() {
  const events = new EventSource("/events?role=control");
  events.addEventListener("open", () => {
    setIndicator(serverIndicator, true, "Dashboard connected", "Dashboard disconnected");
    setStatus("Live state connected.");
  });
  events.addEventListener("snapshot", (event) => {
    const snapshot = JSON.parse(event.data);
    applyState(snapshot.state);
    setIndicator(
      displayIndicator,
      snapshot.presentationConnected,
      "Display connected",
      "Display not connected"
    );
  });
  events.addEventListener("state", (event) => {
    applyState(JSON.parse(event.data));
  });
  events.addEventListener("presence", (event) => {
    const presence = JSON.parse(event.data);
    setIndicator(
      displayIndicator,
      presence.presentationConnected,
      "Display connected",
      "Display not connected"
    );
  });
  events.addEventListener("action", (event) => {
    const action = JSON.parse(event.data);
    setStatus(`Display action delivered: ${action.type}.`);
  });
  events.addEventListener("error", () => {
    setIndicator(serverIndicator, false, "Dashboard connected", "Dashboard reconnecting…");
    setStatus("Live connection lost. Reconnecting automatically.", true);
  });
}

connectPreview();
connectEvents();
