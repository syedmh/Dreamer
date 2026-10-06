import { open } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import path from "node:path";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";
import {
  addRaisedAmount,
  deriveDemoRaised,
  deriveSliderMaximum,
  isAllowedRaisedStep,
  MAX_GOAL,
  MAX_PROGRESS_RATIO,
  MAX_RAISED,
  MIN_GOAL
} from "./src/config.mjs";

const MIME_TYPES = new Map([
  [".html", "text/html; charset=utf-8"],
  [".css", "text/css; charset=utf-8"],
  [".js", "text/javascript; charset=utf-8"],
  [".mjs", "text/javascript; charset=utf-8"],
  [".json", "application/json; charset=utf-8"],
  [".svg", "image/svg+xml"],
  [".png", "image/png"],
  [".jpg", "image/jpeg"],
  [".jpeg", "image/jpeg"]
]);

const BASE_SECURITY_HEADERS = Object.freeze({
  "Cache-Control": "no-store",
  "Content-Security-Policy": "default-src 'self'; script-src 'self' 'sha256-5T7sWeLQQ4jOrxFZHC2c5KDWDLW/9fl6mf5bWn5D1IM='; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  "Cross-Origin-Resource-Policy": "same-origin",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY"
});

const DEFAULT_DISPLAY_PORT = 8080;
const DEFAULT_CONTROL_PORT = 8081;
const JSON_BODY_LIMIT = 4096;
const HEARTBEAT_INTERVAL_MS = 15000;
export const MAX_SSE_CLIENTS_PER_ROLE = 8;
const MAX_SSE_PENDING_BYTES = 65536;
const DEMO_UPDATE_INTERVAL_MS = 200;
const DEMO_DURATION_MS = 86400;
const MAX_DISTANT_SCHOOLS = 25;
const DISTANT_SCHOOL_DROP_DURATION_MS = 6500;
const SEATTLE_SCHOOL_BASELINE = 47;
const SCHOOL_CELEBRATION_MILESTONE = "seattle-schools-50";
const BOOLEAN_STATE_FIELDS = new Set([
  "demoActive",
  "nightMode",
  "studentsClapping",
  "thankYouVisible",
  "continuousFireworks",
  "buildSummaryVisible",
  "totalBoxVisible",
  "keyboardLegendVisible",
  "keypressEnabled",
  "wideScreen"
]);
const ALLOWED_STATE_FIELDS = new Set([
  "raised",
  "goal",
  "overrideRaised",
  "seattleSchools",
  "operationCost",
  ...BOOLEAN_STATE_FIELDS
]);
const ALLOWED_ACTIONS = new Set([
  "kite.add",
  "kite.clear",
  "firework.launch",
  "firework.tcf",
  "firework.clear",
  "total.drop",
  "school.add",
  "school.remove"
]);

const SHARED_PATHS = new Set([
  "/favicon.ico",
  "/Logo.png",
  "/src/config.mjs",
  "/src/currency.mjs"
]);
const DISPLAY_PATHS = new Set([
  ...SHARED_PATHS,
  "/Boy.png",
  "/Girl.png",
  "/index.html",
  "/styles.css",
  "/src/app.mjs",
  "/src/model.mjs",
  "/src/render.mjs",
  "/src/scene.mjs",
  "/tests/harness.html"
]);
const CONTROL_PATHS = new Set([
  ...SHARED_PATHS,
  "/control.html",
  "/control.css",
  "/src/control.mjs"
]);

function sendBuffer(request, response, statusCode, body, contentType, extraHeaders = {}) {
  response.writeHead(statusCode, {
    ...BASE_SECURITY_HEADERS,
    "Content-Type": contentType,
    "Content-Length": body.length,
    ...extraHeaders
  });
  if (request.method === "HEAD") {
    response.end();
  } else {
    response.end(body);
  }
}

function sendText(request, response, statusCode, text, extraHeaders = {}) {
  sendBuffer(
    request,
    response,
    statusCode,
    Buffer.from(text),
    "text/plain; charset=utf-8",
    extraHeaders
  );
}

function sendJson(request, response, statusCode, value, extraHeaders = {}) {
  sendBuffer(
    request,
    response,
    statusCode,
    Buffer.from(`${JSON.stringify(value)}\n`),
    "application/json; charset=utf-8",
    extraHeaders
  );
}

export function createSharedState(initialState = {}, scheduler = {}) {
  const now = scheduler.now ?? Date.now;
  const scheduleTimeout = scheduler.setTimeout ?? setTimeout;
  const cancelTimeout = scheduler.clearTimeout ?? clearTimeout;
  const scheduleInterval = scheduler.setInterval ?? setInterval;
  const cancelInterval = scheduler.clearInterval ?? clearInterval;
  const normalizedInitialState = Object.fromEntries(
    Object.entries(initialState).filter(([key]) => ALLOWED_STATE_FIELDS.has(key))
  );
  const initialRaised = typeof normalizedInitialState.raised === "number"
    ? normalizedInitialState.raised
    : 0;
  let state = Object.freeze({
    revision: 0,
    raised: initialRaised,
    goal: 100000,
    overrideRaised: initialRaised,
    seattleSchools: SEATTLE_SCHOOL_BASELINE,
    operationCost: 0,
    demoActive: false,
    nightMode: false,
    studentsClapping: false,
    thankYouVisible: false,
    continuousFireworks: false,
    buildSummaryVisible: false,
    totalBoxVisible: false,
    keyboardLegendVisible: false,
    keypressEnabled: false,
    wideScreen: false,
    ...normalizedInitialState,
    distantSchools: Object.freeze([])
  });
  const clients = {
    presentation: new Set(),
    preview: new Set(),
    control: new Set()
  };
  const schoolTimers = new Map();
  const schoolGenerations = new Map();
  let demoInterval = null;
  let demoStartedAt = 0;

  function removeClientRecord(role, client) {
    if (client.closed || !clients[role].delete(client)) return false;
    client.closed = true;
    client.response.off("drain", client.onDrain);
    client.response.off("error", client.onError);
    client.response.off("close", client.onClose);
    return true;
  }

  function removeClient(role, response) {
    const client = [...clients[role]].find(
      (candidate) => candidate.response === response
    );
    if (!client) return false;
    const removed = removeClientRecord(role, client);
    if (removed) broadcastPresence();
    return removed;
  }

  function failClient(role, client, error) {
    const removed = removeClientRecord(role, client);
    if (!client.response.destroyed) client.response.destroy();
    if (removed) broadcastPresence();
  }

  function enqueueClientWrite(role, client, payload) {
    const bytes = Buffer.byteLength(payload);
    if (client.pendingBytes + bytes > MAX_SSE_PENDING_BYTES) {
      failClient(role, client, new Error("SSE client exceeded the pending write limit."));
      return false;
    }
    client.queue.push(payload);
    client.pendingBytes += bytes;
    return true;
  }

  function writeClient(role, client, payload) {
    if (client.closed) return false;
    if (client.blocked) return enqueueClientWrite(role, client, payload);
    try {
      if (!client.response.write(payload)) client.blocked = true;
      return true;
    } catch (error) {
      failClient(role, client, error);
      return false;
    }
  }

  function flushClient(role, client) {
    if (client.closed) return;
    client.blocked = false;
    while (client.queue.length > 0 && !client.blocked) {
      const payload = client.queue.shift();
      client.pendingBytes -= Buffer.byteLength(payload);
      writeClient(role, client, payload);
    }
  }

  function writeEvent(role, client, event, value) {
    writeClient(role, client, `event: ${event}\ndata: ${JSON.stringify(value)}\n\n`);
  }

  function broadcast(event, value, roles = ["presentation", "preview", "control"]) {
    for (const role of roles) {
      for (const client of clients[role]) {
        writeEvent(role, client, event, value);
      }
    }
  }

  function presence() {
    const presentationConnected = clients.presentation.size > 0;
    const previewConnected = clients.preview.size > 0;
    return {
      displayConnected: presentationConnected || previewConnected,
      presentationConnected,
      previewConnected
    };
  }

  function broadcastPresence() {
    broadcast("presence", presence());
  }

  function updateState(patch) {
    const synchronizedPatch = "raised" in patch
      ? { ...patch, overrideRaised: patch.raised }
      : patch;
    const changed = Object.entries(synchronizedPatch).some(
      ([key, value]) => state[key] !== value
    );
    if (!changed) return state;
    state = Object.freeze({
      ...state,
      ...synchronizedPatch,
      revision: state.revision + 1
    });
    broadcast("state", state);
    return state;
  }

  function stopDemoScheduler() {
    if (demoInterval === null) return;
    cancelInterval(demoInterval);
    demoInterval = null;
  }

  function runDemoTick() {
    if (!state.demoActive) return;
    const elapsed = Math.max(0, now() - demoStartedAt);
    if (elapsed >= DEMO_DURATION_MS) {
      updateState({ raised: deriveSliderMaximum(state.goal) });
      demoStartedAt = now();
      return;
    }
    updateState({
      raised: deriveDemoRaised(state.goal, elapsed / DEMO_DURATION_MS)
    });
  }

  function startDemoScheduler() {
    stopDemoScheduler();
    demoStartedAt = now();
    demoInterval = scheduleInterval(runDemoTick, DEMO_UPDATE_INTERVAL_MS);
    demoInterval?.unref?.();
  }

  function update(patch) {
    const wasDemoActive = state.demoActive;
    const nextState = updateState(patch);
    if ("demoActive" in patch && nextState.demoActive !== wasDemoActive) {
      if (nextState.demoActive) {
        startDemoScheduler();
      } else {
        stopDemoScheduler();
      }
    }
    return nextState;
  }

  function command(value) {
    switch (value.type) {
      case "raised.add":
        return update({
          raised: Math.min(deriveSliderMaximum(state.goal), Math.max(
            0,
            value.amount > 0 && state.raised > MAX_RAISED - value.amount
              ? MAX_RAISED
              : state.raised + value.amount
          )),
          demoActive: false
        });
      case "raised.step":
        return update({
          raised: addRaisedAmount(state.raised, state.goal, value.fraction),
          demoActive: false
        });
      case "raised.setRatio":
        return update({
          raised: Math.min(
            deriveSliderMaximum(state.goal),
            Math.max(0, state.goal * value.ratio)
          ),
          demoActive: false
        });
      case "state.toggle":
        return update({ [value.field]: !state[value.field] });
      case "celebration.toggle": {
        const active = state.nightMode
          && state.continuousFireworks
          && state.thankYouVisible;
        return update({
          nightMode: !active,
          continuousFireworks: !active,
          thankYouVisible: !active
        });
      }
      default:
        throw new TypeError(`Unknown command "${value.type}".`);
    }
  }

  function completeSchool(slot, generation) {
    schoolTimers.delete(slot);
    const school = state.distantSchools[slot - 1];
    if (!school || school.generation !== generation || school.phase !== "pending") return;
    const previousSeattleSchools = Math.max(
      SEATTLE_SCHOOL_BASELINE,
      Number(state.seattleSchools) || 0
    );
    const distantSchools = state.distantSchools.map((entry) => (
      entry.slot === slot
        ? Object.freeze({
            ...entry,
            phase: "completed"
          })
        : entry
    ));
    updateState({
      distantSchools: Object.freeze(distantSchools),
      seattleSchools: previousSeattleSchools + 1
    });
  }

  function recordSchoolAction(type) {
    if (type === "school.add") {
      if (state.distantSchools.length >= MAX_DISTANT_SCHOOLS) {
        return Object.freeze({
          changed: false,
          state,
          error: "Cannot add another distant school: the 25-school maximum is already active."
        });
      }
      const slot = state.distantSchools.length + 1;
      const generation = (schoolGenerations.get(slot) ?? 0) + 1;
      schoolGenerations.set(slot, generation);
      const startedAt = now();
      const pendingSchools = state.distantSchools.filter(
        (entry) => entry.phase === "pending"
      ).length;
      const projectedSeattleSchools = Math.max(
        SEATTLE_SCHOOL_BASELINE,
        Number(state.seattleSchools) || 0
      ) + pendingSchools;
      const school = Object.freeze({
        slot,
        phase: "pending",
        generation,
        startedAt,
        completesAt: startedAt + DISTANT_SCHOOL_DROP_DURATION_MS,
        ...(projectedSeattleSchools === 49
          ? { celebration: SCHOOL_CELEBRATION_MILESTONE }
          : {})
      });
      const distantSchools = Object.freeze([...state.distantSchools, school]);
      const nextState = updateState({
        distantSchools,
        seattleSchools: Math.max(
          SEATTLE_SCHOOL_BASELINE,
          Number(state.seattleSchools) || 0
        )
      });
      const timer = scheduleTimeout(
        () => completeSchool(slot, generation),
        DISTANT_SCHOOL_DROP_DURATION_MS
      );
      timer?.unref?.();
      schoolTimers.set(slot, timer);
      return Object.freeze({ changed: true, state: nextState });
    }
    if (type !== "school.remove") {
      throw new TypeError(`Unknown school action "${type}".`);
    }
    if (state.distantSchools.length === 0) {
      return Object.freeze({
        changed: false,
        state,
        error: "Cannot remove a distant school: no distant schools are active."
      });
    }
    const school = state.distantSchools.at(-1);
    const timer = schoolTimers.get(school.slot);
    if (timer !== undefined) {
      cancelTimeout(timer);
      schoolTimers.delete(school.slot);
    }
    return Object.freeze({
      changed: true,
      state: updateState({
      distantSchools: Object.freeze(state.distantSchools.slice(0, -1)),
      ...(school.phase === "completed"
        ? {
            seattleSchools: Math.max(
              SEATTLE_SCHOOL_BASELINE,
              (Number(state.seattleSchools) || 0) - 1
            )
          }
        : {})
      })
    });
  }

  if (state.demoActive) startDemoScheduler();

  return Object.freeze({
    getState: () => state,
    hasPresentationClient: () => clients.presentation.size > 0,
    canAddClient: (role) => clients[role].size < MAX_SSE_CLIENTS_PER_ROLE,
    addClient(role, response) {
      if (clients[role].size >= MAX_SSE_CLIENTS_PER_ROLE) return false;
      const client = {
        response,
        queue: [],
        pendingBytes: 0,
        blocked: false,
        closed: false,
        onDrain: null,
        onError: null,
        onClose: null
      };
      client.onDrain = () => flushClient(role, client);
      client.onError = (error) => failClient(role, client, error);
      client.onClose = () => {
        if (removeClientRecord(role, client)) broadcastPresence();
      };
      response.on("drain", client.onDrain);
      response.on("error", client.onError);
      response.on("close", client.onClose);
      clients[role].add(client);
      writeEvent(role, client, "snapshot", {
        state,
        ...presence()
      });
      broadcastPresence();
      return !client.closed;
    },
    removeClient,
    update,
    command,
    recordSchoolAction,
    sendAction(action) {
      broadcast("action", action);
    },
    heartbeat() {
      for (const role of ["presentation", "preview", "control"]) {
        for (const client of clients[role]) {
          writeClient(role, client, ": heartbeat\n\n");
        }
      }
    },
    closeClients() {
      stopDemoScheduler();
      for (const timer of schoolTimers.values()) cancelTimeout(timer);
      schoolTimers.clear();
      for (const role of ["presentation", "preview", "control"]) {
        for (const client of clients[role]) {
          removeClientRecord(role, client);
          client.response.end();
        }
        clients[role].clear();
      }
    }
  });
}

function validateHost(request) {
  const localPort = request.socket.localPort;
  return new Set([
    `127.0.0.1:${localPort}`,
    `localhost:${localPort}`
  ]).has(request.headers.host);
}

function validateMutationOrigin(request) {
  const origin = request.headers.origin;
  return !origin || origin === `http://${request.headers.host}`;
}

async function readJsonBody(request) {
  const contentType = String(request.headers["content-type"] || "")
    .split(";", 1)[0]
    .trim()
    .toLowerCase();
  if (contentType !== "application/json") {
    return { error: 415, message: "Content-Type must be application/json.\n" };
  }
  const declaredLength = Number(request.headers["content-length"]);
  if (Number.isFinite(declaredLength) && declaredLength > JSON_BODY_LIMIT) {
    return { error: 413, message: "Request body too large.\n" };
  }

  const chunks = [];
  let length = 0;
  let tooLarge = false;
  for await (const chunk of request) {
    length += chunk.length;
    if (length > JSON_BODY_LIMIT) {
      tooLarge = true;
    } else if (!tooLarge) {
      chunks.push(chunk);
    }
  }
  if (tooLarge) return { error: 413, message: "Request body too large.\n" };
  if (length === 0) return { error: 400, message: "JSON body is required.\n" };

  try {
    const value = JSON.parse(Buffer.concat(chunks).toString("utf8"));
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return { error: 400, message: "JSON body must be an object.\n" };
    }
    return { value };
  } catch {
    return { error: 400, message: "Malformed JSON.\n" };
  }
}

function validateStatePatch(value, currentState) {
  const keys = Object.keys(value);
  if (keys.length === 0) return "At least one state field is required.";
  if (keys.some((key) => !ALLOWED_STATE_FIELDS.has(key))) {
    return "State patch contains an unknown field.";
  }

  for (const field of BOOLEAN_STATE_FIELDS) {
    if (field in value && typeof value[field] !== "boolean") {
      return `${field} must be a boolean.`;
    }
  }
  if ("seattleSchools" in value && (
    !Number.isSafeInteger(value.seattleSchools) || value.seattleSchools < 0
  )) {
    return "seattleSchools must be a non-negative whole number.";
  }
  if ("operationCost" in value && (
    typeof value.operationCost !== "number"
      || !Number.isFinite(value.operationCost)
      || value.operationCost < 0
      || value.operationCost > MAX_RAISED
  )) {
    return "operationCost must be a finite non-negative number.";
  }
  if ("overrideRaised" in value && (
    typeof value.overrideRaised !== "number"
      || !Number.isFinite(value.overrideRaised)
      || value.overrideRaised < 0
      || value.overrideRaised > MAX_RAISED
  )) {
    return "overrideRaised must be a finite non-negative number within the allowed range.";
  }

  const raised = "raised" in value ? value.raised : currentState.raised;
  const goal = "goal" in value ? value.goal : currentState.goal;
  if (typeof raised !== "number" || !Number.isFinite(raised)
    || raised < 0 || raised > MAX_RAISED) {
    return "raised must be a finite number within the allowed range.";
  }
  if (typeof goal !== "number" || !Number.isFinite(goal)
    || goal < MIN_GOAL || goal > MAX_GOAL) {
    return "goal must be a finite number within the allowed range.";
  }
  if (raised > deriveSliderMaximum(goal)) {
    return "raised must be no more than 120% of goal.";
  }
  return null;
}

function validateCommand(value) {
  const schemas = {
    "raised.add": ["type", "amount"],
    "raised.step": ["type", "fraction"],
    "raised.setRatio": ["type", "ratio"],
    "state.toggle": ["type", "field"],
    "celebration.toggle": ["type"]
  };
  const keys = schemas[value.type];
  if (!keys || Object.keys(value).length !== keys.length
    || keys.some((key) => !(key in value))) {
    return "Unknown or invalid command.";
  }
  if (value.type === "raised.add"
    && (typeof value.amount !== "number" || !Number.isFinite(value.amount))) {
    return "amount must be a finite number.";
  }
  if (value.type === "raised.step"
    && !isAllowedRaisedStep(value.fraction)) {
    return "fraction must be one of -0.05, -0.01, 0.01, or 0.05.";
  }
  if (value.type === "raised.setRatio"
    && (
      typeof value.ratio !== "number"
      || !Number.isFinite(value.ratio)
      || value.ratio < 0
      || value.ratio > MAX_PROGRESS_RATIO
    )) {
    return `ratio must be a finite number from 0 through ${MAX_PROGRESS_RATIO}.`;
  }
  if (value.type === "state.toggle"
    && !BOOLEAN_STATE_FIELDS.has(value.field)) {
    return "field must be an allowed boolean state field.";
  }
  return null;
}

function createStaticSecurityHeaders(surface, runtime, isDisplayDocument) {
  const headers = { ...BASE_SECURITY_HEADERS };
  const frameOrigins = surface === "display"
    ? `http://127.0.0.1:${runtime.controlPort} http://localhost:${runtime.controlPort}`
    : `http://127.0.0.1:${runtime.displayPort} http://localhost:${runtime.displayPort}`;
  if (surface === "control") {
    headers["Content-Security-Policy"] = headers["Content-Security-Policy"].replace(
      "frame-src 'none'",
      `frame-src ${frameOrigins}`
    );
  }
  if (isDisplayDocument) {
    headers["Content-Security-Policy"] = headers["Content-Security-Policy"].replace(
      "frame-ancestors 'none'",
      `frame-ancestors ${frameOrigins}`
    );
    delete headers["X-Frame-Options"];
  }
  return headers;
}

async function serveStatic(request, response, root, surface, runtime, pathname) {
  const allowedPaths = surface === "display" ? DISPLAY_PATHS : CONTROL_PATHS;
  const rootPath = surface === "display" ? "/index.html" : "/control.html";
  if (pathname === "/") pathname = rootPath;
  if (!allowedPaths.has(pathname)) {
    sendText(request, response, 404, "Not Found\n");
    return;
  }

  const assetPathname = pathname === "/favicon.ico" ? "/Logo.png" : pathname;
  const relativePath = assetPathname.replace(/^[/\\]+/, "").replaceAll("/", path.sep);
  const filePath = path.resolve(root, relativePath);
  const rootPrefix = `${root}${path.sep}`;
  if (filePath !== root && !filePath.startsWith(rootPrefix)) {
    sendText(request, response, 404, "Not Found\n");
    return;
  }

  let fileHandle;
  try {
    fileHandle = await open(filePath, "r");
    const fileStat = await fileHandle.stat();
    if (!fileStat.isFile()) {
      await fileHandle.close();
      sendText(request, response, 404, "Not Found\n");
      return;
    }
    const securityHeaders = createStaticSecurityHeaders(
      surface,
      runtime,
      surface === "display" && relativePath === "index.html"
    );
    response.writeHead(200, {
      ...securityHeaders,
      "Content-Type": MIME_TYPES.get(path.extname(filePath).toLowerCase())
        ?? "application/octet-stream",
      "Content-Length": fileStat.size
    });
    if (request.method === "HEAD") {
      await fileHandle.close();
      response.end();
    } else {
      await pipeline(fileHandle.createReadStream(), response);
    }
  } catch (error) {
    if (fileHandle && !fileHandle.closed) await fileHandle.close().catch(() => {});
    if (response.headersSent) {
      response.destroy(error);
    } else if (error?.code === "ENOENT" || error?.code === "EISDIR") {
      sendText(request, response, 404, "Not Found\n");
    } else {
      sendText(request, response, 500, "Internal Server Error\n");
    }
  }
}

export function createServer(rootDirectory, {
  surface = "display",
  sharedState = createSharedState(),
  runtime = {
    displayPort: DEFAULT_DISPLAY_PORT,
    controlPort: DEFAULT_CONTROL_PORT
  }
} = {}) {
  if (!["display", "control"].includes(surface)) {
    throw new TypeError(`Unknown server surface "${surface}".`);
  }
  const root = path.resolve(rootDirectory);

  return createHttpServer(async (request, response) => {
    if (!validateHost(request)) {
      sendText(request, response, 403, "Forbidden\n");
      return;
    }

    let pathname;
    try {
      pathname = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    } catch {
      sendText(request, response, 400, "Bad Request\n");
      return;
    }
    if (pathname.includes("\0")) {
      sendText(request, response, 400, "Bad Request\n");
      return;
    }

    if (pathname === "/api/runtime") {
      if (request.method !== "GET" && request.method !== "HEAD") {
        sendText(request, response, 405, "Method Not Allowed\n", { Allow: "GET, HEAD" });
        return;
      }
      sendJson(request, response, 200, {
        displayPort: runtime.displayPort
      });
      return;
    }

    if (pathname === "/events") {
      if (request.method !== "GET") {
        sendText(request, response, 405, "Method Not Allowed\n", { Allow: "GET" });
        return;
      }
      const requestedRole = new URL(request.url, "http://localhost")
        .searchParams.get("role");
      const role = requestedRole ?? (surface === "display" ? "presentation" : "control");
      const validRole = surface === "display"
        ? ["presentation", "preview"].includes(role)
        : role === "control";
      if (!validRole) {
        sendJson(request, response, 400, { error: "Invalid event stream role." });
        return;
      }
      if (!sharedState.canAddClient(role)) {
        sendJson(request, response, 503, { error: "Event stream capacity reached." }, {
          "Retry-After": "1"
        });
        return;
      }
      response.writeHead(200, {
        ...BASE_SECURITY_HEADERS,
        "Content-Type": "text/event-stream; charset=utf-8",
        Connection: "keep-alive"
      });
      response.write("retry: 1000\n\n");
      sharedState.addClient(role, response);
      request.on("close", () => sharedState.removeClient(role, response));
      return;
    }

    if (pathname === "/api/state") {
      if (request.method === "GET" || request.method === "HEAD") {
        sendJson(request, response, 200, sharedState.getState());
        return;
      }
      if (request.method !== "PATCH") {
        sendText(request, response, 405, "Method Not Allowed\n", {
          Allow: "GET, HEAD, PATCH"
        });
        return;
      }
      if (!validateMutationOrigin(request)) {
        sendText(request, response, 403, "Forbidden\n");
        return;
      }
      const parsed = await readJsonBody(request);
      if (parsed.error) {
        sendText(request, response, parsed.error, parsed.message);
        return;
      }
      const validationError = validateStatePatch(parsed.value, sharedState.getState());
      if (validationError) {
        sendJson(request, response, 400, { error: validationError });
        return;
      }
      sendJson(request, response, 200, sharedState.update(parsed.value));
      return;
    }

    if (pathname === "/api/commands") {
      if (request.method !== "POST") {
        sendText(request, response, 405, "Method Not Allowed\n", { Allow: "POST" });
        return;
      }
      if (!validateMutationOrigin(request)) {
        sendText(request, response, 403, "Forbidden\n");
        return;
      }
      const parsed = await readJsonBody(request);
      if (parsed.error) {
        sendText(request, response, parsed.error, parsed.message);
        return;
      }
      const validationError = validateCommand(parsed.value);
      if (validationError) {
        sendJson(request, response, 400, { error: validationError });
        return;
      }
      sendJson(request, response, 200, sharedState.command(parsed.value));
      return;
    }

    if (pathname === "/api/actions") {
      if (request.method !== "POST") {
        sendText(request, response, 405, "Method Not Allowed\n", { Allow: "POST" });
        return;
      }
      if (!validateMutationOrigin(request)) {
        sendText(request, response, 403, "Forbidden\n");
        return;
      }
      const parsed = await readJsonBody(request);
      if (parsed.error) {
        sendText(request, response, parsed.error, parsed.message);
        return;
      }
      const keys = Object.keys(parsed.value);
      const actionId = parsed.value.id;
      const hasValidKeys = keys.every((key) => key === "type" || key === "id")
        && keys.includes("type")
        && keys.length <= 2;
      const hasValidId = actionId === undefined
        || (
          typeof actionId === "string"
          && actionId.length >= 1
          && actionId.length <= 64
          && /^[a-zA-Z0-9-]+$/.test(actionId)
        );
      if (!hasValidKeys || !hasValidId
        || !ALLOWED_ACTIONS.has(parsed.value.type)) {
        sendJson(request, response, 400, { error: "Unknown or invalid action." });
        return;
      }
      if (!sharedState.hasPresentationClient()) {
        sendJson(request, response, 409, { error: "No presentation display is connected." });
        return;
      }
      const action = Object.freeze(actionId === undefined
        ? { type: parsed.value.type }
        : { type: parsed.value.type, id: actionId });
      let actionState = sharedState.getState();
      if (parsed.value.type === "total.drop") {
        actionState = sharedState.update({ totalBoxVisible: true });
      }
      if (parsed.value.type === "school.add" || parsed.value.type === "school.remove") {
        const schoolMutation = sharedState.recordSchoolAction(parsed.value.type);
        if (!schoolMutation.changed) {
          sendJson(request, response, 409, { error: schoolMutation.error });
          return;
        }
        actionState = schoolMutation.state;
      }
      sharedState.sendAction(action);
      sendJson(request, response, 202, {
        accepted: true,
        action,
        state: actionState
      });
      return;
    }

    if (!["GET", "HEAD"].includes(request.method)) {
      sendText(request, response, 405, "Method Not Allowed\n", { Allow: "GET, HEAD" });
      return;
    }
    await serveStatic(request, response, root, surface, runtime, pathname);
  });
}

function parsePort(value, label) {
  const port = Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new RangeError(`Invalid ${label} port "${value}". Use an integer from 1 through 65535.`);
  }
  return port;
}

function findArgument(args, name) {
  const prefix = `--${name}=`;
  return args.find((argument) => argument.startsWith(prefix))?.slice(prefix.length);
}

export function getPorts(args = [], environment = {}) {
  const displayValue = findArgument(args, "display-port")
    ?? findArgument(args, "port")
    ?? environment.DISPLAY_PORT
    ?? environment.PORT
    ?? DEFAULT_DISPLAY_PORT;
  const controlValue = findArgument(args, "control-port")
    ?? environment.CONTROL_PORT
    ?? DEFAULT_CONTROL_PORT;
  const displayPort = parsePort(displayValue, "display");
  const controlPort = parsePort(controlValue, "control");
  if (displayPort === controlPort) {
    throw new RangeError("Display and control ports must be different.");
  }
  return Object.freeze({ displayPort, controlPort });
}

function listen(server, port) {
  return new Promise((resolve, reject) => {
    const onError = (error) => {
      server.off("listening", onListening);
      reject(error);
    };
    const onListening = () => {
      server.off("error", onError);
      resolve();
    };
    server.once("error", onError);
    server.once("listening", onListening);
    server.listen(port, "127.0.0.1");
  });
}

function closeServer(server) {
  return new Promise((resolve) => {
    if (!server.listening) {
      resolve();
      return;
    }
    server.close(() => resolve());
  });
}

export function createApplication(rootDirectory) {
  const sharedState = createSharedState();
  const runtime = {
    displayPort: DEFAULT_DISPLAY_PORT,
    controlPort: DEFAULT_CONTROL_PORT
  };
  const displayServer = createServer(rootDirectory, {
    surface: "display",
    sharedState,
    runtime
  });
  const controlServer = createServer(rootDirectory, {
    surface: "control",
    sharedState,
    runtime
  });
  const heartbeat = setInterval(() => sharedState.heartbeat(), HEARTBEAT_INTERVAL_MS);
  heartbeat.unref?.();

  return Object.freeze({
    displayServer,
    controlServer,
    async start(displayPort, controlPort) {
      if (displayPort === controlPort) {
        throw new RangeError("Display and control ports must be different.");
      }
      runtime.displayPort = displayPort;
      runtime.controlPort = controlPort;
      const results = await Promise.allSettled([
        listen(displayServer, displayPort),
        listen(controlServer, controlPort)
      ]);
      const failure = results.find((result) => result.status === "rejected");
      if (failure) {
        await Promise.all([
          closeServer(displayServer),
          closeServer(controlServer)
        ]);
        clearInterval(heartbeat);
        throw failure.reason;
      }
    },
    async close() {
      clearInterval(heartbeat);
      sharedState.closeClients();
      await Promise.all([
        closeServer(displayServer),
        closeServer(controlServer)
      ]);
    }
  });
}

const currentFile = fileURLToPath(import.meta.url);
if (process.argv[1] && path.resolve(process.argv[1]) === currentFile) {
  const root = path.dirname(currentFile);
  try {
    const { displayPort, controlPort } = getPorts(process.argv.slice(2), process.env);
    const application = createApplication(root);
    await application.start(displayPort, controlPort);
    console.log(`TCF display running at http://127.0.0.1:${displayPort}`);
    console.log(`TCF control dashboard running at http://127.0.0.1:${controlPort}`);

    const shutdown = async () => {
      await application.close();
      process.exit(0);
    };
    process.once("SIGINT", shutdown);
    process.once("SIGTERM", shutdown);
  } catch (error) {
    console.error(`ERROR: ${error.message}`);
    process.exitCode = 1;
  }
}
