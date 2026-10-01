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

async function withControlHarness(run, {
  fetchImplementation,
  includeDisplayPreview = false,
  windowHref = "http://127.0.0.1:8081/control.html"
} = {}) {
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
  const overrideForm = new FakeElement();
  const overrideRaisedInput = new FakeElement();
  const overrideEditToggle = new FakeElement();
  const applyOverrideButton = new FakeElement();
  const wideScreenToggle = new FakeElement();
  overrideForm.append(
    overrideRaisedInput,
    overrideEditToggle,
    applyOverrideButton
  );
  const buildSummaryForm = new FakeElement();
  const seattleSchoolsInput = new FakeElement();
  const operationCostInput = new FakeElement();
  const buildSummarySubmitButton = new FakeElement();
  buildSummaryForm.append(
    seattleSchoolsInput,
    operationCostInput,
    buildSummarySubmitButton
  );
  const addSchoolButton = new FakeElement();
  addSchoolButton.dataset.action = "school.add";
  addSchoolButton.textContent = "Drop next school";
  const removeSchoolButton = new FakeElement();
  removeSchoolButton.dataset.action = "school.remove";
  removeSchoolButton.textContent = "Remove last school";
  const actionButtons = [addSchoolButton, removeSchoolButton];

  const elements = new Map([
    ["#progress-form", form],
    ["#override-form", overrideForm],
    ["#build-summary-form", buildSummaryForm],
    ["#seattle-schools-input", seattleSchoolsInput],
    ["#operation-cost-input", operationCostInput],
    ["#raised-input", raisedInput],
    ["#goal-input", goalInput],
    ["#override-raised-input", overrideRaisedInput],
    ["#raised-slider", raisedSlider],
    ["#server-indicator", new FakeElement()],
    ["#display-indicator", new FakeElement()],
    ["#operation-status", new FakeElement()],
    ["#demo-toggle", new FakeElement()],
    ["#night-toggle", new FakeElement()],
    ["#clapping-toggle", new FakeElement()],
    ["#thank-you-toggle", new FakeElement()],
    ["#continuous-toggle", new FakeElement()]
    ,
    ["#edit-override-toggle", overrideEditToggle],
    ["#apply-override-button", applyOverrideButton]
    ,
    ["#wide-screen-toggle", wideScreenToggle]
  ]);
  if (includeDisplayPreview) {
    elements.set("#display-preview", new FakeElement());
  }
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
  if (includeDisplayPreview) {
    setGlobal("window", { location: { href: windowHref } });
  }

  try {
    const controlUrl = new URL("../src/control.mjs", import.meta.url);
    controlUrl.searchParams.set("test", `${Date.now()}-${Math.random()}`);
    await import(controlUrl);
    await run({
      elements,
      events: FakeEventSource.instances[0],
      form,
      goalInput,
      overrideForm,
      overrideRaisedInput,
      overrideEditToggle,
      applyOverrideButton,
      wideScreenToggle,
      raisedInput,
      raisedSlider,
      actionButtons,
      buildSummaryForm,
      operationCostInput,
      seattleSchoolsInput
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

test("applying an override preserves its draft and edit mode when focusout has no target", async () => {
  const requests = [];
  await withControlHarness(
    async ({
      events,
      overrideForm,
      overrideRaisedInput,
      overrideEditToggle,
      applyOverrideButton
    }) => {
      sendState(events, {
        revision: 1,
        raised: 600,
        goal: 1000,
        overrideRaised: 600
      });
      overrideEditToggle.checked = true;
      await overrideEditToggle.dispatch("change");
      overrideRaisedInput.value = "900";
      await overrideRaisedInput.dispatch("input");

      await overrideForm.dispatch("focusout", { relatedTarget: null });
      await overrideForm.dispatch("submit");

      assert.deepEqual(JSON.parse(requests[0].options.body), {
        overrideRaised: 900
      });
      assert.equal(overrideRaisedInput.value, "900");
      assert.equal(overrideEditToggle.checked, true);
      assert.equal(overrideRaisedInput.readOnly, false);
      assert.equal(applyOverrideButton.disabled, false);
    },
    {
      fetchImplementation: async (url, options) => {
        requests.push({ url, options });
        return {
          ok: true,
          async json() {
            return {
              revision: 2,
              raised: 600,
              goal: 1000,
              overrideRaised: 900
            };
          }
        };
      }
    }
  );
});

test("focus alone does not block authoritative progress updates", async () => {
  await withControlHarness(async ({ events, raisedInput, goalInput }) => {
    assert.equal(events.url, "/events?role=control");
    sendState(events, { raised: 50, goal: 100 });
    await raisedInput.dispatch("focus");
    sendState(events, { raised: 75, goal: 150 });

    assert.equal(raisedInput.value, "75");
    assert.equal(goalInput.value, "150");
  });

  test("wide-screen checkbox patches explicit state and follows authoritative updates", async () => {
    const requests = [];
    await withControlHarness(
      async ({ events, wideScreenToggle }) => {
        sendState(events, {
          revision: 1,
          raised: 0,
          goal: 100000,
          wideScreen: false
        });
        assert.equal(wideScreenToggle.checked, false);

        wideScreenToggle.checked = true;
        await wideScreenToggle.dispatch("change");
        await new Promise((resolve) => setImmediate(resolve));

        assert.deepEqual(JSON.parse(requests[0].options.body), {
          wideScreen: true
        });
        assert.equal(wideScreenToggle.checked, true);

        sendState(events, {
          revision: 3,
          wideScreen: false
        });
        assert.equal(wideScreenToggle.checked, false);
      },
      {
        fetchImplementation: async (url, options) => {
          requests.push({ url, options });
          assert.equal(url, "/api/state");
          return {
            ok: true,
            async json() {
              return {
                revision: 2,
                raised: 0,
                goal: 100000,
                wideScreen: true
              };
            }
          };
        }
      }
    );
  });
});

test("failed commands refetch and reconcile authoritative dashboard state", async () => {
  const requests = [];
  await withControlHarness(
    async ({ events, elements, raisedInput, goalInput }) => {
      sendState(events, {
        revision: 1,
        raised: 50,
        goal: 100,
        demoActive: false
      });
      await elements.get("#demo-toggle").dispatch("click");
      await new Promise((resolve) => setImmediate(resolve));

      assert.deepEqual(requests.map(({ url }) => url), [
        "/api/commands",
        "/api/state"
      ]);
      assert.deepEqual(JSON.parse(requests[0].options.body), {
        type: "state.toggle",
        field: "demoActive"
      });
      assert.equal(raisedInput.value, "75");
      assert.equal(goalInput.value, "150");
      assert.equal(
        elements.get("#demo-toggle").getAttribute("aria-pressed"),
        "false"
      );
      assert.equal(
        elements.get("#operation-status").textContent,
        "command rejected"
      );
    },
    {
      fetchImplementation: async (url, options) => {
        requests.push({ url, options });
        if (url === "/api/commands") {
          return {
            ok: false,
            status: 409,
            async json() {
              return { error: "command rejected" };
            }
          };
        }
        return {
          ok: true,
          async json() {
            return {
              revision: 2,
              raised: 75,
              goal: 150,
              demoActive: false,
              nightMode: false,
              studentsClapping: false,
              thankYouVisible: true,
              continuousFireworks: false
            };
          }
        };
      }
    }
  );
});

test("dashboard ignores mutation responses older than the latest authoritative revision", async () => {
  const pending = [];
  await withControlHarness(
    async ({ events, elements }) => {
      sendState(events, {
        revision: 0,
        raised: 50,
        goal: 100,
        nightMode: false
      });

      await elements.get("#night-toggle").dispatch("click");
      await elements.get("#night-toggle").dispatch("click");
      assert.equal(pending.length, 2);

      pending[1].resolve({
        ok: true,
        async json() {
          return {
            revision: 2,
            raised: 50,
            goal: 100,
            nightMode: false
          };
        }
      });
      await new Promise((resolve) => setImmediate(resolve));
      assert.equal(
        elements.get("#night-toggle").getAttribute("aria-pressed"),
        "false"
      );

      pending[0].resolve({
        ok: true,
        async json() {
          return {
            revision: 1,
            raised: 50,
            goal: 100,
            nightMode: true
          };
        }
      });
      await new Promise((resolve) => setImmediate(resolve));
      assert.equal(
        elements.get("#night-toggle").getAttribute("aria-pressed"),
        "false"
      );

      sendState(events, {
        revision: 2,
        raised: 50,
        goal: 100,
        nightMode: true
      });
      assert.equal(
        elements.get("#night-toggle").getAttribute("aria-pressed"),
        "true"
      );
    },
    {
      fetchImplementation(url) {
        assert.equal(url, "/api/commands");
        return new Promise((resolve) => pending.push({ resolve }));
      }
    }
  );
});

test("dashboard preview uses the configured display port and exact preview query", async () => {
  await withControlHarness(
    async ({ elements }) => {
      await new Promise((resolve) => setImmediate(resolve));
      assert.equal(
        elements.get("#display-preview").src,
        "http://127.0.0.1:9123/?client=preview"
      );
    },
    {
      includeDisplayPreview: true,
      windowHref: "http://127.0.0.1:8123/control.html?ignored=true#fragment",
      fetchImplementation: async (url) => {
        assert.equal(url, "/api/runtime");
        return {
          ok: true,
          async json() {
            return { displayPort: 9123 };
          }
        };
      }
    }
  );
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
    assert.equal(raisedSlider.max, "240");
    assert.equal(raisedSlider.value, "60");
  });
});

test("stale progress submit responses reconcile controls to newer SSE authority", async () => {
  let resolveSubmit;
  await withControlHarness(
    async ({ events, form, raisedInput, goalInput }) => {
      sendState(events, { revision: 1, raised: 50, goal: 100 });
      raisedInput.value = "80";
      goalInput.value = "160";
      await raisedInput.dispatch("input");

      const submit = form.dispatch("submit");
      sendState(events, { revision: 3, raised: 90, goal: 180 });
      resolveSubmit({
        ok: true,
        async json() {
          return { revision: 2, raised: 80, goal: 160 };
        }
      });
      await submit;

      assert.equal(raisedInput.value, "90");
      assert.equal(goalInput.value, "180");
    },
    {
      fetchImplementation(url) {
        assert.equal(url, "/api/state");
        return new Promise((resolve) => {
          resolveSubmit = resolve;
        });
      }
    }
  );
});

test("failed progress submits refetch and reconcile controls to authority", async () => {
  const requests = [];
  await withControlHarness(
    async ({ events, form, raisedInput, goalInput }) => {
      sendState(events, { revision: 1, raised: 50, goal: 100 });
      raisedInput.value = "80";
      goalInput.value = "160";
      await raisedInput.dispatch("input");
      await form.dispatch("submit");

      assert.deepEqual(requests, ["/api/state", "/api/state"]);
      assert.equal(raisedInput.value, "70");
      assert.equal(goalInput.value, "140");
    },
    {
      fetchImplementation: async (url) => {
        requests.push(url);
        if (requests.length === 1) {
          return {
            ok: false,
            status: 409,
            async json() {
              return { error: "progress rejected" };
            }
          };
        }
        return {
          ok: true,
          async json() {
            return { revision: 2, raised: 70, goal: 140 };
          }
        };
      }
    }
  );
});

test("failed progress submit and refetch reconcile held newer SSE authority", async () => {
  let resolveSubmit;
  const requests = [];
  await withControlHarness(
    async ({
      events,
      elements,
      form,
      raisedInput,
      goalInput,
      operationCostInput,
      seattleSchoolsInput
    }) => {
      sendState(events, {
        revision: 1,
        raised: 50,
        goal: 100,
        seattleSchools: 47,
        operationCost: 1000
      });
      raisedInput.value = "80";
      goalInput.value = "160";
      await raisedInput.dispatch("input");
      seattleSchoolsInput.value = "48";
      operationCostInput.value = "1200";
      await seattleSchoolsInput.dispatch("input");

      const submit = form.dispatch("submit");
      sendState(events, {
        revision: 3,
        raised: 90,
        goal: 180,
        seattleSchools: 50,
        operationCost: 1500
      });
      resolveSubmit({
        ok: false,
        status: 409,
        async json() {
          return { error: "progress rejected" };
        }
      });
      await submit;

      assert.deepEqual(requests, ["/api/state", "/api/state"]);
      assert.equal(raisedInput.value, "90");
      assert.equal(goalInput.value, "180");
      assert.equal(seattleSchoolsInput.value, "48");
      assert.equal(operationCostInput.value, "1200");
      assert.equal(
        elements.get("#operation-status").textContent,
        "progress rejected"
      );
      assert.equal(elements.get("#operation-status").dataset.error, "true");
    },
    {
      fetchImplementation(url) {
        requests.push(url);
        if (requests.length === 1) {
          return new Promise((resolve) => {
            resolveSubmit = resolve;
          });
        }
        throw new Error("recovery offline");
      }
    }
  );
});

test("stale build summary submit responses reconcile controls to newer SSE authority", async () => {
  let resolveSubmit;
  await withControlHarness(
    async ({
      events,
      buildSummaryForm,
      operationCostInput,
      seattleSchoolsInput
    }) => {
      sendState(events, {
        revision: 1,
        raised: 50,
        goal: 100,
        seattleSchools: 47,
        operationCost: 1000
      });
      seattleSchoolsInput.value = "48";
      operationCostInput.value = "1200";
      await seattleSchoolsInput.dispatch("input");

      const submit = buildSummaryForm.dispatch("submit");
      sendState(events, {
        revision: 3,
        raised: 50,
        goal: 100,
        seattleSchools: 50,
        operationCost: 1500
      });
      resolveSubmit({
        ok: true,
        async json() {
          return {
            revision: 2,
            seattleSchools: 48,
            operationCost: 1200
          };
        }
      });
      await submit;

      assert.equal(seattleSchoolsInput.value, "50");
      assert.equal(operationCostInput.value, "1500");
    },
    {
      fetchImplementation(url) {
        assert.equal(url, "/api/state");
        return new Promise((resolve) => {
          resolveSubmit = resolve;
        });
      }
    }
  );
});

test("failed build summary submits refetch and reconcile controls to authority", async () => {
  const requests = [];
  await withControlHarness(
    async ({
      events,
      buildSummaryForm,
      operationCostInput,
      seattleSchoolsInput
    }) => {
      sendState(events, {
        revision: 1,
        raised: 50,
        goal: 100,
        seattleSchools: 47,
        operationCost: 1000
      });
      seattleSchoolsInput.value = "48";
      operationCostInput.value = "1200";
      await operationCostInput.dispatch("input");
      await buildSummaryForm.dispatch("submit");

      assert.deepEqual(requests, ["/api/state", "/api/state"]);
      assert.equal(seattleSchoolsInput.value, "51");
      assert.equal(operationCostInput.value, "1750");
    },
    {
      fetchImplementation: async (url) => {
        requests.push(url);
        if (requests.length === 1) {
          return {
            ok: false,
            status: 409,
            async json() {
              return { error: "summary rejected" };
            }
          };
        }
        return {
          ok: true,
          async json() {
            return {
              revision: 2,
              seattleSchools: 51,
              operationCost: 1750
            };
          }
        };
      }
    }
  );
});

test("failed build summary submit and refetch reconcile held newer SSE authority", async () => {
  let resolveSubmit;
  const requests = [];
  await withControlHarness(
    async ({
      events,
      elements,
      buildSummaryForm,
      raisedInput,
      goalInput,
      operationCostInput,
      seattleSchoolsInput
    }) => {
      sendState(events, {
        revision: 1,
        raised: 50,
        goal: 100,
        seattleSchools: 47,
        operationCost: 1000
      });
      raisedInput.value = "80";
      goalInput.value = "160";
      await raisedInput.dispatch("input");
      seattleSchoolsInput.value = "48";
      operationCostInput.value = "1200";
      await operationCostInput.dispatch("input");

      const submit = buildSummaryForm.dispatch("submit");
      sendState(events, {
        revision: 3,
        raised: 90,
        goal: 180,
        seattleSchools: 50,
        operationCost: 1500
      });
      resolveSubmit({
        ok: false,
        status: 409,
        async json() {
          return { error: "summary rejected" };
        }
      });
      await submit;

      assert.deepEqual(requests, ["/api/state", "/api/state"]);
      assert.equal(seattleSchoolsInput.value, "50");
      assert.equal(operationCostInput.value, "1500");
      assert.equal(raisedInput.value, "80");
      assert.equal(goalInput.value, "160");
      assert.equal(
        elements.get("#operation-status").textContent,
        "summary rejected"
      );
      assert.equal(elements.get("#operation-status").dataset.error, "true");
    },
    {
      fetchImplementation(url) {
        requests.push(url);
        if (requests.length === 1) {
          return new Promise((resolve) => {
            resolveSubmit = resolve;
          });
        }
        throw new Error("recovery offline");
      }
    }
  );
});

test("valid goal drafts update the 200% slider range without invalid NaN state", async () => {
  await withControlHarness(async ({
    events,
    goalInput,
    raisedSlider
  }) => {
    sendState(events, { raised: 120, goal: 100 });

    goalInput.value = "200.5";
    await goalInput.dispatch("input");
    assert.equal(raisedSlider.max, "401");
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
