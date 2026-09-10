import { open } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import path from "node:path";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";
import {
  deriveSliderMaximum,
  MAX_GOAL,
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

const SECURITY_HEADERS = Object.freeze({
  "Cache-Control": "no-store",
  "Content-Security-Policy": "default-src 'self'; script-src 'self' 'sha256-5T7sWeLQQ4jOrxFZHC2c5KDWDLW/9fl6mf5bWn5D1IM='; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  "Cross-Origin-Resource-Policy": "same-origin",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY"
});

const DEFAULT_DISPLAY_PORT = 8080;
const DEFAULT_CONTROL_PORT = 8081;
const JSON_BODY_LIMIT = 4096;
const HEARTBEAT_INTERVAL_MS = 15000;
const BOOLEAN_STATE_FIELDS = new Set([
  "demoActive",
  "nightMode",
  "studentsClapping",
  "thankYouVisible",
  "continuousFireworks"
]);
const ALLOWED_STATE_FIELDS = new Set([
  "raised",
  "goal",
  ...BOOLEAN_STATE_FIELDS
]);
const ALLOWED_ACTIONS = new Set([
  "kite.add",
  "kite.clear",
  "firework.launch",
  "firework.clear",
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
    ...SECURITY_HEADERS,
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

export function createSharedState(initialState = {}) {
  let state = Object.freeze({
    raised: 0,
    goal: 100000,
    demoActive: false,
    nightMode: false,
    studentsClapping: false,
    thankYouVisible: true,
    continuousFireworks: false,
    ...initialState
  });
  const clients = {
    display: new Set(),
    control: new Set()
  };

  function writeEvent(client, event, value) {
    client.write(`event: ${event}\ndata: ${JSON.stringify(value)}\n\n`);
  }

  function broadcast(event, value, surfaces = ["display", "control"]) {
    for (const surface of surfaces) {
      for (const client of clients[surface]) {
        writeEvent(client, event, value);
      }
    }
  }

  function broadcastPresence() {
    broadcast("presence", { displayConnected: clients.display.size > 0 });
  }

  return Object.freeze({
    getState: () => state,
    hasDisplayClient: () => clients.display.size > 0,
    addClient(surface, response) {
      clients[surface].add(response);
      writeEvent(response, "snapshot", {
        state,
        displayConnected: clients.display.size > 0
      });
      if (surface === "display") broadcastPresence();
    },
    removeClient(surface, response) {
      const removed = clients[surface].delete(response);
      if (removed && surface === "display") broadcastPresence();
    },
    update(patch) {
      state = Object.freeze({ ...state, ...patch });
      broadcast("state", state);
      return state;
    },
    sendAction(action) {
      broadcast("action", action);
    },
    heartbeat() {
      for (const surface of ["display", "control"]) {
        for (const client of clients[surface]) client.write(": heartbeat\n\n");
      }
    },
    closeClients() {
      for (const surface of ["display", "control"]) {
        for (const client of clients[surface]) client.end();
        clients[surface].clear();
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
    return "raised must be no more than 135% of goal.";
  }
  return null;
}

async function serveStatic(request, response, root, surface, pathname) {
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
    response.writeHead(200, {
      ...SECURITY_HEADERS,
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
  sharedState = createSharedState()
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

    if (pathname === "/events") {
      if (request.method !== "GET") {
        sendText(request, response, 405, "Method Not Allowed\n", { Allow: "GET" });
        return;
      }
      response.writeHead(200, {
        ...SECURITY_HEADERS,
        "Content-Type": "text/event-stream; charset=utf-8",
        Connection: "keep-alive"
      });
      response.write("retry: 1000\n\n");
      sharedState.addClient(surface, response);
      request.on("close", () => sharedState.removeClient(surface, response));
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
      if (!sharedState.hasDisplayClient()) {
        sendJson(request, response, 409, { error: "No display is connected." });
        return;
      }
      const action = Object.freeze(actionId === undefined
        ? { type: parsed.value.type }
        : { type: parsed.value.type, id: actionId });
      sharedState.sendAction(action);
      sendJson(request, response, 202, { accepted: true, action });
      return;
    }

    if (!["GET", "HEAD"].includes(request.method)) {
      sendText(request, response, 405, "Method Not Allowed\n", { Allow: "GET, HEAD" });
      return;
    }
    await serveStatic(request, response, root, surface, pathname);
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
  const displayServer = createServer(rootDirectory, {
    surface: "display",
    sharedState
  });
  const controlServer = createServer(rootDirectory, {
    surface: "control",
    sharedState
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
