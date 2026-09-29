import test from "node:test";
import assert from "node:assert/strict";
import {
  clamp,
  deriveProgress,
  exponentialStep,
  revealAt,
  revealSeries,
  revealSum
} from "../src/model.mjs";
import {
  addRaisedAmount,
  deriveDemoRaised,
  DEFAULT_CONFIG,
  deriveOperatorControlValues,
  deriveRaisedFromSliderValue,
  deriveSliderMaximum,
  MAX_GOAL,
  MAX_PROGRESS_RATIO,
  MAX_RAISED,
  MIN_GOAL,
  parseConfig,
  clampAmount,
  validateOperatorAmounts
} from "../src/config.mjs";

function adjacentFloat(value, direction) {
  const storage = new ArrayBuffer(8);
  const view = new DataView(storage);
  view.setFloat64(0, value);
  let bits = view.getBigUint64(0);
  bits += direction > 0 ? 1n : -1n;
  view.setBigUint64(0, bits);
  return view.getFloat64(0);
}

test("default configuration is the frozen event configuration", () => {
  assert.deepEqual(DEFAULT_CONFIG, {
    goal: 100000,
    raised: 0,
    currency: "USD",
    locale: "en-US",
    maxStudents: 24,
    overGoalRamp: 0.25,
    animationTimeConstantMs: 420,
    demoDurationMs: 144000
  });
  assert.equal(MAX_PROGRESS_RATIO, 2);
  assert.equal(MAX_GOAL, 4503599627370495.5);
});

test("URL configuration accepts valid event parameters and rejects invalid values", () => {
  const config = parseConfig("?raised=125000&goal=200000&controls=1&demo=true&motion=reduce");
  assert.equal(config.raised, 125000);
  assert.equal(config.goal, 200000);
  assert.equal(config.controls, true);
  assert.equal(config.demo, true);
  assert.equal(config.motion, "reduce");
  assert.deepEqual(config.authoritativeInitialState, {
    goal: 200000,
    raised: 125000
  });
  assert.equal(parseConfig("?goal=0&raised=-1").goal, DEFAULT_CONFIG.goal);
  assert.equal(parseConfig("?goal=0&raised=-1").raised, DEFAULT_CONFIG.raised);
  assert.deepEqual(
    parseConfig("?goal=0&raised=-1").authoritativeInitialState,
    {}
  );
  assert.deepEqual(
    parseConfig("?client=preview&motion=reduce").authoritativeInitialState,
    {}
  );
  assert.deepEqual(
    parseConfig("?goal=&raised=").authoritativeInitialState,
    {}
  );
  assert.deepEqual(
    parseConfig("?raised=210000").authoritativeInitialState,
    { raised: 200000 }
  );

  const arbitrary = parseConfig("?goal=12345&raised=123.75");
  assert.equal(arbitrary.goal, 12345);
  assert.equal(arbitrary.raised, 123.75);

  const fractionalGoal = parseConfig("?goal=0.5&raised=0.25");
  assert.equal(fractionalGoal.goal, 0.5);
  assert.equal(fractionalGoal.raised, 0.25);

  assert.equal(parseConfig(`?goal=${MIN_GOAL}`).goal, MIN_GOAL);
  assert.equal(parseConfig(`?goal=${MAX_GOAL}`).goal, MAX_GOAL);
  assert.equal(parseConfig(`?goal=${MIN_GOAL / 2}`).goal, DEFAULT_CONFIG.goal);
  assert.equal(parseConfig(`?goal=${MAX_GOAL + 1}`).goal, DEFAULT_CONFIG.goal);
  assert.equal(parseConfig(`?raised=${MAX_RAISED}`).raised, 200000);
  assert.equal(
    parseConfig(`?goal=${MAX_GOAL}&raised=${MAX_RAISED}`).raised,
    MAX_RAISED
  );
  assert.equal(parseConfig(`?raised=${MAX_RAISED * 2}`).raised, DEFAULT_CONFIG.raised);
  assert.equal(parseConfig("?goal=100000&raised=210000").raised, 200000);
  assert.equal(clampAmount(Number.POSITIVE_INFINITY), MAX_RAISED);
  assert.equal(clampAmount(Number.MAX_VALUE), MAX_RAISED);
});

test("operator controls preserve accepted values and normalize slider values", () => {
  const fractional = deriveOperatorControlValues(123.75, 12345);
  assert.equal(fractional.raised, "123.75");
  assert.equal(fractional.goal, "12345");
  assert.equal(fractional.sliderMax, "24690");
  assert.equal(fractional.sliderValue, "123.75");

  assert.deepEqual(deriveOperatorControlValues(20000.5, 12345), {
    raised: "20000.5",
    goal: "12345",
    sliderMax: "24690",
    sliderValue: "20000.5"
  });

  const upperBound = deriveOperatorControlValues(MAX_RAISED, MAX_GOAL);
  assert.equal(upperBound.raised, String(MAX_RAISED));
  assert.equal(upperBound.goal, String(MAX_GOAL));
  assert.equal(upperBound.sliderMax, String(MAX_RAISED));
  assert.equal(upperBound.sliderValue, String(MAX_RAISED));

  const lowerBound = deriveOperatorControlValues(MIN_GOAL, MIN_GOAL);
  assert.equal(lowerBound.goal, String(MIN_GOAL));
  assert.equal(lowerBound.sliderMax, "0.02");
  assert.equal(lowerBound.sliderValue, "0.01");

  const keyboardNoise = deriveOperatorControlValues(56000.00000000001, 100000);
  assert.equal(keyboardNoise.raised, "56000.00000000001");
  assert.equal(keyboardNoise.goal, "100000");
  assert.equal(keyboardNoise.sliderValue, "56000.00000000001");

  for (const nearIntegerFraction of [
    0.9999999999999999,
    100000000000000.02
  ]) {
    const config = parseConfig(
      `?raised=${nearIntegerFraction}&goal=${nearIntegerFraction}`
    );
    const controls = deriveOperatorControlValues(
      config.raised,
      config.goal
    );
    assert.equal(controls.raised, String(nearIntegerFraction));
    assert.equal(controls.goal, String(nearIntegerFraction));
    assert.equal(Number(controls.raised), nearIntegerFraction);
    assert.equal(Number(controls.goal), nearIntegerFraction);
  }

  const precisionHostileGoal = 240429705269.63782;
  const exactEndpoint = deriveSliderMaximum(precisionHostileGoal);
  const exactEndpointControls = deriveOperatorControlValues(
    exactEndpoint,
    precisionHostileGoal
  );
  assert.equal(exactEndpointControls.raised, String(exactEndpoint));
  assert.equal(exactEndpointControls.goal, String(precisionHostileGoal));
  assert.equal(exactEndpointControls.sliderMax, String(exactEndpoint));
  assert.equal(exactEndpointControls.sliderValue, String(exactEndpoint));
});

test("operator submissions require valid raised and goal values atomically", () => {
  assert.deepEqual(validateOperatorAmounts("51000", "100000"), {
    valid: true,
    raised: 51000,
    goal: 100000
  });
  assert.deepEqual(validateOperatorAmounts("0", String(MIN_GOAL)), {
    valid: true,
    raised: 0,
    goal: MIN_GOAL
  });
  assert.deepEqual(
    validateOperatorAmounts(String(MAX_RAISED), String(MAX_GOAL)),
    {
      valid: true,
      raised: MAX_RAISED,
      goal: MAX_GOAL
    }
  );
  assert.deepEqual(validateOperatorAmounts("200001", "100000"), {
    valid: false,
    field: "raised",
    message: "Enter a raised amount no greater than 200% of the goal."
  });

  for (const [raised, goal, field] of [
    ["", "100000", "raised"],
    ["51000", "", "goal"],
    ["not-a-number", "100000", "raised"],
    ["51000", "Infinity", "goal"],
    ["-1", "100000", "raised"],
    [String(MAX_RAISED + 1), "100000", "raised"],
    ["51000", String(MIN_GOAL / 2), "goal"],
    ["51000", String(MAX_GOAL + 1), "goal"]
  ]) {
    const result = validateOperatorAmounts(raised, goal);
    assert.equal(result.valid, false);
    assert.equal(result.field, field);
    assert.match(result.message, /^Enter /);
    assert.equal("raised" in result, false);
    assert.equal("goal" in result, false);
  }
});

test("accepted goal bounds complete the slider, demo, and keyboard journey", () => {
  const precisionHostileGoal = 240429705269.63782;
  const keyboardPrecisionHostileGoal = 121106481787.98036;
  assert.equal(
    deriveSliderMaximum(precisionHostileGoal),
    precisionHostileGoal * MAX_PROGRESS_RATIO
  );

  for (const goal of [
    MIN_GOAL,
    0.5,
    0.9999999999999999,
    12345,
    100000000000000.02,
    MAX_GOAL,
    precisionHostileGoal,
    keyboardPrecisionHostileGoal
  ]) {
    const sliderControls = deriveOperatorControlValues(0, goal);
    const endpoint = deriveSliderMaximum(goal);
    const sliderRaised = deriveRaisedFromSliderValue(sliderControls.sliderMax, goal);
    assert.equal(Number(sliderControls.sliderMax), endpoint);
    assert.equal(sliderRaised, endpoint);
    const sliderProgress = deriveProgress(sliderRaised, goal);
    assert.equal(sliderProgress.donationRatio, MAX_PROGRESS_RATIO);
    assert.equal(sliderProgress.studentRatio, 1);
    assert.equal(sliderProgress.busRatio, 1);

    const demoEndpoint = deriveDemoRaised(goal, 1);
    assert.equal(demoEndpoint, endpoint);
    assert.equal(deriveProgress(demoEndpoint, goal).studentRatio, 1);
    assert.equal(deriveProgress(demoEndpoint, goal).busRatio, 1);

    let keyboardRaised = 0;
    for (let step = 0; step < 200; step += 1) {
      const nextRaised = addRaisedAmount(keyboardRaised, goal, 0.01);
      assert.ok(nextRaised > keyboardRaised);
      keyboardRaised = nextRaised;
    }
    assert.equal(keyboardRaised, endpoint);
    assert.equal(deriveProgress(keyboardRaised, goal).studentRatio, 1);
    assert.equal(deriveProgress(keyboardRaised, goal).busRatio, 1);

    let shiftedKeyboardRaised = 0;
    for (let step = 0; step < 40; step += 1) {
      shiftedKeyboardRaised = addRaisedAmount(shiftedKeyboardRaised, goal, 0.05);
    }
    assert.equal(shiftedKeyboardRaised, endpoint);
    assert.equal(deriveProgress(shiftedKeyboardRaised, goal).studentRatio, 1);
    assert.equal(deriveProgress(shiftedKeyboardRaised, goal).busRatio, 1);
  }

  assert.equal(addRaisedAmount(0, MIN_GOAL, -0.01), 0);
  assert.equal(deriveDemoRaised(MAX_GOAL, 0), 0);
  assert.equal(deriveDemoRaised(MAX_GOAL, 0.5), MAX_RAISED * 0.5);
  assert.equal(deriveDemoRaised(MAX_GOAL, 1), MAX_RAISED);
});

test("native range endpoint rounding snaps to the canonical maximum", () => {
  const goal = 240429705269.63782;
  const maximum = deriveSliderMaximum(goal);
  const chromeRoundedEndpoint = adjacentFloat(maximum, -1);

  assert.ok(chromeRoundedEndpoint < maximum);
  assert.equal(
    deriveRaisedFromSliderValue(chromeRoundedEndpoint, goal),
    maximum
  );
  assert.equal(
    deriveProgress(deriveRaisedFromSliderValue(chromeRoundedEndpoint, goal), goal).studentRatio,
    1
  );
  assert.equal(
    deriveRaisedFromSliderValue(maximum - 1, goal),
    maximum - 1
  );

  const smallGoal = MIN_GOAL;
  const smallMaximum = deriveSliderMaximum(smallGoal);
  const smallTolerance = Number.EPSILON * smallMaximum * 8;
  const justInside = smallMaximum - smallTolerance / 2;
  const justOutside = smallMaximum - smallTolerance * 2;
  assert.ok(justInside < smallMaximum);
  assert.ok(justOutside < justInside);
  assert.equal(
    deriveRaisedFromSliderValue(justInside, smallGoal),
    smallMaximum
  );
  assert.equal(
    deriveRaisedFromSliderValue(justOutside, smallGoal),
    justOutside
  );
});

test("keyboard percentages preserve exact grid steps without generic endpoint snapping", () => {
  const precisionHostileGoal = 240429705269.63782;
  const precisionHostileEndpoint = precisionHostileGoal * MAX_PROGRESS_RATIO;

  assert.equal(addRaisedAmount(55000, 100000, 0.01), 56000);

  assert.equal(
    addRaisedAmount(precisionHostileEndpoint, precisionHostileGoal, 0.01),
    precisionHostileEndpoint
  );
  assert.equal(
    addRaisedAmount(precisionHostileGoal * 1.22, precisionHostileGoal, 0.05),
    precisionHostileGoal * 1.27
  );

  assert.equal(addRaisedAmount(125000, 100000, 0.01), 126000);
  assert.equal(addRaisedAmount(122000, 100000, 0.05), 127000);
  assert.equal(addRaisedAmount(200000, 100000, 0.01), 200000);
});

test("deriveProgress preserves campus timing and adds the 125..135% bus phase", () => {
  assert.deepEqual(deriveProgress(0, 100000), {
    donationRatio: 0,
    buildingRatio: 0,
    studentRatio: 0,
    goalAchieved: false,
    swingRatio: 0,
    teacherRatio: 0,
    playgroundRatio: 0,
    busRatio: 0
  });
  assert.deepEqual(deriveProgress(50000, 100000), {
    donationRatio: .5,
    buildingRatio: .5,
    studentRatio: 0,
    goalAchieved: false,
    swingRatio: 0,
    teacherRatio: 0,
    playgroundRatio: 0,
    busRatio: 0
  });
  assert.deepEqual(deriveProgress(100000, 100000), {
    donationRatio: 1,
    buildingRatio: 1,
    studentRatio: 0,
    goalAchieved: true,
    swingRatio: 0,
    teacherRatio: 0,
    playgroundRatio: 0,
    busRatio: 0
  });
  assert.deepEqual(deriveProgress(125000, 100000), {
    donationRatio: 1.25,
    buildingRatio: 1,
    studentRatio: 1,
    goalAchieved: true,
    swingRatio: 1,
    teacherRatio: 1,
    playgroundRatio: 1,
    busRatio: 0
  });
  assert.deepEqual(deriveProgress(130000, 100000), {
    donationRatio: 1.3,
    buildingRatio: 1,
    studentRatio: 1,
    goalAchieved: true,
    swingRatio: 1,
    teacherRatio: 1,
    playgroundRatio: 1,
    busRatio: .5
  });
  assert.deepEqual(deriveProgress(135000, 100000), {
    donationRatio: 1.35,
    buildingRatio: 1,
    studentRatio: 1,
    goalAchieved: true,
    swingRatio: 1,
    teacherRatio: 1,
    playgroundRatio: 1,
    busRatio: 1
  });
  assert.deepEqual(deriveProgress(Number.MAX_VALUE, Number.MIN_VALUE), {
    donationRatio: Number.MAX_VALUE,
    buildingRatio: 1,
    studentRatio: 1,
    goalAchieved: true,
    swingRatio: 1,
    teacherRatio: 1,
    playgroundRatio: 1,
    busRatio: 1
  });
});

test("over-goal campus ratios honor every frozen threshold", () => {
  const cases = [
    [1, true, 0, 0, 0, 0],
    [1.03, true, 0, 0, 0, 0],
    [1.065, true, .5, 0, 0, 0],
    [1.10, true, 1, 0, 0, 0],
    [1.135, true, 1, .5, 0, 0],
    [1.17, true, 1, 1, 0, 0],
    [1.21, true, 1, 1, .5, 0],
    [1.25, true, 1, 1, 1, 0],
    [1.30, true, 1, 1, 1, .5],
    [1.35, true, 1, 1, 1, 1],
    [1.75, true, 1, 1, 1, 1]
  ];

  for (const [
    donationRatio,
    goalAchieved,
    swingRatio,
    teacherRatio,
    playgroundRatio,
    busRatio
  ] of cases) {
    const progress = deriveProgress(donationRatio * 100000, 100000);
    assert.equal(progress.goalAchieved, goalAchieved);
    assert.ok(Math.abs(progress.swingRatio - swingRatio) < 1e-12);
    assert.ok(Math.abs(progress.teacherRatio - teacherRatio) < 1e-12);
    assert.ok(Math.abs(progress.playgroundRatio - playgroundRatio) < 1e-12);
    assert.ok(Math.abs(progress.busRatio - busRatio) < 1e-12);
  }

  for (const endpoint of [1.25, 1.35]) {
    const below = deriveProgress(adjacentFloat(endpoint, -1) * 100000, 100000);
    const exact = deriveProgress(endpoint * 100000, 100000);
    const above = deriveProgress(adjacentFloat(endpoint, 1) * 100000, 100000);
    assert.ok(below.donationRatio <= exact.donationRatio);
    assert.ok(exact.donationRatio <= above.donationRatio);
  }

  const hostileGoal = 240429705269.63782;
  const hostileMidpoint = deriveProgress(hostileGoal * 1.30, hostileGoal);
  assert.equal(hostileMidpoint.donationRatio, 1.30);
  assert.equal(hostileMidpoint.busRatio, .5);
});

test("bus reveal preserves adjacent raised precision inside both endpoints", () => {
  const goal = 100000;
  const startRaised = goal * 1.25;
  const endRaised = goal * 1.35;
  const justInsideStart = adjacentFloat(startRaised, 1);
  const justInsideEnd = adjacentFloat(endRaised, -1);
  const startProgress = deriveProgress(justInsideStart, goal);
  const endProgress = deriveProgress(justInsideEnd, goal);

  assert.ok(justInsideStart > startRaised);
  assert.ok(justInsideEnd < endRaised);
  assert.ok(startProgress.donationRatio > 1.25);
  assert.ok(endProgress.donationRatio < 1.35);
  assert.ok(startProgress.busRatio > 0);
  assert.ok(startProgress.busRatio < 1);
  assert.ok(endProgress.busRatio > 0);
  assert.ok(endProgress.busRatio < 1);
});

test("block and student reveal sums are mathematically exact", () => {
  for (const count of [1, 2, 36, 293, 350]) {
    for (const ratio of [0, .0001, .1, .333333, .5, .999999, 1]) {
      const reveals = revealSeries(ratio, count);
      assert.equal(reveals.length, count);
      assert.ok(reveals.every((value) => value >= 0 && value <= 1));
      assert.ok(Math.abs(revealSum(ratio, count) - clamp(ratio) * count) < 1e-9);
      reveals.forEach((value, index) => assert.equal(value, revealAt(ratio, count, index)));
    }
  }
});

test("exponential smoothing caps frame delta at 50ms and converges", () => {
  const capped = exponentialStep(0, 100, 500, 420);
  assert.equal(capped, exponentialStep(0, 100, 50, 420));
  assert.ok(capped > 0 && capped < 100);
  let value = 0;
  for (let index = 0; index < 1000; index += 1) {
    value = exponentialStep(value, 100, 16.67, 420);
  }
  assert.ok(Math.abs(value - 100) < 1e-9);
  assert.equal(exponentialStep(0, 100, 16, 0), 100);
});
