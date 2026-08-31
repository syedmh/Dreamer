import test from "node:test";
import assert from "node:assert/strict";
import {
  createScene,
  createSchoolBlocks,
  createStudentLayout,
  createTeacherLayout,
  deriveStudentRoutePosition
} from "../src/scene.mjs";
import { revealAt } from "../src/model.mjs";

const FACADE_ORDER = [
  11, 12, 10, 13, 9, 14, 8, 15, 7, 16, 6, 17, 5,
  18, 4, 19, 3, 20, 2, 21, 1, 22, 0, 23, 24, 25
];
const TOWER_ORDER = [2, 3, 1, 4, 0, 5];
const WING_PARAPET_ORDER = [
  8, 15, 7, 16, 6, 17, 5, 18, 4, 19,
  3, 20, 2, 21, 1, 22, 0, 23, 24, 25
];

function columnsFor(blocks, stage, row) {
  return blocks
    .filter((block) => block.stage === stage && block.row === row)
    .map((block) => block.column);
}

test("school layout exactly matches the frozen 308-block stage contract", () => {
  const first = createSchoolBlocks();
  const second = createSchoolBlocks();
  assert.deepEqual(first, second);
  assert.equal(first.length, 308);
  assert.deepEqual(
    [...new Set(first.map((block) => block.stage))],
    ["foundation", "lower", "upper", "tower", "roof"]
  );

  const expectedStages = [
    ["foundation", 0, 51, 52],
    ["lower", 52, 159, 108],
    ["upper", 160, 225, 66],
    ["tower", 226, 255, 30],
    ["roof", 256, 307, 52]
  ];
  for (const [stage, start, end, count] of expectedStages) {
    const stageBlocks = first.slice(start, end + 1);
    assert.equal(stageBlocks.length, count);
    assert.ok(stageBlocks.every((block) => block.stage === stage));
  }

  first.forEach((block, index) => {
    assert.equal(block.index, index);
    assert.equal(block.width, 36);
    assert.equal(block.height, 22);
    assert.equal(block.depth, 8);
    assert.ok(Object.isFrozen(block));
  });
  assert.equal(new Set(first.map((block) => block.id)).size, 308);
  assert.deepEqual({
    minX: Math.min(...first.map((block) => block.x)),
    maxX: Math.max(...first.map((block) => block.x + block.width + block.depth)),
    minY: Math.min(...first.map((block) => block.y - block.depth)),
    maxY: Math.max(...first.map((block) => block.y + block.height))
  }, {
    minX: 522,
    maxX: 1484,
    minY: 249,
    maxY: 697
  });
});

test("foundation and facade rows use the frozen center-out construction order", () => {
  const blocks = createSchoolBlocks();
  assert.deepEqual(columnsFor(blocks, "foundation", 0), FACADE_ORDER);
  assert.deepEqual(columnsFor(blocks, "foundation", 1), FACADE_ORDER);

  const lowerSkipped = new Set([
    1, 2, 4, 5, 7, 8, 11, 12, 16, 17, 19, 20, 22, 23
  ]);
  assert.deepEqual(
    columnsFor(blocks, "lower", 2),
    FACADE_ORDER.filter((column) => !lowerSkipped.has(column))
  );
  assert.deepEqual(
    columnsFor(blocks, "upper", 1),
    FACADE_ORDER.filter((column) => ![
      1, 2, 4, 5, 7, 8, 16, 17, 19, 20, 22, 23
    ].includes(column))
  );
  assert.deepEqual(
    columnsFor(blocks, "tower", 0),
    TOWER_ORDER
  );
});

test("lower, upper, and tower walls omit only the frozen aperture masks", () => {
  const blocks = createSchoolBlocks();
  const lower = blocks.filter((block) => block.stage === "lower");
  const upper = blocks.filter((block) => block.stage === "upper");
  const tower = blocks.filter((block) => block.stage === "tower");
  const windowColumns = [1, 2, 4, 5, 7, 8, 16, 17, 19, 20, 22, 23];

  assert.equal(lower.length, 108);
  for (const block of lower) {
    const entrance = [11, 12].includes(block.column) && block.row <= 5;
    const window = windowColumns.includes(block.column)
      && block.row >= 2
      && block.row <= 4;
    assert.equal(entrance || window, false);
  }

  assert.equal(upper.length, 66);
  for (const block of upper) {
    const entranceHead = [11, 12].includes(block.column) && block.row === 0;
    const window = windowColumns.includes(block.column)
      && block.row >= 1
      && block.row <= 3;
    assert.equal(entranceHead || window, false);
  }

  assert.equal(tower.length, 30);
  for (const block of tower) {
    const window = [2, 3].includes(block.column)
      && block.row >= 1
      && block.row <= 3;
    assert.equal(window, false);
  }
});

test("parapets are flat, full-width wing and tower caps in frozen order", () => {
  const roof = createSchoolBlocks().slice(256);
  assert.equal(roof.length, 52);
  assert.deepEqual(columnsFor(roof, "roof", 0), WING_PARAPET_ORDER);
  assert.deepEqual(columnsFor(roof, "roof", 1), WING_PARAPET_ORDER);
  assert.deepEqual(columnsFor(roof, "roof", 2), TOWER_ORDER.map((column) => column + 30));
  assert.deepEqual(columnsFor(roof, "roof", 3), TOWER_ORDER.map((column) => column + 30));
  assert.ok(roof.filter((block) => block.row === 0).every((block) => block.y === 411));
  assert.ok(roof.filter((block) => block.row === 1).every((block) => block.y === 389));
  assert.ok(roof.filter((block) => block.row === 2).every((block) => block.y === 279));
  assert.ok(roof.filter((block) => block.row === 3).every((block) => block.y === 257));
});

test("student layout is deterministic and capped at 36", () => {
  assert.deepEqual(createStudentLayout(36), createStudentLayout(36));
  assert.equal(createStudentLayout(100).length, 36);
  assert.equal(createStudentLayout(-4).length, 0);
  const students = createStudentLayout(36);
  assert.equal(new Set(students.map((student) => student.id)).size, 36);
  assert.equal(new Set(students.map((student) => [
    student.targetX,
    student.targetY,
    student.scale
  ].join("/"))).size, 36);
  assert.ok(students.every((student) => student.targetX >= 600 && student.targetX <= 1350));
  assert.ok(students.every((student) => student.targetY >= 700 && student.targetY <= 850));
  assert.ok(students.some((student) => student.side < 0));
  assert.ok(students.some((student) => student.side > 0));

  const targetBounds = (student) => ({
    left: student.targetX + student.bounds.left * student.scale,
    right: student.targetX + student.bounds.right * student.scale,
    top: student.targetY + student.bounds.top * student.scale,
    bottom: student.targetY + student.bounds.bottom * student.scale
  });
  let maximumOverlapArea = 0;
  for (let first = 0; first < students.length; first += 1) {
    for (let second = first + 1; second < students.length; second += 1) {
      const firstBounds = targetBounds(students[first]);
      const secondBounds = targetBounds(students[second]);
      const overlapWidth = Math.max(
        0,
        Math.min(firstBounds.right, secondBounds.right)
          - Math.max(firstBounds.left, secondBounds.left)
      );
      const overlapHeight = Math.max(
        0,
        Math.min(firstBounds.bottom, secondBounds.bottom)
          - Math.max(firstBounds.top, secondBounds.top)
      );
      maximumOverlapArea = Math.max(
        maximumOverlapArea,
        overlapWidth * overlapHeight
      );
    }
  }
  assert.ok(
    maximumOverlapArea < 850,
    `maximum student target overlap area: ${maximumOverlapArea}`
  );
});

test("student routes are frozen staged lanes with exact snap endpoints", () => {
  const students = createStudentLayout(36);
  for (const student of students) {
    const targetRow = Math.floor(student.index / 12);
    const expectedLaneY = targetRow === 0
      ? 812
      : targetRow === 1
        ? 880
        : 960;
    const { route } = student;
    assert.ok(Object.isFrozen(route));
    assert.ok(Object.isFrozen(route.waypoints));
    assert.ok(Object.isFrozen(route.segmentLengths));
    assert.ok(route.waypoints.every(Object.isFrozen));
    assert.ok(route.segmentLengths.every((length) => length > 0));
    assert.equal(
      route.segmentLengths.reduce((sum, length) => sum + length, 0),
      route.totalLength
    );
    assert.deepEqual(route.waypoints[0], {
      x: student.startX,
      y: student.startY
    });
    assert.deepEqual(route.waypoints.at(-1), {
      x: student.targetX,
      y: student.targetY
    });
    assert.ok(route.waypoints.some((point) => point.y === expectedLaneY));
    if (student.side > 0) {
      assert.deepEqual(route.waypoints[0], { x: 1360, y: expectedLaneY });
    }
    for (let index = 1; index < route.waypoints.length; index += 1) {
      const previous = route.waypoints[index - 1];
      const current = route.waypoints[index];
      assert.ok(
        previous.x === current.x || previous.y === current.y,
        `${student.id} segment ${index - 1} must be axis-aligned`
      );
      assert.notDeepEqual(current, previous);
    }

    const start = { x: student.startX, y: student.startY };
    const target = { x: student.targetX, y: student.targetY };
    assert.deepEqual(deriveStudentRoutePosition(student, -1), start);
    assert.deepEqual(deriveStudentRoutePosition(student, 0), start);
    assert.deepEqual(deriveStudentRoutePosition(student, 1), target);
    assert.deepEqual(deriveStudentRoutePosition(student, 2), target);
    assert.deepEqual(
      deriveStudentRoutePosition(student, 0, true),
      start
    );
    assert.deepEqual(
      deriveStudentRoutePosition(student, Number.MIN_VALUE, true),
      target
    );
    assert.deepEqual(
      deriveStudentRoutePosition(student, 1, true),
      target
    );
  }
  assert.throws(
    () => deriveStudentRoutePosition(students[0], Number.NaN),
    /student reveal must be finite/
  );
});

test("third-row outer lane clears settled painted students at the former contact ratios", () => {
  const students = createStudentLayout(36);
  const settled = students[26];
  const moving = students[28];
  const settledPaintBottom =
    settled.targetY + settled.bounds.bottom * settled.scale;

  for (const percent of [119.917, 119.926, 119.935]) {
    const studentRatio = (percent - 100) / 25;
    const reveal = revealAt(studentRatio, students.length, moving.index);
    const position = deriveStudentRoutePosition(moving, reveal);
    const movingPaintTop =
      position.y + moving.bounds.top * moving.scale;
    assert.ok(
      movingPaintTop - settledPaintBottom >= 16,
      `${percent}% painted clearance: ${movingPaintTop - settledPaintBottom}`
    );
  }
});

test("full-motion student routes are continuous, bounded, and reverse deterministic", () => {
  const students = createStudentLayout(36);
  for (const student of students) {
    const forward = [];
    let maximumStep = 0;
    for (let step = 0; step <= 1000; step += 1) {
      const position = deriveStudentRoutePosition(student, step / 1000);
      forward.push(position);
      if (step > 0) {
        maximumStep = Math.max(
          maximumStep,
          Math.hypot(
            position.x - forward[step - 1].x,
            position.y - forward[step - 1].y
          )
        );
      }
    }
    const reverse = Array.from(
      { length: 1001 },
      (_, step) => deriveStudentRoutePosition(student, (1000 - step) / 1000)
    ).reverse();
    assert.deepEqual(reverse, forward);
    assert.ok(
      maximumStep < 4,
      `${student.id} maximum 0.1% route step: ${maximumStep}`
    );

    let traversedLength = 0;
    for (const segmentLength of student.route.segmentLengths.slice(0, -1)) {
      traversedLength += segmentLength;
      const boundary = traversedLength / student.route.totalLength;
      const before = deriveStudentRoutePosition(student, boundary - 1e-7);
      const after = deriveStudentRoutePosition(student, boundary + 1e-7);
      assert.ok(
        Math.hypot(after.x - before.x, after.y - before.y) < .001,
        `${student.id} route discontinuity at ${boundary}`
      );
    }
  }
});

test("teacher and campus geometry are deterministic, exact, and deeply frozen", () => {
  const teachers = createTeacherLayout();
  assert.deepEqual(teachers, createTeacherLayout());
  assert.deepEqual(
    teachers.map(({ transform, bounds }) => ({ transform, bounds })),
    [
      {
        transform: "translate(500 704) scale(1.08)",
        bounds: { left: -16, right: 16, top: -68, bottom: 0 }
      },
      {
        transform: "translate(1517 704) scale(1.08)",
        bounds: { left: -16, right: 16, top: -68, bottom: 0 }
      }
    ]
  );

  const scene = createScene();
  assert.deepEqual(scene.campus.swings.bounds, {
    left: 1494,
    right: 1588,
    top: 476,
    bottom: 624
  });
  assert.deepEqual(scene.campus.bus.bounds, {
    left: 150,
    right: 454,
    top: 460,
    bottom: 576
  });
  assert.deepEqual(scene.campus.flag.pole, {
    x: 960,
    top: 111,
    bottom: 252,
    width: 6
  });
  assert.equal(scene.campus.flag.finial.cx, 963);
  assert.equal(scene.campus.flag.cloth.transform, "translate(966 119)");
  assert.deepEqual(scene.campus.flag.cloth, {
    left: 966,
    right: 1098,
    top: 119,
    bottom: 207,
    width: 132,
    height: 88,
    hoistWidth: 33,
    poleEdgeX: 0,
    transform: "translate(966 119)"
  });
  assert.equal(
    scene.campus.flag.cloth.width / scene.campus.flag.cloth.height,
    3 / 2
  );
  assert.equal(
    scene.campus.flag.cloth.hoistWidth / scene.campus.flag.cloth.width,
    .25
  );
  assert.deepEqual(scene.campus.bus.wheels, [
    { cx: 218, cy: 556, r: 20 },
    { cx: 390, cy: 556, r: 20 }
  ]);
  assert.equal(scene.campus.bus.startOffsetX, -360);
  assert.equal(scene.campus.bus.startOffsetY, 34);
  assert.equal(
    scene.campus.bus.routeDistance,
    Math.hypot(
      scene.campus.bus.startOffsetX,
      scene.campus.bus.startOffsetY
    )
  );

  const assertDeeplyFrozen = (value) => {
    if (!value || typeof value !== "object") return;
    assert.ok(Object.isFrozen(value));
    for (const nested of Object.values(value)) assertDeeplyFrozen(nested);
  };
  assertDeeplyFrozen(teachers);
  assertDeeplyFrozen(scene.campus);
});

test("scene combines frozen limits without mutable shared state", () => {
  const scene = createScene({ maxStudents: 20 });
  assert.equal(scene.blocks.length, 308);
  assert.equal(scene.students.length, 20);
  assert.equal(scene.teachers.length, 2);
  assert.ok(Object.isFrozen(scene));
  assert.ok(Object.isFrozen(scene.blocks));
  assert.ok(Object.isFrozen(scene.students));
  assert.ok(Object.isFrozen(scene.teachers));
  assert.ok(Object.isFrozen(scene.campus));
});
