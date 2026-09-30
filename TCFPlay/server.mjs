import { createReadStream } from "node:fs";
import { stat } from "node:fs/promises";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { resolveRequestPath, SECURITY_HEADERS } from "./src/server-utils.mjs";

const host = "127.0.0.1";
const requestedPort = Number.parseInt(process.env.PORT || "8080", 10);
if (!Number.isInteger(requestedPort) || requestedPort < 0 || requestedPort > 65535) {
  throw new Error("PORT must be an integer between 0 and 65535");
}

const rootDirectory = path.dirname(fileURLToPath(import.meta.url));
const server = createServer(async (request, response) => {
  for (const [name, value] of Object.entries(SECURITY_HEADERS)) {
    response.setHeader(name, value);
  }
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.writeHead(405, { Allow: "GET, HEAD", "Content-Type": "text/plain; charset=utf-8" });
    response.end("Method not allowed\n");
    return;
  }

  const resolved = resolveRequestPath(request.url || "/", rootDirectory);
  if (!resolved) {
    response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    response.end("Not found\n");
    return;
  }

  try {
    const details = await stat(resolved.filePath);
    if (!details.isFile()) {
      throw new Error("Not a file");
    }
    response.writeHead(200, {
      "Content-Type": resolved.mimeType,
      "Content-Length": details.size
    });
    if (request.method === "HEAD") {
      response.end();
    } else {
      createReadStream(resolved.filePath).pipe(response);
    }
  } catch {
    response.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
    response.end("Not found\n");
  }
});

server.on("error", (error) => {
  console.error(`Server error: ${error.message}`);
  process.exitCode = 1;
});

server.listen(requestedPort, host, () => {
  const address = server.address();
  console.log(`TCFPlay available at http://${host}:${address.port}/`);
});
