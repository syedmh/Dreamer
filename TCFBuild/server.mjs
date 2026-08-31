import { open } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import path from "node:path";
import { pipeline } from "node:stream/promises";
import { fileURLToPath } from "node:url";

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
  "Content-Security-Policy": "default-src 'self'; script-src 'self' 'sha256-BkeEUBlTdVBoX0gJczrEmcNJxz4kkzzydov9AiKszHE='; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'",
  "Cross-Origin-Resource-Policy": "same-origin",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY"
});

const DEFAULT_PORT = 8080;

const PUBLIC_PATHS = new Set([
  "/favicon.ico",
  "/index.html",
  "/Logo.png",
  "/styles.css",
  "/src/app.mjs",
  "/src/config.mjs",
  "/src/currency.mjs",
  "/src/model.mjs",
  "/src/render.mjs",
  "/src/scene.mjs",
  "/tests/harness.html"
]);

function sendText(response, statusCode, text, extraHeaders = {}) {
  const body = Buffer.from(text);
  response.writeHead(statusCode, {
    ...SECURITY_HEADERS,
    "Content-Type": "text/plain; charset=utf-8",
    "Content-Length": body.length,
    ...extraHeaders
  });
  response.end(body);
}

export function createServer(rootDirectory) {
  const root = path.resolve(rootDirectory);
  const rootPrefix = `${root}${path.sep}`;

  return createHttpServer(async (request, response) => {
    if (!["GET", "HEAD"].includes(request.method)) {
      sendText(response, 405, "Method Not Allowed\n", { Allow: "GET, HEAD" });
      return;
    }

    const localPort = request.socket.localPort;
    const allowedHosts = new Set([
      `127.0.0.1:${localPort}`,
      `localhost:${localPort}`
    ]);
    if (!allowedHosts.has(request.headers.host)) {
      sendText(response, 403, "Forbidden\n");
      return;
    }

    let pathname;
    try {
      pathname = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
    } catch {
      sendText(response, 400, "Bad Request\n");
      return;
    }

    if (pathname.includes("\0")) {
      sendText(response, 400, "Bad Request\n");
      return;
    }

    if (pathname === "/") pathname = "/index.html";
    if (!PUBLIC_PATHS.has(pathname)) {
      sendText(response, 404, "Not Found\n");
      return;
    }
    const assetPathname = pathname === "/favicon.ico" ? "/Logo.png" : pathname;
    const relativePath = assetPathname.replace(/^[/\\]+/, "").replaceAll("/", path.sep);
    const filePath = path.resolve(root, relativePath);
    if (filePath !== root && !filePath.startsWith(rootPrefix)) {
      sendText(response, 404, "Not Found\n");
      return;
    }

    let fileHandle;
    try {
      fileHandle = await open(filePath, "r");
      const fileStat = await fileHandle.stat();
      if (!fileStat.isFile()) {
        await fileHandle.close();
        sendText(response, 404, "Not Found\n");
        return;
      }
      const headers = {
        ...SECURITY_HEADERS,
        "Content-Type": MIME_TYPES.get(path.extname(filePath).toLowerCase()) ?? "application/octet-stream",
        "Content-Length": fileStat.size
      };
      response.writeHead(200, headers);
      if (request.method === "HEAD") {
        await fileHandle.close();
        response.end();
      } else {
        await pipeline(fileHandle.createReadStream(), response);
      }
    } catch (error) {
      if (fileHandle && !fileHandle.closed) {
        await fileHandle.close().catch(() => {});
      }
      if (response.headersSent) {
        response.destroy(error);
      } else if (error?.code === "ENOENT" || error?.code === "EISDIR") {
        sendText(response, 404, "Not Found\n");
      } else {
        sendText(response, 500, "Internal Server Error\n");
      }
    }
  });
}

function getPort(args, environment) {
  const portArgument = args.find((argument) => argument.startsWith("--port="));
  const value = portArgument?.slice("--port=".length) || environment.PORT || DEFAULT_PORT;
  const port = Number(value);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new RangeError(`Invalid port "${value}". Use an integer from 1 through 65535.`);
  }
  return port;
}

const currentFile = fileURLToPath(import.meta.url);
if (process.argv[1] && path.resolve(process.argv[1]) === currentFile) {
  const root = path.dirname(currentFile);
  const port = getPort(process.argv.slice(2), process.env);
  createServer(root).listen(port, "127.0.0.1", () => {
    console.log(`TCF fundraiser running at http://127.0.0.1:${port}`);
  });
}
