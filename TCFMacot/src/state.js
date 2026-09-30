import {
  BRAKING_DECELERATION_PX_PER_SECOND_SQUARED,
  BUBBLE_DURATION_MS,
  BUBBLE_TEXT,
  CLAP_DURATION_MS,
  EDGE_PADDING_PX,
  GAIT_CYCLE_DISTANCE_PX,
  GAIT_REFERENCE_AVATAR_HEIGHT_PX,
  MAX_FRAME_DELTA_MS,
  MOVEMENT_ACCELERATION_PX_PER_SECOND_SQUARED,
  MOVEMENT_SPEED_PX_PER_SECOND,
} from './constants.js';

const VELOCITY_EPSILON = 1e-9;

function requireFiniteNumber(value, name) {
  if (!Number.isFinite(value)) {
    throw new TypeError(`${name} must be a finite number`);
  }

  return value;
}

function normalizeDimension(value, name) {
  return Math.max(0, requireFiniteNumber(value, name));
}

function normalizeAvatarHeight(value, name) {
  const height = normalizeDimension(value, name);
  return height > 0 ? height : GAIT_REFERENCE_AVATAR_HEIGHT_PX;
}

function normalizeVelocity(value) {
  return Math.abs(value) <= VELOCITY_EPSILON ? 0 : value;
}

function signOf(value) {
  if (value > VELOCITY_EPSILON) {
    return 1;
  }

  if (value < -VELOCITY_EPSILON) {
    return -1;
  }

  return 0;
}

function resolveFacing(facing, velocityX, requestedIntent) {
  const velocityDirection = signOf(velocityX);

  if (velocityDirection !== 0) {
    return velocityDirection;
  }

  if (requestedIntent !== 0) {
    return requestedIntent;
  }

  return facing;
}

function boundsFor(layout) {
  const availableSpace = Math.max(0, layout.viewportWidth - layout.avatarWidth);
  const inset = Math.min(layout.edgePadding, availableSpace / 2);

  return {
    min: inset,
    max: Math.max(inset, availableSpace - inset),
  };
}

function clampX(x, layout) {
  const bounds = boundsFor(layout);
  return Math.min(bounds.max, Math.max(bounds.min, x));
}

function directionForCode(code) {
  if (code === 'ArrowLeft') {
    return -1;
  }

  if (code === 'ArrowRight') {
    return 1;
  }

  return 0;
}

function movementIntent(held, inputIntent) {
  if (held.left && held.right) {
    return inputIntent;
  }

  if (held.left) {
    return -1;
  }

  if (held.right) {
    return 1;
  }

  return 0;
}

function updateInputIntent(held, inputIntent, releasedCode) {
  const releasedDirection = directionForCode(releasedCode);

  if (held.left && held.right) {
    return inputIntent === releasedDirection ? -releasedDirection : inputIntent;
  }

  if (held.left) {
    return -1;
  }

  if (held.right) {
    return 1;
  }

  return 0;
}

function integrateVelocity(currentVelocity, targetVelocity, accelerationMagnitude, dtSeconds) {
  const distanceToTarget = targetVelocity - currentVelocity;

  if (distanceToTarget === 0 || dtSeconds <= 0) {
    return {
      velocityX: currentVelocity,
      distanceX: currentVelocity * dtSeconds,
    };
  }

  const direction = Math.sign(distanceToTarget);
  const stepVelocity = accelerationMagnitude * dtSeconds;

  if (Math.abs(distanceToTarget) > stepVelocity) {
    const nextVelocity = currentVelocity + (direction * stepVelocity);

    return {
      velocityX: nextVelocity,
      distanceX: ((currentVelocity + nextVelocity) / 2) * dtSeconds,
    };
  }

  const timeToTarget = Math.abs(distanceToTarget) / accelerationMagnitude;
  const acceleratedDistance = ((currentVelocity + targetVelocity) / 2) * timeToTarget;
  const cruiseDuration = dtSeconds - timeToTarget;

  return {
    velocityX: targetVelocity,
    distanceX: acceleratedDistance + (targetVelocity * cruiseDuration),
  };
}

function integrateMotion(state, targetNow, requestedIntent = movementIntent(state.held, state.inputIntent)) {
  const now = Math.max(state.now, requireFiniteNumber(targetNow, 'event.now'));
  const elapsedMs = Math.min(MAX_FRAME_DELTA_MS, Math.max(0, now - state.now));

  if (elapsedMs === 0) {
    if (now === state.now) {
      return state;
    }

    return {
      ...state,
      now,
    };
  }

  const dtSeconds = elapsedMs / 1000;
  const targetVelocity = requestedIntent * MOVEMENT_SPEED_PX_PER_SECOND;
  const velocityDirection = signOf(state.velocityX);
  const accelerationMagnitude = requestedIntent !== 0
    && (velocityDirection === 0 || velocityDirection === requestedIntent)
    ? MOVEMENT_ACCELERATION_PX_PER_SECOND_SQUARED
    : BRAKING_DECELERATION_PX_PER_SECOND_SQUARED;
  const integrated = integrateVelocity(
    state.velocityX,
    targetVelocity,
    accelerationMagnitude,
    dtSeconds,
  );
  const unclampedX = state.x + integrated.distanceX;
  const nextX = clampX(unclampedX, state.layout);
  const actualDistanceX = nextX - state.x;
  const hitHorizontalBound = nextX !== unclampedX;
  let velocityX = normalizeVelocity(integrated.velocityX);

  if (hitHorizontalBound) {
    velocityX = 0;
  }

  return {
    ...state,
    now,
    x: nextX,
    velocityX,
    gaitDistance: state.gaitDistance + Math.abs(actualDistanceX),
  };
}

function createHeldState() {
  return {
    left: false,
    right: false,
  };
}

export function createInitialState(options = {}) {
  const now = requireFiniteNumber(options.now ?? 0, 'now');
  const layout = {
    viewportWidth: normalizeDimension(options.viewportWidth ?? 1280, 'viewportWidth'),
    avatarWidth: normalizeDimension(options.avatarWidth ?? 240, 'avatarWidth'),
    avatarHeight: normalizeAvatarHeight(
      options.avatarHeight ?? GAIT_REFERENCE_AVATAR_HEIGHT_PX,
      'avatarHeight',
    ),
    edgePadding: normalizeDimension(options.edgePadding ?? EDGE_PADDING_PX, 'edgePadding'),
  };
  const centeredX = (layout.viewportWidth - layout.avatarWidth) / 2;
  const requestedX = options.x === undefined
    ? centeredX
    : requireFiniteNumber(options.x, 'x');

  return {
    now,
    layout,
    x: clampX(requestedX, layout),
    velocityX: 0,
    gaitDistance: 0,
    held: createHeldState(),
    inputIntent: 0,
    facing: 1,
    bubbleStartedAt: null,
    bubbleUntil: null,
    clapStartedAt: null,
    clapUntil: null,
  };
}

function handleKeyDown(state, event) {
  if (event.repeat === true) {
    return state;
  }

  const actionNow = Math.max(
    state.now,
    requireFiniteNumber(event.now ?? state.now, 'event.now'),
  );

  switch (event.code) {
    case 'ArrowLeft': {
      const nextState = integrateMotion(state, actionNow);
      const inputIntent = -1;

      return {
        ...nextState,
        held: { ...nextState.held, left: true },
        inputIntent,
        facing: resolveFacing(nextState.facing, nextState.velocityX, inputIntent),
      };
    }
    case 'ArrowRight': {
      const nextState = integrateMotion(state, actionNow);
      const inputIntent = 1;

      return {
        ...nextState,
        held: { ...nextState.held, right: true },
        inputIntent,
        facing: resolveFacing(nextState.facing, nextState.velocityX, inputIntent),
      };
    }
    case 'Space':
      return {
        ...state,
        bubbleStartedAt: actionNow,
        bubbleUntil: actionNow + BUBBLE_DURATION_MS,
      };
    case 'Digit1':
    case 'Numpad1':
      return {
        ...state,
        clapStartedAt: actionNow,
        clapUntil: actionNow + CLAP_DURATION_MS,
      };
    default:
      return state;
  }
}

function handleKeyUp(state, event) {
  let held;

  if (event.code === 'ArrowLeft' && state.held.left) {
    held = { ...state.held, left: false };
  } else if (event.code === 'ArrowRight' && state.held.right) {
    held = { ...state.held, right: false };
  } else {
    return state;
  }

  const actionNow = Math.max(
    state.now,
    requireFiniteNumber(event.now ?? state.now, 'event.now'),
  );
  const nextState = integrateMotion(state, actionNow);

  const inputIntent = updateInputIntent(held, nextState.inputIntent, event.code);

  return {
    ...nextState,
    held,
    inputIntent,
    facing: resolveFacing(nextState.facing, nextState.velocityX, inputIntent),
  };
}

function handleFrame(state, event) {
  const nextState = integrateMotion(state, event.now);
  const requestedIntent = movementIntent(nextState.held, nextState.inputIntent);

  return {
    ...nextState,
    facing: resolveFacing(nextState.facing, nextState.velocityX, requestedIntent),
  };
}

function handleLayout(state, event) {
  const layout = {
    viewportWidth: normalizeDimension(event.viewportWidth, 'event.viewportWidth'),
    avatarWidth: normalizeDimension(event.avatarWidth, 'event.avatarWidth'),
    avatarHeight: normalizeAvatarHeight(
      event.avatarHeight ?? state.layout.avatarHeight ?? GAIT_REFERENCE_AVATAR_HEIGHT_PX,
      'event.avatarHeight',
    ),
    edgePadding: normalizeDimension(
      event.edgePadding ?? state.layout.edgePadding,
      'event.edgePadding',
    ),
  };
  const x = clampX(state.x, layout);
  const velocityDirection = signOf(state.velocityX);
  let velocityX = state.velocityX;

  if ((x === boundsFor(layout).min && velocityDirection < 0) || (x === boundsFor(layout).max && velocityDirection > 0)) {
    velocityX = 0;
  }

  return {
    ...state,
    layout,
    x,
    velocityX,
    facing: resolveFacing(state.facing, velocityX, movementIntent(state.held, state.inputIntent)),
  };
}

function handleFocusLost(state, event) {
  const nextState = event.now === undefined ? state : integrateMotion(state, event.now);

  if (!nextState.held.left && !nextState.held.right) {
    return nextState;
  }

  return {
    ...nextState,
    held: createHeldState(),
    inputIntent: 0,
    facing: resolveFacing(nextState.facing, nextState.velocityX, 0),
  };
}

export function transition(state, event) {
  if (!state || typeof state !== 'object') {
    throw new TypeError('state must be an object');
  }

  if (!event || typeof event.type !== 'string') {
    throw new TypeError('event.type must be a string');
  }

  switch (event.type) {
    case 'keyDown':
      return handleKeyDown(state, event);
    case 'keyUp':
      return handleKeyUp(state, event);
    case 'frame':
      return handleFrame(state, event);
    case 'layout':
      return handleLayout(state, event);
    case 'focusLost':
      return handleFocusLost(state, event);
    default:
      throw new RangeError(`Unknown event type: ${event.type}`);
  }
}

export function selectViewModel(state) {
  const intendedDirection = movementIntent(state.held, state.inputIntent);
  const movementDirection = signOf(state.velocityX);
  const bounds = boundsFor(state.layout);
  const blockedByBounds = movementDirection === 0 && (
    (intendedDirection < 0 && state.x <= bounds.min + VELOCITY_EPSILON)
    || (intendedDirection > 0 && state.x >= bounds.max - VELOCITY_EPSILON)
  );
  const moving = movementDirection !== 0 || (intendedDirection !== 0 && !blockedByBounds);
  const bubbleVisible = state.bubbleUntil !== null && state.now < state.bubbleUntil;
  const clapping = state.clapUntil !== null && state.now < state.clapUntil;
  const clapProgress = clapping
    ? Math.min(1, Math.max(0, (state.now - state.clapStartedAt) / CLAP_DURATION_MS))
    : 0;
  const gaitHeightRatio = state.layout.avatarHeight / GAIT_REFERENCE_AVATAR_HEIGHT_PX;
  const gaitCycleDistance = GAIT_CYCLE_DISTANCE_PX * gaitHeightRatio;

  return {
    x: state.x,
    moving,
    movementDirection,
    facing: state.facing,
    velocityX: state.velocityX,
    speedNormalized: Math.min(1, Math.abs(state.velocityX) / MOVEMENT_SPEED_PX_PER_SECOND),
    gaitPhase: (state.gaitDistance / gaitCycleDistance) % 1,
    action: clapping ? 'clap' : moving ? 'walk' : 'idle',
    bubbleVisible,
    bubbleText: BUBBLE_TEXT,
    clapping,
    clapProgress,
  };
}
