import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const indexUrl = new URL("../index.html", import.meta.url);

test("index declares an inline favicon to avoid a browser fallback request", async () => {
  const html = await readFile(indexUrl, "utf8");

  assert.match(html, /<link rel="icon" href="data:image\/svg\+xml,[^"]+">/);
});
