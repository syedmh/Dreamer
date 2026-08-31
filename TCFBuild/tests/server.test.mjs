import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createServer } from "../server.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

async function withServer(run) {
  const server = createServer(root);
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  try {
    const { port } = server.address();
    await run(`http://127.0.0.1:${port}`, port);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

function rawRequest(port, requestPath, method = "GET", headers = {}) {
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
    request.end();
  });
}

test("serves the application with MIME, no-store, and CSP headers", async () => {
  await withServer(async (base) => {
    const response = await fetch(`${base}/`);
    assert.equal(response.status, 200);
    assert.match(response.headers.get("content-type"), /^text\/html/);
    assert.equal(response.headers.get("cache-control"), "no-store");
    assert.match(response.headers.get("content-security-policy"), /default-src 'self'/);
    const page = await response.text();
    assert.match(page, /id="fundraiser"/);
    assert.match(page, /id="school-scene" viewBox="0 0 1600 900"/);
    assert.match(page, /id="raised-input"[^>]*min="0"[^>]*max="9007199254740991"[^>]*step="any"[^>]*required/);
    assert.match(page, /id="goal-input"[^>]*min="0\.01"[^>]*max="7205759403792793"[^>]*step="any"[^>]*required/);
    assert.match(page, /id="raised-slider"[^>]*min="0"[^>]*max="125000"[^>]*step="any"/);
    assert.match(page, /<link rel="icon" href="\/favicon\.ico" type="image\/svg\+xml">/);
    assert.match(
      page,
      /<img class="brand-logo" src="\/Logo\.png" alt="" width="430" height="332" draggable="false">/
    );
    assert.doesNotMatch(page, /brand-sun|brand-book/);

    const moduleResponse = await fetch(`${base}/src/model.mjs`);
    assert.equal(moduleResponse.status, 200);
    assert.match(moduleResponse.headers.get("content-type"), /^text\/javascript/);

    const logoResponse = await fetch(`${base}/Logo.png`);
    assert.equal(logoResponse.status, 200);
    assert.equal(logoResponse.headers.get("content-type"), "image/png");
    assert.ok((await logoResponse.arrayBuffer()).byteLength > 0);
  });
});

test("serves an explicit generated favicon for fresh browser loads", async () => {
  await withServer(async (base, port) => {
    const favicon = await fetch(`${base}/favicon.ico`);
    assert.equal(favicon.status, 200);
    assert.equal(favicon.headers.get("content-type"), "image/svg+xml");
    assert.equal(favicon.headers.get("cache-control"), "no-store");
    assert.equal(favicon.headers.get("x-content-type-options"), "nosniff");
    assert.match(await favicon.text(), /^<svg[^>]+viewBox="0 0 64 64"/);

    const head = await rawRequest(port, "/favicon.ico", "HEAD");
    assert.equal(head.status, 200);
    assert.equal(head.body, "");
    assert.ok(Number(head.headers["content-length"]) > 0);
    assert.equal(head.headers["content-type"], "image/svg+xml");

    const unexpectedIcon = await rawRequest(port, "/favicon.png");
    assert.equal(unexpectedIcon.status, 404);
  });
});

test("supports HEAD and rejects unsupported methods", async () => {
  await withServer(async (base, port) => {
    const head = await rawRequest(port, "/index.html", "HEAD");
    assert.equal(head.status, 200);
    assert.equal(head.body, "");
    assert.ok(Number(head.headers["content-length"]) > 0);

    const post = await fetch(`${base}/`, { method: "POST" });
    assert.equal(post.status, 405);
    assert.equal(post.headers.get("allow"), "GET, HEAD");
  });
});

test("returns explicit 404 and prevents encoded traversal", async () => {
  await withServer(async (_base, port) => {
    const missing = await rawRequest(port, "/missing.html");
    assert.equal(missing.status, 404);
    assert.equal(missing.body, "Not Found\n");

    const traversal = await rawRequest(port, "/%2e%2e%2fserver.mjs");
    assert.equal(traversal.status, 404);
    assert.doesNotMatch(traversal.body, /createServer/);

    const localReference = await rawRequest(port, "/Building1.jpg");
    assert.equal(localReference.status, 404);
    assert.equal(localReference.body, "Not Found\n");

    const encodedReference = await rawRequest(port, "/%5cBuilding2.jpg");
    assert.equal(encodedReference.status, 404);

    for (const protectedPath of [
      "/Building1.jpg::$DATA",
      "/Building2.jpg:Zone.Identifier",
      "/BUILDI~1.JPG",
      "/BUILDI~2.JPG",
      "/server.mjs",
      "/README.md",
      "/tests/server.test.mjs"
    ]) {
      const protectedResponse = await rawRequest(port, protectedPath);
      assert.equal(protectedResponse.status, 404, protectedPath);
    }
  });
});

test("serves only approved browser assets and rejects unexpected hosts", async () => {
  await withServer(async (_base, port) => {
    const harness = await rawRequest(port, "/tests/harness.html");
    assert.equal(harness.status, 200);
    assert.match(harness.headers["content-type"], /^text\/html/);
    const inlineScript = harness.body.match(/<script(?:\s[^>]*)?>([\s\S]*?)<\/script>/)?.[1];
    assert.ok(inlineScript);
    const scriptHash = createHash("sha256")
      .update(inlineScript.replace(/\r\n/g, "\n"))
      .digest("base64");
    assert.ok(harness.headers["content-security-policy"].includes(`'sha256-${scriptHash}'`));

    const untrustedHost = await rawRequest(port, "/", "GET", {
      Host: `attacker.example:${port}`
    });
    assert.equal(untrustedHost.status, 403);
    assert.equal(untrustedHost.body, "Forbidden\n");
    assert.equal(untrustedHost.headers["cross-origin-resource-policy"], "same-origin");
  });
});
