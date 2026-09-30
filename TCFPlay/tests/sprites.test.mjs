import test from "node:test";
import assert from "node:assert/strict";
import { loadCharacterSprites } from "../src/sprites.js";

test("sprite loader uses nine turn frames and six review walk frames directly", async () => {
  const previousImage = globalThis.Image;
  const loadedUrls = [];

  class FakeImage {
    addEventListener(type, listener) {
      if (type === "load") {
        this.loadListener = listener;
      }
    }

    set src(value) {
      loadedUrls.push(value);
      queueMicrotask(() => this.loadListener());
    }
  }

  globalThis.Image = FakeImage;
  try {
    const frames = [
      ...Array.from(
        { length: 9 },
        (_, index) => `turn_${String(index + 1).padStart(2, "0")}.png`
      ),
      ...Array.from(
        { length: 6 },
        (_, index) => `walk_${String(index + 1).padStart(2, "0")}.png`
      )
    ];
    const sprites = await loadCharacterSprites({ frames });
    assert.equal(sprites.length, 15);
    assert.deepEqual(loadedUrls, frames);
  } finally {
    globalThis.Image = previousImage;
  }
});
