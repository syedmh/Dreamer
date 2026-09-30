import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { resolveRequestPath, SECURITY_HEADERS } from "../src/server-utils.mjs";

const root = path.resolve("C:/safe/root");

test("server resolves only allowlisted paths", () => {
  assert.equal(resolveRequestPath("/", root).pathname, "/index.html");
  assert.equal(resolveRequestPath("/src/app.js?cache=no", root).pathname, "/src/app.js");
  assert.equal(resolveRequestPath("/src/prompt-compiler.js", root).pathname, "/src/prompt-compiler.js");
  assert.equal(resolveRequestPath("/data/playground.v2.json", root).pathname, "/data/playground.v2.json");
  assert.equal(resolveRequestPath("/assets/boy-turn/turn_01.png", root).pathname, "/assets/boy-turn/turn_01.png");
  assert.equal(resolveRequestPath("/assets/boy-turn/turn_09.png", root).pathname, "/assets/boy-turn/turn_09.png");
  assert.equal(resolveRequestPath("/assets/boy-walk/walk_01.png", root).pathname, "/assets/boy-walk/walk_01.png");
  assert.equal(resolveRequestPath("/assets/boy-walk/walk_06.png", root).pathname, "/assets/boy-walk/walk_06.png");
  assert.equal(resolveRequestPath("/assets/boy-walk/walk_12.png", root).pathname, "/assets/boy-walk/walk_12.png");
  assert.equal(resolveRequestPath("/assets/boy-walk/walk_13.png", root), null);
  assert.equal(resolveRequestPath("/assets/turn_frames_1080p/turn_05.png", root), null);
  assert.equal(resolveRequestPath("/assets/walk_frames_1080p/walk_32.png", root), null);
  assert.equal(resolveRequestPath("/assets/turn_frames_1080p/turn_06.png", root), null);
  assert.equal(resolveRequestPath("/assets/walk_frames_1080p/walk_33.png", root), null);
  assert.equal(resolveRequestPath("/GirlFrames.png", root), null);
  assert.equal(resolveRequestPath("/README.md", root), null);
  assert.equal(resolveRequestPath("/missing.js", root), null);
});

test("server rejects traversal and exposes required security headers", () => {
  assert.equal(resolveRequestPath("/%2e%2e/server.mjs", root), null);
  assert.equal(resolveRequestPath("/src%5capp.js", root), null);
  assert.equal(SECURITY_HEADERS["Cache-Control"], "no-store");
  assert.match(SECURITY_HEADERS["Content-Security-Policy"], /default-src 'self'/);
  assert.equal(SECURITY_HEADERS["X-Content-Type-Options"], "nosniff");
});
