import { clamp, revealAt } from "./model.mjs";
import { createCurrencyFormatter } from "./currency.mjs";
import { deriveStudentRoutePosition } from "./scene.mjs";

const SVG_NS = "http://www.w3.org/2000/svg";
const STUDENT_ROUTE_SPEED_PER_MS = 1.2;
const SETTLED_RENDER_RESULT = Object.freeze({ needsFrame: false });
const PENDING_RENDER_RESULT = Object.freeze({ needsFrame: true });
const WINDOW_X = Object.freeze([569, 677, 785, 1109, 1217, 1325]);
const FINISH_RANGES = Object.freeze({
  shell: Object.freeze([.17, .30]),
  lower: Object.freeze([.31, .52]),
  upper: Object.freeze([.58, .74]),
  tower: Object.freeze([.75, .84]),
  final: Object.freeze([.84, 1])
});
const VIEWBOX = Object.freeze({
  width: 1600,
  height: 900
});
const KITE_PALETTES = Object.freeze([
  Object.freeze({
    body: "#FFD45F",
    accent: "#D54843",
    tail: "#078447"
  }),
  Object.freeze({
    body: "#FFF7DF",
    accent: "#078447",
    tail: "#C96E43"
  }),
  Object.freeze({
    body: "#8BE0FA",
    accent: "#17352A",
    tail: "#FFD45F"
  }),
  Object.freeze({
    body: "#F8AA73",
    accent: "#17352A",
    tail: "#FFF7DF"
  }),
  Object.freeze({
    body: "#BCE7CB",
    accent: "#C96E43",
    tail: "#17352A"
  })
]);
const KITE_OUTLINE_POINTS = Object.freeze([
  Object.freeze([0, -36]),
  Object.freeze([30, 0]),
  Object.freeze([0, 36]),
  Object.freeze([-30, 0]),
  Object.freeze([-9, 58]),
  Object.freeze([9, 58]),
  Object.freeze([-8, 92]),
  Object.freeze([8, 92])
]);
const KITE_SAFE_AREAS = Object.freeze([
  Object.freeze({
    // Spread small kites around the large left cloud without entering the
    // scoreboard or hillside.
    left: 110,
    top: 150,
    right: 430,
    bottom: 365
  }),
  Object.freeze({
    // Fill the open sky between the left and center clouds.
    left: 430,
    top: 105,
    right: 720,
    bottom: 345
  }),
  Object.freeze({
    // Scatter around the small high cloud while staying left of the flag.
    left: 680,
    top: 55,
    right: 940,
    bottom: 275
  }),
  Object.freeze({
    // Use the open strip above the right school wing and below the flag.
    left: 1040,
    top: 225,
    right: 1235,
    bottom: 385
  }),
  Object.freeze({
    // Continue through the far-right cloud while avoiding the progress card.
    left: 1220,
    top: 245,
    right: 1470,
    bottom: 400
  })
]);
const KITE_PLACEMENT_ATTEMPTS = 40;
const KITE_SEVERE_OVERLAP_RATIO = .12;
const KITE_PADDING = 8;
const FIREWORK_SHAPES = Object.freeze({
  // Extents include endpoint sparks and the burst animation's maximum scale.
  classic: Object.freeze({ extentX: 126, extentY: 126 }),
  ring: Object.freeze({ extentX: 102, extentY: 102 }),
  star: Object.freeze({ extentX: 112, extentY: 112 }),
  chrysanthemum: Object.freeze({ extentX: 122, extentY: 122 }),
  willow: Object.freeze({ extentX: 94, extentY: 150 })
});
const FIREWORK_SAFE_REGIONS = Object.freeze([
  Object.freeze({
    id: "left-sky",
    zone: "left",
    // Clear of the HTML header above and the hillside/swing set below.
    bounds: Object.freeze({ left: 80, top: 175, right: 520, bottom: 490 }),
    shapes: Object.freeze(["classic", "ring", "star", "chrysanthemum", "willow"]),
    scale: Object.freeze([.72, 1.02])
  }),
  Object.freeze({
    id: "center-sky",
    zone: "center",
    // Between the header and the school roof, ending left of the tower/flag.
    bounds: Object.freeze({ left: 430, top: 165, right: 825, bottom: 375 }),
    shapes: Object.freeze(["classic", "ring", "star", "chrysanthemum"]),
    scale: Object.freeze([.68, .94])
  }),
  Object.freeze({
    id: "upper-sky",
    zone: "upper",
    // Compact high bursts between the title edge and the flag pole.
    bounds: Object.freeze({ left: 790, top: 38, right: 950, bottom: 292 }),
    shapes: Object.freeze(["classic", "ring", "star"]),
    scale: Object.freeze([.54, .72])
  }),
  Object.freeze({
    id: "right-sky",
    zone: "right",
    // Below the progress card and above the right school wing.
    bounds: Object.freeze({ left: 1105, top: 280, right: 1535, bottom: 380 }),
    shapes: Object.freeze(["ring", "star"]),
    scale: Object.freeze([.42, .52])
  })
]);
const FIREWORK_PALETTES = Object.freeze([
  Object.freeze(["#FFFFFF", "#FFE66D", "#FF5D8F", "#7B61FF", "#28E7FF", "#39FF88"]),
  Object.freeze(["#FFF7DF", "#FF9F1C", "#FF3B30", "#FF4FD8", "#00E5FF", "#7CFF6B"]),
  Object.freeze(["#FFFFFF", "#F9F871", "#00F5D4", "#00BBF9", "#9B5DE5", "#F15BB5"]),
  Object.freeze(["#FFF4B8", "#FF6B35", "#FF206E", "#8A4FFF", "#3A86FF", "#45F0A8"]),
  Object.freeze(["#FFFFFF", "#FFD60A", "#FF375F", "#BF5AF2", "#64D2FF", "#30D158"])
]);
const FIREWORK_FULL_DURATION_MS = 1450;
const FIREWORK_REDUCED_DURATION_MS = 680;
const FIREWORK_MAX_ACTIVE = 14;

function svg(name, attributes = {}, parent) {
  const element = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) {
    element.setAttribute(key, String(value));
  }
  parent?.append(element);
  return element;
}

function randomBetween(min, max) {
  return min + Math.random() * (max - min);
}

function randomItem(items) {
  return items[Math.floor(Math.random() * items.length)];
}

function shuffled(items) {
  const copy = [...items];
  for (let index = copy.length - 1; index > 0; index -= 1) {
    const swapIndex = Math.floor(Math.random() * (index + 1));
    [copy[index], copy[swapIndex]] = [copy[swapIndex], copy[index]];
  }
  return copy;
}

function rectangle(left, top, right, bottom) {
  return { left, top, right, bottom };
}

function rectangleArea(rect) {
  return Math.max(0, rect.right - rect.left)
    * Math.max(0, rect.bottom - rect.top);
}

function expandRectangle(rect, horizontal, vertical = horizontal) {
  return rectangle(
    rect.left - horizontal,
    rect.top - vertical,
    rect.right + horizontal,
    rect.bottom + vertical
  );
}

function intersectionArea(first, second) {
  return rectangleArea(rectangle(
    Math.max(first.left, second.left),
    Math.max(first.top, second.top),
    Math.min(first.right, second.right),
    Math.min(first.bottom, second.bottom)
  ));
}

function overflowArea(rect, bounds) {
  return Math.max(0, rectangleArea(rect) - intersectionArea(rect, bounds));
}

function formatKiteCountText(count) {
  return `${count} ${count === 1 ? "kite" : "kites"} in the sky.`;
}

export function progressiveOpacity(ratio, start, end) {
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) {
    throw new RangeError("finish range must have a finite positive span");
  }
  return clamp((clamp(ratio) - start) / (end - start));
}

function fixedPreservingEndpoints(value, fractionDigits, endpoints) {
  const fixed = value.toFixed(fractionDigits);
  const rounded = Number(fixed);
  return endpoints.some(
    (endpoint) => value !== endpoint && rounded === endpoint
  )
    ? String(value)
    : fixed;
}

function transformPreservingEndpoints(transform, reveal) {
  return reveal > 0 && reveal < 1
    ? `${transform} translate(0 0)`
    : transform;
}

function setMeterWidth(meterFill, meterPercent) {
  const width = `${fixedPreservingEndpoints(
    meterPercent,
    3,
    [0, 100]
  )}%`;
  if (meterPercent > 0 && meterPercent < 100) {
    meterFill.style.setProperty("--meter-width", width);
    meterFill.style.width = "var(--meter-width)";
  } else {
    meterFill.style.removeProperty("--meter-width");
    meterFill.style.width = width;
  }
}

function progressiveGroup(parent, name, layerClass = "") {
  const [start, end] = FINISH_RANGES[name];
  const className = ["architectural-finish", `finish-${name}`, layerClass]
    .filter(Boolean)
    .join(" ");
  return {
    element: svg("g", {
      class: className,
      "data-finish": name,
      "data-start": start,
      "data-end": end,
      opacity: 0
    }, parent),
    start,
    end
  };
}

function addDefinitions(root) {
  const defs = svg("defs", {}, root);
  const sky = svg("linearGradient", {
    id: "sky-gradient",
    x1: 0,
    y1: 0,
    x2: 0,
    y2: 1
  }, defs);
  svg("stop", { offset: "0%", "stop-color": "#68C9F2" }, sky);
  svg("stop", { offset: "100%", "stop-color": "#D9F3FA" }, sky);

  const nightSky = svg("linearGradient", {
    id: "night-sky-gradient",
    x1: 0,
    y1: 0,
    x2: 0,
    y2: 1
  }, defs);
  svg("stop", { offset: "0%", "stop-color": "#07142F" }, nightSky);
  svg("stop", { offset: "100%", "stop-color": "#28476F" }, nightSky);

  const grass = svg("linearGradient", {
    id: "grass-gradient",
    x1: 0,
    y1: 0,
    x2: 0,
    y2: 1
  }, defs);
  svg("stop", { offset: "0%", "stop-color": "#2FAF68" }, grass);
  svg("stop", { offset: "100%", "stop-color": "#078447" }, grass);

  const glass = svg("linearGradient", {
    id: "glass-gradient",
    x1: 0,
    y1: 0,
    x2: 1,
    y2: 1
  }, defs);
  svg("stop", { offset: "0%", "stop-color": "#A9C8C4" }, glass);
  svg("stop", { offset: "100%", "stop-color": "#547B76" }, glass);

  const shadow = svg("filter", {
    id: "soft-shadow",
    x: "-30%",
    y: "-30%",
    width: "160%",
    height: "180%"
  }, defs);
  svg("feDropShadow", {
    dx: 0,
    dy: 12,
    stdDeviation: 12,
    "flood-color": "#17352A",
    "flood-opacity": .22
  }, shadow);

  const entranceClip = svg("clipPath", { id: "entrance-arch-clip" }, defs);
  svg("path", {
    d: "M918 653 V538 A45 45 0 0 1 1008 538 V653 Z"
  }, entranceClip);

  const towerClip = svg("clipPath", { id: "tower-window-clip" }, defs);
  svg("path", {
    d: "M929 407 V373 A34 34 0 0 1 997 373 V407 Z"
  }, towerClip);

  const flagClip = svg("clipPath", { id: "pakistan-flag-clip" }, defs);
  svg("path", {
    d: "M0 2 C36 0 84 8 132 1 L132 86 C84 88 36 80 0 87 Z"
  }, flagClip);
}

function addLandscape(root) {
  const landscape = svg("g", { class: "landscape" }, root);
  svg("rect", {
    width: 1600,
    height: 900,
    fill: "url(#sky-gradient)"
  }, landscape);
  svg("rect", {
    class: "night-sky",
    width: 1600,
    height: 900,
    fill: "url(#night-sky-gradient)"
  }, landscape);

  const stars = svg("g", {
    class: "night-stars",
    "aria-hidden": "true"
  }, landscape);
  const starPoints = [
    [105, 250, 3], [170, 335, 2], [350, 105, 3], [430, 245, 2],
    [520, 90, 2], [610, 205, 3], [755, 75, 2], [830, 285, 2],
    [900, 155, 3], [1040, 75, 2], [1115, 215, 3], [1210, 105, 2],
    [1300, 310, 3], [1430, 225, 2], [1510, 85, 3], [1550, 350, 2]
  ];
  for (const [cx, cy, radius] of starPoints) {
    svg("circle", {
      cx,
      cy,
      r: radius,
      fill: "#FFF7DF"
    }, stars);
  }

  const moon = svg("g", {
    class: "night-moon",
    "aria-hidden": "true"
  }, landscape);
  svg("circle", {
    cx: 740,
    cy: 245,
    r: 52,
    fill: "#FFF2B8"
  }, moon);
  svg("circle", {
    cx: 762,
    cy: 228,
    r: 48,
    fill: "#122746"
  }, moon);

  const sun = svg("g", {
    class: "day-sun",
    "aria-hidden": "true"
  }, landscape);
  svg("circle", {
    cx: 265,
    cy: 280,
    r: 64,
    fill: "#FFD45F",
    opacity: .95
  }, sun);
  svg("circle", {
    cx: 265,
    cy: 280,
    r: 90,
    fill: "none",
    stroke: "#FFD45F",
    "stroke-width": 4,
    opacity: .3
  }, sun);

  const clouds = [
    [280, 190, 1.15, .84, false, -450, 1500, 34, -8],
    [1160, 270, .75, .60, true, -1350, 620, 48, -31],
    [710, 135, .58, .56, true, -900, 1070, 41, -19],
    [90, 92, .42, .42, true, -270, 1690, 58, -44],
    [1420, 118, .48, .46, true, -1600, 360, 63, -13],
    [510, 305, .88, .72, false, -700, 1270, 39, -27],
    [940, 350, .50, .48, true, -1120, 840, 55, -49],
    [1380, 205, .96, .76, false, -1580, 400, 45, -22]
  ];
  for (
    const [x, y, scale, opacity, far, startX, endX, duration, delay] of clouds
  ) {
    const position = svg("g", {
      transform: `translate(${x} ${y}) scale(${scale})`,
      opacity
    }, landscape);
    const group = svg("g", {
      class: `cloud${far ? " cloud--far" : ""}`
    }, position);
    group.style.setProperty("--cloud-start-x", `${startX}px`);
    group.style.setProperty("--cloud-end-x", `${endX}px`);
    group.style.setProperty("--cloud-duration", `${duration}s`);
    group.style.setProperty("--cloud-delay", `${delay}s`);
    svg("ellipse", {
      cx: 0,
      cy: 20,
      rx: 82,
      ry: 28,
      fill: "#FFF7DF"
    }, group);
    svg("circle", { cx: -36, cy: 0, r: 34, fill: "#FFF7DF" }, group);
    svg("circle", { cx: 10, cy: -13, r: 47, fill: "#FFF7DF" }, group);
    svg("circle", { cx: 51, cy: 5, r: 31, fill: "#FFF7DF" }, group);
  }

  svg("path", {
    d: "M0 585 Q210 505 410 578 T810 568 T1210 552 T1600 565 V900 H0Z",
    fill: "#57BE74"
  }, landscape);
  svg("path", {
    d: "M0 665 Q230 590 435 661 T845 648 T1240 632 T1600 650 V900 H0Z",
    fill: "url(#grass-gradient)"
  }, landscape);

  const trees = [
    [230, 590, 1.1],
    [360, 625, .75],
    [1420, 590, 1],
    [1330, 640, .65]
  ];
  for (const [x, y, scale] of trees) {
    const tree = svg("g", {
      transform: `translate(${x} ${y}) scale(${scale})`
    }, landscape);
    svg("rect", {
      x: -10,
      y: 12,
      width: 20,
      height: 98,
      rx: 8,
      fill: "#8F5A38"
    }, tree);
    svg("circle", { cx: 0, cy: 0, r: 52, fill: "#078447" }, tree);
    svg("circle", { cx: -34, cy: 18, r: 35, fill: "#149956" }, tree);
    svg("circle", { cx: 34, cy: 20, r: 38, fill: "#0A713E" }, tree);
  }

  svg("rect", {
    class: "night-ground-shade",
    x: 0,
    y: 430,
    width: 1600,
    height: 470,
    fill: "#07142F"
  }, landscape);
}

function createKiteBounds({
  centerX,
  centerY,
  scale,
  rotation,
  driftX,
  driftY
}) {
  const radians = rotation * Math.PI / 180;
  const cosine = Math.cos(radians);
  const sine = Math.sin(radians);
  let left = Number.POSITIVE_INFINITY;
  let top = Number.POSITIVE_INFINITY;
  let right = Number.NEGATIVE_INFINITY;
  let bottom = Number.NEGATIVE_INFINITY;

  for (const [pointX, pointY] of KITE_OUTLINE_POINTS) {
    const scaledX = pointX * scale;
    const scaledY = pointY * scale;
    const rotatedX = scaledX * cosine - scaledY * sine;
    const rotatedY = scaledX * sine + scaledY * cosine;
    left = Math.min(left, centerX + rotatedX);
    top = Math.min(top, centerY + rotatedY);
    right = Math.max(right, centerX + rotatedX);
    bottom = Math.max(bottom, centerY + rotatedY);
  }

  return expandRectangle(
    rectangle(left, top, right, bottom),
    Math.abs(driftX) + KITE_PADDING,
    Math.abs(driftY) + KITE_PADDING
  );
}

function createKiteCandidate(existingBounds) {
  let bestCandidate = null;
  let bestScore = Number.POSITIVE_INFINITY;

  for (let attempt = 0; attempt < KITE_PLACEMENT_ATTEMPTS; attempt += 1) {
    const safeArea = KITE_SAFE_AREAS[
      Math.floor(Math.random() * KITE_SAFE_AREAS.length)
    ];
    const scale = randomBetween(.30, .48);
    const rotation = randomBetween(-26, 26);
    const driftXStart = randomBetween(-5, -2);
    const driftXEnd = randomBetween(2, 6);
    const driftYStart = randomBetween(1, 4);
    const driftYEnd = randomBetween(-5, -2);
    const driftRotate = randomBetween(.8, 2.4);
    const driftX = Math.max(Math.abs(driftXStart), Math.abs(driftXEnd));
    const driftY = Math.max(Math.abs(driftYStart), Math.abs(driftYEnd));
    const provisionalBounds = createKiteBounds({
      centerX: 0,
      centerY: 0,
      scale,
      rotation,
      driftX,
      driftY
    });
    const minX = safeArea.left - provisionalBounds.left;
    const maxX = safeArea.right - provisionalBounds.right;
    const minY = safeArea.top - provisionalBounds.top;
    const maxY = safeArea.bottom - provisionalBounds.bottom;
    const centerX = minX <= maxX
      ? randomBetween(minX, maxX)
      : (safeArea.left + safeArea.right) / 2;
    const centerY = minY <= maxY
      ? randomBetween(minY, maxY)
      : (safeArea.top + safeArea.bottom) / 2;
    const bounds = createKiteBounds({
      centerX,
      centerY,
      scale,
      rotation,
      driftX,
      driftY
    });

    let score = overflowArea(bounds, safeArea) * 400;
    let blocked = score > 0;

    for (const occupiedBounds of existingBounds) {
      const overlap = intersectionArea(bounds, occupiedBounds);
      if (overlap <= 0) continue;
      const overlapRatio = overlap / Math.max(
        1,
        Math.min(rectangleArea(bounds), rectangleArea(occupiedBounds))
      );
      const bufferedOverlap = intersectionArea(
        expandRectangle(bounds, 10),
        expandRectangle(occupiedBounds, 10)
      );
      if (overlapRatio > KITE_SEVERE_OVERLAP_RATIO) blocked = true;
      score += overlap * 8 + bufferedOverlap * 2 + overlapRatio * 22000;
    }

    const candidate = {
      bounds,
      centerX,
      centerY,
      colors: KITE_PALETTES[Math.floor(Math.random() * KITE_PALETTES.length)],
      safeArea,
      delay: randomBetween(-5, 0),
      driftRotate,
      driftXEnd,
      driftXStart,
      driftYEnd,
      driftYStart,
      duration: randomBetween(4.8, 7.3),
      rotation,
      scale
    };

    if (score < bestScore) {
      bestScore = score;
      bestCandidate = candidate;
    }
    if (!blocked) return candidate;
  }

  return bestCandidate;
}

function appendKite(root, kiteIndex, candidate) {
  const group = svg("g", {
    class: "sky-kite",
    "data-kite-index": kiteIndex,
    "aria-hidden": "true",
    focusable: "false"
  }, root);
  group.style.setProperty("--kite-duration", `${candidate.duration.toFixed(2)}s`);
  group.style.setProperty("--kite-delay", `${candidate.delay.toFixed(2)}s`);
  group.style.setProperty("--kite-drift-x-start", `${candidate.driftXStart.toFixed(2)}px`);
  group.style.setProperty("--kite-drift-x-end", `${candidate.driftXEnd.toFixed(2)}px`);
  group.style.setProperty("--kite-drift-y-start", `${candidate.driftYStart.toFixed(2)}px`);
  group.style.setProperty("--kite-drift-y-end", `${candidate.driftYEnd.toFixed(2)}px`);
  group.style.setProperty("--kite-drift-rotate", `${candidate.driftRotate.toFixed(2)}deg`);

  const anchor = svg("g", {
    transform: `translate(${candidate.centerX.toFixed(2)} ${candidate.centerY.toFixed(2)})`
  }, group);
  const rotated = svg("g", {
    transform: `rotate(${candidate.rotation.toFixed(2)})`
  }, anchor);
  const scaled = svg("g", {
    transform: `scale(${candidate.scale.toFixed(3)})`
  }, rotated);
  const flyer = svg("g", { class: "kite-fly" }, scaled);

  svg("path", {
    d: "M0 -36 L30 0 L0 36 L-30 0 Z",
    fill: candidate.colors.body,
    stroke: "#17352A",
    "stroke-width": 3,
    "stroke-linejoin": "round"
  }, flyer);
  svg("path", {
    d: "M0 -36 L30 0 L0 4 L-18 0 Z",
    fill: candidate.colors.accent
  }, flyer);
  svg("path", {
    d: "M0 4 L30 0 L0 36 Z",
    fill: candidate.colors.tail,
    opacity: .88
  }, flyer);
  svg("line", {
    x1: 0,
    y1: -34,
    x2: 0,
    y2: 34,
    stroke: "#17352A",
    "stroke-width": 2.2,
    "stroke-linecap": "round"
  }, flyer);
  svg("line", {
    x1: -27,
    y1: 0,
    x2: 27,
    y2: 0,
    stroke: "#17352A",
    "stroke-width": 2.2,
    "stroke-linecap": "round"
  }, flyer);
  svg("path", {
    class: "kite-tail",
    d: "M0 36 C-8 48 10 58 1 69 C-7 81 8 90 0 98",
    fill: "none",
    stroke: "#17352A",
    "stroke-width": 2.6,
    "stroke-linecap": "round"
  }, flyer);
  for (const [x, y] of [[0, 58], [1, 82]]) {
    svg("path", {
      d: `M${x} ${y} l-8 -5 l1 11 Z M${x} ${y} l8 -5 l-1 11 Z`,
      fill: candidate.colors.accent,
      stroke: "#17352A",
      "stroke-width": 1.1,
      "stroke-linejoin": "round"
    }, flyer);
  }

  return group;
}

function createKiteController(root, sceneSvg, sceneDescription, sceneDescriptionText) {
  const layer = svg("g", {
    class: "kite-layer",
    "data-kite-count": 0,
    "aria-hidden": "true",
    focusable: "false"
  }, sceneSvg);
  const occupiedBounds = [];

  function updateKiteDescription(count) {
    root.dataset.kiteCount = String(count);
    sceneSvg.dataset.kiteCount = String(count);
    layer.dataset.kiteCount = String(count);
    if (sceneDescription) {
      sceneDescription.textContent = `${sceneDescriptionText} ${formatKiteCountText(count)}`;
    }
  }

  function addKite() {
    const candidate = createKiteCandidate(occupiedBounds);
    const kiteCount = occupiedBounds.length + 1;
    appendKite(layer, kiteCount, candidate);
    occupiedBounds.push(candidate.bounds);
    updateKiteDescription(kiteCount);
    return kiteCount;
  }

  updateKiteDescription(0);

  return Object.freeze({
    addKite
  });
}

function appendEndpointSpark(parent, angle, distance, color, radius = 4.5) {
  const radians = angle * Math.PI / 180;
  svg("circle", {
    class: "firework-spark",
    cx: (Math.cos(radians) * distance).toFixed(2),
    cy: (Math.sin(radians) * distance).toFixed(2),
    r: radius.toFixed(2),
    fill: color
  }, parent);
}

function appendClassicFirework(burst, palette) {
  const streakCount = 22;
  for (let index = 0; index < streakCount; index += 1) {
    const angle = index * (360 / streakCount) + randomBetween(-4, 4);
    const length = randomBetween(62, 92);
    const ray = svg("g", {
      class: "firework-ray",
      transform: `rotate(${(angle + 90).toFixed(2)})`
    }, burst);
    svg("line", {
      class: "firework-trail firework-streak",
      x1: 0,
      y1: -13,
      x2: 0,
      y2: -length.toFixed(2),
      pathLength: 1,
      stroke: palette[index % palette.length],
      "stroke-width": randomBetween(3.5, 6.2).toFixed(2),
      "stroke-linecap": "round"
    }, ray);
    appendEndpointSpark(
      burst,
      angle,
      length + randomBetween(3, 9),
      palette[(index + 2) % palette.length],
      randomBetween(3.2, 5.4)
    );
  }
}

function appendRingFirework(burst, palette) {
  for (const [radius, width, opacity] of [[48, 4.5, .95], [72, 3.2, .72]]) {
    svg("circle", {
      class: "firework-trail firework-orbit",
      cx: 0,
      cy: 0,
      r: radius,
      pathLength: 1,
      fill: "none",
      stroke: palette[radius === 48 ? 1 : 4],
      "stroke-width": width,
      opacity
    }, burst);
  }
  const sparkCount = 24;
  for (let index = 0; index < sparkCount; index += 1) {
    const angle = index * (360 / sparkCount) + randomBetween(-2.5, 2.5);
    const radius = randomBetween(65, 80);
    appendEndpointSpark(
      burst,
      angle,
      radius,
      palette[index % palette.length],
      randomBetween(3.2, 5.2)
    );
  }
}

function starPoints(outerRadius, innerRadius, pointCount, rotation = -90) {
  const points = [];
  for (let index = 0; index < pointCount * 2; index += 1) {
    const radius = index % 2 === 0 ? outerRadius : innerRadius;
    const angle = (rotation + index * 180 / pointCount) * Math.PI / 180;
    points.push(
      `${(Math.cos(angle) * radius).toFixed(2)},${(Math.sin(angle) * radius).toFixed(2)}`
    );
  }
  return points.join(" ");
}

function appendStarFirework(burst, palette) {
  svg("polygon", {
    class: "firework-trail firework-star",
    points: starPoints(86, 38, 5),
    pathLength: 1,
    fill: "none",
    stroke: palette[1],
    "stroke-width": 5,
    "stroke-linejoin": "round"
  }, burst);
  svg("polygon", {
    class: "firework-trail firework-star firework-star--inner",
    points: starPoints(58, 27, 5, -54),
    pathLength: 1,
    fill: "none",
    stroke: palette[4],
    "stroke-width": 3.5,
    "stroke-linejoin": "round"
  }, burst);
  for (let index = 0; index < 10; index += 1) {
    appendEndpointSpark(
      burst,
      -90 + index * 36,
      index % 2 === 0 ? 88 : 62,
      palette[(index + 2) % palette.length],
      randomBetween(3.5, 5.5)
    );
  }
}

function appendChrysanthemumFirework(burst, palette) {
  const petalCount = 28;
  for (let index = 0; index < petalCount; index += 1) {
    const angle = index * (360 / petalCount) + randomBetween(-3, 3);
    const length = randomBetween(72, 98);
    const bend = randomBetween(-16, 16);
    const ray = svg("g", {
      class: "firework-ray",
      transform: `rotate(${angle.toFixed(2)})`
    }, burst);
    svg("path", {
      class: "firework-trail firework-petal",
      d: `M0 0 Q${(length * .48).toFixed(2)} ${bend.toFixed(2)} ${length.toFixed(2)} 0`,
      pathLength: 1,
      fill: "none",
      stroke: palette[index % palette.length],
      "stroke-width": randomBetween(2.8, 5.2).toFixed(2),
      "stroke-linecap": "round"
    }, ray);
    if (index % 2 === 0) {
      appendEndpointSpark(
        burst,
        angle,
        length,
        palette[(index + 3) % palette.length],
        randomBetween(2.8, 4.8)
      );
    }
  }
}

function appendWillowFirework(burst, palette) {
  const branchCount = 20;
  for (let index = 0; index < branchCount; index += 1) {
    const angle = -165 + index * (330 / (branchCount - 1));
    const radians = angle * Math.PI / 180;
    const reach = randomBetween(52, 72);
    const endX = Math.cos(radians) * reach;
    const endY = Math.sin(radians) * reach + randomBetween(34, 54);
    const controlX = endX * .72 + randomBetween(-10, 10);
    const controlY = endY * .25 - randomBetween(18, 38);
    svg("path", {
      class: "firework-trail firework-willow",
      d: `M0 0 Q${controlX.toFixed(2)} ${controlY.toFixed(2)} ${endX.toFixed(2)} ${endY.toFixed(2)}`,
      pathLength: 1,
      fill: "none",
      stroke: palette[index % palette.length],
      "stroke-width": randomBetween(2.8, 5).toFixed(2),
      "stroke-linecap": "round"
    }, burst);
    svg("circle", {
      class: "firework-spark",
      cx: endX.toFixed(2),
      cy: endY.toFixed(2),
      r: randomBetween(2.8, 4.8).toFixed(2),
      fill: palette[(index + 2) % palette.length]
    }, burst);
  }
}

const FIREWORK_RENDERERS = Object.freeze({
  classic: appendClassicFirework,
  ring: appendRingFirework,
  star: appendStarFirework,
  chrysanthemum: appendChrysanthemumFirework,
  willow: appendWillowFirework
});

function createFireworkPlacement(region, shape) {
  const profile = FIREWORK_SHAPES[shape];
  const maximumFittingScale = Math.min(
    (region.bounds.right - region.bounds.left) / (profile.extentX * 2),
    (region.bounds.bottom - region.bounds.top) / (profile.extentY * 2)
  );
  const maximumScale = Math.min(region.scale[1], maximumFittingScale);
  const minimumScale = Math.min(region.scale[0], maximumScale);
  const scale = randomBetween(minimumScale, maximumScale);
  const extentX = profile.extentX * scale;
  const extentY = profile.extentY * scale;
  return {
    centerX: randomBetween(
      region.bounds.left + extentX,
      region.bounds.right - extentX
    ),
    centerY: randomBetween(
      region.bounds.top + extentY,
      region.bounds.bottom - extentY
    ),
    extentX,
    extentY,
    scale
  };
}

function appendFirework(
  layer,
  fireworkIndex,
  { reducedMotion, source },
  region,
  shape,
  onRemove
) {
  const palette = randomItem(FIREWORK_PALETTES);
  const { centerX, centerY, extentX, extentY, scale } =
    createFireworkPlacement(region, shape);
  const rotation = randomBetween(0, 360);
  const duration = reducedMotion
    ? FIREWORK_REDUCED_DURATION_MS
    : FIREWORK_FULL_DURATION_MS + randomBetween(-120, 220);
  const group = svg("g", {
    class: "sky-firework",
    "data-firework-index": fireworkIndex,
    "data-firework-shape": shape,
    "data-firework-region": region.id,
    "data-firework-zone": region.zone,
    "data-firework-source": source,
    "data-reduced-motion": reducedMotion,
    "data-center-x": centerX.toFixed(2),
    "data-center-y": centerY.toFixed(2),
    "data-extent-x": extentX.toFixed(2),
    "data-extent-y": extentY.toFixed(2),
    "data-safe-left": region.bounds.left,
    "data-safe-top": region.bounds.top,
    "data-safe-right": region.bounds.right,
    "data-safe-bottom": region.bounds.bottom,
    "data-palette": palette.join(","),
    "data-state": "active",
    "aria-hidden": "true",
    focusable: "false",
    transform: `translate(${centerX.toFixed(2)} ${centerY.toFixed(2)})`
  }, layer);
  group.style.setProperty("--firework-duration", `${duration.toFixed(0)}ms`);
  const rotated = svg("g", {
    transform: `rotate(${rotation.toFixed(2)}) scale(${scale.toFixed(3)})`
  }, group);
  const burst = svg("g", {
    class: `firework-burst firework-burst--${shape}`
  }, rotated);

  svg("circle", {
    class: "firework-flash",
    cx: 0,
    cy: 0,
    r: 15,
    fill: palette[0]
  }, burst);
  svg("circle", {
    class: "firework-core-ring",
    cx: 0,
    cy: 0,
    r: 19,
    fill: "none",
    stroke: palette[1],
    "stroke-width": 4
  }, burst);
  FIREWORK_RENDERERS[shape](burst, palette);

  const removalTimer = setTimeout(() => {
    group.dataset.state = "finished";
    group.remove();
    onRemove(group);
  }, duration);
  group.removeFirework = () => {
    clearTimeout(removalTimer);
    group.dataset.state = "cancelled";
    group.remove();
    onRemove(group);
  };
  return group;
}

function createFireworkController(sceneSvg) {
  const layer = svg("g", {
    class: "firework-layer",
    "data-firework-count": 0,
    "data-active-fireworks": 0,
    "data-max-active-fireworks": FIREWORK_MAX_ACTIVE,
    "data-safe-regions": FIREWORK_SAFE_REGIONS.map((region) => region.id).join(","),
    "aria-hidden": "true",
    focusable: "false"
  }, sceneSvg);
  let fireworkCount = 0;
  let regionBag = [];
  const activeFireworks = [];

  function updateActiveCount() {
    const activeCount = activeFireworks.length;
    layer.dataset.activeFireworks = String(activeCount);
    sceneSvg.dataset.activeFireworks = String(activeCount);
  }

  function removeFromActive(group) {
    const index = activeFireworks.indexOf(group);
    if (index >= 0) activeFireworks.splice(index, 1);
    updateActiveCount();
  }

  function nextRegion() {
    if (regionBag.length === 0) {
      regionBag = shuffled(FIREWORK_SAFE_REGIONS);
    }
    return regionBag.pop();
  }

  function addFirework({
    reducedMotion = false,
    source = "manual"
  } = {}) {
    while (activeFireworks.length >= FIREWORK_MAX_ACTIVE) {
      activeFireworks[0].removeFirework();
    }
    const region = nextRegion();
    const shape = randomItem(region.shapes);
    fireworkCount += 1;
    layer.dataset.fireworkCount = String(fireworkCount);
    const group = appendFirework(
      layer,
      fireworkCount,
      { reducedMotion, source },
      region,
      shape,
      removeFromActive
    );
    activeFireworks.push(group);
    updateActiveCount();
    return group;
  }

  function clearFireworks() {
    for (const group of [...activeFireworks]) group.removeFirework();
  }

  updateActiveCount();

  return Object.freeze({
    addFirework,
    clearFireworks
  });
}

function addPathAndShadow(root) {
  const layer = svg("g", { class: "path-and-shadow" }, root);
  svg("path", {
    d: "M760 900 C815 790 875 720 918 688 L1008 688 C1055 724 1125 795 1180 900 Z",
    fill: "#E7D6AD",
    opacity: .96
  }, layer);
  svg("ellipse", {
    cx: 1003,
    cy: 700,
    rx: 500,
    ry: 58,
    fill: "#17352A",
    opacity: .14
  }, layer);
}

function addSwings(root, swings) {
  const layer = svg("g", { class: "campus-back swings" }, root);
  const ground = svg("g", { class: "swing-ground", opacity: 0 }, layer);
  svg("ellipse", {
    ...swings.ground,
    fill: "#17352A",
    opacity: .2
  }, ground);
  svg("path", {
    d: "M20 659 Q80 642 145 632",
    fill: "none",
    stroke: "#8CCB62",
    "stroke-width": 5,
    "stroke-linecap": "round",
    opacity: .9
  }, ground);

  const frame = svg("g", { class: "swing-frame", opacity: 0 }, layer);
  svg("line", {
    ...swings.beam,
    stroke: "#F0A533",
    "stroke-width": 11,
    "stroke-linecap": "round"
  }, frame);
  for (const foot of swings.feet) {
    svg("line", {
      ...foot,
      stroke: "#087541",
      "stroke-width": 10,
      "stroke-linecap": "round"
    }, frame);
  }

  const ropes = svg("g", { class: "swing-chains", opacity: 0 }, layer);
  for (const seat of swings.seats) {
    for (const ropeX of [seat.centerX - 5, seat.centerX + 5]) {
      svg("line", {
        x1: ropeX,
        y1: seat.topY,
        x2: ropeX,
        y2: seat.y - 5,
        stroke: "#FFF7DF",
        "stroke-width": 3
      }, ropes);
      svg("line", {
        x1: ropeX,
        y1: seat.topY,
        x2: ropeX,
        y2: seat.y - 5,
        stroke: "#547B76",
        "stroke-width": 1,
        "stroke-dasharray": "4 4"
      }, ropes);
    }
  }

  const seats = svg("g", { class: "swing-seats", opacity: 0 }, layer);
  for (const seat of swings.seats) {
    svg("rect", {
      x: seat.centerX - 9,
      y: seat.y - 4,
      width: 18,
      height: 6,
      rx: 2,
      fill: "#D54843",
      stroke: "#8F432C",
      "stroke-width": 2,
      transform: `rotate(${seat.rotation} ${seat.centerX} ${seat.y})`
    }, seats);
  }

  const safetyPads = svg("g", {
    class: "playground-safety-pads",
    opacity: 0
  }, layer);
  for (const foot of swings.feet) {
    svg("ellipse", {
      cx: foot.x2,
      cy: foot.y2 + 1,
      rx: 11,
      ry: 4,
      fill: "#FFD45F",
      stroke: "#8F432C",
      "stroke-width": 2,
      transform: `rotate(-11 ${foot.x2} ${foot.y2 + 1})`
    }, safetyPads);
  }

  const climbingSide = svg("g", {
    class: "playground-climbing-side",
    opacity: 0
  }, layer);
  for (const brace of swings.braces) {
    svg("line", {
      ...brace,
      stroke: "#F0A533",
      "stroke-width": 6,
      "stroke-linecap": "round"
    }, climbingSide);
  }
  for (const rail of swings.ladder.rails) {
    svg("line", {
      ...rail,
      stroke: "#0A713E",
      "stroke-width": 6,
      "stroke-linecap": "round"
    }, climbingSide);
  }
  for (const rung of swings.ladder.rungs) {
    svg("line", {
      ...rung,
      stroke: "#FFD45F",
      "stroke-width": 5,
      "stroke-linecap": "round"
    }, climbingSide);
  }

  const decorations = svg("g", {
    class: "playground-decorations",
    opacity: 0
  }, layer);
  svg("line", {
    x1: swings.beam.x1 + 8,
    y1: swings.beam.y1 - 7,
    x2: swings.beam.x2 - 8,
    y2: swings.beam.y2 - 7,
    stroke: "#FFF7DF",
    "stroke-width": 2
  }, decorations);
  for (const pennant of swings.pennants) {
    svg("path", {
      d: `M${pennant.x - 6} ${pennant.y - 7} L${pennant.x + 6} ${pennant.y - 8} L${pennant.x + 1} ${pennant.y + 5} Z`,
      fill: pennant.color,
      stroke: "#17352A",
      "stroke-width": 1
    }, decorations);
  }

  return {
    baseNodes: [ground, frame, ropes, seats],
    enhancementNodes: [safetyPads, climbingSide, decorations]
  };
}

function addWindowRecess(parent, x, y) {
  svg("rect", {
    x,
    y,
    width: 68,
    height: 58,
    rx: 3,
    fill: "#17352A"
  }, parent);
}

function addApertureBacking(parent, {
  className,
  x,
  y,
  width,
  height
}) {
  svg("rect", {
    class: `aperture-backing ${className}`,
    x,
    y,
    width,
    height,
    rx: 1,
    fill: "#BE623D",
    stroke: "#8F432C",
    "stroke-width": 1
  }, parent);
}

function addApertureRecesses(root) {
  const finishNodes = [];
  const shell = progressiveGroup(root, "shell", "aperture-recesses");
  const lower = progressiveGroup(shell.element, "lower", "aperture-lower");
  const upper = progressiveGroup(shell.element, "upper", "aperture-upper");
  const tower = progressiveGroup(shell.element, "tower", "aperture-tower");
  finishNodes.push(shell, lower, upper, tower);

  for (const x of WINDOW_X) {
    addApertureBacking(lower.element, {
      className: "backing-lower-window",
      x: x - 11,
      y: 535,
      width: 98,
      height: 74
    });
    addApertureBacking(upper.element, {
      className: "backing-upper-window",
      x: x - 11,
      y: 425,
      width: 98,
      height: 74
    });
  }
  addApertureBacking(lower.element, {
    className: "backing-entrance-lower",
    x: 918,
    y: 513,
    width: 98,
    height: 140
  });
  addApertureBacking(upper.element, {
    className: "backing-entrance-upper",
    x: 936,
    y: 491,
    width: 80,
    height: 30
  });
  addApertureBacking(tower.element, {
    className: "backing-tower-window",
    x: 909,
    y: 337,
    width: 98,
    height: 74
  });

  for (const x of WINDOW_X) {
    addWindowRecess(lower.element, x, 547);
    addWindowRecess(upper.element, x, 437);
  }
  svg("path", {
    d: "M918 653 V538 A45 45 0 0 1 1008 538 V653 Z",
    fill: "#17352A"
  }, lower.element);
  svg("path", {
    d: "M929 407 V373 A34 34 0 0 1 997 373 V407 Z",
    fill: "#17352A"
  }, tower.element);
  return finishNodes;
}

function createBrickNode(block, parent) {
  const group = svg("g", {
    class: `school-block block-${block.stage}`,
    "data-stage": block.stage,
    "data-index": block.index,
    opacity: 0
  }, parent);
  const { x, y, width: w, height: h, depth: d } = block;
  svg("polygon", {
    points: `${x},${y} ${x + w},${y} ${x + w + d},${y - d} ${x + d},${y - d}`,
    fill: "#E9986D"
  }, group);
  svg("rect", {
    x,
    y,
    width: w,
    height: h,
    rx: 1.5,
    fill: block.color,
    stroke: "#8F432C",
    "stroke-width": 1
  }, group);
  svg("polygon", {
    points: `${x + w},${y} ${x + w + d},${y - d} ${x + w + d},${y + h - d} ${x + w},${y + h}`,
    fill: "#8F432C"
  }, group);
  svg("ellipse", {
    cx: x + w * .28,
    cy: y - 2.5,
    rx: 4.3,
    ry: 2.1,
    fill: "#F1A17C",
    stroke: "#9E4C31",
    "stroke-width": .7
  }, group);
  svg("ellipse", {
    cx: x + w * .72,
    cy: y - 2.5,
    rx: 4.3,
    ry: 2.1,
    fill: "#F1A17C",
    stroke: "#9E4C31",
    "stroke-width": .7
  }, group);
  return group;
}

function addWindowFrame(parent, x, y) {
  svg("rect", {
    x: x + 5,
    y: y + 5,
    width: 58,
    height: 48,
    rx: 1,
    fill: "url(#glass-gradient)",
    stroke: "#087541",
    "stroke-width": 6
  }, parent);
  svg("line", {
    x1: x + 34,
    y1: y + 7,
    x2: x + 34,
    y2: y + 51,
    stroke: "#087541",
    "stroke-width": 4
  }, parent);
  svg("line", {
    x1: x + 8,
    y1: y + 29,
    x2: x + 60,
    y2: y + 29,
    stroke: "#087541",
    "stroke-width": 3
  }, parent);
  svg("rect", {
    x: x - 5,
    y: y - 7,
    width: 78,
    height: 7,
    rx: 2,
    fill: "#E59A70",
    stroke: "#8F432C",
    "stroke-width": 1
  }, parent);
  svg("rect", {
    x: x - 3,
    y: y + 58,
    width: 74,
    height: 7,
    rx: 2,
    fill: "#D6875D",
    stroke: "#8F432C",
    "stroke-width": 1
  }, parent);
}

function addEntrance(parent) {
  const doors = svg("g", {
    "clip-path": "url(#entrance-arch-clip)"
  }, parent);
  svg("rect", {
    x: 936,
    y: 493,
    width: 27,
    height: 160,
    fill: "#087541"
  }, doors);
  svg("rect", {
    x: 963,
    y: 493,
    width: 27,
    height: 160,
    fill: "#0A6C3C"
  }, doors);
  svg("line", {
    x1: 963,
    y1: 503,
    x2: 963,
    y2: 653,
    stroke: "#17352A",
    "stroke-width": 3
  }, doors);
  svg("rect", {
    x: 942,
    y: 553,
    width: 15,
    height: 30,
    fill: "#789E93",
    stroke: "#D7E5D9",
    "stroke-width": 2
  }, doors);
  svg("rect", {
    x: 969,
    y: 553,
    width: 15,
    height: 30,
    fill: "#789E93",
    stroke: "#D7E5D9",
    "stroke-width": 2
  }, doors);
  svg("circle", { cx: 958, cy: 607, r: 3, fill: "#FFD45F" }, doors);
  svg("circle", { cx: 968, cy: 607, r: 3, fill: "#FFD45F" }, doors);
  svg("path", {
    d: "M918 653 V538 A45 45 0 0 1 1008 538 V653",
    fill: "none",
    stroke: "#087541",
    "stroke-width": 9
  }, parent);
}

function addTowerWindow(parent) {
  const glass = svg("g", {
    "clip-path": "url(#tower-window-clip)"
  }, parent);
  svg("rect", {
    x: 935,
    y: 339,
    width: 56,
    height: 68,
    fill: "url(#glass-gradient)"
  }, glass);
  svg("line", {
    x1: 963,
    y1: 345,
    x2: 963,
    y2: 407,
    stroke: "#087541",
    "stroke-width": 4
  }, glass);
  svg("line", {
    x1: 935,
    y1: 375,
    x2: 991,
    y2: 375,
    stroke: "#087541",
    "stroke-width": 3
  }, glass);
  svg("path", {
    d: "M929 407 V373 A34 34 0 0 1 997 373 V407",
    fill: "none",
    stroke: "#087541",
    "stroke-width": 8
  }, parent);
  svg("rect", {
    x: 923,
    y: 407,
    width: 80,
    height: 7,
    rx: 2,
    fill: "#D6875D",
    stroke: "#8F432C",
    "stroke-width": 1
  }, parent);
}

function addMasonryBand(parent, x, y, width, height = 8) {
  svg("rect", {
    x,
    y,
    width,
    height,
    rx: 2,
    fill: "#E59A70",
    stroke: "#8F432C",
    "stroke-width": 1
  }, parent);
}

function addArchitecturalFinishes(root) {
  const finishNodes = [];

  const lower = progressiveGroup(root, "lower", "facade-lower");
  finishNodes.push(lower);
  for (const x of WINDOW_X) addWindowFrame(lower.element, x, 547);
  addEntrance(lower.element);
  addMasonryBand(lower.element, 530, 648, 956, 8);

  const upper = progressiveGroup(root, "upper", "facade-upper");
  finishNodes.push(upper);
  for (const x of WINDOW_X) addWindowFrame(upper.element, x, 437);
  addMasonryBand(upper.element, 530, 519, 956, 8);

  const tower = progressiveGroup(root, "tower", "facade-tower");
  finishNodes.push(tower);
  addTowerWindow(tower.element);
  svg("rect", {
    x: 839,
    y: 292,
    width: 14,
    height: 135,
    rx: 2,
    fill: "#D6875D",
    stroke: "#8F432C",
    "stroke-width": 1
  }, tower.element);
  svg("rect", {
    x: 1070,
    y: 292,
    width: 14,
    height: 135,
    rx: 2,
    fill: "#D6875D",
    stroke: "#8F432C",
    "stroke-width": 1
  }, tower.element);
  svg("rect", {
    x: 858,
    y: 300,
    width: 210,
    height: 38,
    rx: 4,
    fill: "#F2D8B5",
    stroke: "#8F432C",
    "stroke-width": 3
  }, tower.element);
  const sign = svg("text", {
    x: 963,
    y: 325,
    "text-anchor": "middle",
    fill: "#078447",
    "font-family": "Segoe UI, sans-serif",
    "font-size": 17,
    "font-weight": 800,
    textLength: 184,
    lengthAdjust: "spacingAndGlyphs"
  }, tower.element);
  sign.textContent = "TCF School Seattle";

  const final = progressiveGroup(root, "final", "facade-final");
  finishNodes.push(final);
  addMasonryBand(final.element, 530, 430, 956, 8);
  addMasonryBand(final.element, 530, 386, 344, 8);
  addMasonryBand(final.element, 1052, 386, 434, 8);
  addMasonryBand(final.element, 847, 254, 232, 9);

  return finishNodes;
}

function addStepsAndShrubs(root) {
  const final = progressiveGroup(root, "final", "steps-and-shrubs");
  const stepColor = ["#E3C99F", "#D7B98A", "#CDAA78"];
  [
    [907, 653, 112, 11],
    [889, 664, 148, 12],
    [871, 676, 184, 14]
  ].forEach(([x, y, width, height], index) => {
    svg("rect", {
      x,
      y,
      width,
      height,
      rx: 2,
      fill: stepColor[index],
      stroke: "#9A7652",
      "stroke-width": 1
    }, final.element);
  });

  const shrubPositions = [
    [570, 682, 18],
    [620, 678, 22],
    [678, 682, 17],
    [735, 676, 23],
    [798, 681, 18],
    [842, 679, 20],
    [1092, 679, 20],
    [1140, 681, 18],
    [1200, 676, 23],
    [1265, 682, 18],
    [1325, 677, 22],
    [1387, 682, 18],
    [1435, 679, 20]
  ];
  for (const [x, y, radius] of shrubPositions) {
    svg("ellipse", {
      cx: x,
      cy: y + 12,
      rx: radius + 5,
      ry: 6,
      fill: "#17352A",
      opacity: .14
    }, final.element);
    svg("circle", {
      cx: x - radius * .45,
      cy: y,
      r: radius * .65,
      fill: "#0A713E"
    }, final.element);
    svg("circle", {
      cx: x + radius * .35,
      cy: y - 3,
      r: radius * .75,
      fill: "#149956"
    }, final.element);
  }
  return [final];
}

function addPakistanFlag(root, flag) {
  const layer = svg("g", {
    class: "goal-flag",
    opacity: 0
  }, root);
  svg("rect", {
    x: flag.pole.x,
    y: flag.pole.top,
    width: flag.pole.width,
    height: flag.pole.bottom - flag.pole.top,
    rx: 2.5,
    fill: "#E8D7AF",
    stroke: "#8F7652",
    "stroke-width": 1
  }, layer);
  svg("circle", {
    ...flag.finial,
    fill: "#F0A533",
    stroke: "#8F7652",
    "stroke-width": 1
  }, layer);

  const positionedCloth = svg("g", {
    transform: flag.cloth.transform
  }, layer);
  const cloth = svg("g", {
    class: "flag-cloth",
    "data-pole-edge-x": flag.cloth.poleEdgeX,
    "data-hoist-side": "left",
    "data-fly-direction": "right",
    "data-width": flag.cloth.width,
    "data-height": flag.cloth.height
  }, positionedCloth);
  svg("path", {
    d: "M0 2 C36 0 84 8 132 1 L132 86 C84 88 36 80 0 87 Z",
    fill: "#FFFFFF",
    stroke: "#17352A",
    "stroke-width": 1.5,
    "stroke-linejoin": "round"
  }, cloth);
  svg("rect", {
    x: flag.cloth.hoistWidth,
    y: 0,
    width: flag.cloth.width - flag.cloth.hoistWidth,
    height: flag.cloth.height,
    fill: "#01411C",
    "clip-path": "url(#pakistan-flag-clip)"
  }, cloth);
  svg("circle", {
    cx: 82,
    cy: 44,
    r: 22,
    fill: "#FFFFFF",
    "clip-path": "url(#pakistan-flag-clip)"
  }, cloth);
  svg("circle", {
    cx: 91,
    cy: 39,
    r: 20,
    fill: "#01411C",
    "clip-path": "url(#pakistan-flag-clip)"
  }, cloth);
  svg("polygon", {
    points: "112,23 115.4,32.6 125.6,32.8 117.5,39 120.5,48.8 112,43 103.5,48.8 106.5,39 98.4,32.8 108.6,32.6",
    fill: "#FFFFFF",
    transform: "rotate(-12 112 36)"
  }, cloth);
  return layer;
}

function createTeacherNode(teacher, parent) {
  const group = svg("g", {
    class: "teacher",
    "data-index": teacher.index,
    opacity: 0
  }, parent);
  svg("ellipse", {
    cx: 0,
    cy: -4,
    rx: 16,
    ry: 4,
    fill: "#17352A",
    opacity: .18
  }, group);
  svg("line", {
    x1: -7,
    y1: -25,
    x2: -9,
    y2: -3,
    stroke: "#17352A",
    "stroke-width": 7,
    "stroke-linecap": "round"
  }, group);
  svg("line", {
    x1: 7,
    y1: -25,
    x2: 9,
    y2: -3,
    stroke: "#17352A",
    "stroke-width": 7,
    "stroke-linecap": "round"
  }, group);
  svg("path", {
    d: "M-15 -51 Q0 -59 15 -51 L12 -23 H-12 Z",
    fill: teacher.clothing
  }, group);
  svg("line", {
    x1: -12,
    y1: -47,
    x2: -13,
    y2: -27,
    stroke: teacher.skin,
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("line", {
    x1: 12,
    y1: -47,
    x2: 13,
    y2: -30,
    stroke: teacher.skin,
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("rect", {
    x: -12,
    y: -42,
    width: 24,
    height: 17,
    rx: 2,
    fill: teacher.accent,
    stroke: "#FFF7DF",
    "stroke-width": 1.5
  }, group);
  svg("circle", { cx: 0, cy: -58, r: 10, fill: teacher.skin }, group);
  svg("path", {
    d: "M-10 -60 Q0 -68 10 -60 L8 -53 Q0 -59 -8 -53 Z",
    fill: "#17352A"
  }, group);
  return group;
}

function createStudentNode(student, parent) {
  const group = svg("g", {
    class: "student",
    "data-index": student.index,
    opacity: 0
  }, parent);
  svg("ellipse", {
    class: "student-ground-shadow",
    cx: 0,
    cy: 43,
    rx: 9,
    ry: 2.5,
    fill: "#17352A",
    opacity: .16
  }, group);
  svg("line", {
    x1: -6,
    y1: 26,
    x2: -9,
    y2: 42,
    stroke: "#17352A",
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("line", {
    x1: 6,
    y1: 26,
    x2: 9,
    y2: 42,
    stroke: "#17352A",
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("rect", {
    x: -13,
    y: -4,
    width: 26,
    height: 34,
    rx: 8,
    fill: student.shirt
  }, group);
  svg("line", {
    x1: -11,
    y1: 2,
    x2: -22,
    y2: 18,
    stroke: student.skin,
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("line", {
    x1: 11,
    y1: 2,
    x2: 22,
    y2: 16,
    stroke: student.skin,
    "stroke-width": 6,
    "stroke-linecap": "round"
  }, group);
  svg("circle", { cx: 0, cy: -18, r: 14, fill: student.skin }, group);
  svg("path", {
    d: "M-13 -22 Q0 -37 13 -22 L11 -13 Q0 -22 -11 -13Z",
    fill: "#17352A"
  }, group);
  return group;
}

function addCelebration(root) {
  const group = svg("g", { class: "celebration-burst" }, root);
  const colors = ["#FFD45F", "#078447", "#C96E43", "#FFF7DF"];
  for (let index = 0; index < 28; index += 1) {
    const x = 470 + ((index * 97) % 700);
    const y = 390 + ((index * 43) % 190);
    const piece = svg("rect", {
      class: "confetti",
      x,
      y,
      width: 10 + index % 3 * 4,
      height: 6,
      rx: 2,
      fill: colors[index % colors.length]
    }, group);
    piece.style.animationDelay = `${(index % 9) * 45}ms`;
  }
  return group;
}

export function createFundraiserView(root, scene, config) {
  const sceneSvg = root.querySelector("#school-scene");
  const sceneDescription = sceneSvg.querySelector("#scene-description");
  const sceneDescriptionText = sceneDescription?.textContent ?? "";
  addDefinitions(sceneSvg);
  addLandscape(sceneSvg);
  const kiteController = createKiteController(
    root,
    sceneSvg,
    sceneDescription,
    sceneDescriptionText
  );
  const fireworkController = createFireworkController(sceneSvg);
  const {
    baseNodes: swingNodes,
    enhancementNodes: playgroundNodes
  } = addSwings(sceneSvg, scene.campus.swings);
  addPathAndShadow(sceneSvg);
  const finishNodes = addApertureRecesses(sceneSvg);

  const school = svg("g", {
    class: "school",
    filter: "url(#soft-shadow)"
  }, sceneSvg);
  const blockNodes = scene.blocks.map((block) => createBrickNode(block, school));

  finishNodes.push(...addArchitecturalFinishes(sceneSvg));
  finishNodes.push(...addStepsAndShrubs(sceneSvg));

  const goalFlag = addPakistanFlag(sceneSvg, scene.campus.flag);
  const teacherLayer = svg("g", { class: "campus-people teachers" }, sceneSvg);
  const teacherNodes = scene.teachers.map(
    (teacher) => createTeacherNode(teacher, teacherLayer)
  );
  const studentLayer = svg("g", { class: "students" }, sceneSvg);
  const studentNodes = scene.students.map(
    (student) => createStudentNode(student, studentLayer)
  );
  const displayedStudentReveals = scene.students.map(() => 0);
  const celebration = addCelebration(sceneSvg);

  const raisedDisplay = root.querySelector("#raised-display");
  const goalDisplay = root.querySelector("#goal-display");
  const percentDisplay = root.querySelector("#percent-display");
  const meterFill = root.querySelector("#meter-fill");
  const overGoalMessage = root.querySelector("#over-goal-message");
  const money = createCurrencyFormatter(config.locale, config.currency);

  function update({
    raised,
    goal,
    donationRatio,
    buildingRatio,
    studentRatio,
    goalAchieved = donationRatio >= 1,
    swingRatio = 0,
    teacherRatio = 0,
    playgroundRatio = 0,
    deltaMs = null,
    reducedMotion = false,
    celebrationActive = false
  }) {
    let visibleBlocks = 0;
    for (let index = 0; index < blockNodes.length; index += 1) {
      const reveal = revealAt(buildingRatio, blockNodes.length, index);
      visibleBlocks += reveal;
      const drop = reducedMotion ? 0 : (1 - reveal) * -85;
      const scale = reducedMotion ? 1 : .72 + reveal * .28;
      const centerX = scene.blocks[index].x + scene.blocks[index].width / 2;
      const centerY = scene.blocks[index].y + scene.blocks[index].height / 2;
      const translatedY = centerY + drop;
      blockNodes[index].setAttribute(
        "opacity",
        fixedPreservingEndpoints(reveal, 4, [0, 1])
      );
      const transform = `translate(${centerX.toFixed(2)} ${fixedPreservingEndpoints(
        translatedY,
        2,
        reducedMotion ? [centerY] : [centerY - 85, centerY]
      )}) scale(${fixedPreservingEndpoints(
        scale,
        4,
        reducedMotion ? [1] : [.72, 1]
      )}) translate(${-centerX.toFixed(2)} ${-centerY.toFixed(2)})`;
      blockNodes[index].setAttribute(
        "transform",
        transformPreservingEndpoints(transform, reveal)
      );
    }

    for (const finish of finishNodes) {
      finish.element.setAttribute(
        "opacity",
        fixedPreservingEndpoints(
          progressiveOpacity(buildingRatio, finish.start, finish.end),
          4,
          [0, 1]
        )
      );
    }

    for (let index = 0; index < swingNodes.length; index += 1) {
      swingNodes[index].setAttribute(
        "opacity",
        fixedPreservingEndpoints(
          revealAt(swingRatio, swingNodes.length, index),
          4,
          [0, 1]
        )
      );
    }
    for (let index = 0; index < playgroundNodes.length; index += 1) {
      playgroundNodes[index].setAttribute(
        "opacity",
        fixedPreservingEndpoints(
          revealAt(playgroundRatio, playgroundNodes.length, index),
          4,
          [0, 1]
        )
      );
    }

    goalFlag.setAttribute("opacity", goalAchieved ? "1" : "0");

    let visibleTeachers = 0;
    for (let index = 0; index < teacherNodes.length; index += 1) {
      const reveal = revealAt(teacherRatio, teacherNodes.length, index);
      visibleTeachers += reveal;
      const teacher = scene.teachers[index];
      const eased = reducedMotion
        ? (reveal > 0 ? 1 : 0)
        : 1 - Math.pow(1 - reveal, 3);
      const y = teacher.y + (1 - eased) * 12;
      teacherNodes[index].setAttribute(
        "opacity",
        fixedPreservingEndpoints(reveal, 4, [0, 1])
      );
      const transform = `translate(${teacher.x} ${fixedPreservingEndpoints(
        y,
        2,
        reducedMotion ? [teacher.y] : [teacher.y, teacher.y + 12]
      )}) scale(${teacher.scale})`;
      teacherNodes[index].setAttribute(
        "transform",
        reveal === 1 || (reducedMotion && reveal > 0)
          ? teacher.transform
          : transformPreservingEndpoints(transform, reveal)
      );
    }

    let visibleStudents = 0;
    let studentRoutesPending = false;
    const targetStudentReveals = studentNodes.map((_, index) => revealAt(
      studentRatio,
      studentNodes.length,
      index
    ));
    const animatedStudents = !reducedMotion && Number.isFinite(deltaMs);
    let advancingStudentIndex = -1;
    if (animatedStudents) {
      const targetTotal = targetStudentReveals.reduce(
        (sum, reveal) => sum + reveal,
        0
      );
      const displayedTotal = displayedStudentReveals.reduce(
        (sum, reveal) => sum + reveal,
        0
      );
      const direction = targetTotal >= displayedTotal ? 1 : -1;
      const start = direction > 0 ? 0 : studentNodes.length - 1;
      const end = direction > 0 ? studentNodes.length : -1;
      for (let index = start; index !== end; index += direction) {
        if (displayedStudentReveals[index] !== targetStudentReveals[index]) {
          advancingStudentIndex = index;
          break;
        }
      }
    }
    for (let index = 0; index < studentNodes.length; index += 1) {
      const targetReveal = targetStudentReveals[index];
      const student = scene.students[index];
      if (!animatedStudents) {
        displayedStudentReveals[index] = targetReveal;
      } else if (index === advancingStudentIndex) {
        const maximumRouteDistance =
          Math.max(0, deltaMs) * STUDENT_ROUTE_SPEED_PER_MS;
        const maximumRevealStep =
          maximumRouteDistance / student.route.totalLength;
        displayedStudentReveals[index] = targetReveal
          > displayedStudentReveals[index]
          ? Math.min(
              targetReveal,
              displayedStudentReveals[index] + maximumRevealStep
            )
          : Math.max(
              targetReveal,
              displayedStudentReveals[index] - maximumRevealStep
            );
      }
      const displayedReveal = displayedStudentReveals[index];
      if (displayedReveal !== targetReveal) studentRoutesPending = true;
      visibleStudents += displayedReveal;
      const position = deriveStudentRoutePosition(
        student,
        displayedReveal,
        reducedMotion
      );
      studentNodes[index].setAttribute(
        "opacity",
        fixedPreservingEndpoints(displayedReveal, 4, [0, 1])
      );
      const transform = `translate(${fixedPreservingEndpoints(
        position.x,
        2,
        [student.startX, student.targetX]
      )} ${fixedPreservingEndpoints(
        position.y,
        2,
        [student.startY, student.targetY]
      )}) scale(${student.scale.toFixed(3)})`;
      studentNodes[index].setAttribute(
        "transform",
        transformPreservingEndpoints(transform, displayedReveal)
      );
    }

    const percentage = Math.min(Number.MAX_VALUE, donationRatio * 100);
    const wholePercentage = Math.floor(
      percentage + Number.EPSILON * Math.max(1, Math.abs(percentage))
    );
    raisedDisplay.textContent = money.format(raised);
    goalDisplay.textContent = `Goal ${money.format(goal)}`;
    if (donationRatio < 1) {
      percentDisplay.textContent = `${Math.min(99, wholePercentage)}% complete`;
    } else if (donationRatio < 1.03) {
      percentDisplay.textContent = `${wholePercentage}% — students arriving!`;
    } else if (donationRatio < 1.10) {
      percentDisplay.textContent = `${wholePercentage}% — playground growing!`;
    } else if (donationRatio < 1.17) {
      percentDisplay.textContent = `${wholePercentage}% — teachers joining!`;
    } else if (donationRatio < 1.25) {
      percentDisplay.textContent = `${wholePercentage}% — playground finishing!`;
    } else {
      percentDisplay.textContent = `${wholePercentage}% — campus ready!`;
    }
    const meterPercent = Math.min(100, donationRatio * 100);
    setMeterWidth(meterFill, meterPercent);
    overGoalMessage.hidden = !goalAchieved;
    celebration.classList.toggle("is-active", celebrationActive);

    root.dataset.buildingPercent = fixedPreservingEndpoints(
      buildingRatio * 100,
      3,
      [0, 100]
    );
    root.dataset.visibleBlocks = fixedPreservingEndpoints(
      visibleBlocks,
      6,
      [0, blockNodes.length]
    );
    root.dataset.goalAchieved = String(goalAchieved);
    root.dataset.studentPercent = fixedPreservingEndpoints(
      studentRatio * 100,
      3,
      [0, 100]
    );
    root.dataset.visibleStudents = fixedPreservingEndpoints(
      visibleStudents,
      6,
      [0, studentNodes.length]
    );
    root.dataset.swingPercent = fixedPreservingEndpoints(
      swingRatio * 100,
      3,
      [0, 100]
    );
    root.dataset.teacherPercent = fixedPreservingEndpoints(
      teacherRatio * 100,
      3,
      [0, 100]
    );
    root.dataset.visibleTeachers = fixedPreservingEndpoints(
      visibleTeachers,
      6,
      [0, teacherNodes.length]
    );
    root.dataset.playgroundPercent = fixedPreservingEndpoints(
      playgroundRatio * 100,
      3,
      [0, 100]
    );
    return studentRoutesPending
      ? PENDING_RENDER_RESULT
      : SETTLED_RENDER_RESULT;
  }

  function setNightMode(enabled) {
    const night = Boolean(enabled);
    sceneSvg.classList.toggle("is-night", night);
    root.dataset.timeOfDay = night ? "night" : "day";
  }

  return Object.freeze({
    addFirework: fireworkController.addFirework,
    clearFireworks: fireworkController.clearFireworks,
    addKite: kiteController.addKite,
    setNightMode,
    update,
    blockCount: blockNodes.length,
    studentCount: studentNodes.length,
    teacherCount: teacherNodes.length
  });
}
