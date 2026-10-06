const STAGE_ORDER = Object.freeze({
  foundation: 0,
  lower: 1,
  upper: 2,
  tower: 3,
  roof: 4
});

const BRICK_COLORS = ["#C96E43", "#BE623D", "#D47A4C", "#B95B39"];
const BLOCK_WIDTH = 36;
const BLOCK_HEIGHT = 22;
const BLOCK_DEPTH = 8;
const FACADE_ORDER = Object.freeze([
  11, 12, 10, 13, 9, 14, 8, 15, 7, 16, 6, 17, 5,
  18, 4, 19, 3, 20, 2, 21, 1, 22, 0, 23, 24, 25
]);
const TOWER_ORDER = Object.freeze([2, 3, 1, 4, 0, 5]);
const WING_PARAPET_ORDER = Object.freeze([
  8, 15, 7, 16, 6, 17, 5, 18, 4, 19,
  3, 20, 2, 21, 1, 22, 0, 23, 24, 25
]);

function deepFreeze(value) {
  if (!value || typeof value !== "object" || Object.isFrozen(value)) return value;
  for (const nested of Object.values(value)) deepFreeze(nested);
  return Object.freeze(value);
}

function distantSchoolSlot(slot, x, baseY, scale) {
  return {
    slot,
    x,
    baseY,
    scale,
    bounds: {
      left: x,
      right: x + 80 * scale,
      top: baseY - 98 * scale,
      bottom: baseY
    }
  };
}

const CAMPUS_GEOMETRY = deepFreeze({
  flag: {
    pole: { x: 960, top: 111, bottom: 252, width: 6 },
    finial: { cx: 963, cy: 108, r: 6 },
    cloth: {
      left: 966,
      right: 1098,
      top: 119,
      bottom: 207,
      width: 132,
      height: 88,
      hoistWidth: 33,
      poleEdgeX: 0,
      transform: "translate(966 119)"
    }
  },
  swings: {
    offsetX: -15,
    offsetY: 205,
    bounds: { left: 5, right: 130, top: 695, bottom: 865 },
    ground: {
      cx: 82,
      cy: 646,
      rx: 66,
      ry: 13,
      transform: "rotate(-11 82 646)"
    },
    beam: { x1: 44, y1: 516, x2: 126, y2: 495 },
    feet: [
      { x1: 54, y1: 513, x2: 20, y2: 659 },
      { x1: 54, y1: 513, x2: 78, y2: 644 },
      { x1: 116, y1: 498, x2: 91, y2: 641 },
      { x1: 116, y1: 498, x2: 145, y2: 632 }
    ],
    seats: [
      { centerX: 75, topY: 508.1, y: 590, rotation: -11 },
      { centerX: 101, topY: 501.4, y: 581, rotation: -11 }
    ]
  },
  distantSchools: [
    distantSchoolSlot(1, 18, 571, .54),
    distantSchoolSlot(2, 82, 555, .5),
    distantSchoolSlot(3, 325, 559, .56),
    distantSchoolSlot(4, 390, 578, .52),
    distantSchoolSlot(5, 455, 602, .48),
    distantSchoolSlot(6, 18, 620, .44),
    distantSchoolSlot(7, 65, 610, .44),
    distantSchoolSlot(8, 112, 604, .44),
    distantSchoolSlot(9, 314, 604, .44),
    distantSchoolSlot(10, 361, 622, .44),
    distantSchoolSlot(11, 408, 634, .44),
    distantSchoolSlot(12, 455, 648, .44),
    distantSchoolSlot(13, 15, 670, .36),
    distantSchoolSlot(14, 56, 662, .36),
    distantSchoolSlot(15, 97, 656, .36),
    distantSchoolSlot(16, 138, 652, .36),
    distantSchoolSlot(17, 179, 650, .36),
    distantSchoolSlot(18, 220, 654, .36),
    distantSchoolSlot(19, 261, 652, .36),
    distantSchoolSlot(20, 302, 650, .36),
    distantSchoolSlot(21, 343, 664, .36),
    distantSchoolSlot(22, 384, 676, .36),
    distantSchoolSlot(23, 425, 682, .36),
    distantSchoolSlot(24, 466, 692, .36),
    distantSchoolSlot(25, 220, 696, .36)
  ],
  trees: [
    { x: 230, y: 520, scale: 1.1, trunkHeight: 76 },
    { x: 1420, y: 590, scale: 1, trunkHeight: 98 },
    { x: 1330, y: 640, scale: .65, trunkHeight: 98 }
  ],
  bus: {
    facing: "left",
    mirrorAxisX: 698,
    bounds: { left: 1215, right: 1580, top: 815, bottom: 960 },
    artworkOffset: { x: 1050, y: 125 },
    startOffset: { x: 550, y: -115 },
    startBounds: { left: 1765, right: 2130, top: 700, bottom: 845 },
    routeDistance: Math.hypot(550, 115),
    wheels: [
      { cx: 235, cy: 805, r: 30 },
      { cx: 475, cy: 805, r: 30 }
    ],
    body: {
      x: 169,
      y: 732,
      width: 339,
      height: 73,
      strokeWidth: 4
    },
    cabin: {
      path: "M185 730 V712 Q185 702 195 702 H465 Q478 702 478 715 V732 H185 Z",
      bounds: { left: 183, right: 480, top: 700, bottom: 732 }
    },
    hood: {
      path: "M476 751 H509 Q520 751 523 760 L529 782 V805 H476 Z",
      bounds: { left: 476, right: 530, top: 750, bottom: 807 }
    },
    windows: [
      { x: 194, y: 720, width: 27, height: 25 },
      { x: 226, y: 716, width: 27, height: 29 },
      { x: 258, y: 710, width: 27, height: 35 },
      { x: 290, y: 710, width: 27, height: 35 },
      { x: 322, y: 710, width: 27, height: 35 },
      { x: 354, y: 710, width: 27, height: 35 },
      { x: 386, y: 710, width: 27, height: 35 }
    ],
    windshield: {
      path: "M445 710 H465 Q473 710 473 719 V745 H445 Z"
    },
    door: { x: 421, y: 712, width: 30, height: 83 },
    rails: [
      { x: 174, y: 758, width: 334, height: 4 },
      { x: 174, y: 782, width: 334, height: 4 }
    ],
    stopSign: { cx: 443, cy: 752, r: 10 }
  }
});

function brick(stage, column, row, x, y) {
  const seed = (column * 17 + row * 31 + STAGE_ORDER[stage] * 13) >>> 0;
  return {
    id: `${stage}-${column}-${row}`,
    stage,
    stageOrder: STAGE_ORDER[stage],
    column,
    row,
    x,
    y,
    width: BLOCK_WIDTH,
    height: BLOCK_HEIGHT,
    depth: BLOCK_DEPTH,
    color: BRICK_COLORS[seed % BRICK_COLORS.length]
  };
}

function appendWall(blocks, {
  stage,
  columns,
  rows,
  startX,
  baseY,
  columnOrder,
  skip = () => false
}) {
  const orderedColumns = columnOrder ?? Array.from(
    { length: columns },
    (_, column) => column
  );
  for (let row = 0; row < rows; row += 1) {
    for (const column of orderedColumns) {
      if (skip(column, row)) continue;
      const stagger = row % 2 ? BLOCK_WIDTH / 2 : 0;
      blocks.push(brick(
        stage,
        column,
        row,
        startX + column * BLOCK_WIDTH - stagger,
        baseY - row * BLOCK_HEIGHT
      ));
    }
  }
}

export function createSchoolBlocks() {
  const blocks = [];

  appendWall(blocks, {
    stage: "foundation",
    columns: 26,
    rows: 2,
    startX: 540,
    baseY: 675,
    columnOrder: FACADE_ORDER
  });

  appendWall(blocks, {
    stage: "lower",
    columns: 26,
    rows: 6,
    startX: 540,
    baseY: 631,
    columnOrder: FACADE_ORDER,
    skip(column, row) {
      const door = (column === 11 || column === 12) && row <= 5;
      const windowColumns = [1, 2, 4, 5, 7, 8, 16, 17, 19, 20, 22, 23];
      const window = windowColumns.includes(column) && row >= 2 && row <= 4;
      return door || window;
    }
  });

  appendWall(blocks, {
    stage: "upper",
    columns: 26,
    rows: 4,
    startX: 540,
    baseY: 499,
    columnOrder: FACADE_ORDER,
    skip(column, row) {
      const entranceArchHead = (column === 11 || column === 12) && row === 0;
      const windowColumns = [1, 2, 4, 5, 7, 8, 16, 17, 19, 20, 22, 23];
      const window = windowColumns.includes(column) && row >= 1 && row <= 3;
      return entranceArchHead || window;
    }
  });

  appendWall(blocks, {
    stage: "tower",
    columns: 6,
    rows: 6,
    startX: 855,
    baseY: 411,
    columnOrder: TOWER_ORDER,
    skip(column, row) {
      return (column === 2 || column === 3) && row >= 1 && row <= 3;
    }
  });

  for (const [row, y] of [411, 389].entries()) {
    for (const column of WING_PARAPET_ORDER) {
      blocks.push(brick(
        "roof",
        column,
        row,
        540 + column * BLOCK_WIDTH,
        y
      ));
    }
  }

  for (const [row, y] of [279, 257].entries()) {
    for (const column of TOWER_ORDER) {
      blocks.push(brick(
        "roof",
        column + 30,
        row + 2,
        855 + column * BLOCK_WIDTH,
        y
      ));
    }
  }

  return Object.freeze(blocks.map((block, index) => Object.freeze({ ...block, index })));
}

const STUDENT_FIGURES = Object.freeze([
  Object.freeze({ asset: "/Boy.png", label: "Boy" }),
  Object.freeze({ asset: "/Girl.png", label: "Girl" })
]);
const STUDENT_BOUNDS = Object.freeze({
  left: -18,
  right: 18,
  top: -67,
  bottom: 49
});

function createStudentRoute({
  side,
  startX,
  startY,
  targetX,
  targetY,
  targetRow
}) {
  const laneY = targetRow === 0
    ? 812
    : targetRow === 1
      ? 880
      : 960;
  const spawnX = side > 0 ? 1360 : startX;
  const spawnY = side > 0 ? laneY : startY;
  const waypoints = [
    [spawnX, spawnY],
    [spawnX, laneY],
    [targetX, laneY],
    [targetX, targetY]
  ]
    .filter((point, index, points) =>
      index === 0
      || point[0] !== points[index - 1][0]
      || point[1] !== points[index - 1][1]
    )
    .map(([x, y]) => Object.freeze({ x, y }));
  const segmentLengths = waypoints.slice(1).map((point, index) =>
    Math.hypot(
      point.x - waypoints[index].x,
      point.y - waypoints[index].y
    )
  );
  return Object.freeze({
    waypoints: Object.freeze(waypoints),
    segmentLengths: Object.freeze(segmentLengths),
    totalLength: segmentLengths.reduce((sum, length) => sum + length, 0)
  });
}

export function deriveStudentRoutePosition(
  student,
  reveal,
  reducedMotion = false
) {
  if (!Number.isFinite(reveal)) {
    throw new RangeError("student reveal must be finite");
  }
  const progress = Math.min(1, Math.max(0, reveal));
  const { route } = student;
  const start = route.waypoints[0];
  const target = route.waypoints.at(-1);
  if (reducedMotion) return progress > 0 ? target : start;
  if (progress === 0) return start;
  if (progress === 1) return target;

  let remainingDistance = progress * route.totalLength;
  let segmentIndex = 0;
  while (
    segmentIndex < route.segmentLengths.length - 1
    && remainingDistance > route.segmentLengths[segmentIndex]
  ) {
    remainingDistance -= route.segmentLengths[segmentIndex];
    segmentIndex += 1;
  }

  const segmentProgress = remainingDistance
    / route.segmentLengths[segmentIndex];
  const eased = segmentProgress * segmentProgress
    * (3 - 2 * segmentProgress);
  const from = route.waypoints[segmentIndex];
  const to = route.waypoints[segmentIndex + 1];
  const bobEnvelope = Math.sin(Math.PI * progress) ** 2;
  const bob = Math.sin(progress * Math.PI * 6 + student.index)
    * 2
    * bobEnvelope;
  return {
    x: from.x + (to.x - from.x) * eased,
    y: from.y + (to.y - from.y) * eased + bob
  };
}

export function createStudentLayout(maxStudents = 24) {
  const count = Math.min(24, Math.max(0, Math.trunc(maxStudents)));
  const students = Array.from({ length: count }, (_, index) => {
    const side = index % 2 === 0 ? -1 : 1;
    const lane = Math.floor(index / 2);
    const targetColumn = index % 12;
    const targetRow = Math.floor(index / 12);
    const targetX = 610 + targetColumn * 60 + (targetRow % 2) * 30;
    const targetY = 722 + targetRow * 58 + (targetColumn % 3) * 2;
    const route = createStudentRoute({
      side,
      startX: side < 0 ? 390 - lane * 13 : 1360,
      startY: 755 + (lane % 4) * 20,
      targetX,
      targetY,
      targetRow
    });
    const figure = STUDENT_FIGURES[index % STUDENT_FIGURES.length];
    return Object.freeze({
      id: `student-${index}`,
      index,
      side,
      startX: route.waypoints[0].x,
      startY: route.waypoints[0].y,
      targetX,
      targetY,
      scale: 0.82 + (targetY - 700) / 470,
      bounds: STUDENT_BOUNDS,
      route,
      figureAsset: figure.asset,
      figureLabel: figure.label
    });
  });
  return Object.freeze(students);
}

export function createTeacherLayout() {
  return deepFreeze([]);
}

export function createScene(config = {}) {
  const blocks = createSchoolBlocks();
  const students = createStudentLayout(config.maxStudents ?? 24);
  const teachers = createTeacherLayout();
  return deepFreeze({
    blocks,
    students,
    teachers,
    campus: CAMPUS_GEOMETRY,
    stageOrder: STAGE_ORDER
  });
}
