import test from "node:test";
import assert from "node:assert/strict";

class FakeElement {
  constructor() {
    this.attributes = new Map();
    this.children = [];
    this.dataset = {};
    this.listeners = new Map();
    this.textContent = "";
    this.value = "";
    this.max = "";
  }

  addEventListener(type, listener) {
    const listeners = this.listeners.get(type) ?? [];
    listeners.push(listener);
    this.listeners.set(type, listeners);
  }

  append(...children) {
    this.children.push(...children);
  }

  contains(candidate) {
    return candidate === this
      || this.children.some((child) => child.contains(candidate));
  }

  async dispatch(type, event = {}) {
    for (const listener of this.listeners.get(type) ?? []) {
      await listener({ preventDefault() {}, target: this, ...event });
    }
  }

  focus() {}

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
  }
}

class FakeEventSource {
  static instances = [];

  constructor(url) {
    this.url = url;
    this.listeners = new Map();
    FakeEventSource.instances.push(this);
  }

  addEventListener(type, listener) {
    this.listeners.set(type, listener);
  }

  dispatch(type, data = "") {
    this.listeners.get(type)?.({ data });
  }
}

async function withControlHarness(run, { fetchImplementation } = {}) {
  const originalGlobals = new Map();
  const setGlobal = (name, value) => {
    originalGlobals.set(name, Object.getOwnPropertyDescriptor(globalThis, name));
    Object.defineProperty(globalThis, name, {
      configurable: true,
      writable: true,
      value
    });
  };

  const form = new FakeElement();
  const raisedInput = new FakeElement();
  const goalInput = new FakeElement();
  const raisedSlider = new FakeElement();
  const submitButton = new FakeElement();
  form.append(raisedInput, goalInput, raisedSlider, submitButton);
  const addSchoolButton = new FakeElement();
  addSchoolButton.dataset.action = "school.add";
  addSchoolButton.textContent = "Drop next school";
  const removeSchoolButton = new FakeElement();
  removeSchoolButton.dataset.action = "school.remove";
  removeSchoolButton.textContent = "Remove last school";
  const actionButtons = [addSchoolButton, removeSchoolButton];

  const elements = new Map([
    ["#progress-form", form],
    ["#raised-input", raisedInput],
    ["#goal-input", goalInput],
    ["#raised-slider", raisedSlider],
    ["#server-indicator", new FakeElement()],
    ["#display-indicator", new FakeElement()],
    ["#operation-status", new FakeElement()],
    ["#demo-toggle", new FakeElement()],
    ["#night-toggle", new FakeElement()],
    ["#clapping-toggle", new FakeElement()],
    ["#thank-you-toggle", new FakeElement()],
    ["#continuous-toggle", new FakeElement()]
  ]);
  const document = {
    querySelector(selector) {
      return elements.get(selector);
    },
    querySelectorAll(selector) {
      return selector === "[data-action]" ? actionButtons : [];
    }
  };

  FakeEventSource.instances = [];
  setGlobal("document", document);
  setGlobal("EventSource", FakeEventSource);
  setGlobal("fetch", fetchImplementation ?? (async () => {
    throw new Error("Unexpected fetch");
  }));

  try {
    const controlUrl = new URL("../src/control.mjs", import.meta.url);
    controlUrl.searchParams.set("test", `${Date.now()}-${Math.random()}`);
    await import(controlUrl);
    await run({
      elements,
      events: FakeEventSource.instances[0],
      form,
      goalInput,
      raisedInput,
      raisedSlider,
      actionButtons
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

function sendState(events, state) {
  events.dispatch("state", JSON.stringify({
    demoActive: false,
    nightMode: false,
    studentsClapping: false,
    thankYouVisible: true,
    continuousFireworks: false,
    ...state
  }));
}

test("focus alone does not block authoritative progress updates", async () => {
  await withControlHarness(async ({ events, raisedInput, goalInput }) => {
    sendState(events, { raised: 50, goal: 100 });
    await raisedInput.dispatch("focus");
    sendState(events, { raised: 75, goal: 150 });

    assert.equal(raisedInput.value, "75");
    assert.equal(goalInput.value, "150");
  });
});

test("dirty progress drafts survive SSE and reconcile on leaving the form", async () => {
  await withControlHarness(async ({
    events,
    form,
    raisedInput,
    goalInput,
    raisedSlider
  }) => {
    sendState(events, { raised: 50, goal: 100 });
    raisedInput.value = "80";
    await raisedInput.dispatch("input");
    sendState(events, { raised: 60, goal: 120 });

    assert.equal(raisedInput.value, "80");
    assert.equal(goalInput.value, "100");

    await form.dispatch("focusout", { relatedTarget: null });
    assert.equal(raisedInput.value, "60");
    assert.equal(goalInput.value, "120");
    assert.equal(raisedSlider.max, "162");
    assert.equal(raisedSlider.value, "60");
  });
});

test("valid goal drafts update the 135% slider range without invalid NaN state", async () => {
  await withControlHarness(async ({
    events,
    goalInput,
    raisedSlider
  }) => {
    sendState(events, { raised: 120, goal: 100 });

    goalInput.value = "200.5";
    await goalInput.dispatch("input");
    assert.equal(raisedSlider.max, "270.675");
    assert.equal(raisedSlider.value, "120");
    assert.match(raisedSlider.getAttribute("aria-valuetext"), /59\.9% of goal/);

    const validRange = {
      max: raisedSlider.max,
      value: raisedSlider.value,
      aria: raisedSlider.getAttribute("aria-valuetext")
    };
    goalInput.value = "not-a-number";
    await goalInput.dispatch("input");

    assert.deepEqual({
      max: raisedSlider.max,
      value: raisedSlider.value,
      aria: raisedSlider.getAttribute("aria-valuetext")
    }, validRange);
    assert.doesNotMatch(raisedSlider.getAttribute("aria-valuetext"), /NaN/);
  });
});

test("school dashboard buttons send add and remove actions", async () => {
  const requests = [];
  await withControlHarness(
    async ({ actionButtons, elements }) => {
      for (const button of actionButtons) await button.dispatch("click");
      assert.deepEqual(
        requests.map((request) => JSON.parse(request.options.body)),
        [{ type: "school.add" }, { type: "school.remove" }]
      );
      assert.equal(
        elements.get("#operation-status").textContent,
        "Remove last school sent to the display."
      );
    },
    {
      fetchImplementation: async (url, options) => {
        requests.push({ url, options });
        return {
          ok: true,
          async json() {
            return { accepted: true };
          }
        };
      }
    }
  );
});
