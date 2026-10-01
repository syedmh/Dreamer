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
    offsetY: 95,
    bounds: { left: 5, right: 130, top: 585, bottom: 755 },
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
    ],
    braces: [
      { x1: 29, y1: 616, x2: 72, y2: 605 },
      { x1: 98, y1: 598, x2: 137, y2: 588 }
    ],
    ladder: {
      rails: [
        { x1: 108, y1: 526, x2: 119, y2: 637 },
        { x1: 123, y1: 522, x2: 145, y2: 632 }
      ],
      rungs: [
        { x1: 111, y1: 559, x2: 130, y2: 554 },
        { x1: 113, y1: 581, x2: 134, y2: 576 },
        { x1: 115, y1: 603, x2: 138, y2: 598 }
      ]
    },
    pennants: [
      { x: 65, y: 511, color: "#FFD45F" },
      { x: 86, y: 506, color: "#FFF7DF" },
      { x: 107, y: 500, color: "#D54843" }
    ]
  },
  distantSchools: [
    {
      slot: 1,
      x: 18,
      baseY: 571,
      scale: .54,
      bounds: { left: 18, right: 61.2, top: 518.08, bottom: 571 }
    },
    {
      slot: 2,
      x: 82,
      baseY: 555,
      scale: .5,
      bounds: { left: 82, right: 122, top: 506, bottom: 555 }
    },
    {
      slot: 3,
      x: 325,
      baseY: 559,
      scale: .56,
      bounds: { left: 325, right: 369.8, top: 504.12, bottom: 559 }
    },
    {
      slot: 4,
      x: 390,
      baseY: 578,
      scale: .52,
      bounds: { left: 390, right: 431.6, top: 527.04, bottom: 578 }
    },
    {
      slot: 5,
      x: 455,
      baseY: 602,
      scale: .48,
      bounds: { left: 455, right: 493.4, top: 554.96, bottom: 602 }
    },
    {
      slot: 6,
      x: 357,
      baseY: 625,
      scale: .5,
      bounds: { left: 357, right: 397, top: 576, bottom: 625 }
    },
    {
      slot: 7,
      x: 422,
      baseY: 640,
      scale: .5,
      bounds: { left: 422, right: 462, top: 591, bottom: 640 }
    },
    {
      slot: 8,
      x: 302,
      baseY: 610,
      scale: .5,
      bounds: { left: 302, right: 342, top: 561, bottom: 610 }
    },
    {
      slot: 9,
      x: 260,
      baseY: 660,
      scale: .48,
      bounds: { left: 260, right: 298.4, top: 612.96, bottom: 660 }
    },
    {
      slot: 10,
      x: 315,
      baseY: 660,
      scale: .48,
      bounds: { left: 315, right: 353.4, top: 612.96, bottom: 660 }
    }
  ],
  trees: [
    { x: 230, y: 520, scale: 1.1, trunkHeight: 76 },
    { x: 1420, y: 590, scale: 1, trunkHeight: 98 },
    { x: 1330, y: 640, scale: .65, trunkHeight: 98 }
  ],
  bus: {
    bounds: { left: 150, right: 515, top: 675, bottom: 820 },
    artworkOffset: { x: -15, y: -15 },
    startOffset: { x: -600, y: 25 },
    startBounds: { left: -450, right: -85, top: 700, bottom: 845 },
    routeDistance: Math.hypot(600, 25),
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
