import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { EventEmitter } from "node:events";
import { readFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  createServer,
  createSharedState,
  getPorts,
  MAX_SSE_CLIENTS_PER_ROLE
} from "../server.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

test("wide-screen mode defaults off and accepts boolean state patches", async () => {
  await withServers(async ({ controlBase }) => {
    let response = await fetch(`${controlBase}/api/state`);
    let state = await response.json();
    assert.equal(state.wideScreen, false);

    response = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ wideScreen: true })
    });
    assert.equal(response.status, 200);
    state = await response.json();
    assert.equal(state.wideScreen, true);

    response = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ wideScreen: "yes" })
    });
    assert.equal(response.status, 400);
  });
});

function adjacentFloat(value, direction) {
  const storage = new ArrayBuffer(8);
  const view = new DataView(storage);
  view.setFloat64(0, value);
  let bits = view.getBigUint64(0);
  bits += direction > 0 ? 1n : -1n;
  view.setBigUint64(0, bits);
  return view.getFloat64(0);
}

function listen(server) {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
}

function close(server) {
  return new Promise((resolve) => server.close(resolve));
}

async function withServers(run) {
  const sharedState = createSharedState();
  const runtime = { displayPort: 8080, controlPort: 8081 };
  const displayServer = createServer(root, {
    surface: "display",
    sharedState,
    runtime
  });
  const controlServer = createServer(root, {
    surface: "control",
    sharedState,
    runtime
  });
  await Promise.all([listen(displayServer), listen(controlServer)]);
  const displayPort = displayServer.address().port;
  const controlPort = controlServer.address().port;
  runtime.displayPort = displayPort;
  runtime.controlPort = controlPort;
  try {
    await run({
      displayBase: `http://127.0.0.1:${displayPort}`,
      controlBase: `http://127.0.0.1:${controlPort}`,
      displayPort,
      controlPort,
      sharedState
    });
  } finally {
    sharedState.closeClients();
    await Promise.all([close(displayServer), close(controlServer)]);
  }
}

function rawRequest(port, requestPath, method = "GET", headers = {}, body = "") {
  return new Promise((resolve, reject) => {
    const request = http.request({
      hostname: "127.0.0.1",
      port,
      path: requestPath,
      method,
      headers
    }, (response) => {
      const chunks = [];
      response.on("data", (chunk) => chunks.push(chunk));
      response.on("end", () => resolve({
        status: response.statusCode,
        headers: response.headers,
        body: Buffer.concat(chunks).toString("utf8")
      }));
    });
    request.on("error", reject);
    request.end(body);
  });
}

function openEventStream(port, role) {
  return new Promise((resolve, reject) => {
    const request = http.get({
      hostname: "127.0.0.1",
      port,
      path: role ? `/events?role=${role}` : "/events"
    });
    request.on("response", (response) => {
      response.setEncoding("utf8");
      let buffer = "";
      const waiters = [];
      response.on("data", (chunk) => {
        buffer += chunk;
        for (let index = waiters.length - 1; index >= 0; index -= 1) {
          const waiter = waiters[index];
          if (buffer.includes(`event: ${waiter.event}\n`)) {
            waiters.splice(index, 1);
            waiter.resolve(buffer);
          }
        }
      });
      resolve({
        request,
        response,
        waitFor(event) {
          if (buffer.includes(`event: ${event}\n`)) return Promise.resolve(buffer);
          return new Promise((resolveEvent, rejectEvent) => {
            const timer = setTimeout(() => {
              rejectEvent(new Error(`Timed out waiting for ${event}: ${buffer}`));
            }, 2000);
            waiters.push({
              event,
              resolve(value) {
                clearTimeout(timer);
                resolveEvent(value);
              }
            });
          });
        },
        waitForMatch(pattern) {
          if (pattern.test(buffer)) return Promise.resolve(buffer);
          return new Promise((resolveEvent, rejectEvent) => {
            const timer = setTimeout(() => {
              rejectEvent(new Error(`Timed out waiting for ${pattern}: ${buffer}`));
            }, 2000);
            const onData = () => {
              if (!pattern.test(buffer)) return;
              clearTimeout(timer);
              response.off("data", onData);
              resolveEvent(buffer);
            };
            response.on("data", onData);
          });
        },
        eventCount(event) {
          return buffer.split(`event: ${event}\n`).length - 1;
        }
      });
    });
    request.on("error", reject);
  });
}

test("serves isolated display and control surfaces with security headers", async () => {
  await withServers(async ({ displayBase, controlBase, displayPort }) => {
    const display = await fetch(`${displayBase}/`);
    assert.equal(display.status, 200);
    assert.match(display.headers.get("content-security-policy"), /connect-src 'self'/);
    assert.match(
      display.headers.get("content-security-policy"),
      new RegExp(
        `frame-ancestors http://127\\.0\\.0\\.1:${controlBase.split(":").at(-1)} `
          + `http://localhost:${controlBase.split(":").at(-1)}`
      )
    );
    assert.doesNotMatch(display.headers.get("content-security-policy"), /:\*/);
    assert.equal(display.headers.get("x-frame-options"), null);
    const displayHtml = await display.text();
    assert.match(displayHtml, /id="fundraiser"/);
    assert.match(displayHtml, /data-bus-percent="0"/);
    assert.match(displayHtml, /data-distant-schools="0"/);
    assert.match(displayHtml, /<kbd>S<\/kbd> drop next school/);
    assert.match(displayHtml, /<kbd>X<\/kbd> remove last school/);
    assert.doesNotMatch(displayHtml, /operator-panel|progress-form|<kbd>C<\/kbd>/);

    const control = await fetch(`${controlBase}/`);
    assert.equal(control.status, 200);
    assert.match(
      control.headers.get("content-security-policy"),
      new RegExp(
        `frame-src http://127\\.0\\.0\\.1:${displayBase.split(":").at(-1)} `
          + `http://localhost:${displayBase.split(":").at(-1)}`
      )
    );
    assert.doesNotMatch(control.headers.get("content-security-policy"), /:\*/);
    assert.match(control.headers.get("content-security-policy"), /frame-ancestors 'none'/);
    assert.equal(control.headers.get("x-frame-options"), "DENY");
    const controlHtml = await control.text();
    assert.match(controlHtml, /id="progress-form"/);
    assert.match(controlHtml, /data-action="firework\.launch"/);
    assert.match(controlHtml, /data-action="school\.add"/);
    assert.match(controlHtml, /data-action="school\.remove"/);
    assert.match(controlHtml, /max="200000"/);
    assert.match(controlHtml, /max="4503599627370495\.5"/);

    const runtime = await fetch(`${controlBase}/api/runtime`);
    assert.equal(runtime.status, 200);
    assert.deepEqual(await runtime.json(), { displayPort });

    assert.equal((await fetch(`${displayBase}/control.html`)).status, 404);
    assert.equal((await fetch(`${displayBase}/src/control.mjs`)).status, 404);
    assert.equal((await fetch(`${controlBase}/index.html`)).status, 404);
    assert.equal((await fetch(`${controlBase}/src/app.mjs`)).status, 404);
    assert.equal((await fetch(`${displayBase}/styles.css`)).status, 200);
    assert.equal((await fetch(`${controlBase}/control.css`)).status, 200);
    assert.equal((await fetch(`${displayBase}/favicon.ico`)).headers.get("content-type"), "image/png");
    assert.equal((await fetch(`${controlBase}/favicon.ico`)).headers.get("content-type"), "image/png");
    for (const asset of ["Boy.png", "Girl.png"]) {
      const displayAsset = await fetch(`${displayBase}/${asset}`);
      assert.equal(displayAsset.status, 200);
      assert.equal(displayAsset.headers.get("content-type"), "image/png");
      assert.ok(Number(displayAsset.headers.get("content-length")) > 0);
      assert.equal((await fetch(`${controlBase}/${asset}`)).status, 404);
    }

    const harness = await fetch(`${displayBase}/tests/harness.html`);
    assert.equal(harness.status, 200);
    const harnessText = await harness.text();
    const script = harnessText.match(/<script type="module">([\s\S]*?)<\/script>/)?.[1];
    assert.ok(script);
    const scriptHash = createHash("sha256")
      .update(script.replaceAll("\r\n", "\n"))
      .digest("base64");
    assert.match(
      harness.headers.get("content-security-policy"),
      new RegExp(`script-src 'self' 'sha256-${scriptHash.replaceAll("+", "\\+")}'`)
    );
    assert.equal(
      harnessText,
      await readFile(path.join(root, "tests", "harness.html"), "utf8")
    );
  });
});

test("shares strict state mutations across listeners", async () => {
  await withServers(async ({ displayBase, controlBase, controlPort }) => {
    const update = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: {
        "Content-Type": "application/json",
        Origin: controlBase
      },
      body: JSON.stringify({
        raised: 42000,
        goal: 120000,
        nightMode: true
      })
    });
    assert.equal(update.status, 200);

    const displayState = await fetch(`${displayBase}/api/state`).then((response) => response.json());
    assert.equal(displayState.raised, 42000);
    assert.equal(displayState.goal, 120000);
    assert.equal(displayState.nightMode, true);
    assert.equal(displayState.revision, 1);
    assert.deepEqual(displayState.distantSchools, []);

    const unknown = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: 1, unexpected: true })
    });
    assert.equal(unknown.status, 400);

    const invalidAtomic = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: 1, goal: 0 })
    });
    assert.equal(invalidAtomic.status, 400);
    const unchanged = await fetch(`${displayBase}/api/state`).then((response) => response.json());
    assert.equal(unchanged.raised, 42000);
    assert.equal(unchanged.goal, 120000);

    const wrongOrigin = await rawRequest(controlPort, "/api/state", "PATCH", {
      "Content-Type": "application/json",
      Origin: "http://attacker.example"
    }, JSON.stringify({ raised: 1 }));
    assert.equal(wrongOrigin.status, 403);

    const exactEndpoint = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: 200000, goal: 100000 })
    });
    assert.equal(exactEndpoint.status, 200);

    const aboveEndpoint = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: adjacentFloat(200000, 1) })
    });
    assert.equal(aboveEndpoint.status, 400);
    assert.deepEqual(await aboveEndpoint.json(), {
      error: "raised must be no more than 200% of goal."
    });

    const invalidGoalReduction = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ goal: 99999 })
    });
    assert.equal(invalidGoalReduction.status, 400);
    assert.deepEqual(await invalidGoalReduction.json(), {
      error: "raised must be no more than 200% of goal."
    });
    const endpointState = await fetch(`${displayBase}/api/state`)
      .then((response) => response.json());
    assert.equal(endpointState.raised, 200000);
    assert.equal(endpointState.goal, 100000);
  });
});

test("SSE roles separate preview presence from presentation readiness", async () => {
  await withServers(async ({ displayPort, controlPort, controlBase }) => {
    const invalidRole = await fetch(
      `http://127.0.0.1:${displayPort}/events?role=invalid`
    );
    assert.equal(invalidRole.status, 400);

    const unavailable = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "kite.add" })
    });
    assert.equal(unavailable.status, 409);

    const controlEvents = await openEventStream(controlPort, "control");
    await controlEvents.waitFor("snapshot");
    const previewEvents = await openEventStream(displayPort, "preview");
    assert.match(await previewEvents.waitFor("snapshot"), /"previewConnected":true/);
    await controlEvents.waitFor("presence");

    const stillUnavailable = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "kite.add" })
    });
    assert.equal(stillUnavailable.status, 409);

    const displayEvents = await openEventStream(displayPort, "presentation");
    assert.match(await displayEvents.waitFor("snapshot"), /"presentationConnected":true/);

    const update = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ studentsClapping: true })
    });
    assert.equal(update.status, 200);
    assert.match(await displayEvents.waitFor("state"), /"studentsClapping":true/);
    assert.match(await controlEvents.waitFor("state"), /"studentsClapping":true/);
    const displayStateEvents = displayEvents.eventCount("state");
    const controlStateEvents = controlEvents.eventCount("state");

    const invalid = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: 200001, goal: 100000 })
    });
    assert.equal(invalid.status, 400);
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(displayEvents.eventCount("state"), displayStateEvents);
    assert.equal(controlEvents.eventCount("state"), controlStateEvents);

    for (const type of [
      "firework.launch",
      "school.add",
      "school.remove"
    ]) {
      const id = `test-${type.replaceAll(".", "-")}`;
      const actionPattern = new RegExp(
        `event: action\\ndata: \\{"type":"${type.replace(".", "\\.")}","id":"${id}"\\}`
      );
      const action = await fetch(`${controlBase}/api/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, id })
      });
      assert.equal(action.status, 202);
      const delivered = await displayEvents.waitForMatch(actionPattern);
      assert.match(delivered, actionPattern);
      if (type.startsWith("school.")) {
        assert.ok(
          delivered.lastIndexOf("event: state\n")
            < delivered.lastIndexOf(`event: action\ndata: {"type":"${type}"`),
          "school state must be broadcast before its accepted action"
        );
      }
    }

    const invalidAction = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "school.drop.1" })
    });
    assert.equal(invalidAction.status, 400);

    const invalidActionId = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "school.add", id: "invalid id" })
    });
    assert.equal(invalidActionId.status, 400);

    displayEvents.request.destroy();
    previewEvents.request.destroy();
    controlEvents.request.destroy();
  });
});

test("school boundary actions reject without state mutation or SSE broadcasts", async () => {
  await withServers(async ({ controlBase, displayPort }) => {
    const displayEvents = await openEventStream(displayPort, "presentation");
    await displayEvents.waitFor("snapshot");

    for (let index = 0; index < 25; index += 1) {
      const response = await fetch(`${controlBase}/api/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type: "school.add", id: `fill-${index}` })
      });
      assert.equal(response.status, 202);
    }

    const fullState = await fetch(`${controlBase}/api/state`)
      .then((response) => response.json());
    assert.equal(fullState.distantSchools.length, 25);
    const fullStateEvents = displayEvents.eventCount("state");
    const fullActionEvents = displayEvents.eventCount("action");

    const fullAdd = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "school.add", id: "full-add" })
    });
    assert.equal(fullAdd.status, 409);
    assert.deepEqual(await fullAdd.json(), {
      error: "Cannot add another distant school: the 25-school maximum is already active."
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.deepEqual(
      await fetch(`${controlBase}/api/state`).then((response) => response.json()),
      fullState
    );
    assert.equal(displayEvents.eventCount("state"), fullStateEvents);
    assert.equal(displayEvents.eventCount("action"), fullActionEvents);

    for (let index = 0; index < 25; index += 1) {
      const response = await fetch(`${controlBase}/api/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type: "school.remove", id: `empty-${index}` })
      });
      assert.equal(response.status, 202);
    }

    const emptyState = await fetch(`${controlBase}/api/state`)
      .then((response) => response.json());
    assert.deepEqual(emptyState.distantSchools, []);
    const emptyStateEvents = displayEvents.eventCount("state");
    const emptyActionEvents = displayEvents.eventCount("action");

    const emptyRemove = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "school.remove", id: "empty-remove" })
    });
    assert.equal(emptyRemove.status, 409);
    assert.deepEqual(await emptyRemove.json(), {
      error: "Cannot remove a distant school: no distant schools are active."
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.deepEqual(
      await fetch(`${controlBase}/api/state`).then((response) => response.json()),
      emptyState
    );
    assert.equal(displayEvents.eventCount("state"), emptyStateEvents);
    assert.equal(displayEvents.eventCount("action"), emptyActionEvents);

    displayEvents.request.destroy();
  });
});

test("strict commands are atomic, concurrent-safe, protected, and revisioned once", async () => {
  await withServers(async ({ controlBase }) => {
    const protectedPatch = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ revision: 99, distantSchools: [] })
    });
    assert.equal(protectedPatch.status, 400);

    const additions = await Promise.all(Array.from({ length: 20 }, () => (
      fetch(`${controlBase}/api/commands`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type: "raised.add", amount: 100 })
      }).then((response) => response.json())
    )));
    assert.equal(additions.length, 20);
    const afterAdditions = await fetch(`${controlBase}/api/state`)
      .then((response) => response.json());
    assert.equal(afterAdditions.raised, 2000);
    assert.equal(afterAdditions.revision, 20);

    const invalid = await fetch(`${controlBase}/api/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "raised.add", amount: 1, extra: true })
    });
    assert.equal(invalid.status, 400);
    const unchanged = await fetch(`${controlBase}/api/state`).then((response) => response.json());
    assert.equal(unchanged.raised, 2000);
    assert.equal(unchanged.revision, 20);

    const saturated = await fetch(`${controlBase}/api/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "raised.add", amount: Number.MAX_VALUE })
    });
    assert.equal(saturated.status, 200);
    const saturatedState = await saturated.json();
    assert.equal(saturatedState.raised, 200000);
    assert.equal(Number.isFinite(saturatedState.raised), true);
    assert.equal(saturatedState.revision, 21);
    assert.deepEqual(
      await fetch(`${controlBase}/api/state`).then((response) => response.json()),
      saturatedState
    );

    const celebration = await fetch(`${controlBase}/api/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "celebration.toggle" })
    }).then((response) => response.json());
    assert.equal(celebration.revision, 22);
    assert.equal(celebration.nightMode, true);
    assert.equal(celebration.continuousFireworks, true);
    assert.equal(celebration.thankYouVisible, true);
  });
});

test("strict raised step, ratio, and state toggle commands validate and mutate atomically", async () => {
  await withServers(async ({ controlBase, displayPort }) => {
    const command = (body) => fetch(`${controlBase}/api/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    const getState = () => fetch(`${controlBase}/api/state`)
      .then((response) => response.json());

    let response = await command({ type: "state.toggle", field: "demoActive" });
    assert.equal(response.status, 200);
    let current = await response.json();
    assert.equal(current.demoActive, true);
    assert.equal(current.revision, 1);

    response = await command({ type: "state.toggle", field: "demoActive" });
    assert.equal(response.status, 200);
    current = await response.json();
    assert.equal(current.demoActive, false);
    assert.equal(current.revision, 2);

    const events = await openEventStream(displayPort, "presentation");
    await events.waitFor("snapshot");
    const beforeInvalidStep = await getState();
    const stateEvents = events.eventCount("state");
    for (const fraction of [
      -.1,
      adjacentFloat(-.05, -1),
      adjacentFloat(-.05, 1),
      0,
      .001,
      .1,
      3,
      Number.MAX_VALUE
    ]) {
      response = await command({ type: "raised.step", fraction });
      assert.equal(response.status, 400, `expected rejection for ${fraction}`);
      assert.deepEqual(await response.json(), {
        error: "fraction must be one of -0.05, -0.01, 0.01, or 0.05."
      });
      assert.deepEqual(await getState(), beforeInvalidStep);
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
    assert.equal(events.eventCount("state"), stateEvents);

    response = await command({ type: "raised.setRatio", ratio: 1 });
    current = await response.json();
    assert.equal(current.raised, 100000);
    assert.equal(current.revision, 3);
    for (const [fraction, expectedRaised] of [
      [-.05, 95000],
      [-.01, 94000],
      [.01, 95000],
      [.05, 100000]
    ]) {
      response = await command({ type: "raised.step", fraction });
      assert.equal(response.status, 200);
      current = await response.json();
      assert.equal(current.raised, expectedRaised);
      assert.equal(current.demoActive, false);
    }

    response = await command({ type: "raised.setRatio", ratio: .25 });
    assert.equal(response.status, 200);
    current = await response.json();
    assert.equal(current.raised, 25000);
    assert.equal(current.demoActive, false);
    assert.equal(current.revision, 8);

    response = await command({ type: "state.toggle", field: "nightMode" });
    assert.equal(response.status, 200);
    current = await response.json();
    assert.equal(current.nightMode, true);
    assert.equal(current.revision, 9);

    const invalidCommands = [
      { type: "raised.step" },
      { type: "raised.step", fraction: .1, extra: true },
      { type: "raised.step", fraction: "0.1" },
      { type: "raised.step", fraction: Number.POSITIVE_INFINITY },
      { type: "raised.setRatio" },
      { type: "raised.setRatio", ratio: 1, extra: true },
      { type: "raised.setRatio", ratio: -1 },
      { type: "raised.setRatio", ratio: 2.01 },
      { type: "raised.setRatio", ratio: "1" },
      { type: "state.toggle" },
      { type: "state.toggle", field: "nightMode", extra: true },
      { type: "state.toggle", field: "raised" },
      { type: "state.toggle", field: "unknown" }
    ];
    const beforeRejections = await getState();
    for (const invalidCommand of invalidCommands) {
      const invalidResponse = await command(invalidCommand);
      assert.equal(
        invalidResponse.status,
        400,
        `expected rejection for ${JSON.stringify(invalidCommand)}`
      );
      assert.deepEqual(await getState(), beforeRejections);
    }
    events.request.destroy();
  });
});

function createFakeScheduler() {
  let current = 0;
  let nextId = 1;
  const timers = new Map();
  const intervalDelays = [];
  function schedule(callback, delay, interval) {
    const id = nextId++;
    timers.set(id, { callback, at: current + delay, delay, interval });
    return id;
  }
  function advance(milliseconds) {
    const target = current + milliseconds;
    while (true) {
      const due = [...timers.entries()]
        .filter(([, timer]) => timer.at <= target)
        .sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
      if (!due) break;
      const [id, timer] = due;
      current = timer.at;
      if (timer.interval) timer.at += timer.delay;
      else timers.delete(id);
      timer.callback();
    }
    current = target;
  }
  return {
    now: () => current,
    setTimeout: (callback, delay) => schedule(callback, delay, false),
    clearTimeout: (id) => timers.delete(id),
    setInterval: (callback, delay) => {
      intervalDelays.push(delay);
      return schedule(callback, delay, true);
    },
    clearInterval: (id) => timers.delete(id),
    intervalDelays,
    advance
  };
}

test("authoritative demo preserves milestones and exact maximum before looping", () => {
  const clock = createFakeScheduler();
  const shared = createSharedState({ goal: 100000 }, clock);
  shared.command({ type: "state.toggle", field: "demoActive" });
  assert.deepEqual(clock.intervalDelays, [200]);
  assert.equal(shared.getState().revision, 1);
  clock.advance(90000);
  assert.equal(shared.getState().raised, 125000);
  clock.advance(7200);
  assert.equal(shared.getState().raised, 135000);
  clock.advance(46800);
  assert.equal(shared.getState().raised, 200000);
  const maxRevision = shared.getState().revision;
  clock.advance(200);
  assert.ok(shared.getState().raised > 0);
  assert.ok(shared.getState().raised < 200000);
  assert.equal(shared.getState().revision, maxRevision + 1);
  shared.closeClients();
});

test("demo scheduling changes only on real transitions and preserves elapsed playback", () => {
  const clock = createFakeScheduler();
  const shared = createSharedState({ goal: 100000 }, clock);

  shared.update({ demoActive: true });
  assert.deepEqual(clock.intervalDelays, [200]);
  clock.advance(30000);
  const raisedAtThirtySeconds = shared.getState().raised;
  assert.ok(raisedAtThirtySeconds > 41000);
  assert.ok(raisedAtThirtySeconds < 42000);

  const revisionBeforeRepeat = shared.getState().revision;
  shared.update({ demoActive: true });
  assert.equal(shared.getState().revision, revisionBeforeRepeat);
  assert.equal(shared.getState().raised, raisedAtThirtySeconds);
  assert.deepEqual(clock.intervalDelays, [200]);

  shared.update({ goal: 200000 });
  assert.equal(shared.getState().raised, raisedAtThirtySeconds);
  clock.advance(200);
  assert.ok(shared.getState().raised > 83000);
  assert.ok(shared.getState().raised < 84000);
  assert.deepEqual(clock.intervalDelays, [200]);

  shared.update({ demoActive: false });
  const stoppedState = shared.getState();
  shared.update({ demoActive: false });
  clock.advance(1000);
  assert.deepEqual(shared.getState(), stoppedState);
  assert.deepEqual(clock.intervalDelays, [200]);
  shared.closeClients();
});

test("SSE capacity, cleanup, and backpressure remain bounded per role", async () => {
  await withServers(async ({ displayPort }) => {
    const streams = [];
    for (let index = 0; index < MAX_SSE_CLIENTS_PER_ROLE; index += 1) {
      const stream = await openEventStream(displayPort, "presentation");
      await stream.waitFor("snapshot");
      streams.push(stream);
    }

    const full = await fetch(
      `http://127.0.0.1:${displayPort}/events?role=presentation`
    );
    assert.equal(full.status, 503);
    assert.equal(full.headers.get("retry-after"), "1");
    assert.deepEqual(await full.json(), {
      error: "Event stream capacity reached."
    });

    streams.pop().request.destroy();
    await new Promise((resolve) => setImmediate(resolve));
    const replacement = await openEventStream(displayPort, "presentation");
    await replacement.waitFor("snapshot");
    replacement.request.destroy();
    for (const stream of streams) stream.request.destroy();
  });

  class FakeResponse extends EventEmitter {
    constructor() {
      super();
      this.destroyed = false;
      this.writes = [];
      this.blockNextWrite = true;
      this.throwOnWrite = false;
    }

    write(value) {
      if (this.throwOnWrite) throw new Error("write failed");
      this.writes.push(value);
      if (!this.blockNextWrite) return true;
      this.blockNextWrite = false;
      return false;
    }

    destroy() {
      this.destroyed = true;
    }

    end() {}
  }

  const shared = createSharedState();
  const response = new FakeResponse();
  assert.equal(shared.addClient("presentation", response), true);
  assert.equal(shared.hasPresentationClient(), true);
  shared.update({ raised: 100 });
  assert.equal(
    response.writes.some((write) => write.includes("event: state")),
    false
  );
  response.emit("drain");
  assert.equal(
    response.writes.some((write) => write.includes("event: state")),
    true
  );
  assert.equal(shared.hasPresentationClient(), true);

  response.throwOnWrite = true;
  shared.update({ raised: 200 });
  assert.equal(response.destroyed, true);
  assert.equal(shared.hasPresentationClient(), false);

  const blockedResponse = new FakeResponse();
  assert.equal(shared.addClient("presentation", blockedResponse), true);
  for (let raised = 201; raised < 5000 && !blockedResponse.destroyed; raised += 1) {
    shared.update({ raised });
  }
  assert.equal(blockedResponse.destroyed, true);
  assert.equal(shared.hasPresentationClient(), false);

  const replacement = new FakeResponse();
  replacement.blockNextWrite = false;
  assert.equal(shared.addClient("presentation", replacement), true);
  assert.equal(shared.hasPresentationClient(), true);
  shared.update({ raised: 1000 });
  assert.equal(
    replacement.writes.some((write) => write.includes("event: state")),
    true
  );
  shared.closeClients();
});

test("school lifecycle is authoritative, cancellable, contiguous, and generation-safe", () => {
  const clock = createFakeScheduler();
  const shared = createSharedState({ seattleSchools: 3 }, clock);
  shared.recordSchoolAction("school.add");
  let current = shared.getState();
  assert.equal(current.revision, 1);
  assert.equal(current.seattleSchools, 47);
  assert.deepEqual(current.distantSchools[0], {
    slot: 1,
    phase: "pending",
    generation: 1,
    startedAt: 0,
    completesAt: 6500
  });
  clock.advance(3000);
  shared.recordSchoolAction("school.remove");
  assert.equal(shared.getState().seattleSchools, 47);
  assert.deepEqual(shared.getState().distantSchools, []);
  clock.advance(4000);
  assert.equal(shared.getState().seattleSchools, 47);

  shared.recordSchoolAction("school.add");
  assert.equal(shared.getState().distantSchools[0].generation, 2);
  clock.advance(6500);
  current = shared.getState();
  assert.equal(current.distantSchools[0].phase, "completed");
  assert.equal(current.seattleSchools, 48);
  assert.equal(current.revision, 4);
  shared.recordSchoolAction("school.remove");
  assert.equal(shared.getState().seattleSchools, 47);
  assert.equal(shared.getState().revision, 5);
  shared.closeClients();
});

test("only the school projected to raise Seattle Schools from 49 to 50 is marked to celebrate", () => {
  const clock = createFakeScheduler();
  const shared = createSharedState({ seattleSchools: 48 }, clock);

  shared.recordSchoolAction("school.add");
  clock.advance(6500);
  let current = shared.getState();
  assert.equal(current.seattleSchools, 49);
  assert.equal(current.distantSchools[0].celebration, undefined);

  shared.recordSchoolAction("school.add");
  assert.equal(
    shared.getState().distantSchools[1].celebration,
    "seattle-schools-50"
  );
  clock.advance(6500);
  current = shared.getState();
  assert.equal(current.seattleSchools, 50);
  assert.equal(
    current.distantSchools[1].celebration,
    "seattle-schools-50"
  );
  assert.equal(current.distantSchools[0].celebration, undefined);

  shared.recordSchoolAction("school.remove");
  assert.equal(shared.getState().seattleSchools, 49);
  shared.recordSchoolAction("school.add");
  assert.equal(
    shared.getState().distantSchools[1].celebration,
    "seattle-schools-50"
  );
  clock.advance(6500);
  current = shared.getState();
  assert.equal(current.seattleSchools, 50);
  assert.equal(current.distantSchools[1].generation, 2);
  assert.equal(
    current.distantSchools[1].celebration,
    "seattle-schools-50"
  );
  shared.closeClients();
});

test("rejects unexpected hosts, methods, traversal, and oversized JSON", async () => {
  await withServers(async ({ displayPort, controlBase }) => {
    const host = await rawRequest(displayPort, "/", "GET", {
      Host: `attacker.example:${displayPort}`
    });
    assert.equal(host.status, 403);

    assert.equal((await fetch(`${controlBase}/Building1.jpg`)).status, 404);
    assert.equal((await fetch(`${controlBase}/%2e%2e%2fserver.mjs`)).status, 404);
    assert.equal((await fetch(`${controlBase}/`, { method: "POST" })).status, 405);

    const oversized = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: 1, padding: "x".repeat(5000) })
    });
    assert.equal(oversized.status, 413);
  });
});

test("parses new and legacy port settings and rejects invalid pairs", () => {
  assert.deepEqual(getPorts([], {}), { displayPort: 8080, controlPort: 8081 });
  assert.deepEqual(
    getPorts(["--port=9000", "--control-port=9001"], {}),
    { displayPort: 9000, controlPort: 9001 }
  );
  assert.deepEqual(
    getPorts(["--display-port=9100"], { PORT: "9200", CONTROL_PORT: "9101" }),
    { displayPort: 9100, controlPort: 9101 }
  );
  assert.throws(() => getPorts(["--display-port=8080", "--control-port=8080"], {}));
  assert.throws(() => getPorts(["--display-port=0"], {}));
  assert.throws(() => getPorts([], { CONTROL_PORT: "not-a-port" }));
});
