export function clamp(value, minimum = 0, maximum = 1) {
  return Math.min(maximum, Math.max(minimum, value));
}

function rampRatio(value, start, span) {
  const ratio = (value - start) / span;
  if (Math.abs(ratio) < 1e-12) return 0;
  if (Math.abs(ratio - 1) < 1e-12) return 1;
  return clamp(ratio);
}

export function deriveProgress(raised, goal, overGoalRamp = 0.25) {
  const safeRaised = Number.isFinite(raised) ? Math.max(0, raised) : 0;
  const safeGoal = Number.isFinite(goal) && goal > 0 ? goal : 1;
  const safeRamp = Number.isFinite(overGoalRamp) && overGoalRamp > 0
    ? overGoalRamp
    : 0.25;
  const completionRatio = 1 + safeRamp;
  const completionRaised = safeGoal * completionRatio;
  const donationRatio = safeRaised === completionRaised
    ? completionRatio
    : Math.min(Number.MAX_VALUE, Math.max(0, safeRaised / safeGoal));
  return {
    donationRatio,
    buildingRatio: clamp(donationRatio),
    studentRatio: clamp((donationRatio - 1) / safeRamp),
    goalAchieved: donationRatio >= 1,
    swingRatio: rampRatio(
      donationRatio,
      1 + .12 * safeRamp,
      .28 * safeRamp
    ),
    teacherRatio: rampRatio(
      donationRatio,
      1 + .40 * safeRamp,
      .28 * safeRamp
    ),
    playgroundRatio: rampRatio(
      donationRatio,
      1 + .68 * safeRamp,
      .32 * safeRamp
    )
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
