import test from "node:test";
import assert from "node:assert/strict";
import {
  cloudOffsetAt,
  flagWaveAt,
  swingSeatAt
} from "../src/background.js";

test("cloud drift wraps cleanly after one duration", () => {
  const start = cloudOffsetAt(9000, -450, 1500, 34, -8);
  const wrapped = cloudOffsetAt(43000, -450, 1500, 34, -8);
  assert.ok(Math.abs(start - wrapped) < 0.000001);
  assert.ok(start >= -450 && start <= 1500);
});

test("flag wave stays bounded and repeats every 2.8 seconds", () => {
  for (let elapsedMs = 0; elapsedMs <= 2800; elapsedMs += 100) {
    assert.ok(Math.abs(flagWaveAt(elapsedMs)) <= 6);
  }
  assert.ok(Math.abs(flagWaveAt(700) - 6) < 0.000001);
  assert.ok(Math.abs(flagWaveAt(0) - flagWaveAt(2800)) < 0.000001);
});

test("swing seats remain attached to their pivots and counter-swing", () => {
  const length = 80;
  const first = swingSeatAt(1200, length);
  const second = swingSeatAt(1200, length, Math.PI);
  assert.ok(Math.abs(Math.hypot(first.x, first.y) - length) < 0.000001);
  assert.ok(Math.abs(first.x + second.x) < 0.000001);
  assert.ok(Math.abs(first.y - second.y) < 0.000001);
});
