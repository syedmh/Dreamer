import test from "node:test";
import assert from "node:assert/strict";
import { commandForKeyboardEvent, isEditableTarget } from "../src/controls.js";

const keyMap = new Map([["Digit1", "welcome"]]);

test("key mapping uses code and rejects modified or repeated input", () => {
  const base = { code: "Digit1", repeat: false, ctrlKey: false, metaKey: false, altKey: false, target: null };
  assert.deepEqual(commandForKeyboardEvent(base, keyMap), { type: "scene", sceneId: "welcome" });
  assert.equal(commandForKeyboardEvent({ ...base, repeat: true }, keyMap), null);
  assert.equal(commandForKeyboardEvent({ ...base, ctrlKey: true }, keyMap), null);
  assert.deepEqual(commandForKeyboardEvent({ ...base, code: "Escape" }, keyMap), { type: "dismiss" });
});

test("editable targets are ignored", () => {
  assert.equal(isEditableTarget({ tagName: "INPUT" }), true);
  assert.equal(isEditableTarget({ tagName: "DIV", isContentEditable: true }), true);
  assert.equal(commandForKeyboardEvent({
    code: "Digit1",
    repeat: false,
    ctrlKey: false,
    metaKey: false,
    altKey: false,
    target: { tagName: "TEXTAREA" }
  }, keyMap), null);
});
