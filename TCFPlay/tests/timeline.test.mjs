import test from "node:test";
import assert from "node:assert/strict";
import {
  advanceTimeline,
  createSceneState,
  dismissScene,
  frameIndexForActor
} from "../src/timeline.js";

const animations = {
  front: { frames: [0] },
  turnRight: { frames: [0, 1, 2, 3, 4, 5, 6, 7, 8] },
  walkRight: { frames: Array.from({ length: 24 }, (_, index) => index + 9) }
};

function crossingScene() {
  return {
    id: "boy-cross-stage",
    actions: [
      { type: "spawn", actor: "boy", x: -0.12, y: 0.9, facing: "right" },
      { type: "moveTo", actor: "boy", x: 0.5, y: 0.9, durationMs: 4500, walkFps: 24 },
      { type: "turn", actor: "boy", facing: "front", durationMs: 1600, fps: 6 },
      { type: "wait", durationMs: 600 },
      { type: "turn", actor: "boy", facing: "right", durationMs: 1600, fps: 6 },
      { type: "moveTo", actor: "boy", x: 1.12, y: 0.9, durationMs: 4500, walkFps: 24 }
    ]
  };
}

test("Boy walks from the left edge to center using all twenty-four frames", () => {
  const state = createSceneState(crossingScene());
  advanceTimeline(state, 0);
  const actor = state.actors.get("boy");
  assert.equal(actor.x, -0.12);
  assert.equal(actor.animation, "walkRight");

  const observed = new Set();
  for (let offset = 0; offset < 24; offset += 1) {
    actor.animationElapsedMs = offset * (1000 / 24) + 0.01;
    observed.add(frameIndexForActor(actor, animations));
  }
  assert.deepEqual([...observed].sort((a, b) => a - b), Array.from({ length: 24 }, (_, index) => index + 9));

  advanceTimeline(state, 4500);
  assert.equal(actor.x, 0.5);
  assert.equal(actor.animation, "turnRight");
  assert.equal(actor.animationReverse, true);
  assert.equal(frameIndexForActor(actor, animations), 8);
});

test("Boy faces front, turns right, and walks off the right edge", () => {
  const state = createSceneState(crossingScene());
  advanceTimeline(state, 4500);
  const actor = state.actors.get("boy");

  advanceTimeline(state, 800);
  assert.equal(frameIndexForActor(actor, animations), 4);
  advanceTimeline(state, 800);
  assert.equal(actor.facing, "front");
  assert.equal(actor.animation, "front");

  advanceTimeline(state, 600);
  assert.equal(actor.animation, "turnRight");
  assert.equal(actor.animationReverse, false);
  assert.equal(frameIndexForActor(actor, animations), 0);

  advanceTimeline(state, 1600);
  assert.equal(actor.facing, "right");
  assert.equal(actor.animation, "walkRight");
  assert.equal(actor.x, 0.5);

  advanceTimeline(state, 4500);
  assert.equal(actor.x, 1.12);
  assert.equal(state.complete, true);
});

test("Escape dismisses the Boy anywhere in the crossing", () => {
  const state = createSceneState(crossingScene());
  advanceTimeline(state, 3000);
  dismissScene(state);
  assert.equal(state.actors.get("boy").visible, false);
  assert.equal(state.complete, true);
});
