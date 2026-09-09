import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  createServer,
  createSharedState,
  getPorts
} from "../server.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

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
  const displayServer = createServer(root, { surface: "display", sharedState });
  const controlServer = createServer(root, { surface: "control", sharedState });
  await Promise.all([listen(displayServer), listen(controlServer)]);
  const displayPort = displayServer.address().port;
  const controlPort = controlServer.address().port;
  try {
    await run({
      displayBase: `http://127.0.0.1:${displayPort}`,
      controlBase: `http://127.0.0.1:${controlPort}`,
      displayPort,
      controlPort
    });
  } finally {
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

function openEventStream(port) {
  return new Promise((resolve, reject) => {
    const request = http.get({
      hostname: "127.0.0.1",
      port,
      path: "/events"
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
        eventCount(event) {
          return buffer.split(`event: ${event}\n`).length - 1;
        }
      });
    });
    request.on("error", reject);
  });
}

test("serves isolated display and control surfaces with security headers", async () => {
  await withServers(async ({ displayBase, controlBase }) => {
    const display = await fetch(`${displayBase}/`);
    assert.equal(display.status, 200);
    assert.match(display.headers.get("content-security-policy"), /connect-src 'self'/);
    const displayHtml = await display.text();
    assert.match(displayHtml, /id="fundraiser"/);
    assert.match(displayHtml, /data-bus-percent="0"/);
    assert.match(displayHtml, /data-distant-schools="0"/);
    assert.match(displayHtml, /<kbd>S<\/kbd> drop next school/);
    assert.match(displayHtml, /<kbd>X<\/kbd> remove last school/);
    assert.doesNotMatch(displayHtml, /operator-panel|progress-form|<kbd>C<\/kbd>/);

    const control = await fetch(`${controlBase}/`);
    assert.equal(control.status, 200);
    const controlHtml = await control.text();
    assert.match(controlHtml, /id="progress-form"/);
    assert.match(controlHtml, /data-action="firework\.launch"/);
    assert.match(controlHtml, /data-action="school\.add"/);
    assert.match(controlHtml, /data-action="school\.remove"/);
    assert.match(controlHtml, /max="135000"/);
    assert.match(controlHtml, /max="6671999447956289"/);

    assert.equal((await fetch(`${displayBase}/control.html`)).status, 404);
    assert.equal((await fetch(`${displayBase}/src/control.mjs`)).status, 404);
    assert.equal((await fetch(`${controlBase}/index.html`)).status, 404);
    assert.equal((await fetch(`${controlBase}/src/app.mjs`)).status, 404);
    assert.equal((await fetch(`${displayBase}/styles.css`)).status, 200);
    assert.equal((await fetch(`${controlBase}/control.css`)).status, 200);
    assert.equal((await fetch(`${displayBase}/favicon.ico`)).headers.get("content-type"), "image/png");
    assert.equal((await fetch(`${controlBase}/favicon.ico`)).headers.get("content-type"), "image/png");

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
      body: JSON.stringify({ raised: 135000, goal: 100000 })
    });
    assert.equal(exactEndpoint.status, 200);

    const aboveEndpoint = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ raised: adjacentFloat(135000, 1) })
    });
    assert.equal(aboveEndpoint.status, 400);
    assert.deepEqual(await aboveEndpoint.json(), {
      error: "raised must be no more than 135% of goal."
    });

    const invalidGoalReduction = await fetch(`${controlBase}/api/state`, {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ goal: 99999 })
    });
    assert.equal(invalidGoalReduction.status, 400);
    assert.deepEqual(await invalidGoalReduction.json(), {
      error: "raised must be no more than 135% of goal."
    });
    const endpointState = await fetch(`${displayBase}/api/state`)
      .then((response) => response.json());
    assert.equal(endpointState.raised, 135000);
    assert.equal(endpointState.goal, 100000);
  });
});

test("SSE sends snapshots, state changes, presence, and transient actions", async () => {
  await withServers(async ({ displayPort, controlPort, controlBase }) => {
    const unavailable = await fetch(`${controlBase}/api/actions`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ type: "kite.add" })
    });
    assert.equal(unavailable.status, 409);

    const controlEvents = await openEventStream(controlPort);
    await controlEvents.waitFor("snapshot");
    const displayEvents = await openEventStream(displayPort);
    await displayEvents.waitFor("snapshot");
    await controlEvents.waitFor("presence");

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
      body: JSON.stringify({ raised: 135001, goal: 100000 })
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
      const nextAction = new Promise((resolve) => {
        displayEvents.response.once("data", resolve);
      });
      const action = await fetch(`${controlBase}/api/actions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, id })
      });
      assert.equal(action.status, 202);
      assert.match(
        await nextAction,
        new RegExp(
          `event: action\\ndata: \\{"type":"${type.replace(".", "\\.")}","id":"${id}"\\}`
        )
      );
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
    controlEvents.request.destroy();
  });
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
