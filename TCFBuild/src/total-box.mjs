import { exponentialStep } from "./model.mjs";

function amount(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? Math.max(0, numeric) : 0;
}

function equalAmount(left, right) {
  return Math.abs(left - right) < .01;
}

export function hideTotalBoxRoll(raised, overrideRaised) {
  return {
    displayed: 0,
    override: amount(overrideRaised),
    stage: "hidden",
    target: amount(raised)
  };
}

export function beginTotalBoxRoll(raised, overrideRaised) {
  return {
    displayed: 0,
    override: amount(overrideRaised),
    stage: "raised",
    target: amount(raised)
  };
}

export function retargetTotalBoxRoll(roll, {
  overrideChanged,
  overrideRaised,
  raised,
  raisedChanged = false
}) {
  const nextRaised = amount(raised);
  const nextOverride = amount(overrideRaised);

  if (overrideChanged && !equalAmount(nextOverride, nextRaised)) {
    return {
      displayed: nextRaised,
      override: nextOverride,
      stage: "override",
      target: nextOverride
    };
  }

  if (raisedChanged || overrideChanged) {
    return {
      displayed: roll.displayed,
      override: nextOverride,
      stage: equalAmount(roll.displayed, nextRaised) ? "settled" : "raised",
      target: nextRaised
    };
  }

  return {
    ...roll,
    override: nextOverride
  };
}

export function advanceTotalBoxRoll(roll, {
  deltaMs,
  reducedMotion,
  timeConstantMs
}) {
  if (roll.stage === "hidden" || roll.stage === "settled") return roll;

  let displayed = reducedMotion
    ? roll.target
    : exponentialStep(roll.displayed, roll.target, deltaMs, timeConstantMs);
  if (equalAmount(displayed, roll.target)) displayed = roll.target;
  if (!equalAmount(displayed, roll.target)) {
    return { ...roll, displayed };
  }

  if (roll.stage === "raised" && !equalAmount(roll.override, roll.target)) {
    return {
      displayed,
      override: roll.override,
      stage: "override",
      target: roll.override
    };
  }

  return {
    displayed,
    override: roll.override,
    stage: "settled",
    target: roll.target
  };
}
