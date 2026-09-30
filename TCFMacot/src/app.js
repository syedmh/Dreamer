import { createRenderer } from './render.js';
import { createInitialState, selectViewModel, transition } from './state.js';

const handledCodes = new Set(['ArrowLeft', 'ArrowRight', 'Space', 'Digit1', 'Numpad1']);
const preventDefaultCodes = new Set(['ArrowLeft', 'ArrowRight', 'Space']);

export function createAvatarApp({
  document: appDocument = globalThis.document,
  window: appWindow = globalThis.window,
  performance: appPerformance = globalThis.performance,
  requestAnimationFrame: scheduleAnimationFrame = globalThis.requestAnimationFrame,
  cancelAnimationFrame: cancelScheduledAnimationFrame = globalThis.cancelAnimationFrame,
  rendererFactory = createRenderer,
} = {}) {
  const mount = appDocument?.querySelector('#app');
  if (!mount) {
    throw new Error('The #app mount element is required');
  }

  const renderer = rendererFactory({ document: appDocument, mount });
  const initialLayout = renderer.measure();
  let state = createInitialState({
    now: appPerformance.now(),
    ...initialLayout,
  });
  let animationFrameId = null;
  let stopped = false;

  function render() {
    renderer.render(selectViewModel(state));
  }

  function handleKeyDown(event) {
    if (!handledCodes.has(event.code)) {
      return;
    }

    if (preventDefaultCodes.has(event.code)) {
      event.preventDefault();
    }

    state = transition(state, {
      type: 'keyDown',
      code: event.code,
      now: appPerformance.now(),
      repeat: event.repeat === true,
    });
    render();
  }

  function handleKeyUp(event) {
    if (!handledCodes.has(event.code)) {
      return;
    }

    if (preventDefaultCodes.has(event.code)) {
      event.preventDefault();
    }

    state = transition(state, {
      type: 'keyUp',
      code: event.code,
      now: appPerformance.now(),
    });
    render();
  }

  function clearMovement() {
    state = transition(state, { type: 'focusLost', now: appPerformance.now() });
    render();
  }

  function handleVisibilityChange() {
    if (appDocument.visibilityState !== 'visible') {
      clearMovement();
    }
  }

  function updateLayout() {
    const layout = renderer.measure();
    state = transition(state, {
      type: 'layout',
      ...layout,
    });
    render();
  }

  function animate(now) {
    if (stopped) {
      return;
    }

    state = transition(state, { type: 'frame', now });
    render();
    animationFrameId = scheduleAnimationFrame(animate);
  }

  function stop() {
    if (stopped) {
      return;
    }

    stopped = true;
    if (animationFrameId !== null) {
      cancelScheduledAnimationFrame(animationFrameId);
    }
    appWindow.removeEventListener('keydown', handleKeyDown);
    appWindow.removeEventListener('keyup', handleKeyUp);
    appWindow.removeEventListener('resize', updateLayout);
    appWindow.removeEventListener('blur', clearMovement);
    appWindow.removeEventListener('pagehide', stop);
    appDocument.removeEventListener('visibilitychange', handleVisibilityChange);
    renderer.destroy();
  }

  appWindow.addEventListener('keydown', handleKeyDown, { passive: false });
  appWindow.addEventListener('keyup', handleKeyUp, { passive: false });
  appWindow.addEventListener('resize', updateLayout);
  appWindow.addEventListener('blur', clearMovement);
  appWindow.addEventListener('pagehide', stop);
  appDocument.addEventListener('visibilitychange', handleVisibilityChange);

  render();
  animationFrameId = scheduleAnimationFrame(animate);

  return {
    getState: () => state,
    stop,
  };
}

if (typeof document !== 'undefined' && typeof window !== 'undefined') {
  createAvatarApp();
}
