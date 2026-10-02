export function clamp(value, minimum = 0, maximum = 1) {
  return Math.min(maximum, Math.max(minimum, value));
}

function rampRatio(value, start, span) {
  const ratio = (value - start) / span;
  if (Math.abs(ratio) < 1e-12) return 0;
  if (Math.abs(ratio - 1) < 1e-12) return 1;
  return clamp(ratio);
}

function interpolateMilestone(value, start, end, startValue, endValue) {
  const progress = rampRatio(value, start, end - start);
  return startValue + (endValue - startValue) * progress;
}

const SCHOOL_BLOCK_COUNT = 308;
const SCHOOL_BLOCK_MILESTONES = Object.freeze([
  Object.freeze([0, 0]),
  Object.freeze([.2, 100]),
  Object.freeze([.4, 160]),
  Object.freeze([.6, 226]),
  Object.freeze([.8, 244]),
  Object.freeze([1, SCHOOL_BLOCK_COUNT])
]);

function deriveBuildingRatio(donationRatio) {
  const boundedRatio = clamp(donationRatio, 0, 1);
  for (let index = 1; index < SCHOOL_BLOCK_MILESTONES.length; index += 1) {
    const [endRatio, endBlocks] = SCHOOL_BLOCK_MILESTONES[index];
    if (boundedRatio > endRatio) continue;
    const [startRatio, startBlocks] = SCHOOL_BLOCK_MILESTONES[index - 1];
    return interpolateMilestone(
      boundedRatio,
      startRatio,
      endRatio,
      startBlocks / SCHOOL_BLOCK_COUNT,
      endBlocks / SCHOOL_BLOCK_COUNT
    );
  }
  return 1;
}

export function deriveProgress(raised, goal, overGoalRamp = 0.15) {
  const safeRaised = Number.isFinite(raised) ? Math.max(0, raised) : 0;
  const safeGoal = Number.isFinite(goal) && goal > 0 ? goal : 1;
  void overGoalRamp;
  const donationRatio = Math.min(
    Number.MAX_VALUE,
    Math.max(0, safeRaised / safeGoal)
  );
  const buildingRatio = deriveBuildingRatio(donationRatio);
  const studentRatio = donationRatio < .8
    ? 0
    : donationRatio < 1
      ? interpolateMilestone(donationRatio, .8, 1, 0, .5)
      : interpolateMilestone(donationRatio, 1, 1.2, .5, 1);
  return {
    donationRatio,
    buildingRatio: clamp(buildingRatio),
    studentRatio: clamp(studentRatio),
    goalAchieved: donationRatio >= 1,
    swingRatio: rampRatio(donationRatio, .2, .2),
    teacherRatio: rampRatio(donationRatio, 1, .2),
    playgroundRatio: rampRatio(donationRatio, 1, .2),
    busRatio: rampRatio(donationRatio, .4, .2)
  };
}

export function revealAt(ratio, count, index) {
  if (!Number.isInteger(count) || count < 0) {
    throw new RangeError("count must be a non-negative integer");
  }
  return clamp(clamp(ratio) * count - index);
}

export function revealSeries(ratio, count) {
  return Array.from({ length: count }, (_, index) => revealAt(ratio, count, index));
}

export function revealSum(ratio, count) {
  return revealSeries(ratio, count).reduce((sum, reveal) => sum + reveal, 0);
}

export function exponentialStep(current, target, deltaMs, timeConstantMs) {
  if (!Number.isFinite(current) || !Number.isFinite(target)) return target;
  if (!Number.isFinite(timeConstantMs) || timeConstantMs <= 0) return target;
  const cappedDelta = clamp(Number.isFinite(deltaMs) ? deltaMs : 0, 0, 50);
  const alpha = 1 - Math.exp(-cappedDelta / timeConstantMs);
  return current + (target - current) * alpha;
}

function totalBoxAmount(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) ? Math.max(0, numeric) : 0;
}

function equalTotalBoxAmount(left, right) {
  return Math.abs(left - right) < .01;
}

export function hideTotalBoxRoll(raised, overrideRaised) {
  return {
    displayed: 0,
    override: totalBoxAmount(overrideRaised),
    stage: "hidden",
    target: totalBoxAmount(raised)
  };
}

export function beginTotalBoxRoll(raised, overrideRaised) {
  return {
    displayed: 0,
    override: totalBoxAmount(overrideRaised),
    stage: "raised",
    target: totalBoxAmount(raised)
  };
}

export function retargetTotalBoxRoll(roll, {
  overrideChanged,
  overrideRaised,
  raised,
  raisedChanged = false
}) {
  const nextRaised = totalBoxAmount(raised);
  const nextOverride = totalBoxAmount(overrideRaised);

  if (overrideChanged && !equalTotalBoxAmount(nextOverride, nextRaised)) {
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
      stage: equalTotalBoxAmount(roll.displayed, nextRaised) ? "settled" : "raised",
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
  if (equalTotalBoxAmount(displayed, roll.target)) displayed = roll.target;
  if (!equalTotalBoxAmount(displayed, roll.target)) {
    return { ...roll, displayed };
  }

  if (roll.stage === "raised" && !equalTotalBoxAmount(roll.override, roll.target)) {
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
