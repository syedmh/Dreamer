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

export function deriveProgress(raised, goal, overGoalRamp = 0.15) {
  const safeRaised = Number.isFinite(raised) ? Math.max(0, raised) : 0;
  const safeGoal = Number.isFinite(goal) && goal > 0 ? goal : 1;
  void overGoalRamp;
  const donationRatio = Math.min(
    Number.MAX_VALUE,
    Math.max(0, safeRaised / safeGoal)
  );
  const firstLevelComplete = 160 / 308;
  const secondLevelComplete = 226 / 308;
  const buildingRatio = donationRatio < .2
    ? interpolateMilestone(donationRatio, 0, .2, 0, firstLevelComplete)
    : donationRatio < .4
      ? interpolateMilestone(
        donationRatio,
        .2,
        .4,
        firstLevelComplete,
        secondLevelComplete
      )
      : donationRatio < .8
        ? interpolateMilestone(
          donationRatio,
          .4,
          .8,
          secondLevelComplete,
          1
        )
        : 1;
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
