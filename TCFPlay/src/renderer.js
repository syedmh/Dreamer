import { frameIndexForActor, shouldMirrorActor } from "./timeline.js";

export class Renderer {
  constructor(canvas, config, sprites, background) {
    this.canvas = canvas;
    this.context = canvas.getContext("2d");
    this.config = config;
    this.sprites = sprites;
    this.background = background;
    this.reduceMotionQuery = window.matchMedia("(prefers-reduced-motion: reduce)");
    this.resize();
  }

  resize() {
    const rect = this.canvas.getBoundingClientRect();
    const stage = this.config.stage;
    const dpr = window.devicePixelRatio || 1;
    const scale = Math.min(dpr, stage.maxBackingWidth / rect.width, stage.maxBackingHeight / rect.height);
    const width = Math.max(1, Math.round(rect.width * scale));
    const height = Math.max(1, Math.round(rect.height * scale));
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
    }
    this.context.setTransform(width / stage.width, 0, 0, height / stage.height, 0, 0);
    this.context.imageSmoothingEnabled = true;
    this.context.imageSmoothingQuality = "high";
  }

  draw(state, elapsedMs) {
    const context = this.context;
    const stage = this.config.stage;
    context.clearRect(0, 0, stage.width, stage.height);
    this.background.draw(
      context,
      elapsedMs,
      this.reduceMotionQuery.matches
    );
    if (!state) {
      return;
    }
    for (const actor of state.actors.values()) {
      if (actor.visible) {
        this.drawActor(actor);
        if (actor.speech) {
          this.drawSpeechBubble(actor);
        }
      }
    }
  }

  drawActor(actor) {
    const character = this.config.characters[actor.id];
    const index = frameIndexForActor(actor, character.animations);
    this.drawActorFrame(actor, index, 1);
  }

  drawActorFrame(actor, index, opacity) {
    const character = this.config.characters[actor.id];
    const frames = this.sprites.get(actor.id);
    const frame = frames[index];
    const walking = index >= character.walkingFrameStart;
    const anchor = walking
      ? character.walkingAnchor
      : character.frameAnchors?.[index] || character.anchor;
    const scale = walking
      ? character.walkingScale
      : character.frameScales?.[index] || 1;
    const height = character.renderHeight * scale;
    const width = height * (frame.width / frame.height);
    const x = actor.x * this.config.stage.width;
    const y = actor.y * this.config.stage.height;
    const drawX = x - width * anchor.x;
    const drawY = y - height * anchor.y;
    const context = this.context;
    const mirror = shouldMirrorActor(actor, character.animations);
    if (mirror || opacity < 1) {
      context.save();
      context.globalAlpha *= opacity;
    }
    if (mirror) {
      context.translate(x, 0);
      context.scale(-1, 1);
      context.drawImage(
        frame,
        -width * anchor.x,
        drawY,
        width,
        height
      );
    } else {
      context.drawImage(frame, drawX, drawY, width, height);
    }
    if (mirror || opacity < 1) {
      context.restore();
    }
  }

  drawSpeechBubble(actor) {
    const context = this.context;
    const character = this.config.characters[actor.id];
    const stage = this.config.stage;
    const centerX = actor.x * stage.width;
    const maxWidth = 520;
    const padding = 28;
    const lineHeight = 52;
    context.save();
    context.font = '700 42px "Trebuchet MS", "Segoe UI", sans-serif';
    context.textAlign = "center";
    context.textBaseline = "middle";
    const words = actor.speech.split(/\s+/);
    const lines = [];
    let line = "";
    for (const word of words) {
      const candidate = line ? `${line} ${word}` : word;
      if (line && context.measureText(candidate).width > maxWidth - padding * 2) {
        lines.push(line);
        line = word;
      } else {
        line = candidate;
      }
    }
    lines.push(line);

    const measuredWidth = Math.max(...lines.map((text) => context.measureText(text).width));
    const width = Math.min(maxWidth, measuredWidth + padding * 2);
    const height = lines.length * lineHeight + padding * 2;
    const x = Math.max(24, Math.min(stage.width - width - 24, centerX - width / 2));
    const actorTop = actor.y * stage.height - character.renderHeight;
    const y = Math.max(24, actorTop - height - 42);
    const tailX = Math.max(x + 36, Math.min(x + width - 36, centerX));

    context.fillStyle = "#fffdf2";
    context.strokeStyle = "#17364a";
    context.lineWidth = 7;
    context.beginPath();
    context.roundRect(x, y, width, height, 28);
    context.fill();
    context.stroke();
    context.beginPath();
    context.moveTo(tailX - 24, y + height - 2);
    context.lineTo(centerX, Math.min(actorTop - 4, y + height + 38));
    context.lineTo(tailX + 24, y + height - 2);
    context.closePath();
    context.fill();
    context.stroke();

    context.fillStyle = "#17364a";
    lines.forEach((text, index) => {
      context.fillText(text, x + width / 2, y + padding + lineHeight * (index + 0.5));
    });
    context.restore();
  }
}
