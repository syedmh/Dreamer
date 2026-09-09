import {
  deriveOperatorControlValues,
  deriveRaisedFromSliderValue,
  validateOperatorAmounts
} from "./config.mjs";
import { createCurrencyFormatter } from "./currency.mjs";

const form = document.querySelector("#progress-form");
const raisedInput = document.querySelector("#raised-input");
const goalInput = document.querySelector("#goal-input");
const raisedSlider = document.querySelector("#raised-slider");
const serverIndicator = document.querySelector("#server-indicator");
const displayIndicator = document.querySelector("#display-indicator");
const operationStatus = document.querySelector("#operation-status");
const money = createCurrencyFormatter("en-US", "USD");

const toggles = {
  demoActive: document.querySelector("#demo-toggle"),
  nightMode: document.querySelector("#night-toggle"),
  studentsClapping: document.querySelector("#clapping-toggle"),
  thankYouVisible: document.querySelector("#thank-you-toggle"),
  continuousFireworks: document.querySelector("#continuous-toggle")
};

let state = null;
let editingProgress = false;

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

function synchronizeToggles() {
  if (!state) return;
  const labels = {
    demoActive: state.demoActive ? "Pause demo" : "Start demo",
    nightMode: state.nightMode ? "Switch to day" : "Switch to night",
    studentsClapping: state.studentsClapping ? "Stop clapping" : "Start clapping",
    thankYouVisible: state.thankYouVisible
      ? "Hide thank-you bubbles"
      : "Show thank-you bubbles",
    continuousFireworks: state.continuousFireworks
      ? "Stop continuous fireworks"
      : "Start continuous fireworks"
  };
  for (const [field, button] of Object.entries(toggles)) {
    button.setAttribute("aria-pressed", String(state[field]));
    button.textContent = labels[field];
  }
}

function applyState(nextState) {
  state = { ...state, ...nextState };
  synchronizeProgressControls();
  synchronizeToggles();
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

async function patchState(patch, message) {
  try {
    const nextState = await requestJson("/api/state", {
      method: "PATCH",
      body: JSON.stringify(patch)
    });
    applyState(nextState);
    setStatus(message);
  } catch (error) {
    setStatus(error.message, true);
  }
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
    applyState(nextState);
    setStatus("Raised amount and goal applied.");
  } catch (error) {
    setStatus(error.message, true);
  }
});

for (const [field, button] of Object.entries(toggles)) {
  button.addEventListener("click", () => {
    if (!state) {
      setStatus("Wait for the initial live state before changing controls.", true);
      return;
    }
    patchState({ [field]: !state[field] }, `${button.textContent} requested.`);
  });
}

for (const button of document.querySelectorAll("[data-action]")) {
  button.addEventListener("click", async () => {
    try {
      await requestJson("/api/actions", {
        method: "POST",
        body: JSON.stringify({ type: button.dataset.action })
      });
      setStatus(`${button.textContent} sent to the display.`);
    } catch (error) {
      setStatus(error.message, true);
    }
  });
}

function connectEvents() {
  const events = new EventSource("/events");
  events.addEventListener("open", () => {
    setIndicator(serverIndicator, true, "Dashboard connected", "Dashboard disconnected");
    setStatus("Live state connected.");
  });
  events.addEventListener("snapshot", (event) => {
    const snapshot = JSON.parse(event.data);
    applyState(snapshot.state);
    setIndicator(
      displayIndicator,
      snapshot.displayConnected,
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
      presence.displayConnected,
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

connectEvents();
