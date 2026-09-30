import test from "node:test";
import assert from "node:assert/strict";
import { Renderer } from "../src/renderer.js";

test("speech renders in a comic bubble above the actor", () => {
  const calls = [];
  const context = {
    save() {},
    restore() {},
    beginPath() {},
    fill() {},
    stroke() {},
    closePath() {},
    moveTo() {},
    lineTo() {},
    roundRect(...args) {
      calls.push(["roundRect", ...args]);
    },
    fillText(...args) {
      calls.push(["fillText", ...args]);
    },
    measureText(text) {
      return { width: text.length * 20 };
    }
  };
  const renderer = {
    context,
    config: {
      stage: { width: 1920, height: 1080 },
      characters: { boy: { renderHeight: 500 } }
    }
  };

  Renderer.prototype.drawSpeechBubble.call(renderer, {
    id: "boy",
    x: 0.5,
    y: 0.9,
    speech: "Welcome Seattle"
  });

  const bubble = calls.find(([name]) => name === "roundRect");
  const text = calls.find(([name]) => name === "fillText");
  assert.ok(bubble);
  assert.ok(bubble[2] + bubble[4] < 0.9 * 1080 - 500);
  assert.deepEqual(text.slice(1), ["Welcome Seattle", 960, bubble[2] + 54]);
});

test("per-frame anchors keep the Boy pivot fixed while turning", () => {
  const drawCalls = [];
  const renderer = {
    drawActorFrame: Renderer.prototype.drawActorFrame,
    context: {
      drawImage(...args) {
        drawCalls.push(args);
      }
    },
    config: {
      stage: { width: 1920, height: 1080 },
      characters: {
        boy: {
          renderHeight: 500,
          anchor: { x: 0.5, y: 1 },
          frameAnchors: [
            { x: 0.6, y: 0.998 },
            { x: 0.33, y: 0.99 }
          ],
          animations: {
            front: { frames: [0] },
            turnRight: { frames: [0, 1] }
          }
        }
      }
    },
    sprites: new Map([["boy", [
      { width: 204, height: 502 },
      { width: 204, height: 502 }
    ]]])
  };
  const actor = {
    id: "boy",
    x: 0.5,
    y: 0.9,
    animation: "turnRight",
    animationElapsedMs: 0,
    turnDurationMs: 1000,
    animationReverse: false
  };

  Renderer.prototype.drawActor.call(renderer, actor);
  actor.animationElapsedMs = 600;
  Renderer.prototype.drawActor.call(renderer, actor);

  for (let index = 0; index < drawCalls.length; index += 1) {
    const [, drawX, drawY, width, height] = drawCalls[index];
    const anchor = renderer.config.characters.boy.frameAnchors[index];
    assert.ok(Math.abs(drawX + width * anchor.x - 960) < 0.000001);
    assert.ok(Math.abs(drawY + height * anchor.y - 972) < 0.000001);
  }
});

test("walking renders one opaque calibrated frame without ghost blending", () => {
  const drawCalls = [];
  const context = {
    globalAlpha: 1,
    save() {},
    restore() {
      this.globalAlpha = 1;
    },
    drawImage(...args) {
      drawCalls.push({ alpha: this.globalAlpha, args });
    }
  };
  const renderer = {
    drawActorFrame: Renderer.prototype.drawActorFrame,
    context,
    config: {
      stage: { width: 1920, height: 1080 },
      characters: {
        boy: {
          renderHeight: 500,
          anchor: { x: 0.5, y: 1 },
          walkingFrameStart: 0,
          walkingAnchor: { x: 0.5, y: 1 },
          walkingScale: 1,
          frameAnchors: [
            { x: 0.5, y: 1 },
            { x: 0.5, y: 1 }
          ],
          frameScales: [1, 0.8],
          animations: {
            front: { frames: [0] },
            walkRight: { frames: [0, 1] }
          }
        }
      }
    },
    sprites: new Map([["boy", [
      { width: 100, height: 100 },
      { width: 100, height: 100 }
    ]]])
  };
  const actor = {
    id: "boy",
    x: 0.5,
    y: 0.9,
    animation: "walkRight",
    animationElapsedMs: 62.5,
    animationFps: 8,
    animationReverse: false
  };

  Renderer.prototype.drawActor.call(renderer, actor);

  assert.equal(drawCalls.length, 1);
  assert.deepEqual(drawCalls.map(({ alpha }) => alpha), [1]);
  assert.deepEqual(drawCalls.map(({ args }) => args[4]), [500]);
});
