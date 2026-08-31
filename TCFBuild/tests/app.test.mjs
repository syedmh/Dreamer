import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { deriveSliderMaximum, MAX_GOAL } from "../src/config.mjs";
import { deriveProgress } from "../src/model.mjs";
import { createFundraiserView, progressiveOpacity } from "../src/render.mjs";
import {
  createScene,
  deriveStudentRoutePosition
} from "../src/scene.mjs";

class FakeStyle {
  constructor() {
    this.properties = new Map();
  }

  getPropertyValue(name) {
    return this.properties.get(name) ?? "";
  }

  removeProperty(name) {
    const previous = this.getPropertyValue(name);
    this.properties.delete(name);
    return previous;
  }

  setProperty(name, value) {
    this.properties.set(name, String(value));
  }
}

class FakeElement {
  constructor(tagName = "") {
    this.tagName = tagName;
    this.attributes = new Map();
    this.classList = { toggle() {} };
    this.children = [];
    this.dataset = {};
    this.hidden = false;
    this.listeners = new Map();
    this.style = new FakeStyle();
    this.textContent = "";
    this.value = "";
    this.max = "";
    this.focusCalls = 0;
    this.setAttributeCalls = 0;
  }

  addEventListener(type, listener) {
    this.listeners.set(type, listener);
  }

  append(...children) {
    this.children.push(...children);
  }

  focus() {
    this.focusCalls += 1;
    if (globalThis.document) globalThis.document.activeElement = this;
  }

  setAttribute(name, value) {
    this.setAttributeCalls += 1;
    this.attributes.set(name, String(value));
  }
}

class FakeInputElement extends FakeElement {}

function adjacentFloat(value, direction) {
  if (!Number.isFinite(value)) return value;
  if (value === 0) {
    return direction > 0 ? Number.MIN_VALUE : -Number.MIN_VALUE;
  }
  const storage = new ArrayBuffer(8);
  const view = new DataView(storage);
  view.setFloat64(0, value);
  let bits = view.getBigUint64(0);
  bits += (value > 0) === (direction > 0) ? 1n : -1n;
  view.setBigUint64(0, bits);
  return view.getFloat64(0);
}

async function withAppHarness({
  locationSearch,
  mediaMatches = false,
  startedAt = 2000
}, run) {
  const originalGlobals = new Map();
  const setGlobal = (name, value) => {
    originalGlobals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, {
      configurable: true,
      writable: true,
      value
    });

  };

  const elements = new Map([
    ["#announcer", new FakeElement()],
    ["#close-controls", new FakeElement()],
    ["#goal-input", new FakeInputElement()],
    ["#operator-form", new FakeElement()],
    ["#operator-panel", new FakeElement()],
    ["#raised-input", new FakeInputElement()],
    ["#raised-slider", new FakeInputElement()]
  ]);
  elements.get("#operator-panel").hidden = true;

  const root = new FakeElement();
  const rootElements = new Map([
    ["#goal-display", new FakeElement()],
    ["#meter-fill", new FakeElement()],
    ["#over-goal-message", new FakeElement()],
    ["#percent-display", new FakeElement()],
    ["#raised-display", new FakeElement()],
    ["#school-scene", new FakeElement()]
  ]);
  root.querySelector = (selector) => rootElements.get(selector);
  elements.set("#fundraiser", root);

  const animationFrames = [];
  const timers = new Map();
  let nextTimerId = 1;
  let currentMediaMatches = mediaMatches;
  const mediaListeners = new Set();
  const mediaQueryList = {
    get matches() {
      return currentMediaMatches;
    },
    addEventListener(type, listener) {
      if (type === "change") mediaListeners.add(listener);
    },
    removeEventListener(type, listener) {
      if (type === "change") mediaListeners.delete(listener);
    }
  };
  const documentListeners = new Map();
  const createdElements = [];
  const document = {
    activeElement: null,
    fullscreenElement: null,
    addEventListener(type, listener) {
      documentListeners.set(type, listener);
    },
    createElementNS(_namespace, name) {
      const element = new FakeElement(name);
      createdElements.push(element);
      return element;
    },
    querySelector(selector) {
      return elements.get(selector);
    }
  };

  setGlobal("document", document);
  setGlobal("HTMLInputElement", FakeInputElement);
  setGlobal("performance", { now: () => startedAt });
  setGlobal("requestAnimationFrame", (callback) => {
    animationFrames.push(callback);
  });
  setGlobal("setTimeout", (callback, delay = 0) => {
    const timerId = nextTimerId;
    nextTimerId += 1;
    timers.set(timerId, { callback, delay });
    return timerId;
  });
  setGlobal("clearTimeout", (timerId) => {
    timers.delete(timerId);
  });
  setGlobal("window", {
    location: { search: locationSearch },
    matchMedia: () => mediaQueryList
  });

  try {
    const appUrl = new URL("../src/app.mjs", import.meta.url);
    appUrl.searchParams.set("test", `${Date.now()}-${Math.random()}`);
    await import(appUrl);
    await run({
      animationFrames,
      createdElements,
      dispatchMediaChange(matches) {
        currentMediaMatches = matches;
        const event = { matches, media: "(prefers-reduced-motion: reduce)" };
        for (const listener of mediaListeners) listener(event);
      },
      document,
      documentListeners,
      elements,
      mediaListenerCount: () => mediaListeners.size,
      pendingTimerCount: () => timers.size,
      root,
      rootElements,
      runNextTimer() {
        const nextTimer = timers.entries().next().value;
        if (!nextTimer) return false;
        const [timerId, timer] = nextTimer;
        timers.delete(timerId);
        timer.callback();
        return true;
      },
      startedAt
    });
  } finally {
    for (const [name, descriptor] of originalGlobals) {
      if (descriptor) {
        Object.defineProperty(globalThis, name, descriptor);
      } else {
        delete globalThis[name];
      }
    }
  }
}

test("pointer-close focus layer is contained, above stage content, and pointer-transparent", async () => {
  const css = await readFile(new URL("../styles.css", import.meta.url), "utf8");
  const stageRule = css.match(/^\.stage\s*\{([^}]*)\}/m);
  assert.ok(stageRule, "stage rule must exist");
  assert.match(stageRule[1], /overflow:\s*hidden;/, "stage must clip focus drawing to its viewport");
  assert.match(stageRule[1], /isolation:\s*isolate;/, "stage must isolate its stacking context");
  assert.match(
    stageRule[1],
    /box-shadow:\s*0 0 80px rgb\(0 0 0 \/ \.35\);/,
    "base stage shadow must remain unchanged"
  );

  const layerRule = css.match(/^\.stage::after\s*\{([^}]*)\}/m);
  assert.ok(layerRule, "stage focus layer must exist");
  assert.match(layerRule[1], /content:\s*"";/);
  assert.match(layerRule[1], /position:\s*absolute;/);
  assert.match(layerRule[1], /inset:\s*0;/, "focus layer must stay within the stage");
  assert.match(layerRule[1], /pointer-events:\s*none;/, "focus layer must not intercept controls");
  assert.match(layerRule[1], /opacity:\s*0;/, "focus layer must be hidden by default");
  assert.match(
    layerRule[1],
    /transition:\s*opacity\s+160ms\s+ease-out;/,
    "focus treatment must transition opacity rather than stage shadows"
  );

  const layerZIndex = Number(layerRule[1].match(/z-index:\s*(-?\d+);/)?.[1]);
  assert.ok(Number.isFinite(layerZIndex), "focus layer must have a numeric z-index");
  const cssWithoutLayer = css.replace(layerRule[0], "");
  const contentZIndexes = [...cssWithoutLayer.matchAll(/z-index:\s*(-?\d+);/g)]
    .map((match) => Number(match[1]));
  assert.ok(contentZIndexes.length > 0, "stage content should include explicit stacking levels");
  assert.ok(
    layerZIndex > Math.max(...contentZIndexes),
    "focus layer must paint above every stage child, including the opaque SVG scene"
  );

  const declaration = layerRule[1].match(/box-shadow:\s*([^;]+);/);
  assert.ok(declaration, "stage focus layer must declare box-shadow");

  const layers = [];
  let layerStart = 0;
  let parenthesisDepth = 0;
  for (let index = 0; index < declaration[1].length; index += 1) {
    const character = declaration[1][index];
    if (character === "(") parenthesisDepth += 1;
    if (character === ")") parenthesisDepth -= 1;
    if (character === "," && parenthesisDepth === 0) {
      layers.push(declaration[1].slice(layerStart, index).trim());
      layerStart = index + 1;
    }
  }
  layers.push(declaration[1].slice(layerStart).trim());

  assert.ok(layers.length >= 2, "focus treatment should have a visible layered ring");
  assert.ok(
    layers.every((layer) => layer.startsWith("inset ")),
    "every pointer-close shadow must paint inside a full-viewport stage"
  );
  assert.ok(
    layers.some((layer) => /^inset 0 0 0 clamp\(/.test(layer)),
    "focus treatment should include a crisp inset ring"
  );

  const activeRule = css.match(
    /#fundraiser\[data-focus-restore="pointer-close"\] \.stage::after\s*\{([^}]*)\}/
  );
  assert.ok(activeRule, "pointer-close state must reveal the stage focus layer");
  assert.match(activeRule[1], /opacity:\s*1;/);
  assert.doesNotMatch(
    activeRule[1],
    /box-shadow:/,
    "pointer-close state must not replace or interpolate the base stage shadow"
  );
});

test("pointer closing controls adds a temporary focus restoration state while keyboard close paths do not", async () => {
  await withAppHarness(
    { locationSearch: "?goal=100000&raised=51000" },
    async ({
      document,
      documentListeners,
      elements,
      pendingTimerCount,
      root,
      runNextTimer
    }) => {
      const keydown = documentListeners.get("keydown");
      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#operator-panel").hidden, false);
      assert.equal(document.activeElement, elements.get("#raised-input"));

      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);

      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      elements.get("#close-controls").listeners.get("click")({ detail: 0 });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);

      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      elements.get("#close-controls").listeners.get("click")({ detail: 1 });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);
      assert.equal(root.dataset.focusRestore, "pointer-close");
      assert.equal(pendingTimerCount(), 1);
      root.listeners.get("blur")();
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);
      assert.equal(runNextTimer(), false);

      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#operator-panel").hidden, false);
      assert.equal(document.activeElement, elements.get("#raised-input"));
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);

      keydown({
        key: "Escape",
        target: elements.get("#raised-input"),
        preventDefault() {}
      });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);

      keydown({
        key: "c",
        target: root,
        preventDefault() {}
      });
      elements.get("#close-controls").listeners.get("click")({ detail: 1 });
      assert.equal(root.dataset.focusRestore, "pointer-close");
      assert.equal(runNextTimer(), true);
      assert.equal(root.dataset.focusRestore, undefined);
      assert.equal(pendingTimerCount(), 0);
    }
  );
});

test("progress labels preserve exact integers, floor fractions, and distinguish goal states", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement();
  const elements = new Map([
    ["#goal-display", new FakeElement()],
    ["#meter-fill", new FakeElement()],
    ["#over-goal-message", new FakeElement()],
    ["#percent-display", new FakeElement()],
    ["#raised-display", new FakeElement()],
    ["#school-scene", new FakeElement()]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  try {
    const view = createFundraiserView(
      root,
      createScene({ maxStudents: 0 }),
      { locale: "en-US", currency: "USD" }
    );

    for (const [donationRatio, expectedLabel] of [
      [.29, "29% complete"],
      [.57, "57% complete"],
      [.58, "58% complete"],
      [.579, "57% complete"]
    ]) {
      view.update({
        raised: donationRatio * 100000,
        goal: 100000,
        donationRatio,
        buildingRatio: donationRatio,
        studentRatio: 0,
        reducedMotion: true,
        celebrationActive: false
      });
      assert.equal(elements.get("#percent-display").textContent, expectedLabel);
    }

    const boundaryRaised = 7205759403792792;
    const boundaryGoal = 7205759403792793;
    const boundaryRatio = boundaryRaised / boundaryGoal;
    assert.ok(boundaryRatio < 1);
    view.update({
      raised: boundaryRaised,
      goal: boundaryGoal,
      donationRatio: boundaryRatio,
      buildingRatio: boundaryRatio,
      studentRatio: 0,
      reducedMotion: true,
      celebrationActive: false
    });
    assert.equal(elements.get("#percent-display").textContent, "99% complete");

    view.update({
      raised: 0.00995,
      goal: 0.01,
      donationRatio: .995,
      buildingRatio: .995,
      studentRatio: 0,
      reducedMotion: true,
      celebrationActive: false
    });
    assert.equal(elements.get("#percent-display").textContent, "99% complete");

    view.update({
      raised: 0.01,
      goal: 0.01,
      donationRatio: 1,
      buildingRatio: 1,
      studentRatio: 0,
      reducedMotion: true,
      celebrationActive: false
    });

    assert.equal(elements.get("#raised-display").textContent, "$0.01");
    assert.equal(elements.get("#goal-display").textContent, "Goal $0.01");
    assert.equal(
      elements.get("#percent-display").textContent,
      "100% — students arriving!"
    );
    assert.equal(elements.get("#over-goal-message").hidden, false);

    view.update({
      raised: 0.01001,
      goal: 0.01,
      donationRatio: 1.001,
      buildingRatio: 1,
      studentRatio: .004,
      reducedMotion: true,
      celebrationActive: false
    });
    assert.notEqual(
      elements.get("#percent-display").textContent,
      "100% complete"
    );
    assert.equal(
      elements.get("#percent-display").textContent,
      "100% — students arriving!"
    );

    view.update({
      raised: 0.0125,
      goal: 0.01,
      donationRatio: 1.25,
      buildingRatio: 1,
      studentRatio: 1,
      reducedMotion: true,
      celebrationActive: false
    });
    assert.equal(elements.get("#raised-display").textContent, "$0.0125");
    assert.equal(elements.get("#goal-display").textContent, "Goal $0.01");
    assert.equal(
      elements.get("#percent-display").textContent,
      "125% — campus ready!"
    );

    for (const [donationRatio, expectedLabel] of [
      [1.029, "102% — students arriving!"],
      [1.03, "103% — playground growing!"],
      [1.099, "109% — playground growing!"],
      [1.10, "110% — teachers joining!"],
      [1.169, "116% — teachers joining!"],
      [1.17, "117% — school bus arriving!"],
      [1.249, "124% — school bus arriving!"],
      [1.40, "140% — campus ready!"]
    ]) {
      view.update({
        raised: donationRatio * 100000,
        goal: 100000,
        ...deriveProgress(donationRatio * 100000, 100000),
        reducedMotion: true,
        celebrationActive: false
      });
      assert.equal(elements.get("#percent-display").textContent, expectedLabel);
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("renderer preserves adjacent and extreme non-endpoint rendering states", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const update = (view, raised, goal) => {
    const progress = deriveProgress(raised, goal);
    view.update({
      raised,
      goal,
      ...progress,
      reducedMotion: false,
      celebrationActive: false
    });
    return progress;
  };

  try {
    const goal = 7205759403792793;
    const scene = createScene({ maxStudents: 36 });
    const view = createFundraiserView(root, scene, {
      locale: "en-US",
      currency: "USD"
    });
    const nodes = descendants(elements.get("#school-scene"));
    const blocks = nodes.filter(
      (node) => (node.attributes.get("class") ?? "").includes("school-block")
    );
    const students = nodes.filter(
      (node) => node.attributes.get("class") === "student"
    );
    const lastBlock = blocks.at(-1);
    const firstBlock = blocks[0];
    const firstStudent = students[0];
    const lastStudent = students.at(-1);

    update(view, goal, goal);
    const completedBlockTransform = lastBlock.attributes.get("transform");
    const emptyStudentTransform = firstStudent.attributes.get("transform");
    assert.equal(Number(lastBlock.attributes.get("opacity")), 1);
    assert.equal(Number(root.dataset.visibleBlocks), blocks.length);
    assert.equal(Number(firstStudent.attributes.get("opacity")), 0);
    assert.equal(Number(root.dataset.visibleStudents), 0);

    const belowGoal = update(view, goal - 1, goal);
    const lastBlockReveal = belowGoal.buildingRatio * blocks.length
      - (blocks.length - 1);
    assert.ok(lastBlockReveal > 0 && lastBlockReveal < 1);
    assert.ok(Number(lastBlock.attributes.get("opacity")) < 1);
    assert.notEqual(
      lastBlock.attributes.get("transform"),
      completedBlockTransform
    );
    const meterStateDistinct = elements.get("#meter-fill").style.width
      === "var(--meter-width)"
      && elements.get("#meter-fill").style.getPropertyValue("--meter-width")
        === "99.99999999999999%";
    assert.ok(Number(root.dataset.buildingPercent) < 100);
    assert.ok(Number(root.dataset.visibleBlocks) < blocks.length);

    const aboveGoal = update(view, goal + 1, goal);
    const firstStudentReveal = aboveGoal.studentRatio * students.length;
    assert.ok(firstStudentReveal > 0 && firstStudentReveal < 1);
    assert.ok(Number(firstStudent.attributes.get("opacity")) > 0);
    assert.notEqual(
      firstStudent.attributes.get("transform"),
      emptyStudentTransform
    );
    assert.ok(Number(root.dataset.studentPercent) > 0);
    assert.ok(Number(root.dataset.visibleStudents) > 0);

    update(view, 0, 1);
    const emptyBlockTransform = firstBlock.attributes.get("transform");
    update(view, Number.MIN_VALUE, 1);
    const firstBlockOpacity = Number(firstBlock.attributes.get("opacity"));
    assert.ok(firstBlockOpacity > 0 && firstBlockOpacity < 1);
    const initialBlockTransformDistinct = firstBlock.attributes.get("transform")
      !== emptyBlockTransform;

    const completionRaised = deriveSliderMaximum(MAX_GOAL);
    update(view, completionRaised, MAX_GOAL);
    const completedStudentTransform = lastStudent.attributes.get("transform");
    update(view, completionRaised - 1, MAX_GOAL);
    const lastStudentOpacity = Number(lastStudent.attributes.get("opacity"));
    assert.ok(lastStudentOpacity > 0 && lastStudentOpacity < 1);
    const finalStudentTransformDistinct = lastStudent.attributes.get("transform")
      !== completedStudentTransform;
    assert.deepEqual(
      {
        meterStateDistinct,
        initialBlockTransformDistinct,
        finalStudentTransformDistinct
      },
      {
        meterStateDistinct: true,
        initialBlockTransformDistinct: true,
        finalStudentTransformDistinct: true
      }
    );
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("finished facade uses frozen drawing order, geometry, and progressive finish ranges", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const classes = (node) => node.attributes.get("class") ?? "";

  try {
    const scene = createScene({ maxStudents: 36 });
    const view = createFundraiserView(root, scene, {
      locale: "en-US",
      currency: "USD"
    });
    const sceneSvg = elements.get("#school-scene");
    assert.equal(view.blockCount, 308);
    assert.equal(view.studentCount, 36);
    assert.equal(view.teacherCount, 2);
    assert.deepEqual(
      sceneSvg.children
        .map(classes)
        .filter(Boolean),
      [
        "landscape",
        "campus-back swings",
        "path-and-shadow",
        "architectural-finish finish-shell aperture-recesses",
        "school",
        "architectural-finish finish-lower facade-lower",
        "architectural-finish finish-upper facade-upper",
        "architectural-finish finish-tower facade-tower",
        "architectural-finish finish-final facade-final",
        "architectural-finish finish-final steps-and-shrubs",
        "goal-flag",
        "campus-people teachers",
        "students",
        "school-bus",
        "celebration-burst"
      ]
    );

    const nodes = descendants(sceneSvg);
    assert.equal(
      nodes.filter((node) => classes(node).includes("school-block")).length,
      308
    );
    const studentGroundShadows = nodes.filter(
      (node) => classes(node) === "student-ground-shadow"
    );
    assert.equal(studentGroundShadows.length, 36);
    assert.ok(studentGroundShadows.every((shadow) =>
      shadow.tagName === "ellipse"
      && shadow.attributes.get("cx") === "0"
      && shadow.attributes.get("cy") === "43"
      && shadow.attributes.get("rx") === "9"
      && shadow.attributes.get("ry") === "2.5"
      && shadow.attributes.get("opacity") === "0.16"
    ));
    assert.ok(nodes.some((node) => node.attributes.get("d")
      === "M760 900 C815 790 875 720 918 688 L1008 688 C1055 724 1125 795 1180 900 Z"));
    assert.ok(nodes.some((node) =>
      node.tagName === "ellipse"
      && node.attributes.get("cx") === "1003"
      && node.attributes.get("cy") === "700"
      && node.attributes.get("rx") === "500"
      && node.attributes.get("ry") === "58"
    ));
    assert.ok(nodes.some((node) =>
      node.textContent === "TCF School Seattle"
      && node.attributes.get("x") === "963"
      && node.attributes.get("y") === "325"
      && node.attributes.get("textLength") === "184"
      && node.attributes.get("lengthAdjust") === "spacingAndGlyphs"
    ));
    assert.ok(nodes.some((node) =>
      classes(node) === "flag-cloth"
      && node.children.some((child) => child.attributes.get("fill") === "#01411C")
    ));
    const flagCloth = nodes.find((node) => classes(node) === "flag-cloth");
    assert.equal(flagCloth.attributes.get("data-pole-edge-x"), "0");
    assert.equal(flagCloth.attributes.get("data-hoist-side"), "left");
    assert.equal(flagCloth.attributes.get("data-fly-direction"), "right");
    assert.equal(flagCloth.attributes.get("data-width"), "132");
    assert.equal(flagCloth.attributes.get("data-height"), "88");
    assert.ok(flagCloth.children.some((child) =>
      child.tagName === "rect"
      && child.attributes.get("x") === "33"
      && child.attributes.get("width") === "99"
      && child.attributes.get("height") === "88"
    ));
    assert.ok(nodes.some((node) =>
      node.attributes.get("transform") === "translate(966 119)"
    ));
    assert.equal(nodes.filter((node) => classes(node) === "teacher").length, 2);
    assert.ok(nodes.some((node) => node.textContent === "SCHOOL BUS"));
    for (const [x, y, width, height] of [
      [907, 653, 112, 11],
      [889, 664, 148, 12],
      [871, 676, 184, 14]
    ]) {
      assert.ok(nodes.some((node) =>
        node.tagName === "rect"
        && node.attributes.get("x") === String(x)
        && node.attributes.get("y") === String(y)
        && node.attributes.get("width") === String(width)
        && node.attributes.get("height") === String(height)
      ));
    }

    const finishGroups = nodes.filter(
      (node) => node.attributes.get("data-finish")
    );
    assert.deepEqual(
      finishGroups.map((node) => [
        node.attributes.get("data-finish"),
        Number(node.attributes.get("data-start")),
        Number(node.attributes.get("data-end"))
      ]),
      [
        ["shell", .17, .30],
        ["lower", .31, .52],
        ["upper", .58, .74],
        ["tower", .75, .84],
        ["final", .84, 1],
        ["final", .84, 1],
        ["lower", .31, .52],
        ["upper", .58, .74],
        ["tower", .75, .84]
      ]
    );

    const update = (buildingRatio, reducedMotion = false) => view.update({
      raised: buildingRatio * 100000,
      goal: 100000,
      donationRatio: buildingRatio,
      buildingRatio,
      studentRatio: 0,
      reducedMotion,
      celebrationActive: false
    });
    update(.92);
    const forward = finishGroups.map((node) => node.attributes.get("opacity"));
    update(.46);
    const reversed = finishGroups.map((node) => node.attributes.get("opacity"));
    update(.92);
    assert.deepEqual(
      finishGroups.map((node) => node.attributes.get("opacity")),
      forward
    );
    assert.notDeepEqual(reversed, forward);
    update(.46, true);
    assert.deepEqual(
      finishGroups.map((node) => node.attributes.get("opacity")),
      reversed
    );
    assert.equal(root.dataset.visibleBlocks, "141.680000");
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("campus additions reveal at frozen thresholds with bounded one-shot motion", async () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const classes = (node) => node.attributes.get("class") ?? "";

  try {
    const view = createFundraiserView(
      root,
      createScene({ maxStudents: 36 }),
      { locale: "en-US", currency: "USD" }
    );
    const nodes = descendants(elements.get("#school-scene"));
    const flag = nodes.find((node) => classes(node) === "goal-flag");
    const swingParts = ["swing-frame", "swing-ropes", "swing-seats"]
      .map((className) => nodes.find((node) => classes(node) === className));
    const teachers = nodes.filter((node) => classes(node) === "teacher");
    const bus = nodes.find((node) => classes(node) === "school-bus");
    assert.ok(flag);
    assert.ok(swingParts.every(Boolean));
    assert.equal(teachers.length, 2);
    assert.ok(bus);

    const update = (ratio, reducedMotion = false) => view.update({
      raised: ratio * 100000,
      goal: 100000,
      ...deriveProgress(ratio * 100000, 100000),
      reducedMotion,
      celebrationActive: false
    });

    update(.999999);
    assert.equal(flag.attributes.get("opacity"), "0");
    assert.equal(elements.get("#over-goal-message").hidden, true);
    assert.equal(root.dataset.goalAchieved, "false");

    update(1);
    assert.equal(flag.attributes.get("opacity"), "1");
    assert.equal(elements.get("#over-goal-message").hidden, false);
    assert.deepEqual(swingParts.map((node) => node.attributes.get("opacity")), [
      "0.0000",
      "0.0000",
      "0.0000"
    ]);
    assert.equal(root.dataset.goalAchieved, "true");

    update(1.065);
    assert.deepEqual(
      swingParts.map((node) => Number(node.attributes.get("opacity"))),
      [1, .5, 0]
    );
    assert.equal(root.dataset.swingPercent, "50.000");

    update(1.10);
    assert.deepEqual(
      swingParts.map((node) => Number(node.attributes.get("opacity"))),
      [1, 1, 1]
    );
    assert.equal(Number(teachers[0].attributes.get("opacity")), 0);

    update(1.135);
    assert.ok(
      Math.abs(Number(teachers[0].attributes.get("opacity")) - 1) < 1e-12
    );
    assert.equal(Number(teachers[1].attributes.get("opacity")), 0);
    assert.equal(root.dataset.visibleTeachers, "1.000000");
    assert.equal(root.dataset.teacherPercent, "50.000");

    update(1.17);
    assert.equal(teachers[0].attributes.get("transform"), "translate(500 704) scale(1.08)");
    assert.equal(teachers[1].attributes.get("transform"), "translate(1517 704) scale(1.08)");
    assert.equal(Number(bus.attributes.get("opacity")), 0);
    assert.equal(bus.attributes.get("transform"), "translate(-360 34)");

    update(1.21);
    assert.equal(Number(bus.attributes.get("opacity")), .5);
    assert.equal(bus.attributes.get("transform"), "translate(-45.00 4.25)");
    assert.equal(root.dataset.busPercent, "50.000");

    update(1.21, true);
    assert.equal(bus.attributes.get("transform"), "translate(0 0)");

    update(1.25);
    assert.equal(Number(bus.attributes.get("opacity")), 1);
    assert.equal(bus.attributes.get("transform"), "translate(0 0)");
    assert.equal(root.dataset.busPercent, "100.000");

    const campusLayers = nodes.filter((node) => [
      "campus-back swings",
      "goal-flag",
      "campus-people teachers",
      "school-bus"
    ].includes(classes(node)));
    const campusPrimitiveCount = campusLayers.reduce(
      (count, layer) => count + descendants(layer)
        .filter((node) => node.children.length === 0).length,
      0
    );
    assert.ok(campusPrimitiveCount < 80, `campus primitives: ${campusPrimitiveCount}`);

    const css = await readFile(new URL("../styles.css", import.meta.url), "utf8");
    assert.match(css, /\.flag-cloth\s*\{[^}]*animation:\s*flag-wave 2\.8s ease-in-out infinite alternate;/s);
    assert.match(css, /\.flag-cloth\s*\{[^}]*transform-origin:\s*left center;/s);
    assert.doesNotMatch(css, /@keyframes flag-wave\s*\{[^}]*scaleY\(/s);
    assert.match(css, /#fundraiser\[data-motion="reduce"\] \.flag-cloth\s*\{[^}]*animation:\s*none !important;/s);
    assert.doesNotMatch(css, /swing-seat[^}]*animation:/s);
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("staged student routes stay below overlap limits and clear the moving bus", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const classes = (node) => node.attributes.get("class") ?? "";
  const intersects = (first, second) =>
    first.left < second.right
    && first.right > second.left
    && first.top < second.bottom
    && first.bottom > second.top;
  const overlapArea = (first, second) =>
    Math.max(
      0,
      Math.min(first.right, second.right) - Math.max(first.left, second.left)
    )
    * Math.max(
      0,
      Math.min(first.bottom, second.bottom) - Math.max(first.top, second.top)
    );

  try {
    const scene = createScene({ maxStudents: 36 });
    const view = createFundraiserView(
      root,
      scene,
      { locale: "en-US", currency: "USD" }
    );
    const nodes = descendants(elements.get("#school-scene"));
    const students = nodes.filter((node) => classes(node) === "student");
    const bus = nodes.find((node) => classes(node) === "school-bus");
    const milestoneExpectations = new Map([
      [120, { studentRatio: .12, swingRatio: 0, teacherRatio: 0, busRatio: 0 }],
      [400, { studentRatio: .4, swingRatio: 1, teacherRatio: 0, busRatio: 0 }],
      [540, { studentRatio: .54, swingRatio: 1, teacherRatio: .5, busRatio: 0 }],
      [840, { studentRatio: .84, swingRatio: 1, teacherRatio: 1, busRatio: .5 }],
      [980, { studentRatio: .98, swingRatio: 1, teacherRatio: 1, busRatio: .9375 }]
    ]);

    for (const reducedMotion of [false, true]) {
      const forwardSignatures = new Map();
      let maximumStudentOverlap = 0;
      let overlapViolationCount = 0;
      let busIntersectionCount = 0;
      for (const direction of ["ascending", "descending"]) {
        for (let sample = 0; sample <= 1000; sample += 1) {
          const step = direction === "ascending" ? sample : 1000 - sample;
          const ratio = 1 + .25 * step / 1000;
          const progress = deriveProgress(ratio * 100000, 100000);
          const milestone = milestoneExpectations.get(step);
          if (milestone) {
            for (const [name, expected] of Object.entries(milestone)) {
              assert.ok(
                Math.abs(progress[name] - expected) < 1e-12,
                `${ratio} ${name}: ${progress[name]}`
              );
            }
          }
          view.update({
            raised: ratio * 100000,
            goal: 100000,
            ...progress,
            reducedMotion,
            celebrationActive: false
          });

          const visibleStudents = [];
          for (let index = 0; index < students.length; index += 1) {
            const transform = students[index].attributes.get("transform");
            const match = transform.match(
              /^translate\(([-\d.]+) ([-\d.]+)\) scale\(([-\d.]+)\)/
            );
            assert.ok(match, transform);
            const [, xValue, yValue, scaleValue] = match;
            const x = Number(xValue);
            const y = Number(yValue);
            const scale = Number(scaleValue);
            const reveal = Math.min(
              1,
              Math.max(0, progress.studentRatio * students.length - index)
            );
            const expected = deriveStudentRoutePosition(
              scene.students[index],
              reveal,
              reducedMotion
            );
            assert.ok(Math.abs(x - expected.x) <= .011, `${ratio} ${transform}`);
            assert.ok(Math.abs(y - expected.y) <= .011, `${ratio} ${transform}`);
            if (Number(students[index].attributes.get("opacity")) <= 0) continue;
            const local = scene.students[index].bounds;
            visibleStudents.push({
              index,
              bounds: {
                left: x + local.left * scale,
                right: x + local.right * scale,
                top: y + local.top * scale,
                bottom: y + local.bottom * scale
              }
            });
          }

          for (let first = 0; first < visibleStudents.length; first += 1) {
            for (
              let second = first + 1;
              second < visibleStudents.length;
              second += 1
            ) {
              const area = overlapArea(
                visibleStudents[first].bounds,
                visibleStudents[second].bounds
              );
              maximumStudentOverlap = Math.max(maximumStudentOverlap, area);
              if (area >= 850) overlapViolationCount += 1;
            }
          }

          if (Number(bus.attributes.get("opacity")) > 0) {
            const busOffset = Number(
              bus.attributes.get("transform").match(
                /^translate\(([-\d.]+) 0\)$/
              )?.[1]
            );
            const busBounds = {
              left: scene.campus.bus.bounds.left + busOffset,
              right: scene.campus.bus.bounds.right + busOffset,
              top: scene.campus.bus.bounds.top,
              bottom: scene.campus.bus.bounds.bottom
            };
            for (const student of visibleStudents) {
              if (intersects(busBounds, student.bounds)) {
                busIntersectionCount += 1;
              }
            }
          }

          const signature = JSON.stringify([
            ...students.map((student) => [
              student.attributes.get("opacity"),
              student.attributes.get("transform")
            ]),
            bus.attributes.get("transform")
          ]);
          const key = `${reducedMotion}:${step}`;
          if (direction === "ascending") {
            forwardSignatures.set(key, signature);
          } else {
            assert.equal(signature, forwardSignatures.get(key), key);
          }
        }
      }
      assert.equal(
        overlapViolationCount,
        0,
        `${reducedMotion ? "reduced" : "full"} overlap maximum ${maximumStudentOverlap}`
      );
      assert.ok(
        maximumStudentOverlap < 850,
        `${reducedMotion ? "reduced" : "full"} maximum overlap ${maximumStudentOverlap}`
      );
      assert.equal(
        busIntersectionCount,
        0,
        `${reducedMotion ? "reduced" : "full"} bus intersections`
      );
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("student route display progress bounds painted-frame movement and settles exactly", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const parsePosition = (student) => {
    const transform = student.attributes.get("transform");
    const match = transform.match(
      /^translate\(([-\d.]+) ([-\d.]+)\) scale\(/
    );
    assert.ok(match, transform);
    return {
      x: Number(match[1]),
      y: Number(match[2]),
      opacity: Number(student.attributes.get("opacity"))
    };
  };
  const movement = (before, after) =>
    Math.hypot(after.x - before.x, after.y - before.y);

  try {
    const scene = createScene({ maxStudents: 36 });
    const view = createFundraiserView(
      root,
      scene,
      { locale: "en-US", currency: "USD" }
    );
    const students = descendants(elements.get("#school-scene"))
      .filter((node) => node.attributes.get("class") === "student");
    const render = (ratio, deltaMs, reducedMotion = false) => view.update({
      raised: ratio * 100000,
      goal: 100000,
      ...deriveProgress(ratio * 100000, 100000),
      deltaMs,
      reducedMotion,
      celebrationActive: false
    });

    let result = render(1, 0);
    assert.equal(result.needsFrame, false);
    assert.equal(root.dataset.visibleStudents, "0.000000");
    let previous = students.map(parsePosition);
    let forwardFrames = 0;
    let maximumNormalMovement = 0;
    do {
      result = render(1.25, 16);
      const current = students.map(parsePosition);
      for (let index = 0; index < current.length; index += 1) {
        if (previous[index].opacity > 0 || current[index].opacity > 0) {
          maximumNormalMovement = Math.max(
            maximumNormalMovement,
            movement(previous[index], current[index])
          );
        }
      }
      previous = current;
      forwardFrames += 1;
      assert.ok(forwardFrames < 2000, "forward student routes must settle");
    } while (result.needsFrame);

    assert.ok(forwardFrames > 1);
    assert.ok(
      maximumNormalMovement <= 30,
      `maximum 16ms movement: ${maximumNormalMovement}`
    );
    assert.equal(root.dataset.studentPercent, "100.000");
    assert.equal(root.dataset.visibleStudents, "36.000000");
    for (let index = 0; index < students.length; index += 1) {
      const position = parsePosition(students[index]);
      assert.ok(Math.abs(position.x - scene.students[index].targetX) <= .011);
      assert.ok(Math.abs(position.y - scene.students[index].targetY) <= .011);
    }

    let reverseFrames = 0;
    let maximumCappedMovement = 0;
    do {
      result = render(1, 50);
      const current = students.map(parsePosition);
      for (let index = 0; index < current.length; index += 1) {
        if (previous[index].opacity > 0 || current[index].opacity > 0) {
          maximumCappedMovement = Math.max(
            maximumCappedMovement,
            movement(previous[index], current[index])
          );
        }
      }
      previous = current;
      reverseFrames += 1;
      assert.ok(reverseFrames < 2000, "reverse student routes must settle");
    } while (result.needsFrame);

    assert.ok(reverseFrames > 1);
    assert.ok(
      maximumCappedMovement <= 100,
      `maximum 50ms movement: ${maximumCappedMovement}`
    );
    assert.equal(root.dataset.studentPercent, "0.000");
    assert.equal(root.dataset.visibleStudents, "0.000000");

    result = render(1.25, 16, true);
    assert.equal(result.needsFrame, false);
    assert.equal(root.dataset.visibleStudents, "36.000000");
    for (let index = 0; index < students.length; index += 1) {
      const position = parsePosition(students[index]);
      assert.equal(position.x, scene.students[index].targetX);
      assert.equal(position.y, scene.students[index].targetY);
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("direct and initial full-motion student targets keep the bus clear at production frame deltas", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const intersects = (first, second) =>
    first.left < second.right
    && first.right > second.left
    && first.top < second.bottom
    && first.bottom > second.top;

  try {
    for (const deltaMs of [1000 / 60, 50]) {
      for (const initialTarget of [false, true]) {
        const root = new FakeElement("main");
        const elements = new Map([
          ["#goal-display", new FakeElement("span")],
          ["#meter-fill", new FakeElement("span")],
          ["#over-goal-message", new FakeElement("p")],
          ["#percent-display", new FakeElement("span")],
          ["#raised-display", new FakeElement("p")],
          ["#school-scene", new FakeElement("svg")]
        ]);
        root.querySelector = (selector) => elements.get(selector);
        const scene = createScene({ maxStudents: 36 });
        const view = createFundraiserView(
          root,
          scene,
          { locale: "en-US", currency: "USD" }
        );
        const nodes = descendants(elements.get("#school-scene"));
        const studentNodes = nodes.filter(
          (node) => node.attributes.get("class") === "student"
        );
        const busNode = nodes.find(
          (node) => node.attributes.get("class") === "school-bus"
        );
        let maximumBusMovement = 0;
        let previousBusOffset = null;
        const render = (ratio) => view.update({
          raised: ratio * 100000,
          goal: 100000,
          ...deriveProgress(ratio * 100000, 100000),
          deltaMs,
          reducedMotion: false,
          celebrationActive: false
        });
        const assertBusClear = (frame, phase) => {
          const busTransform = busNode.attributes.get("transform");
          const busMatch = busTransform.match(
            /^translate\(([-\d.]+) ([-\d.]+)\)$/
          );
          assert.ok(busMatch, busTransform);
          const busOffset = {
            x: Number(busMatch[1]),
            y: Number(busMatch[2])
          };
          if (previousBusOffset !== null) {
            maximumBusMovement = Math.max(
              maximumBusMovement,
              Math.hypot(
                busOffset.x - previousBusOffset.x,
                busOffset.y - previousBusOffset.y
              )
            );
          }
          previousBusOffset = busOffset;
          if (Number(busNode.attributes.get("opacity")) <= 0) return;
          const busBounds = {
            left: scene.campus.bus.bounds.left + busOffset.x,
            right: scene.campus.bus.bounds.right + busOffset.x,
            top: scene.campus.bus.bounds.top + busOffset.y,
            bottom: scene.campus.bus.bounds.bottom + busOffset.y
          };
          for (let index = 0; index < studentNodes.length; index += 1) {
            const studentNode = studentNodes[index];
            if (Number(studentNode.attributes.get("opacity")) <= 0) continue;
            const transform = studentNode.attributes.get("transform");
            const match = transform.match(
              /^translate\(([-\d.]+) ([-\d.]+)\) scale\(([-\d.]+)\)/
            );
            assert.ok(match, transform);
            const student = scene.students[index];
            const x = Number(match[1]);
            const y = Number(match[2]);
            const scale = Number(match[3]);
            const studentBounds = {
              left: x + student.bounds.left * scale,
              right: x + student.bounds.right * scale,
              top: y + student.bounds.top * scale,
              bottom: y + student.bounds.bottom * scale
            };
            assert.equal(
              intersects(busBounds, studentBounds),
              false,
              `${phase} ${deltaMs}ms frame ${frame} student ${index}`
            );
          }
        };
        const settle = (ratio, phase) => {
          let result;
          let frame = 0;
          do {
            result = render(ratio);
            assertBusClear(frame, phase);
            frame += 1;
            assert.ok(frame < 2000, `${phase} must settle`);
          } while (result.needsFrame);
          return frame;
        };

        if (!initialTarget) settle(1, "direct baseline");
        assert.ok(settle(1.25, initialTarget ? "initial 125%" : "direct 100-125") > 1);
        assert.equal(root.dataset.visibleStudents, "36.000000");
        assert.equal(root.dataset.busPercent, "100.000");
        assert.equal(busNode.attributes.get("transform"), "translate(0 0)");
        for (let index = 0; index < studentNodes.length; index += 1) {
          const transform = studentNodes[index].attributes.get("transform");
          const match = transform.match(
            /^translate\(([-\d.]+) ([-\d.]+)\) scale\(/
          );
          assert.ok(match, transform);
          assert.equal(Number(match[1]), scene.students[index].targetX);
          assert.equal(Number(match[2]), scene.students[index].targetY);
        }

        if (!initialTarget) {
          const interruptedFrames = Math.ceil(500 / deltaMs);
          for (let frame = 0; frame < interruptedFrames; frame += 1) {
            const result = render(1);
            assert.equal(result.needsFrame, true);
            assertBusClear(frame, "mid-transition reverse");
          }
          settle(1.25, "mid-transition retarget");
        }
        assert.ok(settle(1, "reverse 125-100") > 1);
        assert.equal(root.dataset.visibleStudents, "0.000000");
        assert.equal(root.dataset.busPercent, "0.000");
        assert.equal(
          busNode.attributes.get("transform"),
          `translate(${scene.campus.bus.startOffsetX} ${scene.campus.bus.startOffsetY})`
        );
        assert.ok(
          maximumBusMovement <= deltaMs * 1.2 + .011,
          `${deltaMs}ms maximum bus movement: ${maximumBusMovement}`
        );
      }
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("integrated student route jumps remain collision-safe forward and reverse", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const parseStudent = (element, student) => {
    const transform = element.attributes.get("transform");
    const match = transform.match(
      /^translate\(([-\d.]+) ([-\d.]+)\) scale\(([-\d.]+)\)/
    );
    assert.ok(match, transform);
    const x = Number(match[1]);
    const y = Number(match[2]);
    const scale = Number(match[3]);
    return {
      opacity: Number(element.attributes.get("opacity")),
      bounds: {
        left: x + student.bounds.left * scale,
        right: x + student.bounds.right * scale,
        top: y + student.bounds.top * scale,
        bottom: y + student.bounds.bottom * scale
      }
    };
  };
  const overlapArea = (first, second) =>
    Math.max(
      0,
      Math.min(first.right, second.right) - Math.max(first.left, second.left)
    )
    * Math.max(
      0,
      Math.min(first.bottom, second.bottom) - Math.max(first.top, second.top)
    );

  try {
    const scene = createScene({ maxStudents: 36 });
    const view = createFundraiserView(
      root,
      scene,
      { locale: "en-US", currency: "USD" }
    );
    const students = descendants(elements.get("#school-scene"))
      .filter((node) => node.attributes.get("class") === "student");
    const render = (ratio, deltaMs) => view.update({
      raised: ratio * 100000,
      goal: 100000,
      ...deriveProgress(ratio * 100000, 100000),
      deltaMs,
      reducedMotion: false,
      celebrationActive: false
    });
    const targets = [
      1,
      1.19917,
      1.19926,
      1.19935,
      1.25,
      1.19935,
      1.19926,
      1.19917,
      1
    ];

    render(targets[0], 0);
    let maximumOverlap = 0;
    let maximumDescription = "";
    for (const target of targets.slice(1)) {
      let result;
      let frames = 0;
      do {
        result = render(target, 16);
        const visible = students
          .map((element, index) => ({
            index,
            ...parseStudent(element, scene.students[index])
          }))
          .filter((student) => student.opacity > 0);
        for (let first = 0; first < visible.length; first += 1) {
          for (let second = first + 1; second < visible.length; second += 1) {
            const area = overlapArea(
              visible[first].bounds,
              visible[second].bounds
            );
            if (area > maximumOverlap) {
              maximumOverlap = area;
              maximumDescription =
                `${target * 100}% frame ${frames + 1} students `
                + `${visible[first].index}/${visible[second].index}`;
            }
          }
        }
        frames += 1;
        assert.ok(frames < 1000, `${target * 100}% routes must settle`);
      } while (result.needsFrame);
    }

    assert.ok(
      maximumOverlap < 850,
      `maximum integrated overlap ${maximumOverlap} at ${maximumDescription}`
    );
    assert.equal(root.dataset.visibleStudents, "0.000000");
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("renderer preserves values immediately inside every finish range endpoint", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];

  try {
    const view = createFundraiserView(
      root,
      createScene({ maxStudents: 0 }),
      { locale: "en-US", currency: "USD" }
    );
    const finishGroups = descendants(elements.get("#school-scene")).filter(
      (node) => node.attributes.get("data-finish")
    );
    const update = (buildingRatio) => view.update({
      raised: buildingRatio * 100000,
      goal: 100000,
      donationRatio: buildingRatio,
      buildingRatio,
      studentRatio: 0,
      reducedMotion: false,
      celebrationActive: false
    });

    for (const finish of finishGroups) {
      const start = Number(finish.attributes.get("data-start"));
      const end = Number(finish.attributes.get("data-end"));

      update(start);
      assert.equal(Number(finish.attributes.get("opacity")), 0);
      update(adjacentFloat(start, 1));
      assert.ok(
        Number(finish.attributes.get("opacity")) > 0,
        `${finish.attributes.get("data-finish")} must remain above zero just inside its start`
      );

      update(end);
      assert.equal(Number(finish.attributes.get("opacity")), 1);
      update(adjacentFloat(end, -1));
      assert.ok(
        Number(finish.attributes.get("opacity")) < 1,
        `${finish.attributes.get("data-finish")} must remain below one just inside its end`
      );
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("aperture recesses reveal with their wall phases forward, reverse, and reduced", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  try {
    const view = createFundraiserView(
      root,
      createScene({ maxStudents: 0 }),
      { locale: "en-US", currency: "USD" }
    );
    const sceneSvg = elements.get("#school-scene");
    const classes = (node) => node.attributes.get("class") ?? "";
    const apertureLayer = sceneSvg.children.find(
      (node) => classes(node).includes("aperture-recesses")
    );
    assert.ok(apertureLayer);
    assert.deepEqual(
      apertureLayer.children.map((node) => [
        classes(node),
        node.attributes.get("data-finish"),
        Number(node.attributes.get("data-start")),
        Number(node.attributes.get("data-end"))
      ]),
      [
        ["architectural-finish finish-lower aperture-lower", "lower", .31, .52],
        ["architectural-finish finish-upper aperture-upper", "upper", .58, .74],
        ["architectural-finish finish-tower aperture-tower", "tower", .75, .84]
      ]
    );

    const [lower, upper, tower] = apertureLayer.children;
    assert.equal(
      lower.children.filter((node) =>
        node.tagName === "rect" && node.attributes.get("y") === "547"
      ).length,
      6
    );
    assert.ok(lower.children.some((node) =>
      node.attributes.get("d") === "M918 653 V538 A45 45 0 0 1 1008 538 V653 Z"
    ));
    assert.equal(
      upper.children.filter((node) =>
        node.tagName === "rect" && node.attributes.get("y") === "437"
      ).length,
      6
    );
    assert.ok(tower.children.some((node) =>
      node.attributes.get("d") === "M929 407 V373 A34 34 0 0 1 997 373 V407 Z"
    ));

    const update = (buildingRatio, reducedMotion = false) => view.update({
      raised: buildingRatio * 100000,
      goal: 100000,
      donationRatio: buildingRatio,
      buildingRatio,
      studentRatio: 0,
      reducedMotion,
      celebrationActive: false
    });
    const opacity = () => apertureLayer.children.map(
      (node) => node.attributes.get("opacity")
    );
    const cases = [
      [.235, ["0.0000", "0.0000", "0.0000"]],
      [.31, ["0.0000", "0.0000", "0.0000"]],
      [.415, ["0.5000", "0.0000", "0.0000"]],
      [.52, ["1.0000", "0.0000", "0.0000"]],
      [.58, ["1.0000", "0.0000", "0.0000"]],
      [.66, ["1.0000", "0.5000", "0.0000"]],
      [.74, ["1.0000", "1.0000", "0.0000"]],
      [.75, ["1.0000", "1.0000", "0.0000"]],
      [.795, ["1.0000", "1.0000", "0.5000"]],
      [.84, ["1.0000", "1.0000", "1.0000"]]
    ];

    for (const [ratio, expected] of cases) {
      update(ratio);
      assert.deepEqual(opacity(), expected);
      update(1);
      update(ratio);
      assert.deepEqual(opacity(), expected);
      update(ratio, true);
      assert.deepEqual(opacity(), expected);
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("completed aperture masks have phased masonry backing outside every intended opening", () => {
  const originalDocument = Object.getOwnPropertyDescriptor(globalThis, "document");
  const root = new FakeElement("main");
  const elements = new Map([
    ["#goal-display", new FakeElement("span")],
    ["#meter-fill", new FakeElement("span")],
    ["#over-goal-message", new FakeElement("p")],
    ["#percent-display", new FakeElement("span")],
    ["#raised-display", new FakeElement("p")],
    ["#school-scene", new FakeElement("svg")]
  ]);
  root.querySelector = (selector) => elements.get(selector);
  Object.defineProperty(globalThis, "document", {
    configurable: true,
    writable: true,
    value: {
      createElementNS(_namespace, name) {
        return new FakeElement(name);
      }
    }
  });

  const descendants = (node) => [
    ...node.children,
    ...node.children.flatMap(descendants)
  ];
  const classes = (node) => node.attributes.get("class") ?? "";
  const contains = (backing, bounds) => {
    const x = Number(backing.attributes.get("x"));
    const y = Number(backing.attributes.get("y"));
    const right = x + Number(backing.attributes.get("width"));
    const bottom = y + Number(backing.attributes.get("height"));
    return x <= bounds.left
      && y <= bounds.top
      && right >= bounds.right
      && bottom >= bounds.bottom;
  };
  const windowXs = [569, 677, 785, 1109, 1217, 1325];

  try {
    createFundraiserView(
      root,
      createScene({ maxStudents: 0 }),
      { locale: "en-US", currency: "USD" }
    );
    const nodes = descendants(elements.get("#school-scene"));
    const lowerBackings = nodes.filter(
      (node) => classes(node).includes("backing-lower-window")
    );
    const upperBackings = nodes.filter(
      (node) => classes(node).includes("backing-upper-window")
    );
    const entranceLowerBacking = nodes.find(
      (node) => classes(node).includes("backing-entrance-lower")
    );
    const entranceUpperBacking = nodes.find(
      (node) => classes(node).includes("backing-entrance-upper")
    );
    const towerBacking = nodes.find(
      (node) => classes(node).includes("backing-tower-window")
    );

    assert.equal(lowerBackings.length, 6);
    assert.equal(upperBackings.length, 6);
    assert.ok(entranceLowerBacking);
    assert.ok(entranceUpperBacking);
    assert.ok(towerBacking);

    for (let index = 0; index < windowXs.length; index += 1) {
      const frameX = windowXs[index];
      assert.ok(contains(lowerBackings[index], {
        left: frameX - 11,
        top: 543 - 8,
        right: frameX + 79 + 8,
        bottom: 609
      }));
      assert.ok(contains(upperBackings[index], {
        left: frameX - 11,
        top: 433 - 8,
        right: frameX + 79 + 8,
        bottom: 499
      }));
    }
    assert.ok(contains(entranceLowerBacking, {
      left: 918,
      top: 521 - 8,
      right: 1008 + 8,
      bottom: 653
    }));
    assert.ok(contains(entranceUpperBacking, {
      left: 936,
      top: 499 - 8,
      right: 1008 + 8,
      bottom: 521
    }));
    assert.ok(contains(towerBacking, {
      left: 909,
      top: 345 - 8,
      right: 999 + 8,
      bottom: 411
    }));

    for (const backing of [
      ...lowerBackings,
      ...upperBackings,
      entranceLowerBacking,
      entranceUpperBacking,
      towerBacking
    ]) {
      assert.equal(backing.attributes.get("fill"), "#BE623D");
      assert.equal(backing.attributes.get("stroke"), "#8F432C");
    }
  } finally {
    if (originalDocument) {
      Object.defineProperty(globalThis, "document", originalDocument);
    } else {
      delete globalThis.document;
    }
  }
});

test("progressive opacity clamps exact stage ramps and reverses deterministically", () => {
  const closeTo = (actual, expected) => {
    assert.ok(Math.abs(actual - expected) < 1e-12, `${actual} != ${expected}`);
  };
  assert.equal(progressiveOpacity(0, .17, .30), 0);
  assert.equal(progressiveOpacity(.17, .17, .30), 0);
  closeTo(progressiveOpacity(.235, .17, .30), .5);
  assert.equal(progressiveOpacity(.30, .17, .30), 1);
  assert.equal(progressiveOpacity(1, .17, .30), 1);
  closeTo(progressiveOpacity(.92, .84, 1), .5);
  closeTo(progressiveOpacity(.46, .31, .52), 5 / 7);
  assert.throws(() => progressiveOpacity(.5, .5, .5), RangeError);
});

test("event controls and hint are left-aligned without the old narrow override", async () => {
  const css = await readFile(new URL("../styles.css", import.meta.url), "utf8");
  const operator = css.match(/^\.operator-panel\s*\{([^}]*)\}/m)?.[1];
  assert.ok(operator);
  assert.match(operator, /left:\s*2\.2%;/);
  assert.match(operator, /right:\s*auto;/);
  assert.match(operator, /bottom:\s*5\.5%;/);
  assert.match(operator, /width:\s*clamp\(280px,\s*20cqw,\s*340px\);/);
  assert.match(operator, /min-width:\s*0;/);

  const hint = css.match(/^\.keyboard-hint\s*\{([^}]*)\}/m)?.[1];
  assert.ok(hint);
  assert.match(hint, /left:\s*2\.2%;/);
  assert.match(hint, /right:\s*auto;/);
  assert.match(hint, /bottom:\s*1\.8%;/);

  const progress = css.match(/^\.progress-card\s*\{([^}]*)\}/m)?.[1];
  assert.ok(progress);
  assert.match(progress, /right:\s*1\.5%;/);
  assert.doesNotMatch(css, /\.operator-panel\s*\{\s*width:\s*42%;/);

  for (const [viewportWidth, viewportHeight] of [
    [1920, 1080],
    [1024, 768]
  ]) {
    const stageWidth = Math.min(viewportWidth, viewportHeight * 16 / 9);
    const operatorWidth = Math.min(340, Math.max(280, stageWidth * .20));
    const operatorLeft = stageWidth * .022;
    const schoolLeft = stageWidth * 522 / 1600;
    const progressWidth = Math.max(
      viewportWidth / viewportHeight <= 4 / 3 ? 260 : 300,
      stageWidth * .29
    );
    const progressLeft = stageWidth - stageWidth * .015 - progressWidth;
    const flagRight = stageWidth * 1087 / 1600;
    const towerRight = stageWidth * 1084 / 1600;
    assert.ok(
      operatorLeft + operatorWidth < schoolLeft,
      `${viewportWidth}x${viewportHeight} controls must remain left of the school`
    );
    assert.ok(
      operatorLeft + operatorWidth < progressLeft,
      `${viewportWidth}x${viewportHeight} controls must not overlap the progress card`
    );
    assert.ok(
      progressLeft - flagRight >= 12,
      `${viewportWidth}x${viewportHeight} flag/card clearance`
    );
    assert.ok(
      progressLeft > towerRight,
      `${viewportWidth}x${viewportHeight} progress card must clear the tower`
    );
  }
});

test("default-motion smoothing settles and stops while controls validate and filter shortcuts", async () => {
  await withAppHarness(
    { locationSearch: "?goal=100000&raised=51000" },
    async ({
      animationFrames,
      createdElements,
      document,
      documentListeners,
      elements,
      root,
      rootElements,
      startedAt
    }) => {
      assert.equal(animationFrames.length, 1);
      animationFrames.shift()(startedAt);
      assert.equal(animationFrames.length, 0);
      assert.equal(root.dataset.motion, "full");
      const settledWrites = createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );
      assert.ok(settledWrites > 0);
      assert.equal(
        createdElements.reduce(
          (total, element) => total + element.setAttributeCalls,
          0
        ),
        settledWrites
      );

      const keydown = documentListeners.get("keydown");
      let prevented = 0;
      const press = (key, overrides = {}) => keydown({
        key,
        target: root,
        preventDefault() {
          prevented += 1;
        },
        ...overrides
      });

      press("c");
      assert.equal(elements.get("#operator-panel").hidden, false);
      assert.equal(document.activeElement, elements.get("#raised-input"));

      for (const modifier of ["ctrlKey", "metaKey", "altKey"]) {
        press("c", { [modifier]: true });
        assert.equal(elements.get("#operator-panel").hidden, false);
      }

      press("c");
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);

      press("c");
      elements.get("#close-controls").listeners.get("click")({ detail: 1 });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);

      press("c");
      keydown({
        key: "Escape",
        target: elements.get("#raised-input"),
        preventDefault() {}
      });
      assert.equal(elements.get("#operator-panel").hidden, true);
      assert.equal(document.activeElement, root);
      assert.ok(prevented >= 3);

      const submit = elements.get("#operator-form").listeners.get("submit");
      const slider = elements.get("#raised-slider");
      const priorSliderValue = slider.value;
      const priorSliderMax = slider.max;
      for (const [raised, goal, focused] of [
        ["60000", "", elements.get("#goal-input")],
        ["", "100000", elements.get("#raised-input")],
        ["not-a-number", "100000", elements.get("#raised-input")],
        ["51000", "Infinity", elements.get("#goal-input")],
        ["-1", "100000", elements.get("#raised-input")],
        ["9007199254740992", "100000", elements.get("#raised-input")],
        ["51000", "0", elements.get("#goal-input")],
        ["51000", "7205759403792794", elements.get("#goal-input")]
      ]) {
        elements.get("#raised-input").value = raised;
        elements.get("#goal-input").value = goal;
        submit({ preventDefault() {} });
        assert.match(elements.get("#announcer").textContent, /^Enter /);
        assert.equal(document.activeElement, focused);
        assert.equal(slider.value, priorSliderValue);
        assert.equal(slider.max, priorSliderMax);
        assert.equal(animationFrames.length, 0);
      }

      for (const modifier of ["ctrlKey", "metaKey", "altKey"]) {
        press("ArrowRight", { [modifier]: true });
        assert.equal(elements.get("#raised-input").value, "51000");
        assert.equal(animationFrames.length, 0);
      }

      const writesBeforeSmoothing = createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );
      press("ArrowRight");
      assert.equal(elements.get("#raised-input").value, "52000");
      assert.equal(elements.get("#goal-input").value, "100000");
      assert.equal(animationFrames.length, 1);

      let smoothingFrames = 0;
      let frameNow = startedAt;
      while (animationFrames.length > 0 && smoothingFrames < 1000) {
        const callback = animationFrames.shift();
        frameNow += 16;
        callback(frameNow);
        smoothingFrames += 1;
      }

      assert.ok(smoothingFrames > 1);
      assert.equal(animationFrames.length, 0);
      assert.equal(rootElements.get("#raised-display").textContent, "$52,000");
      const settledSceneWrites = createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );
      assert.ok(settledSceneWrites > writesBeforeSmoothing);

      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
      assert.equal(
        createdElements.reduce(
          (total, element) => total + element.setAttributeCalls,
          0
        ),
        settledSceneWrites
      );
    }
  );
});

test("full-motion initial student target keeps the single scheduler alive until routes settle", async () => {
  await withAppHarness(
    { locationSearch: "?goal=100000&raised=125000&motion=full" },
    async ({
      animationFrames,
      createdElements,
      root,
      startedAt
    }) => {
      const students = createdElements.filter(
        (element) => element.attributes.get("class") === "student"
      );
      assert.equal(students.length, 36);
      assert.equal(animationFrames.length, 1);

      animationFrames.shift()(startedAt);
      assert.equal(root.dataset.studentPercent, "100.000");
      assert.ok(Number(root.dataset.visibleStudents) < 36);
      assert.equal(animationFrames.length, 1);

      let frameCount = 1;
      let now = startedAt;
      while (animationFrames.length > 0 && frameCount < 2000) {
        now += 16;
        animationFrames.shift()(now);
        frameCount += 1;
      }

      assert.ok(frameCount > 1);
      assert.equal(animationFrames.length, 0);
      assert.equal(root.dataset.visibleStudents, "36.000000");
      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
    }
  );
});

test("auto motion follows reduced-motion changes in both directions after idle", async () => {
  await withAppHarness(
    {
      locationSearch: "?goal=100000&raised=51000&motion=auto",
      mediaMatches: false
    },
    async ({
      animationFrames,
      dispatchMediaChange,
      mediaListenerCount,
      root,
      startedAt
    }) => {
      assert.equal(mediaListenerCount(), 1);
      animationFrames.shift()(startedAt);
      assert.equal(animationFrames.length, 0);
      assert.equal(root.dataset.motion, "full");

      dispatchMediaChange(true);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 1);
      animationFrames.shift()(startedAt + 16);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 0);

      dispatchMediaChange(false);
      assert.equal(root.dataset.motion, "full");
      assert.equal(animationFrames.length, 1);
      animationFrames.shift()(startedAt + 32);
      assert.equal(root.dataset.motion, "full");
      assert.equal(animationFrames.length, 0);
    }
  );
});

test("dynamic reduced motion stops an active celebration after its transition frame", async () => {
  await withAppHarness(
    {
      locationSearch: "?goal=100000&raised=99000&motion=auto",
      mediaMatches: false
    },
    async ({
      animationFrames,
      createdElements,
      dispatchMediaChange,
      documentListeners,
      elements,
      root,
      startedAt
    }) => {
      const sceneWrites = () => createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );

      animationFrames.shift()(startedAt);
      assert.equal(animationFrames.length, 0);
      assert.equal(root.dataset.motion, "full");

      documentListeners.get("keydown")({
        key: "ArrowRight",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#raised-input").value, "100000");
      assert.equal(animationFrames.length, 1);

      let now = startedAt;
      let crossingFrames = 0;
      while (root.dataset.goalAchieved !== "true" && crossingFrames < 1000) {
        const callback = animationFrames.shift();
        assert.ok(callback, "full-motion goal crossing must keep a frame queued");
        now += 16;
        callback(now);
        crossingFrames += 1;
      }
      assert.ok(crossingFrames > 1);
      assert.equal(root.dataset.goalAchieved, "true");
      assert.equal(root.dataset.motion, "full");
      assert.equal(animationFrames.length, 1);

      dispatchMediaChange(true);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 1);

      animationFrames.shift()(now + 16);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 0);
      const transitionWrites = sceneWrites();

      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
      assert.equal(sceneWrites(), transitionWrites);
    }
  );
});

test("explicit motion modes ignore operating-system preference changes while idle", async () => {
  for (const [motion, mediaMatches, expected] of [
    ["full", true, "full"],
    ["reduce", false, "reduce"]
  ]) {
    await withAppHarness(
      {
        locationSearch: `?goal=100000&raised=51000&motion=${motion}`,
        mediaMatches
      },
      async ({
        animationFrames,
        dispatchMediaChange,
        mediaListenerCount,
        root,
        startedAt
      }) => {
        assert.equal(mediaListenerCount(), 0);
        animationFrames.shift()(startedAt);
        assert.equal(root.dataset.motion, expected);
        assert.equal(animationFrames.length, 0);

        dispatchMediaChange(!mediaMatches);
        assert.equal(root.dataset.motion, expected);
        assert.equal(animationFrames.length, 0);
      }
    );
  }
});

test("explicit reduced motion settles each update without queued frames or later scene writes", async () => {
  await withAppHarness(
    { locationSearch: "?goal=100000&raised=51000&motion=reduce" },
    async ({
      animationFrames,
      createdElements,
      documentListeners,
      elements,
      root,
      rootElements,
      startedAt
    }) => {
      const sceneWrites = () => createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );

      assert.equal(animationFrames.length, 1);
      animationFrames.shift()(startedAt);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 0);
      const initialSettledWrites = sceneWrites();
      assert.ok(initialSettledWrites > 0);

      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
      assert.equal(sceneWrites(), initialSettledWrites);

      documentListeners.get("keydown")({
        key: "ArrowRight",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#raised-input").value, "52000");
      assert.equal(animationFrames.length, 1);

      animationFrames.shift()(startedAt + 16);
      assert.equal(animationFrames.length, 0);
      assert.equal(rootElements.get("#raised-display").textContent, "$52,000");
      const updateSettledWrites = sceneWrites();
      assert.ok(updateSettledWrites > initialSettledWrites);

      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
      assert.equal(sceneWrites(), updateSettledWrites);
    }
  );
});

test("explicit reduced motion goal crossing settles in one frame without queued celebration writes", async () => {
  await withAppHarness(
    { locationSearch: "?goal=100000&raised=99000&motion=reduce" },
    async ({
      animationFrames,
      createdElements,
      documentListeners,
      elements,
      root,
      rootElements,
      startedAt
    }) => {
      const sceneWrites = () => createdElements.reduce(
        (total, element) => total + element.setAttributeCalls,
        0
      );

      assert.equal(animationFrames.length, 1);
      animationFrames.shift()(startedAt);
      assert.equal(root.dataset.motion, "reduce");
      assert.equal(animationFrames.length, 0);
      const initialSettledWrites = sceneWrites();
      assert.ok(initialSettledWrites > 0);

      documentListeners.get("keydown")({
        key: "ArrowRight",
        target: root,
        preventDefault() {}
      });
      assert.equal(elements.get("#raised-input").value, "100000");
      assert.equal(animationFrames.length, 1);

      animationFrames.shift()(startedAt + 16);
      assert.equal(rootElements.get("#raised-display").textContent, "$100,000");
      assert.equal(
        rootElements.get("#percent-display").textContent,
        "100% — students arriving!"
      );
      const updateSettledWrites = sceneWrites();
      assert.ok(updateSettledWrites > initialSettledWrites);
      assert.equal(animationFrames.length, 0);

      await Promise.resolve();
      assert.equal(animationFrames.length, 0);
      assert.equal(sceneWrites(), updateSettledWrites);
    }
  );
});

test("production demo holds the endpoint until routes settle before restarting", async () => {
  const originalGlobals = new Map();
  const setGlobal = (name, value) => {
    originalGlobals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, {
      configurable: true,
      writable: true,
      value
    });
  };

  const elements = new Map([
    ["#announcer", new FakeElement()],
    ["#close-controls", new FakeElement()],
    ["#goal-input", new FakeInputElement()],
    ["#operator-form", new FakeElement()],
    ["#operator-panel", new FakeElement()],
    ["#raised-input", new FakeInputElement()],
    ["#raised-slider", new FakeInputElement()]
  ]);
  elements.get("#operator-panel").hidden = true;

  const root = new FakeElement();
  const rootElements = new Map([
    ["#goal-display", new FakeElement()],
    ["#meter-fill", new FakeElement()],
    ["#over-goal-message", new FakeElement()],
    ["#percent-display", new FakeElement()],
    ["#raised-display", new FakeElement()],
    ["#school-scene", new FakeElement()]
  ]);
  root.querySelector = (selector) => rootElements.get(selector);
  elements.set("#fundraiser", root);

  const animationFrames = [];
  const createdElements = [];
  const documentListeners = new Map();
  const startedAt = 1000;
  const document = {
    fullscreenElement: null,
    addEventListener(type, listener) {
      documentListeners.set(type, listener);
    },
    createElementNS(_namespace, name) {
      const element = new FakeElement(name);
      createdElements.push(element);
      return element;
    },
    querySelector(selector) {
      return elements.get(selector);
    }
  };

  setGlobal("document", document);
  setGlobal("HTMLInputElement", FakeInputElement);
  setGlobal("performance", { now: () => startedAt });
  setGlobal("requestAnimationFrame", (callback) => {
    animationFrames.push(callback);
  });
  setGlobal("window", {
    location: {
      search: "?demo=1&motion=full&goal=12345"
    },
    matchMedia: () => ({ matches: false })
  });

  try {
    const appUrl = new URL("../src/app.mjs", import.meta.url);
    appUrl.searchParams.set("test", String(Date.now()));
    await import(appUrl);

    const runNextFrame = (now) => {
      const callback = animationFrames.shift();
      assert.equal(typeof callback, "function");
      callback(now);
    };

    runNextFrame(startedAt + 89999);
    assert.ok(Number(elements.get("#raised-input").value) < 15431.25);
    assert.ok(Number(root.dataset.studentPercent) < 100);

    runNextFrame(startedAt + 90000);
    assert.equal(elements.get("#raised-input").value, "15431.25");
    assert.equal(root.dataset.studentPercent, "100.000");
    assert.ok(Number(root.dataset.visibleStudents) < 36);
    assert.ok(Number(root.dataset.busPercent) > 0);
    assert.ok(Number(root.dataset.busPercent) < 100);

    let now = startedAt + 90000;
    let settlementFrames = 0;
    while (
      (
        root.dataset.visibleStudents !== "36.000000"
        || root.dataset.busPercent !== "100.000"
      )
      && settlementFrames < 1000
    ) {
      now += 50;
      runNextFrame(now);
      settlementFrames += 1;
      assert.equal(elements.get("#raised-input").value, "15431.25");
    }

    assert.ok(settlementFrames > 1);
    assert.ok(settlementFrames < 1000, "demo endpoint routes must settle");
    assert.equal(root.dataset.visibleStudents, "36.000000");
    assert.equal(root.dataset.busPercent, "100.000");
    const students = createdElements.filter(
      (element) => element.attributes.get("class") === "student"
    );
    const bus = createdElements.find(
      (element) => element.attributes.get("class") === "school-bus"
    );
    const scene = createScene({ maxStudents: 36 });
    assert.equal(students.length, 36);
    for (let index = 0; index < students.length; index += 1) {
      const transform = students[index].attributes.get("transform");
      const match = transform.match(
        /^translate\(([-\d.]+) ([-\d.]+)\) scale\(/
      );
      assert.ok(match, transform);
      assert.equal(Number(match[1]), scene.students[index].targetX);
      assert.equal(Number(match[2]), scene.students[index].targetY);
    }
    assert.equal(bus.attributes.get("transform"), "translate(0 0)");
    assert.equal(Number(bus.attributes.get("opacity")), 1);

    runNextFrame(now + 50);
    assert.equal(
      elements.get("#raised-input").value,
      String(15431.25 * (50 / 90000))
    );
    assert.ok(Number(root.dataset.studentPercent) > 0);
    assert.ok(Number(root.dataset.studentPercent) < 100);

    const hostileGoal = 240429705269.63782;
    const endpoint = hostileGoal * 1.25;
    assert.equal(endpoint, 300537131587.04724);
    const submit = elements.get("#operator-form").listeners.get("submit");
    elements.get("#goal-input").value = String(hostileGoal);
    elements.get("#raised-input").value = "0";
    submit({ preventDefault() {} });

    const slider = elements.get("#raised-slider");
    assert.equal(slider.max, String(endpoint));
    slider.value = slider.max;
    slider.listeners.get("input")();
    assert.equal(elements.get("#raised-input").value, String(endpoint));
    submit({ preventDefault() {} });
    assert.equal(elements.get("#raised-input").value, String(endpoint));
    assert.equal(slider.value, String(endpoint));
    assert.equal(
      slider.attributes.get("aria-valuetext"),
      `$300,537,131,587.0472 raised (125.0% of goal)`
    );

    elements.get("#goal-input").value = "0.01";
    elements.get("#raised-input").value = "0.01";
    submit({ preventDefault() {} });
    assert.equal(
      elements.get("#announcer").textContent,
      "$0.01 raised toward a goal of $0.01."
    );

    elements.get("#raised-input").value = "0.0125";
    submit({ preventDefault() {} });
    assert.equal(
      elements.get("#announcer").textContent,
      "$0.0125 raised toward a goal of $0.01."
    );

    for (const nearIntegerFraction of [
      "0.9999999999999999",
      "100000000000000.02"
    ]) {
      elements.get("#goal-input").value = nearIntegerFraction;
      elements.get("#raised-input").value = nearIntegerFraction;
      submit({ preventDefault() {} });
      assert.equal(elements.get("#raised-input").value, nearIntegerFraction);
      assert.equal(elements.get("#goal-input").value, nearIntegerFraction);

      submit({ preventDefault() {} });
      assert.equal(elements.get("#raised-input").value, nearIntegerFraction);
      assert.equal(elements.get("#goal-input").value, nearIntegerFraction);
      assert.equal(elements.get("#raised-slider").value, nearIntegerFraction);
    }

    elements.get("#goal-input").value = "100000";
    elements.get("#raised-input").value = "51000";
    submit({ preventDefault() {} });
    documentListeners.get("keydown")({
      key: "ArrowRight",
      shiftKey: true,
      target: root,
      preventDefault() {}
    });
    assert.equal(elements.get("#raised-input").value, "56000");
    assert.equal(elements.get("#raised-slider").value, "56000");
  } finally {
    for (const [name, descriptor] of originalGlobals) {
      if (descriptor) {
        Object.defineProperty(globalThis, name, descriptor);
      } else {
        delete globalThis[name];
      }
    }
  }
});
