import test from "node:test";
import assert from "node:assert/strict";
import { compilePrompt } from "../src/prompt-compiler.js";

const characters = { boy: { name: "Boy" } };
const prompt = "Start middle of screen front facing then turn right";

test("Boy prompt compiles into the approved turn-only scene", () => {
  assert.deepEqual(compilePrompt(prompt, characters), [
    { type: "spawn", actor: "boy", x: 0.5, y: 0.9, facing: "front", animation: "front" },
    { type: "turn", actor: "boy", facing: "right", durationMs: 1600, fps: 6 }
  ]);
});

test("unsupported prompts and missing Boy fail explicitly", () => {
  assert.throws(() => compilePrompt("Boy turn left", characters), /unsupported prompt/);
  assert.throws(() => compilePrompt(prompt, {}), /character "boy" is required/);
});
