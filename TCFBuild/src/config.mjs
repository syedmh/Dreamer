export const DEFAULT_CONFIG = Object.freeze({
  goal: 100000,
  raised: 0,
  currency: "USD",
  locale: "en-US",
  maxStudents: 36,
  overGoalRamp: 0.25,
  animationTimeConstantMs: 420,
  demoDurationMs: 90000
});

export const MIN_GOAL = 0.01;
export const MAX_RAISED = Number.MAX_SAFE_INTEGER;
export const MAX_GOAL = MAX_RAISED / 1.25;

export function clampAmount(value) {
  if (!Number.isFinite(value)) return 0;
  return Math.min(MAX_RAISED, Math.max(0, value));
}

export function deriveSliderMaximum(goal) {
  return clampAmount(goal * 1.25);
}

export function deriveSliderValue(raised, goal) {
  if (!isOperableGoal(goal) || !Number.isFinite(raised) || raised <= 0) return 0;
  return Math.min(deriveSliderMaximum(goal), raised);
}

export function deriveRaisedFromSliderValue(sliderValue, goal) {
  const value = Number(sliderValue);
  if (!isOperableGoal(goal) || !Number.isFinite(value) || value <= 0) return 0;
  const maximum = deriveSliderMaximum(goal);
  const endpointTolerance = Number.EPSILON * Math.abs(maximum) * 8;
  if (value >= maximum || maximum - value <= endpointTolerance) return maximum;
  return clampAmount(value);
}

function deriveExactIntegerPercentAmount(goal, percent) {
  if (!Number.isSafeInteger(goal) || !Number.isSafeInteger(percent)) return null;
  const numerator = BigInt(goal) * BigInt(percent);
  return numerator % 100n === 0n
    ? Number(numerator / 100n)
    : null;
}

export function addRaisedAmount(raised, goal, stepFraction) {
  const currentRatio = raised / goal;
  const currentPercent = Math.round(currentRatio * 100);
  const stepPercent = stepFraction * 100;
  const exactCurrent = deriveExactIntegerPercentAmount(goal, currentPercent);
  if (
    Number.isInteger(stepPercent)
    && (
      raised === goal * (currentPercent / 100)
      || (exactCurrent !== null && raised === exactCurrent)
    )
  ) {
    const nextPercent = currentPercent + stepPercent;
    const exactNext = deriveExactIntegerPercentAmount(goal, nextPercent);
    if (exactNext !== null) return clampAmount(exactNext);
    return clampAmount(goal * (nextPercent / 100));
  }
  return clampAmount((currentRatio + stepFraction) * goal);
}

export function deriveDemoRaised(goal, progress) {
  const safeProgress = Number.isFinite(progress)
    ? Math.min(1, Math.max(0, progress))
    : 0;
  return deriveSliderMaximum(goal) * safeProgress;
}

export function deriveOperatorControlValues(raised, goal) {
  return Object.freeze({
    raised: String(raised),
    goal: String(goal),
    sliderMax: String(deriveSliderMaximum(goal)),
    sliderValue: String(deriveSliderValue(raised, goal))
  });
}

export function isOperableGoal(value) {
  return Number.isFinite(value) && value >= MIN_GOAL && value <= MAX_GOAL;
}

export function validateOperatorAmounts(raisedValue, goalValue) {
  const raisedText = String(raisedValue ?? "").trim();
  const goalText = String(goalValue ?? "").trim();
  if (!raisedText || !goalText) {
    return Object.freeze({
      valid: false,
      field: !raisedText ? "raised" : "goal",
      message: "Enter both a raised amount and a goal."
    });
  }

  const raised = Number(raisedText);
  const goal = Number(goalText);
  if (!isOperableGoal(goal)) {
    return Object.freeze({
      valid: false,
      field: "goal",
      message: "Enter a valid goal within the allowed range."
    });
  }
  if (!Number.isFinite(raised) || raised < 0 || raised > MAX_RAISED) {
    return Object.freeze({
      valid: false,
      field: "raised",
      message: "Enter a valid raised amount within the allowed range."
    });
  }

  return Object.freeze({ valid: true, raised, goal });
}

function finiteNumber(value, fallback, minimum = -Infinity, maximum = Infinity) {
  const parsed = Number(value);
  return Number.isFinite(parsed) && parsed >= minimum && parsed <= maximum
    ? parsed
    : fallback;
}

function flag(value, fallback = false) {
  if (value == null) return fallback;
  return !["0", "false", "off", "no"].includes(String(value).toLowerCase());
}

export function parseConfig(search = "", overrides = {}) {
  const params = new URLSearchParams(search);
  const base = { ...DEFAULT_CONFIG, ...overrides };
  const goal = finiteNumber(params.get("goal"), base.goal, MIN_GOAL, MAX_GOAL);
  const raised = finiteNumber(params.get("raised"), base.raised, 0, MAX_RAISED);
  const requestedMotion = (params.get("motion") || "auto").toLowerCase();
  const motion = ["auto", "reduce", "full"].includes(requestedMotion)
    ? requestedMotion
    : "auto";

  return Object.freeze({
    ...base,
    goal,
    raised,
    controls: flag(params.get("controls")),
    demo: flag(params.get("demo")),
    motion
  });
}
