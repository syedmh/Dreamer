export function createSceneState(scene) {
  return {
    sceneId: scene.id,
    scene,
    actionIndex: 0,
    actionElapsedMs: 0,
    actionStarted: false,
    actors: new Map(),
    dismissed: false,
    complete: false
  };
}

function actorFor(state, actorId) {
  const actor = state.actors.get(actorId);
  if (!actor) {
    throw new Error(`Timeline error: actor "${actorId}" has not been spawned`);
  }
  return actor;
}

function beginAction(state, action) {
  state.actionStarted = true;
  if (action.type === "spawn") {
    state.actors.set(action.actor, {
      id: action.actor,
      x: action.x,
      y: action.y,
      facing: action.facing,
      animation: "front",
      animationElapsedMs: 0,
      turnDurationMs: 0,
      animationReverse: false,
      animationFps: 1,
      speech: null,
      visible: true
    });
    return true;
  }
  if (action.type === "turn") {
    const actor = actorFor(state, action.actor);
    if (action.facing === "front") {
      if (actor.facing !== "left" && actor.facing !== "right") {
        throw new Error(`Timeline error: actor "${action.actor}" must face left or right before turning front`);
      }
      actor.animation = actor.facing === "left" ? "turnLeft" : "turnRight";
      actor.animationReverse = true;
    } else {
      actor.animation = action.facing === "left" ? "turnLeft" : "turnRight";
      actor.animationReverse = false;
    }
    actor.animationElapsedMs = 0;
    actor.turnDurationMs = action.durationMs;
  } else if (action.type === "moveTo") {
    const actor = actorFor(state, action.actor);
    actor.startX = actor.x;
    actor.startY = actor.y;
    actor.targetX = action.x;
    actor.targetY = action.y;
    actor.facing = action.x < actor.x ? "left" : "right";
    actor.animation = actor.facing === "left" ? "walkLeft" : "walkRight";
    actor.animationElapsedMs = 0;
    actor.animationFps = action.walkFps;
    actor.animationReverse = false;
  } else if (action.type === "say") {
    actorFor(state, action.actor).speech = action.text;
  }
  return false;
}

function applyTimedAction(state, action, elapsedMs) {
  const duration = Math.max(0, action.durationMs);
  if (action.type === "turn") {
    actorFor(state, action.actor).animationElapsedMs = elapsedMs;
  } else if (action.type === "moveTo") {
    const actor = actorFor(state, action.actor);
    const progress = duration === 0 ? 1 : Math.min(1, elapsedMs / duration);
    actor.x = actor.startX + (actor.targetX - actor.startX) * progress;
    actor.y = actor.startY + (actor.targetY - actor.startY) * progress;
    actor.animationElapsedMs = elapsedMs;
  }
  return duration === 0 || elapsedMs >= duration;
}

function finishTimedAction(state, action) {
  if (action.type === "say") {
    actorFor(state, action.actor).speech = null;
    return;
  }
  if (action.type === "moveTo") {
    const actor = actorFor(state, action.actor);
    actor.x = actor.targetX;
    actor.y = actor.targetY;
    return;
  }
  if (action.type === "turn") {
    const actor = actorFor(state, action.actor);
    actor.facing = action.facing;
    if (action.facing === "front") {
      actor.animation = "front";
      actor.animationElapsedMs = 0;
      actor.animationReverse = false;
    } else {
      actor.animation = action.facing === "left" ? "turnLeft" : "turnRight";
      actor.animationElapsedMs = action.durationMs;
    }
  }
}

export function advanceTimeline(state, deltaMs) {
  if (state.complete) {
    return state;
  }
  let remaining = Math.max(0, deltaMs);
  let safety = 0;
  while (!state.complete && safety < 100) {
    safety += 1;
    const action = state.scene.actions[state.actionIndex];
    if (!action) {
      state.complete = true;
      break;
    }
    if (!state.actionStarted && beginAction(state, action)) {
      state.actionIndex += 1;
      state.actionElapsedMs = 0;
      state.actionStarted = false;
      continue;
    }
    const duration = Math.max(0, action.durationMs);
    const consumed = Math.min(remaining, Math.max(0, duration - state.actionElapsedMs));
    state.actionElapsedMs += consumed;
    remaining -= consumed;
    if (applyTimedAction(state, action, state.actionElapsedMs)) {
      finishTimedAction(state, action);
      state.actionIndex += 1;
      state.actionElapsedMs = 0;
      state.actionStarted = false;
      continue;
    }
    break;
  }
  return state;
}

export function dismissScene(state) {
  if (!state || state.dismissed) {
    return state;
  }
  state.dismissed = true;
  state.complete = true;
  for (const actor of state.actors.values()) {
    actor.speech = null;
    actor.visible = false;
  }
  return state;
}

export function frameIndexForActor(actor, animations) {
  if (actor.animation === "walkRight" || actor.animation === "walkLeft") {
    const frames = animations[actor.animation].frames;
    const frame = Math.floor((actor.animationElapsedMs / 1000) * actor.animationFps);
    return frames[frame % frames.length];
  }
  if (actor.animation !== "turnRight" && actor.animation !== "turnLeft") {
    return animations.front.frames[0];
  }
  const frames = animations[actor.animation].frames;
  const progress = Math.min(1, actor.animationElapsedMs / Math.max(1, actor.turnDurationMs));
  const forwardIndex = Math.min(frames.length - 1, Math.floor(progress * frames.length));
  return frames[actor.animationReverse ? frames.length - 1 - forwardIndex : forwardIndex];
}

export function shouldMirrorActor(actor, animations) {
  return animations[actor.animation]?.mirror === true;
}
