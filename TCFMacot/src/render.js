const IDLE_FRAME = './assets/sprites/idle.png';
const WALK_RIGHT_FRAMES = Array.from(
  { length: 14 },
  (_, index) => `./assets/sprites/walk-right-${index}.png`,
);
const WALK_LEFT_FRAMES = Array.from(
  { length: 15 },
  (_, index) => `./assets/sprites/walk-left-${index}.png`,
);
const CLAP_FRAMES = Array.from(
  { length: 5 },
  (_, index) => `./assets/sprites/clap-${index}.png`,
);
const ALL_FRAMES = [
  IDLE_FRAME,
  ...WALK_RIGHT_FRAMES,
  ...WALK_LEFT_FRAMES,
  ...CLAP_FRAMES,
];

const BUBBLE_TAIL_SAFE_INSET_PX = 56;
const DEFAULT_BACKGROUND_URL = './background.jpg';

function requireElement(value, name) {
  if (!value || typeof value.appendChild !== 'function') {
    throw new TypeError(`${name} must be a DOM element`);
  }
  return value;
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function loopFrame(phase, frameCount) {
  const normalizedPhase = ((phase % 1) + 1) % 1;
  return Math.min(frameCount - 1, Math.floor(normalizedPhase * frameCount));
}

function selectSprite(viewModel) {
  if (viewModel.clapping) {
    const clapPhase = ((viewModel.clapProgress ?? 0) * 5) % 1;
    const clapSequence = [0, 1, 2, 3, 4, 3, 2, 1];
    return CLAP_FRAMES[clapSequence[loopFrame(clapPhase, clapSequence.length)]];
  }

  if (viewModel.moving) {
    const frames = viewModel.facing < 0 ? WALK_LEFT_FRAMES : WALK_RIGHT_FRAMES;
    return frames[loopFrame(viewModel.gaitPhase ?? 0, frames.length)];
  }

  return IDLE_FRAME;
}

function preloadFrames(document) {
  const ImageType = document.defaultView?.Image;
  if (typeof ImageType !== 'function') {
    return;
  }
  for (const source of ALL_FRAMES) {
    const image = new ImageType();
    image.src = source;
  }
}

function normalizeLocalBackgroundUrl(document, value) {
  if (typeof value !== 'string') {
    return null;
  }
  const source = value.trim();
  if (
    source === ''
    || source.startsWith('//')
    || source.includes('\\')
    || /[\u0000-\u0020"'(),\u007f]/.test(source)
  ) {
    return null;
  }

  try {
    const baseUrl = new URL(document.baseURI ?? document.defaultView?.location?.href);
    const resolvedUrl = new URL(source, baseUrl);
    return resolvedUrl.origin === baseUrl.origin ? resolvedUrl.href : null;
  } catch {
    return null;
  }
}

function monitorOptionalBackground({ document, stage, backgroundSrc, backgroundFetch }) {
  const source = normalizeLocalBackgroundUrl(
    document,
    backgroundSrc ?? DEFAULT_BACKGROUND_URL,
  );
  if (source === null) {
    return () => {};
  }

  if (backgroundSrc !== undefined) {
    stage.style.setProperty('--operator-background', `url(${JSON.stringify(source)})`);
    return () => {};
  }

  const fetchBackground = backgroundFetch
    ?? document.defaultView?.fetch?.bind(document.defaultView);
  if (typeof fetchBackground !== 'function') {
    return () => {};
  }

  let active = true;
  void Promise.resolve(fetchBackground(source, {
    cache: 'no-store',
    method: 'HEAD',
  })).then((response) => {
    if (active && response?.ok === true) {
      stage.style.setProperty('--operator-background', `url(${JSON.stringify(source)})`);
    }
  }).catch(() => {});

  return () => {
    active = false;
  };
}

export function createRenderer({
  document,
  mount,
  backgroundSrc,
  backgroundFetch,
}) {
  if (!document || typeof document.createElement !== 'function') {
    throw new TypeError('document must be a DOM document');
  }
  requireElement(mount, 'mount');

  const stage = document.createElement('section');
  stage.className = 'stage';
  stage.setAttribute('aria-label', 'TCF presentation stage');
  const stopMonitoringBackground = monitorOptionalBackground({
    document,
    stage,
    backgroundSrc,
    backgroundFetch,
  });

  const bubbleTarget = document.createElement('div');
  bubbleTarget.className = 'speech-bubble-target';
  bubbleTarget.hidden = true;

  const bubble = document.createElement('p');
  bubble.className = 'speech-bubble';
  bubble.setAttribute('role', 'status');
  bubble.setAttribute('aria-live', 'polite');
  bubbleTarget.append(bubble);

  const track = document.createElement('div');
  track.className = 'avatar-track';
  track.setAttribute('role', 'img');
  track.setAttribute('aria-label', 'An illustrated host wearing a white shalwar qameez and green waistcoat');

  const sprites = [0, 1].map((index) => {
    const sprite = document.createElement('img');
    sprite.className = `avatar-sprite${index === 0 ? ' is-visible' : ''}`;
    sprite.src = IDLE_FRAME;
    sprite.alt = '';
    sprite.draggable = false;
    return sprite;
  });
  track.append(...sprites);
  let activeSpriteIndex = 0;
  let currentSprite = IDLE_FRAME;

  const instructions = document.createElement('p');
  instructions.className = 'screen-reader-only';
  instructions.textContent = 'Use left and right arrows to walk, Space to welcome, and 1 to clap.';

  stage.append(bubbleTarget, track, instructions);
  mount.replaceChildren(stage);
  preloadFrames(document);

  function measure() {
    const trackRect = track.getBoundingClientRect();
    return {
      viewportWidth: stage.clientWidth,
      avatarWidth: trackRect.width,
      avatarHeight: trackRect.height,
    };
  }

  function render(viewModel) {
    track.style.transform = `translate3d(${viewModel.x.toFixed(2)}px, 0, 0)`;
    track.dataset.action = viewModel.clapping
      ? 'clap'
      : viewModel.moving
        ? 'walk'
        : 'idle';

    const nextSprite = selectSprite(viewModel);
    if (nextSprite !== currentSprite) {
      currentSprite = nextSprite;
      const nextSpriteIndex = activeSpriteIndex === 0 ? 1 : 0;
      const outgoingSprite = sprites[activeSpriteIndex];
      const incomingSprite = sprites[nextSpriteIndex];
      incomingSprite.src = nextSprite;
      incomingSprite.dataset.frame = nextSprite;
      incomingSprite.classList.add('is-visible');
      outgoingSprite.classList.remove('is-visible');
      activeSpriteIndex = nextSpriteIndex;
    }
    const stepLift = viewModel.moving
      ? Math.sin((viewModel.gaitPhase ?? 0) * Math.PI * 2) ** 2
      : 0;
    const spriteTransform = `translateY(${-0.45 * stepLift * (viewModel.speedNormalized ?? 0)}%)`;
    for (const sprite of sprites) {
      sprite.style.transform = spriteTransform;
    }

    if (viewModel.bubbleVisible) {
      bubble.textContent = viewModel.bubbleText;
      bubbleTarget.hidden = false;

      const stageRect = stage.getBoundingClientRect();
      const trackRect = track.getBoundingClientRect();
      const bubbleRect = bubbleTarget.getBoundingClientRect();
      const bubbleWidth = Math.max(bubbleRect.width, 1);
      const safeInset = Math.min(BUBBLE_TAIL_SAFE_INSET_PX, bubbleWidth / 2);
      const avatarCenter = trackRect.left - stageRect.left + (trackRect.width / 2);
      const bubbleLeft = bubbleRect.left - stageRect.left;
      const localTarget = avatarCenter - bubbleLeft;
      const tailPosition = clamp(
        localTarget,
        safeInset,
        Math.max(safeInset, bubbleWidth - safeInset),
      );
      bubbleTarget.style.setProperty('--bubble-tail-left-px', `${tailPosition.toFixed(2)}px`);
    } else {
      bubbleTarget.hidden = true;
      bubble.textContent = '';
    }
  }

  function destroy() {
    stopMonitoringBackground();
    stage.remove();
  }

  return {
    measure,
    render,
    destroy,
  };
}
