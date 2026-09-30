import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { describe, it } from 'node:test';

import {
  BUBBLE_DURATION_MS,
  BUBBLE_TEXT,
  CLAP_DURATION_MS,
  EDGE_PADDING_PX,
  MAX_FRAME_DELTA_MS,
  MOVEMENT_SPEED_PX_PER_SECOND,
} from '../src/constants.js';
import { createAvatarApp } from '../src/app.js';
import { AVATAR_RIG } from '../src/avatar-rig.js';
import * as renderModule from '../src/render.js';
import { createInitialState, selectViewModel, transition } from '../src/state.js';

const { createRenderer } = renderModule;
const FIVE_LAYER_CONTRACT = Object.freeze([
  Object.freeze({
    role: 'leftLeg',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 420, y: 806 }),
    zIndex: 10,
    url: './assets/avatar/leftLeg.png',
    cssProperty: '--left-leg-rotate',
  }),
  Object.freeze({
    role: 'rightLeg',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 515, y: 812 }),
    zIndex: 20,
    url: './assets/avatar/rightLeg.png',
    cssProperty: '--right-leg-rotate',
  }),
  Object.freeze({
    role: 'torsoHead',
    parent: null,
    pivot: Object.freeze({ x: 448, y: 785 }),
    zIndex: 30,
    url: './assets/avatar/torsoHead.png',
    cssProperty: '--torso-head-rotate',
  }),
  Object.freeze({
    role: 'leftArm',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 337, y: 276 }),
    zIndex: 40,
    url: './assets/avatar/leftArm.png',
    cssProperty: '--left-arm-rotate',
  }),
  Object.freeze({
    role: 'rightArm',
    parent: 'torsoHead',
    pivot: Object.freeze({ x: 560, y: 278 }),
    zIndex: 50,
    url: './assets/avatar/rightArm.png',
    cssProperty: '--right-arm-rotate',
  }),
]);
const FIVE_LAYER_RASTER_URLS = Object.freeze([
  './assets/avatar/idle.png',
  ...FIVE_LAYER_CONTRACT.map(({ url }) => url),
].sort());
const FIVE_LAYER_ROTATION_PROPERTIES = Object.freeze(
  FIVE_LAYER_CONTRACT.map(({ role }) => `${role}RotateDeg`),
);
const LEGACY_ROTATION_PROPERTIES = Object.freeze([
  'bodyLeanDeg',
  'headTiltDeg',
  'leftUpperArmRotateDeg',
  'rightUpperArmRotateDeg',
  'leftForearmRotateDeg',
  'rightForearmRotateDeg',
  'leftHandRotateDeg',
  'rightHandRotateDeg',
  'leftUpperLegRotateDeg',
  'rightUpperLegRotateDeg',
  'leftLowerLegRotateDeg',
  'rightLowerLegRotateDeg',
  'leftShoeRotateDeg',
  'rightShoeRotateDeg',
]);

function keyDown(state, code, now = state.now, repeat = false) {
  return transition(state, {
    type: 'keyDown',
    code,
    now,
    repeat,
  });
}

function frame(state, now) {
  return transition(state, { type: 'frame', now });
}

function keyUp(state, code) {
  return transition(state, { type: 'keyUp', code });
}

function runFrames(state, frameTimes) {
  let nextState = state;

  for (const now of frameTimes) {
    nextState = frame(nextState, now);
  }

  return nextState;
}

function assertApprox(actual, expected, epsilon = 1e-6, message = undefined) {
  assert.ok(
    Math.abs(actual - expected) <= epsilon,
    message ?? `expected ${actual} to be within ${epsilon} of ${expected}`,
  );
}

function assertLocomotionViewModel(viewModel) {
  assert.equal(
    typeof viewModel.velocityX,
    'number',
    'selectViewModel() must expose signed velocityX',
  );
  assert.equal(
    typeof viewModel.speedNormalized,
    'number',
    'selectViewModel() must expose normalized speed',
  );
  assert.equal(
    typeof viewModel.gaitPhase,
    'number',
    'selectViewModel() must expose a normalized gaitPhase',
  );
  assert.ok(
    viewModel.speedNormalized >= 0 && viewModel.speedNormalized <= 1,
    `speedNormalized must stay within [0, 1], got ${viewModel.speedNormalized}`,
  );
  assertApprox(
    viewModel.speedNormalized,
    Math.abs(viewModel.velocityX) / MOVEMENT_SPEED_PX_PER_SECOND,
    1e-6,
    'speedNormalized must mirror absolute velocity as a 0..1 ratio of top speed',
  );
  assert.ok(
    viewModel.gaitPhase >= 0 && viewModel.gaitPhase < 1,
    `gaitPhase must stay within [0, 1), got ${viewModel.gaitPhase}`,
  );
}

function getPoseHelper() {
  assert.equal(
    typeof renderModule.computePoseKinematics,
    'function',
    'render.js must export computePoseKinematics(viewModel) for deterministic pose regressions',
  );

  return renderModule.computePoseKinematics;
}

function createPoseViewModel(overrides = {}) {
  const speedNormalized = overrides.speedNormalized ?? 0;
  const movementDirection = overrides.movementDirection ?? 0;

  return {
    x: overrides.x ?? 0,
    moving: overrides.moving ?? speedNormalized > 0,
    movementDirection,
    facing: overrides.facing ?? (movementDirection === 0 ? 1 : movementDirection),
    velocityX: overrides.velocityX ?? (movementDirection * speedNormalized * MOVEMENT_SPEED_PX_PER_SECOND),
    speedNormalized,
    gaitPhase: overrides.gaitPhase ?? 0,
    action: overrides.action ?? (overrides.clapping ? 'clap' : speedNormalized > 0 ? 'walk' : 'idle'),
    bubbleVisible: overrides.bubbleVisible ?? false,
    bubbleText: overrides.bubbleText ?? BUBBLE_TEXT,
    clapping: overrides.clapping ?? false,
    clapProgress: overrides.clapProgress ?? 0,
  };
}

function computePose(overrides = {}) {
  const computePoseKinematics = getPoseHelper();
  return computePoseKinematics(createPoseViewModel(overrides));
}

function assertNeutralPoseCss(cssVariables, messagePrefix) {
  for (const [name, expected] of Object.entries({
    bodyBobPx: 0,
    torsoHeadRotateDeg: 0,
    leftArmRotateDeg: 0,
    rightArmRotateDeg: 0,
    leftLegRotateDeg: 0,
    rightLegRotateDeg: 0,
  })) {
    assertApprox(
      cssVariables[name],
      expected,
      1e-6,
      `${messagePrefix}: expected ${name} to be ${expected} but received ${cssVariables[name]}`,
    );
  }
  for (const removedProperty of ['shadowScaleX', 'shadowShiftXPx']) {
    assert.equal(
      Object.hasOwn(cssVariables, removedProperty),
      false,
      `${messagePrefix}: ${removedProperty} must stay removed with the floor shadow`,
    );
  }
}

function translateXFromTransform(transform) {
  const match = /translate3d\(([-\d.]+)px,\s*0,\s*0\)/.exec(transform);
  assert.ok(match, `expected a translate3d() transform but received ${transform}`);
  return Number(match[1]);
}

function rasterLayerSources(element) {
  const sources = [];
  const visit = (node) => {
    if (node?.tagName === 'IMG') {
      const source = node.attributes?.get('src') ?? node.src;
      if (source) {
        sources.push(String(source));
      }
    }
    for (const child of node?.children ?? []) {
      visit(child);
    }
  };

  visit(element);
  for (const match of element.innerHTML.matchAll(/<img\b[^>]*\bsrc=["']([^"']+)["']/gi)) {
    sources.push(match[1]);
  }
  return [...new Set(sources)];
}

function elementHasClass(element, className) {
  return (
    element?.className.split(/\s+/).includes(className)
    || element?.classList.contains?.(className)
  ) ?? false;
}

class FakeEventTarget {
  constructor() {
    this.listeners = new Map();
  }

  addEventListener(type, listener, options = {}) {
    const listeners = this.listeners.get(type) ?? new Map();
    listeners.set(listener, { once: options?.once === true });
    this.listeners.set(type, listeners);
  }

  removeEventListener(type, listener) {
    this.listeners.get(type)?.delete(listener);
  }

  listenerCount(type) {
    return this.listeners.get(type)?.size ?? 0;
  }

  dispatch(type, event = {}) {
    const listeners = this.listeners.get(type);
    for (const [listener, options] of [...(listeners ?? [])]) {
      listener(event);
      if (options.once) {
        listeners.delete(listener);
      }
    }
  }
}

class FakeMediaQueryList extends FakeEventTarget {
  constructor(matches = false) {
    super();
    this.matches = matches;
    this.media = '(prefers-reduced-motion: reduce)';
  }

  setMatches(matches) {
    this.matches = matches;
    this.dispatch('change', {
      matches,
      media: this.media,
    });
  }
}

class FakeClassList {
  constructor() {
    this.values = new Set();
  }

  toggle(name, force) {
    if (force) {
      this.values.add(name);
    } else {
      this.values.delete(name);
    }
  }

  contains(name) {
    return this.values.has(name);
  }
}

class FakeElement extends FakeEventTarget {
  constructor(tagName, ownerDocument = null) {
    super();
    this.tagName = tagName.toUpperCase();
    this.ownerDocument = ownerDocument;
    this.attributes = new Map();
    this.children = [];
    this.innerHtmlElements = [];
    this.parentElement = null;
    this.classList = new FakeClassList();
    this.className = '';
    this.clientWidth = 1280;
    this.dataset = {};
    this.hidden = false;
    this.removed = false;
    this.style = {
      setProperty: (name, value) => {
        this.style[name] = value;
      },
      transform: '',
    };
    this.textContentWrites = 0;
    this._innerHTML = '';
    this._textContent = '';
    this.face = null;
    this.boundingRect = {
      left: 0,
      top: 0,
      width: 360,
      height: 640,
    };
    this.boundingClientRectReads = 0;
  }

  set textContent(value) {
    this.textContentWrites += 1;
    this._textContent = String(value);
  }

  get textContent() {
    return this._textContent;
  }

  set innerHTML(value) {
    this._innerHTML = String(value);
    this.children = [];
    this.innerHtmlElements = [];
    const stack = [this];
    const voidElements = new Set(['AREA', 'BASE', 'BR', 'COL', 'EMBED', 'HR', 'IMG', 'INPUT', 'LINK', 'META', 'PARAM', 'SOURCE', 'TRACK', 'WBR']);

    for (const match of this._innerHTML.matchAll(/<(\/?)([a-z][\w:-]*)\b([^>]*)>/gi)) {
      const [, closingMarker, tagName, attributeSource] = match;
      const normalizedTagName = tagName.toUpperCase();

      if (closingMarker) {
        while (stack.length > 1) {
          const closedElement = stack.pop();
          if (closedElement.tagName === normalizedTagName) {
            break;
          }
        }
        continue;
      }

      const element = this.ownerDocument?.createElement(tagName) ?? new FakeElement(tagName);
      for (const attribute of attributeSource.matchAll(/([\w:-]+)=["']([^"']*)["']/g)) {
        element.setAttribute(attribute[1], attribute[2]);
      }
      stack.at(-1).appendChild(element);
      this.innerHtmlElements.push(element);

      if (!voidElements.has(normalizedTagName) && !/\/\s*>$/.test(match[0])) {
        stack.push(element);
      }
    }

    const configuredLayerCount = this.ownerDocument?.avatarLayerCount;
    if (Number.isInteger(configuredLayerCount) && configuredLayerCount >= 0) {
      const avatarLayers = this.querySelectorAll('.avatar-layer');

      while (avatarLayers.length > configuredLayerCount) {
        const removedLayer = avatarLayers.pop();
        const parent = removedLayer.parentElement;
        parent.children = parent.children.filter((child) => child !== removedLayer);
        removedLayer.parentElement = null;
        this.innerHtmlElements = this.innerHtmlElements.filter(
          (element) => element !== removedLayer,
        );
      }

      while (avatarLayers.length < configuredLayerCount) {
        const extraLayer = this.ownerDocument.createElement('img');
        extraLayer.setAttribute('class', 'avatar-layer');
        extraLayer.setAttribute('data-avatar-role', `extra-${avatarLayers.length}`);
        this.appendChild(extraLayer);
        this.innerHtmlElements.push(extraLayer);
        avatarLayers.push(extraLayer);
      }
    }

    if (this._innerHTML.includes('class="face-photo"')) {
      this.face = new FakeElement('image', this.ownerDocument);
    }
  }

  get innerHTML() {
    return this._innerHTML;
  }

  append(...children) {
    for (const child of children) {
      child.parentElement = this;
    }
    this.children.push(...children);
  }

  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }

  replaceChildren(...children) {
    for (const child of this.children) {
      child.parentElement = null;
    }
    for (const child of children) {
      child.parentElement = this;
    }
    this.children = children;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === 'class') {
      this.className = String(value);
    }
  }

  querySelector(selector) {
    return this.querySelectorAll(selector)[0] ?? null;
  }

  querySelectorAll(selector) {
    const classMatch = /^\.([\w-]+)$/.exec(selector);
    const attributeMatch = /^\[data-avatar-role=["']([^"']+)["']\]$/.exec(selector);
    const matches = [];
    const visit = (element) => {
      for (const child of element.children) {
        if (
          (classMatch && elementHasClass(child, classMatch[1]))
          || (
            attributeMatch
            && child.attributes.get('data-avatar-role') === attributeMatch[1]
          )
        ) {
          matches.push(child);
        }
        visit(child);
      }
    };

    visit(this);
    return matches;
  }

  setBoundingClientRect(rect) {
    this.boundingRect = {
      ...this.boundingRect,
      ...rect,
    };
  }

  getBoundingClientRect() {
    this.boundingClientRectReads += 1;
    const translateXMatch = /translate3d\(([-\d.]+)px,\s*0,\s*0\)/.exec(this.style.transform);
    const translateX = translateXMatch ? Number(translateXMatch[1]) : 0;
    const left = this.boundingRect.left + translateX;
    const top = this.boundingRect.top;
    const width = this.boundingRect.width;
    const height = this.boundingRect.height;

    return {
      left,
      right: left + width,
      top,
      bottom: top + height,
      width,
      height,
    };
  }

  remove() {
    this.removed = true;
  }
}

class FakeDocument extends FakeEventTarget {
  constructor(mount, { avatarLayerCount = null, imageState = {} } = {}) {
    super();
    this.baseURI = 'http://localhost/';
    this.mount = mount;
    this.avatarLayerCount = avatarLayerCount;
    this.imageState = {
      complete: false,
      naturalWidth: 0,
      ...imageState,
    };
    this.visibilityState = 'visible';
  }

  createElement(tagName) {
    const element = new FakeElement(tagName, this);
    if (element.tagName === 'IMG') {
      element.complete = this.imageState.complete;
      element.naturalWidth = this.imageState.naturalWidth;
    }
    return element;
  }

  querySelector(selector) {
    return selector === '#app' ? this.mount : null;
  }
}

function createKeyboardEvent(code, { repeat = false } = {}) {
  return {
    code,
    repeat,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
  };
}

function removeCssAtRuleBlocks(source) {
  const css = source.replace(/\/\*[\s\S]*?\*\//g, '');
  let result = '';
  let index = 0;

  while (index < css.length) {
    if (css[index] !== '@') {
      result += css[index];
      index += 1;
      continue;
    }

    const semicolon = css.indexOf(';', index);
    const openingBrace = css.indexOf('{', index);
    if (semicolon !== -1 && (openingBrace === -1 || semicolon < openingBrace)) {
      index = semicolon + 1;
      continue;
    }
    if (openingBrace === -1) {
      break;
    }

    let depth = 1;
    index = openingBrace + 1;
    while (index < css.length && depth > 0) {
      if (css[index] === '{') {
        depth += 1;
      } else if (css[index] === '}') {
        depth -= 1;
      }
      index += 1;
    }
  }

  return result;
}

function extractCssAtRuleBody(source, header) {
  const headerIndex = source.indexOf(header);
  assert.notEqual(headerIndex, -1, `styles.css must define ${header}`);
  const openingBrace = source.indexOf('{', headerIndex + header.length);
  assert.notEqual(openingBrace, -1, `${header} must have a block`);
  let depth = 1;
  let index = openingBrace + 1;

  while (index < source.length && depth > 0) {
    if (source[index] === '{') {
      depth += 1;
    } else if (source[index] === '}') {
      depth -= 1;
    }
    index += 1;
  }
  assert.equal(depth, 0, `${header} block must close`);
  return source.slice(openingBrace + 1, index - 1);
}

function splitCssSelectorList(selectorText) {
  const selectors = [];
  let current = '';
  let bracketDepth = 0;
  let parenthesisDepth = 0;

  for (const character of selectorText) {
    if (character === '[') {
      bracketDepth += 1;
    } else if (character === ']') {
      bracketDepth -= 1;
    } else if (character === '(') {
      parenthesisDepth += 1;
    } else if (character === ')') {
      parenthesisDepth -= 1;
    }

    if (character === ',' && bracketDepth === 0 && parenthesisDepth === 0) {
      if (current.trim() !== '') {
        selectors.push(current.trim());
      }
      current = '';
    } else {
      current += character;
    }
  }
  if (current.trim() !== '') {
    selectors.push(current.trim());
  }
  return selectors;
}

function parseCssDeclarations(source) {
  const declarations = new Map();
  for (const match of source.matchAll(/([\w-]+)\s*:\s*([^;]+)(?:;|$)/g)) {
    const important = /\s*!important\s*$/i.test(match[2]);
    declarations.set(match[1], {
      important,
      value: match[2].replace(/\s*!important\s*$/i, '').trim(),
    });
  }
  return declarations;
}

function cssSpecificity(selector) {
  const ids = (selector.match(/#[\w-]+/g) ?? []).length;
  const classesAndAttributes = (
    selector.match(/\.[\w-]+|\[[^\]]+\]|:(?!:)[\w-]+/g) ?? []
  ).length;
  const types = splitCssSelectorTokens(selector)
    .filter((token) => token !== ' ' && token !== '>')
    .filter((token) => /^[a-z][\w-]*/i.test(token))
    .length;
  return [ids, classesAndAttributes, types];
}

function splitCssSelectorTokens(selector) {
  const tokens = [];
  let current = '';
  let bracketDepth = 0;
  let parenthesisDepth = 0;
  let pendingDescendant = false;

  function flushCurrent() {
    if (current.trim() !== '') {
      tokens.push(current.trim());
      current = '';
    }
  }

  for (let index = 0; index < selector.length; index += 1) {
    const character = selector[index];
    if (character === '[') {
      bracketDepth += 1;
    } else if (character === ']') {
      bracketDepth -= 1;
    } else if (character === '(') {
      parenthesisDepth += 1;
    } else if (character === ')') {
      parenthesisDepth -= 1;
    }

    if (bracketDepth === 0 && parenthesisDepth === 0 && character === '>') {
      flushCurrent();
      if (tokens.at(-1) === ' ') {
        tokens.pop();
      }
      tokens.push('>');
      pendingDescendant = false;
      continue;
    }
    if (bracketDepth === 0 && parenthesisDepth === 0 && /\s/.test(character)) {
      flushCurrent();
      if (tokens.length > 0 && tokens.at(-1) !== '>') {
        pendingDescendant = true;
      }
      continue;
    }
    if (pendingDescendant) {
      tokens.push(' ');
      pendingDescendant = false;
    }
    current += character;
  }
  flushCurrent();
  return tokens;
}

function matchesCssCompound(element, compound) {
  if (!element || compound.includes('::')) {
    return false;
  }

  const normalizedCompound = compound.replace(
    /:(?:active|checked|disabled|enabled|focus|focus-visible|focus-within|hover|last-child|first-child)\b/g,
    '',
  );
  if (normalizedCompound.includes(':')) {
    return false;
  }

  const type = /^[a-z][\w-]*/i.exec(normalizedCompound)?.[0];
  if (type && element.tagName !== type.toUpperCase()) {
    return false;
  }
  for (const [, id] of normalizedCompound.matchAll(/#([\w-]+)/g)) {
    if (element.attributes.get('id') !== id) {
      return false;
    }
  }
  for (const [, className] of normalizedCompound.matchAll(/\.([\w-]+)/g)) {
    if (!elementHasClass(element, className)) {
      return false;
    }
  }
  for (const [, name, rawValue] of normalizedCompound.matchAll(
    /\[([\w:-]+)(?:\s*=\s*["']?([^"'\]]+)["']?)?\]/g,
  )) {
    const attributeValue = element.attributes.has(name)
      ? element.attributes.get(name)
      : name.startsWith('data-')
        ? element.dataset[name.slice(5).replace(/-([a-z])/g, (match, letter) => letter.toUpperCase())]
        : undefined;
    if (attributeValue === undefined) {
      return false;
    }
    if (rawValue !== undefined && String(attributeValue) !== rawValue) {
      return false;
    }
  }
  return true;
}

function matchesCssSelector(element, selector) {
  const tokens = splitCssSelectorTokens(selector);
  if (tokens.length === 0 || tokens.length % 2 === 0) {
    return false;
  }

  function matchAt(candidate, tokenIndex) {
    if (!matchesCssCompound(candidate, tokens[tokenIndex])) {
      return false;
    }
    if (tokenIndex === 0) {
      return true;
    }

    const combinator = tokens[tokenIndex - 1];
    if (combinator === '>') {
      return matchAt(candidate.parentElement, tokenIndex - 2);
    }
    if (combinator === ' ') {
      let ancestor = candidate.parentElement;
      while (ancestor) {
        if (matchAt(ancestor, tokenIndex - 2)) {
          return true;
        }
        ancestor = ancestor.parentElement;
      }
    }
    return false;
  }

  return matchAt(element, tokens.length - 1);
}

function parseCssRules(source) {
  const rules = [];
  let order = 0;
  for (const match of removeCssAtRuleBlocks(source).matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const declarations = parseCssDeclarations(match[2]);
    for (const selector of splitCssSelectorList(match[1])) {
      rules.push({
        declarations,
        order,
        selector,
        specificity: cssSpecificity(selector),
      });
      order += 1;
    }
  }
  return rules;
}

function parseAllCssRules(source, contexts = []) {
  const css = source.replace(/\/\*[\s\S]*?\*\//g, '');
  const rules = [];
  let index = 0;
  let order = 0;

  while (index < css.length) {
    while (index < css.length && /\s/.test(css[index])) {
      index += 1;
    }
    if (index >= css.length) {
      break;
    }

    const semicolon = css.indexOf(';', index);
    const openingBrace = css.indexOf('{', index);
    if (openingBrace === -1) {
      break;
    }
    if (semicolon !== -1 && semicolon < openingBrace) {
      index = semicolon + 1;
      continue;
    }

    const header = css.slice(index, openingBrace).trim();
    let depth = 1;
    let closingBrace = openingBrace + 1;
    while (closingBrace < css.length && depth > 0) {
      if (css[closingBrace] === '{') {
        depth += 1;
      } else if (css[closingBrace] === '}') {
        depth -= 1;
      }
      closingBrace += 1;
    }
    assert.equal(depth, 0, `CSS block must close: ${header}`);
    const body = css.slice(openingBrace + 1, closingBrace - 1);

    if (/^@(?:-webkit-)?keyframes\b/i.test(header)) {
      index = closingBrace;
      continue;
    }
    if (header.startsWith('@')) {
      for (const nestedRule of parseAllCssRules(body, [...contexts, header])) {
        rules.push({
          ...nestedRule,
          order,
        });
        order += 1;
      }
      index = closingBrace;
      continue;
    }

    const declarations = parseCssDeclarations(body);
    for (const selector of splitCssSelectorList(header)) {
      rules.push({
        contexts,
        declarations,
        order,
        selector,
        specificity: cssSpecificity(selector),
      });
      order += 1;
    }
    index = closingBrace;
  }

  return rules;
}

function compareCssCandidates(left, right) {
  for (let index = 0; index < left.rank.length; index += 1) {
    if (left.rank[index] !== right.rank[index]) {
      return left.rank[index] - right.rank[index];
    }
  }
  return left.order - right.order;
}

function inlineCssDeclarations(element) {
  const declarations = parseCssDeclarations(element?.attributes.get('style') ?? '');
  for (const [name, value] of Object.entries(element?.style ?? {})) {
    if (typeof value !== 'string' || value === '') {
      continue;
    }
    const property = name === 'transformOrigin' ? 'transform-origin' : name;
    declarations.set(property, { important: false, value });
  }
  return declarations;
}

function cascadedCssValue(element, rules, property) {
  const candidates = [];
  for (const rule of rules) {
    const declaration = rule.declarations.get(property);
    if (!declaration || !matchesCssSelector(element, rule.selector)) {
      continue;
    }
    candidates.push({
      order: rule.order,
      rank: [
        declaration.important ? 1 : 0,
        0,
        ...rule.specificity,
      ],
      value: declaration.value,
    });
  }
  const inlineDeclaration = inlineCssDeclarations(element).get(property);
  if (inlineDeclaration) {
    candidates.push({
      order: Number.MAX_SAFE_INTEGER,
      rank: [
        inlineDeclaration.important ? 1 : 0,
        1,
        0,
        0,
        0,
      ],
      value: inlineDeclaration.value,
    });
  }
  return candidates.sort(compareCssCandidates).at(-1)?.value;
}

function computedCssCustomProperties(element, rules, names, rootValues) {
  const values = element?.parentElement
    ? computedCssCustomProperties(element.parentElement, rules, names, rootValues)
    : new Map(rootValues);

  for (const name of names) {
    const value = cascadedCssValue(element, rules, name);
    if (value !== undefined) {
      values.set(name, value);
    }
  }
  return values;
}

function descendantsAndSelf(element) {
  return [
    element,
    ...element.children.flatMap((child) => descendantsAndSelf(child)),
  ];
}

function avatarMotionViolations({ rules, track, jointByRole = new Map() }) {
  const roleByJoint = new Map(
    [...jointByRole].map(([role, joint]) => [joint, role]),
  );
  const allowedRotationByElement = new Map(
    FIVE_LAYER_CONTRACT.flatMap((layer) => {
      const joint = jointByRole.get(layer.role);
      return joint
        ? [[
          joint,
          new RegExp(
            `^\\s*rotate\\(\\s*var\\(\\s*${layer.cssProperty.replaceAll('-', '\\-')}\\s*\\)\\s*\\)\\s*$`,
          ),
        ]]
        : [];
    }),
  );
  const violations = [];
  const trackElements = descendantsAndSelf(track);
  const rotationFunctionPattern = /\brotate(?:[xyz]|3d)?\s*\(/i;
  const scenarios = ['-1', '1'].flatMap((direction) => [
    { action: 'idle', clapping: false, direction, walking: false },
    { action: 'walk', clapping: false, direction, walking: true },
    { action: 'clap', clapping: true, direction, walking: false },
    { action: 'clap', clapping: true, direction, walking: true },
  ]);

  for (const scenario of scenarios) {
    track.dataset.action = scenario.action;
    track.dataset.direction = scenario.direction;
    track.classList.toggle('is-walking', scenario.walking);
    track.classList.toggle('is-clapping', scenario.clapping);

    for (const rule of rules) {
      for (const element of trackElements) {
        if (!matchesCssSelector(element, rule.selector)) {
          continue;
        }

        const rotate = rule.declarations.get('rotate')?.value;
        if (rotate !== undefined && rotate !== 'none') {
          violations.push({
            mechanism: 'rotate property',
            role: roleByJoint.get(element) ?? null,
            scenario: scenario.action,
            selector: rule.selector,
            value: rotate,
          });
        }

        const transform = rule.declarations.get('transform')?.value;
        if (rotationFunctionPattern.test(transform ?? '')) {
          const allowed = allowedRotationByElement.get(element);
          if (!allowed?.test(transform)) {
            violations.push({
              mechanism: 'rotation transform',
              role: roleByJoint.get(element) ?? null,
              scenario: scenario.action,
              selector: rule.selector,
              value: transform,
            });
          }
        }

        for (const property of ['transition', 'transition-property']) {
          const transition = rule.declarations.get(property)?.value;
          if (
            String(transition ?? '')
              .split(',')
              .some((item) => /\b(?:all|transform)\b/i.test(item))
          ) {
            violations.push({
              mechanism: property,
              role: roleByJoint.get(element) ?? null,
              scenario: scenario.action,
              selector: rule.selector,
              value: transition,
            });
          }
        }

        for (const property of ['animation', 'animation-name']) {
          const animation = rule.declarations.get(property)?.value;
          if (animation !== undefined && !/^(?:none|initial|inherit|unset)$/i.test(animation)) {
            violations.push({
              mechanism: property,
              role: roleByJoint.get(element) ?? null,
              scenario: scenario.action,
              selector: rule.selector,
              value: animation,
            });
          }
        }
      }
    }
  }

  return violations;
}

describe('avatar state', () => {
  it('keeps the frozen frame cap and edge padding literal values', () => {
    assert.equal(MAX_FRAME_DELTA_MS, 50);
    assert.equal(EDGE_PADDING_PX, 24);
  });

  it('starts centered, idle, and inside the configured layout', () => {
    const state = createInitialState({
      now: 100,
      viewportWidth: 1000,
      avatarWidth: 200,
      edgePadding: 20,
    });
    const viewModel = selectViewModel(state);

    assert.equal(state.x, 400);
    assert.equal(viewModel.x, 400);
    assert.equal(viewModel.moving, false);
    assert.equal(viewModel.movementDirection, 0);
    assert.equal(viewModel.facing, 1);
    assert.equal(viewModel.bubbleVisible, false);
    assert.equal(viewModel.bubbleText, BUBBLE_TEXT);
    assert.equal(viewModel.clapping, false);
    assert.equal(viewModel.clapProgress, 0);
    assertLocomotionViewModel(viewModel);
    assert.equal(viewModel.velocityX, 0);
    assert.equal(viewModel.speedNormalized, 0);
    assert.equal(viewModel.gaitPhase, 0);
  });

  it('ramps held movement into capped velocity and publishes locomotion metrics', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1400,
      avatarWidth: 200,
      edgePadding: 0,
      x: 400,
    });
    state = keyDown(state, 'ArrowRight');

    const positions = [state.x];
    const viewModels = [];

    for (const now of [50, 100, 150, 200, 250, 300, 350, 400, 450, 500]) {
      state = frame(state, now);
      positions.push(state.x);
      viewModels.push(selectViewModel(state));
    }

    const deltas = positions.slice(1).map((position, index) => position - positions[index]);
    const topSpeedDistance = MOVEMENT_SPEED_PX_PER_SECOND * (MAX_FRAME_DELTA_MS / 1000);
    const finalViewModel = viewModels.at(-1);

    assert.ok(
      deltas[0] > 0 && deltas[0] < topSpeedDistance,
      'the first walking frame should accelerate from rest instead of snapping to full speed',
    );
    assert.ok(
      deltas[1] > deltas[0],
      'equal walking frames should cover more distance while the avatar accelerates',
    );
    assert.ok(
      deltas[2] >= deltas[1],
      'continued held movement should keep ramping until it reaches the speed cap',
    );
    assertLocomotionViewModel(finalViewModel);
    assertApprox(
      finalViewModel.velocityX,
      MOVEMENT_SPEED_PX_PER_SECOND,
      1e-6,
      'continued held movement should clamp at the configured top speed',
    );
    assertApprox(finalViewModel.speedNormalized, 1, 1e-6);
    assert.equal(finalViewModel.movementDirection, 1);
    assert.equal(finalViewModel.moving, true);
    assert.equal(finalViewModel.facing, 1);
  });

  for (const code of ['Space', 'Digit1', 'Numpad1']) {
    it(`preserves elapsed held movement when ${code} starts between frames`, () => {
      let withAction = createInitialState({
        now: 0,
        viewportWidth: 1000,
        avatarWidth: 200,
        edgePadding: 0,
        x: 400,
      });
      let withoutAction = createInitialState({
        now: 0,
        viewportWidth: 1000,
        avatarWidth: 200,
        edgePadding: 0,
        x: 400,
      });
      withAction = keyDown(withAction, 'ArrowRight');
      withoutAction = keyDown(withoutAction, 'ArrowRight');
      withAction = frame(withAction, 10);
      withoutAction = frame(withoutAction, 10);

      const xAfterFirstFrame = withAction.x;

      withAction = keyDown(withAction, code, 40);
      assert.equal(withAction.now, 10);
      withAction = frame(withAction, 40);
      withoutAction = frame(withoutAction, 40);

      assert.equal(withAction.x, withoutAction.x);
      assert.ok(withAction.x > xAfterFirstFrame);
    });
  }

  it('caps a long frame delta to avoid movement jumps', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 2000,
      avatarWidth: 200,
      edgePadding: 0,
      x: 500,
    });
    state = keyDown(state, 'ArrowRight');
    state = frame(state, 1000);
    const viewModel = selectViewModel(state);

    assert.equal(state.now, 1000);
    assert.ok(
      state.x > 500,
      'a long frame should still move forward when a walk key is held',
    );
    assert.ok(
      state.x < 500 + MOVEMENT_SPEED_PX_PER_SECOND * (MAX_FRAME_DELTA_MS / 1000),
      'a capped long frame should not teleport as if it ran at full speed from rest',
    );
    assertLocomotionViewModel(viewModel);
    assert.ok(viewModel.velocityX > 0);
    assert.ok(viewModel.velocityX < MOVEMENT_SPEED_PX_PER_SECOND);
  });

  it('decelerates after release while preserving continued movement until stop', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1800,
      avatarWidth: 200,
      edgePadding: 0,
      x: 400,
    });
    state = keyDown(state, 'ArrowRight');
    state = runFrames(state, [50, 100, 150, 200, 250, 300, 350, 400, 450, 500]);

    const xBeforeRelease = state.x;

    state = keyUp(state, 'ArrowRight');
    state = frame(state, 550);
    const firstCoastFrame = selectViewModel(state);
    const firstReleaseDelta = state.x - xBeforeRelease;

    assert.ok(
      firstReleaseDelta > 0,
      'releasing a walk key should continue moving with residual velocity instead of stopping instantly',
    );
    assert.ok(
      firstReleaseDelta < MOVEMENT_SPEED_PX_PER_SECOND * (MAX_FRAME_DELTA_MS / 1000),
      'the first release frame should be slower than a capped full-speed step',
    );
    assertLocomotionViewModel(firstCoastFrame);
    assert.ok(firstCoastFrame.velocityX > 0);
    assert.ok(firstCoastFrame.velocityX < MOVEMENT_SPEED_PX_PER_SECOND);
    assert.equal(firstCoastFrame.movementDirection, 1);
    assert.equal(firstCoastFrame.moving, true);

    state = runFrames(state, [600, 650, 700, 750, 800, 850, 900, 950, 1000]);
    const stoppedViewModel = selectViewModel(state);
    const xAtStop = state.x;

    assertLocomotionViewModel(stoppedViewModel);
    assertApprox(stoppedViewModel.velocityX, 0, 1e-6);
    assertApprox(stoppedViewModel.speedNormalized, 0, 1e-6);
    assert.equal(stoppedViewModel.movementDirection, 0);
    assert.equal(stoppedViewModel.moving, false);

    state = frame(state, 1050);
    assertApprox(state.x, xAtStop, 1e-6, 'position must stay stable once deceleration reaches zero');
  });

  it('smoothly reverses toward the last pressed arrow without snapping or freezing', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1800,
      avatarWidth: 200,
      edgePadding: 0,
      x: 400,
    });
    state = keyDown(state, 'ArrowRight');
    state = runFrames(state, [50, 100, 150, 200, 250, 300, 350, 400, 450, 500]);

    const xBeforeOpposingInput = state.x;

    state = keyDown(state, 'ArrowLeft', 500);
    assert.equal(
      selectViewModel(state).facing,
      1,
      'facing must stay committed to actual positive velocity until the avatar reaches zero',
    );

    state = frame(state, 550);
    const firstOpposingFrame = selectViewModel(state);
    const xAfterFirstOpposingFrame = state.x;

    assert.ok(
      xAfterFirstOpposingFrame > xBeforeOpposingInput,
      'pressing the opposite arrow should bleed off current motion before reversing it',
    );
    assertLocomotionViewModel(firstOpposingFrame);
    assert.ok(firstOpposingFrame.velocityX > 0);
    assert.equal(firstOpposingFrame.movementDirection, 1);
    assert.equal(firstOpposingFrame.facing, 1);
    assert.equal(firstOpposingFrame.moving, true);

    state = runFrames(state, [600, 650, 700, 750, 800, 850, 900, 950, 1000]);
    const reversedViewModel = selectViewModel(state);

    assert.ok(
      state.x < xAfterFirstOpposingFrame,
      'continued opposing input should eventually reverse the avatar back the other way',
    );
    assertLocomotionViewModel(reversedViewModel);
    assert.ok(reversedViewModel.velocityX < 0);
    assert.equal(reversedViewModel.movementDirection, -1);
    assert.equal(reversedViewModel.facing, -1);
    assert.equal(reversedViewModel.moving, true);
  });

  it('lets stationary input choose facing immediately while keeping moving reversals velocity-committed', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1800,
      avatarWidth: 200,
      edgePadding: 0,
      x: 400,
    });

    state = keyDown(state, 'ArrowLeft', 0);
    assert.equal(
      selectViewModel(state).facing,
      -1,
      'a stationary turn should face the newly requested direction immediately',
    );

    state = keyUp(state, 'ArrowLeft');
    state = keyDown(state, 'ArrowRight', 0);
    assert.equal(selectViewModel(state).facing, 1);

    state = frame(state, 50);
    state = keyDown(state, 'ArrowLeft', 50);
    assert.equal(
      selectViewModel(state).facing,
      1,
      'once actual motion is underway, facing must wait for the signed velocity to reverse',
    );
  });

  it('zeros signed velocity and gait motion at viewport bounds', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 500,
      avatarWidth: 100,
      edgePadding: 20,
      x: 379.9,
    });
    state = keyDown(state, 'ArrowRight');
    state = frame(state, 50);
    let viewModel = selectViewModel(state);

    assert.equal(state.x, 380);
    assert.equal(
      viewModel.moving,
      false,
      'walking into a bound should zero motion immediately instead of leaving the gait active',
    );
    assertLocomotionViewModel(viewModel);
    assert.equal(viewModel.velocityX, 0);
    assert.equal(viewModel.speedNormalized, 0);
    assert.equal(viewModel.movementDirection, 0);

    const gaitPhaseAtBound = viewModel.gaitPhase;

    state = frame(state, 100);
    viewModel = selectViewModel(state);

    assert.equal(state.x, 380);
    assert.equal(viewModel.gaitPhase, gaitPhaseAtBound);
  });

  it('derives gait phase from actual distance and keeps it frame-cadence invariant', () => {
    function runCadence(frameTimes) {
      let cadenceState = createInitialState({
        now: 0,
        viewportWidth: 1800,
        avatarWidth: 200,
        edgePadding: 0,
        x: 400,
      });
      cadenceState = keyDown(cadenceState, 'ArrowRight');
      cadenceState = runFrames(cadenceState, frameTimes);
      return {
        state: cadenceState,
        viewModel: selectViewModel(cadenceState),
      };
    }

    const coarseCadence = runCadence([50, 100, 150, 200, 250, 300]);
    const fineCadence = runCadence([25, 50, 75, 100, 125, 150, 175, 200, 225, 250, 275, 300]);

    assertLocomotionViewModel(coarseCadence.viewModel);
    assertLocomotionViewModel(fineCadence.viewModel);
    assertApprox(
      fineCadence.state.x,
      coarseCadence.state.x,
      1e-6,
      'actual walked distance should be invariant to frame cadence',
    );
    assertApprox(
      fineCadence.viewModel.velocityX,
      coarseCadence.viewModel.velocityX,
      1e-6,
      'signed locomotion velocity should be invariant to frame cadence',
    );
    assertApprox(
      fineCadence.viewModel.gaitPhase,
      coarseCadence.viewModel.gaitPhase,
      1e-6,
      'gaitPhase should derive from actual distance instead of elapsed frame count',
    );

    let idleState = fineCadence.state;
    idleState = keyUp(idleState, 'ArrowRight');
    idleState = runFrames(idleState, [350, 400, 450, 500, 550, 600, 650, 700]);
    const gaitAtStop = selectViewModel(idleState).gaitPhase;
    idleState = frame(idleState, 750);
    assertApprox(
      selectViewModel(idleState).gaitPhase,
      gaitAtStop,
      1e-6,
      'idle frames must not advance gaitPhase once the avatar stops covering distance',
    );
  });

  it('scales gait cadence with rendered avatar height while preserving traveled distance', () => {
    function runAtHeight(avatarHeight) {
      let heightState = createInitialState({
        now: 0,
        viewportWidth: 1800,
        avatarWidth: 200,
        avatarHeight,
        edgePadding: 0,
        x: 400,
      });
      heightState = keyDown(heightState, 'ArrowRight');
      heightState = runFrames(heightState, [50, 100]);
      return {
        state: heightState,
        viewModel: selectViewModel(heightState),
      };
    }

    const referenceHeight = runAtHeight(360);
    const doubleHeight = runAtHeight(720);

    assertApprox(
      doubleHeight.state.x,
      referenceHeight.state.x,
      1e-6,
      'avatar height must not alter horizontal motion semantics',
    );
    assertApprox(
      doubleHeight.viewModel.gaitPhase,
      referenceHeight.viewModel.gaitPhase / 2,
      1e-6,
      'doubling rendered avatar height must double stride distance and halve gait cadence',
    );
  });

  it('ignores repeated keydown without advancing time or restarting actions', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1200,
      avatarWidth: 200,
      x: 500,
    });
    state = keyDown(state, 'ArrowRight', 0);
    state = frame(state, 16);

    const beforeArrowRepeat = state;
    state = keyDown(state, 'ArrowRight', 32, true);
    assert.strictEqual(state, beforeArrowRepeat);
    const firstFrameDistance = beforeArrowRepeat.x - 500;
    state = frame(state, 32);
    const secondFrameDistance = state.x - beforeArrowRepeat.x;
    assert.ok(
      secondFrameDistance >= firstFrameDistance,
      'repeat keydown must not reset the walking acceleration ramp',
    );
    assert.ok(
      secondFrameDistance < MOVEMENT_SPEED_PX_PER_SECOND * 0.016,
      'repeat keydown must preserve a sub-cap frame distance until the ramp reaches top speed',
    );

    state = keyDown(state, 'Space', 1000);
    assert.equal(state.bubbleUntil, 4000);
    const beforeSpaceRepeat = state;
    state = keyDown(state, 'Space', 2000, true);
    assert.strictEqual(state, beforeSpaceRepeat);
    assert.equal(state.bubbleUntil, 4000);

    state = keyDown(state, 'Digit1', 3000);
    assert.equal(state.clapUntil, 8000);
    const beforeClapRepeat = state;
    state = keyDown(state, 'Digit1', 4000, true);
    assert.strictEqual(state, beforeClapRepeat);
    assert.equal(state.clapUntil, 8000);
  });

  it('clears held movement on focus loss', () => {
    let state = createInitialState({ now: 0 });
    state = keyDown(state, 'ArrowRight');
    state = transition(state, { type: 'focusLost' });

    assert.deepEqual(state.held, { left: false, right: false });
    assert.equal(selectViewModel(state).moving, false);
  });

  it('reclamps the current position when layout changes', () => {
    let state = createInitialState({
      viewportWidth: 1200,
      avatarWidth: 200,
      edgePadding: 20,
      x: 900,
    });
    state = transition(state, {
      type: 'layout',
      viewportWidth: 600,
      avatarWidth: 180,
      edgePadding: 20,
    });

    assert.equal(state.x, 400);
  });

  it('shows the exact bubble text for 3000ms and restarts on Space', () => {
    let state = createInitialState({ now: 100 });
    state = keyDown(state, 'Space', 100);

    assert.equal(state.bubbleUntil, 100 + BUBBLE_DURATION_MS);
    assert.equal(selectViewModel(state).bubbleVisible, true);
    assert.equal(selectViewModel(state).bubbleText, 'Welcome to TCF');

    state = frame(state, 2500);
    state = keyDown(state, 'Space', 2500);
    assert.equal(state.bubbleUntil, 2500 + BUBBLE_DURATION_MS);

    state = frame(state, 5499);
    assert.equal(selectViewModel(state).bubbleVisible, true);
    state = frame(state, 5500);
    assert.equal(selectViewModel(state).bubbleVisible, false);
  });

  for (const code of ['Digit1', 'Numpad1']) {
    it(`claps for 5000ms and restarts with ${code}`, () => {
      let state = createInitialState({ now: 0 });
      state = keyDown(state, code, 10);
      assert.equal(state.clapUntil, 10 + CLAP_DURATION_MS);
      assert.equal(selectViewModel(state).clapping, true);

      state = frame(state, 4000);
      state = keyDown(state, code, 4000);
      assert.equal(state.clapUntil, 4000 + CLAP_DURATION_MS);

      state = frame(state, 8999);
      assert.equal(selectViewModel(state).clapping, true);
      state = frame(state, 9000);
      assert.equal(selectViewModel(state).clapping, false);
    });
  }

  it('keeps movement, bubble, and clapping independent', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1000,
      avatarWidth: 200,
      x: 400,
    });
    state = keyDown(state, 'ArrowRight');
    state = keyDown(state, 'Space');
    state = keyDown(state, 'Digit1');
    state = frame(state, 25);

    const viewModel = selectViewModel(state);
    assert.equal(viewModel.moving, true);
    assert.equal(viewModel.bubbleVisible, true);
    assert.equal(viewModel.clapping, true);
    assert.ok(state.x > 400);
  });

  it('rejects malformed input and unknown transitions', () => {
    assert.throws(
      () => createInitialState({ viewportWidth: Number.NaN }),
      /viewportWidth must be a finite number/,
    );
    assert.throws(
      () => transition(createInitialState(), { type: 'unknown' }),
      /Unknown event type/,
    );
    assert.throws(
      () => transition(createInitialState(), { type: 'frame', now: Number.NaN }),
      /event.now must be a finite number/,
    );
  });
});

describe('browser integration', () => {
  it('keeps repeat keydown idempotent while preventing browser defaults', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const window = new FakeEventTarget();
    const frames = new Map();
    const rendered = [];
    let nextFrameId = 1;
    let now = 0;

    const app = createAvatarApp({
      document,
      window,
      performance: { now: () => now },
      requestAnimationFrame(callback) {
        const id = nextFrameId;
        nextFrameId += 1;
        frames.set(id, callback);
        return id;
      },
      cancelAnimationFrame(id) {
        frames.delete(id);
      },
      rendererFactory() {
        return {
          measure: () => ({ viewportWidth: 1200, avatarWidth: 200 }),
          render: (viewModel) => rendered.push(viewModel),
          destroy() {},
        };
      },
    });

    function runNextFrame(frameNow) {
      const [id, callback] = frames.entries().next().value;
      frames.delete(id);
      callback(frameNow);
    }

    const right = createKeyboardEvent('ArrowRight');
    window.dispatch('keydown', right);
    assert.equal(right.defaultPrevented, true);
    runNextFrame(16);
    const firstWalkingFrame = rendered.at(-1);

    assertLocomotionViewModel(firstWalkingFrame);
    assert.ok(firstWalkingFrame.x > 500);
    assert.ok(firstWalkingFrame.x < 500 + MOVEMENT_SPEED_PX_PER_SECOND * 0.016);

    now = 32;
    const repeatedRight = createKeyboardEvent('ArrowRight', { repeat: true });
    window.dispatch('keydown', repeatedRight);
    assert.equal(repeatedRight.defaultPrevented, true);
    assert.equal(app.getState().now, 16);
    runNextFrame(32);
    const secondWalkingFrame = rendered.at(-1);

    assertLocomotionViewModel(secondWalkingFrame);
    assert.ok(secondWalkingFrame.x > firstWalkingFrame.x);
    assert.ok(secondWalkingFrame.velocityX >= firstWalkingFrame.velocityX);

    window.dispatch('keyup', createKeyboardEvent('ArrowRight'));
    now = 1000;
    window.dispatch('keydown', createKeyboardEvent('Space'));
    assert.equal(app.getState().bubbleUntil, 4000);

    now = 2000;
    const repeatedSpace = createKeyboardEvent('Space', { repeat: true });
    window.dispatch('keydown', repeatedSpace);
    assert.equal(repeatedSpace.defaultPrevented, true);
    assert.equal(app.getState().bubbleUntil, 4000);

    window.dispatch('keyup', createKeyboardEvent('Space'));
    now = 2500;
    window.dispatch('keydown', createKeyboardEvent('Space'));
    assert.equal(app.getState().bubbleUntil, 5500);

    now = 3000;
    window.dispatch('keydown', createKeyboardEvent('Numpad1'));
    assert.equal(app.getState().clapUntil, 8000);
    now = 4000;
    window.dispatch('keydown', createKeyboardEvent('Numpad1', { repeat: true }));
    assert.equal(app.getState().clapUntil, 8000);
    window.dispatch('keyup', createKeyboardEvent('Numpad1'));
    now = 4500;
    window.dispatch('keydown', createKeyboardEvent('Numpad1'));
    assert.equal(app.getState().clapUntil, 9500);

    assert.ok(rendered.length > 1);
    app.stop();
    assert.equal(frames.size, 0);
  });

  it('five-layer: default renderer preserves app controls while using exactly six rasters', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const window = new FakeEventTarget();
    const frames = new Map();
    let nextFrameId = 1;
    let now = 0;

    const app = createAvatarApp({
      document,
      window,
      performance: { now: () => now },
      requestAnimationFrame(callback) {
        const id = nextFrameId;
        nextFrameId += 1;
        frames.set(id, callback);
        return id;
      },
      cancelAnimationFrame(id) {
        frames.delete(id);
      },
    });

    function runNextFrame(frameNow) {
      now = frameNow;
      const [id, callback] = frames.entries().next().value;
      frames.delete(id);
      callback(frameNow);
    }

    const [stage] = mount.children;
    const [bubbleTarget, track] = stage.children;
    const [bubble] = bubbleTarget.children;
    const layerSources = rasterLayerSources(track);
    assert.deepEqual(
      [...layerSources].sort(),
      FIVE_LAYER_RASTER_URLS,
      'renderer must use exactly idle plus the five frozen rigid layer URLs',
    );
    assert.ok(layerSources.every((source) => /\.png(?:[?#]|$)/i.test(source)));
    assert.doesNotMatch(track.innerHTML, /<(?:svg|path|ellipse|circle)\b/i);
    assert.doesNotMatch(track.innerHTML, /face-photo|avatar-face\.jpg/i);
    assert.equal(track.style.transform, 'translate3d(460.00px, 0, 0)');
    assert.equal(track.dataset.direction, '1');
    assert.equal(track.classList.contains('is-walking'), false);
    assert.equal(track.classList.contains('is-clapping'), false);
    assert.equal(bubbleTarget.hidden, true);
    assert.equal(
      stage.style['--operator-background'],
      undefined,
      'the default renderer must not request an absent background.jpg',
    );

    const right = createKeyboardEvent('ArrowRight');
    window.dispatch('keydown', right);
    assert.equal(right.defaultPrevented, true);
    assert.equal(track.classList.contains('is-walking'), true);
    runNextFrame(10);
    const xAfter10 = translateXFromTransform(track.style.transform);
    assert.ok(xAfter10 > 460);
    assert.ok(
      xAfter10 < 460 + MOVEMENT_SPEED_PX_PER_SECOND * 0.01,
      'the first renderer frame should accelerate instead of jumping to full speed',
    );

    now = 40;
    const space = createKeyboardEvent('Space');
    window.dispatch('keydown', space);
    assert.equal(space.defaultPrevented, true);
    assert.equal(app.getState().now, 10);
    assert.equal(bubbleTarget.hidden, false);
    assert.equal(bubble.textContent, BUBBLE_TEXT);
    runNextFrame(40);
    const xAfter40 = translateXFromTransform(track.style.transform);
    assert.ok(xAfter40 > xAfter10);

    const bubbleUntil = app.getState().bubbleUntil;
    now = 50;
    const repeatedSpace = createKeyboardEvent('Space', { repeat: true });
    window.dispatch('keydown', repeatedSpace);
    assert.equal(repeatedSpace.defaultPrevented, true);
    assert.equal(app.getState().bubbleUntil, bubbleUntil);

    window.dispatch('keyup', createKeyboardEvent('Space'));
    now = 60;
    window.dispatch('keydown', createKeyboardEvent('Space'));
    assert.equal(app.getState().bubbleUntil, 60 + BUBBLE_DURATION_MS);

    now = 70;
    window.dispatch('keydown', createKeyboardEvent('Digit1'));
    assert.equal(track.classList.contains('is-clapping'), true);
    const clapUntil = app.getState().clapUntil;
    now = 80;
    window.dispatch('keydown', createKeyboardEvent('Digit1', { repeat: true }));
    assert.equal(app.getState().clapUntil, clapUntil);
    window.dispatch('keyup', createKeyboardEvent('Digit1'));
    now = 90;
    window.dispatch('keydown', createKeyboardEvent('Digit1'));
    assert.equal(app.getState().clapUntil, 90 + CLAP_DURATION_MS);

    runNextFrame(90);
    const xAfter90 = translateXFromTransform(track.style.transform);
    assert.ok(xAfter90 > xAfter40);

    const left = createKeyboardEvent('ArrowLeft');
    window.dispatch('keydown', left);
    assert.equal(left.defaultPrevented, true);
    assert.equal(track.classList.contains('is-walking'), true);
    assert.equal(
      track.dataset.direction,
      '1',
      'rendered facing should stay aligned with actual positive velocity until reversal completes',
    );
    runNextFrame(100);
    const xAfter100 = translateXFromTransform(track.style.transform);
    assert.ok(
      xAfter100 > xAfter90,
      'opposing input should keep coasting before the renderer shows a reversal',
    );

    window.dispatch('keyup', createKeyboardEvent('ArrowRight'));
    assert.equal(track.classList.contains('is-walking'), true);
    assert.equal(track.dataset.direction, '1');
    runNextFrame(150);
    const xAfter150 = translateXFromTransform(track.style.transform);
    runNextFrame(200);
    const xAfter200 = translateXFromTransform(track.style.transform);
    runNextFrame(250);
    const xAfter250 = translateXFromTransform(track.style.transform);
    assert.ok(
      xAfter250 < xAfter150 || xAfter250 < xAfter200,
      'after the opposite key owns intent, the renderer should eventually move back the other way',
    );
    assert.equal(track.dataset.direction, '-1');

    now = 260;
    const repeatedLeft = createKeyboardEvent('ArrowLeft', { repeat: true });
    window.dispatch('keydown', repeatedLeft);
    assert.equal(repeatedLeft.defaultPrevented, true);
    assert.equal(app.getState().now, 250);
    runNextFrame(260);
    const xAfter260 = translateXFromTransform(track.style.transform);
    assert.ok(xAfter260 <= xAfter250);

    window.dispatch('keyup', createKeyboardEvent('ArrowLeft'));
    runNextFrame(310);
    runNextFrame(360);
    runNextFrame(410);
    runNextFrame(460);
    runNextFrame(510);
    assert.equal(track.classList.contains('is-walking'), false);

    app.stop();
    assert.equal(frames.size, 0);
    assert.equal(stage.removed, true);
  });

  it('five-layer: runtime rig and DOM agree on exact hierarchy and painter order', () => {
    assert.deepEqual(
      AVATAR_RIG.layers.map((layer) => ({
        role: layer.role,
        parent: layer.parent,
        pivot: layer.pivot,
        zIndex: layer.zIndex,
        url: layer.url,
      })),
      FIVE_LAYER_CONTRACT.map(({
        role,
        parent,
        pivot,
        zIndex,
        url,
      }) => ({
        role,
        parent,
        pivot,
        zIndex,
        url,
      })),
      'src/avatar-rig.js must expose the frozen five-layer contract',
    );
    assert.equal(AVATAR_RIG.idleUrl, './assets/avatar/idle.png');

    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [, track] = stage.children;
    const torsoPainter = track.querySelector('[data-avatar-role="torsoHead"]');
    const torsoRoot = torsoPainter?.parentElement;
    const wrapperByRole = new Map();
    let previousRoleOffset = -1;

    assert.ok(torsoPainter, 'the rendered rig must paint the torsoHead raster');
    assert.ok(
      elementHasClass(torsoRoot, 'avatar-joint'),
      'the torsoHead raster must live inside one rotating root joint wrapper',
    );
    assert.equal(
      track.querySelectorAll('.avatar-joint').length,
      FIVE_LAYER_CONTRACT.length,
      'the DOM rotation graph must contain exactly five avatar joints',
    );
    const idleFallback = track.querySelector('.avatar-idle-fallback');
    assert.equal(
      idleFallback?.attributes.get('src'),
      AVATAR_RIG.idleUrl,
      'the fallback image must use the registered idle URL',
    );

    for (const layer of FIVE_LAYER_CONTRACT) {
      const roleMarker = `data-avatar-role="${layer.role}"`;
      const roleOffset = track.innerHTML.indexOf(roleMarker);
      const roleTag = new RegExp(
        `<(?:div|img)\\b(?=[^>]*${roleMarker})(?=[^>]*style="[^"]*z-index:\\s*${layer.zIndex}(?:;|"))[^>]*>`,
        'i',
      );

      assert.match(
        track.innerHTML,
        roleTag,
        `${layer.role} must apply registered zIndex ${layer.zIndex} to its painted element`,
      );
      assert.ok(
        roleOffset > previousRoleOffset,
        `${layer.role} must follow the registered painter order in generated markup`,
      );
      previousRoleOffset = roleOffset;

      const renderedRole = track.querySelector(`[data-avatar-role="${layer.role}"]`);
      assert.ok(renderedRole, `${layer.role} must exist in the rendered fake DOM`);

      if (layer.role === 'torsoHead') {
        assert.equal(renderedRole.tagName, 'IMG');
        assert.equal(
          renderedRole.attributes.get('src'),
          layer.url,
          'torsoHead must paint its own registered raster URL',
        );
        assert.equal(
          renderedRole.parentElement,
          torsoRoot,
          'the torsoHead painter must be a direct child of the rotating root wrapper',
        );
        wrapperByRole.set(layer.role, torsoRoot);
      } else {
        assert.equal(renderedRole.tagName, 'DIV');
        assert.ok(
          elementHasClass(renderedRole, 'avatar-joint'),
          `${layer.role} must render as a joint wrapper`,
        );
        assert.ok(
          elementHasClass(renderedRole.children[0], 'avatar-layer'),
          `${layer.role} must paint its single rigid raster`,
        );
        assert.equal(
          renderedRole.children[0].attributes.get('src'),
          layer.url,
          `${layer.role} must paint its own registered raster URL`,
        );
        wrapperByRole.set(layer.role, renderedRole);
      }
    }

    for (const layer of FIVE_LAYER_CONTRACT) {
      if (layer.parent === null) {
        continue;
      }

      const wrapper = wrapperByRole.get(layer.role);
      const parentWrapper = wrapperByRole.get(layer.parent);
      assert.equal(
        wrapper.parentElement,
        parentWrapper,
        `${layer.role} wrapper must be an immediate child of its declared ${layer.parent} parent wrapper`,
      );
    }

    renderer.destroy();
  });

  it('five-layer: reduced motion neutralizes all five rigid rotations', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const reducedMotion = new FakeMediaQueryList(true);
    document.defaultView = {
      matchMedia(query) {
        assert.equal(query, '(prefers-reduced-motion: reduce)');
        return reducedMotion;
      },
    };
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [, track] = stage.children;
    const animatedViewModel = createPoseViewModel({
      x: 240,
      moving: true,
      movementDirection: 1,
      facing: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.25,
      clapping: true,
      clapProgress: 0.15,
    });
    const articulatedProperties = [
      '--body-translate-y-pct',
      '--torso-head-rotate',
      '--left-arm-rotate',
      '--right-arm-rotate',
      '--left-leg-rotate',
      '--right-leg-rotate',
      '--clap-accent-opacity',
    ];

    renderer.render(animatedViewModel);
    assert.equal(track.style.transform, 'translate3d(240.00px, 0, 0)');
    assert.equal(track.classList.contains('is-walking'), true);
    assert.equal(track.classList.contains('is-clapping'), true);
    assert.equal(track.dataset.action, 'clap');
    assert.equal(track.dataset.reducedMotion, 'reduce');
    assert.equal(reducedMotion.listenerCount('change'), 1);
    for (const property of articulatedProperties) {
      assert.equal(
        Number.parseFloat(track.style[property]),
        0,
        `${property} must be neutral when reduced motion is active`,
      );
    }

    reducedMotion.setMatches(false);
    assert.equal(track.dataset.reducedMotion, 'no-preference');
    assert.notEqual(Number.parseFloat(track.style['--torso-head-rotate']), 0);
    assert.notEqual(Number.parseFloat(track.style['--left-arm-rotate']), 0);

    reducedMotion.setMatches(true);
    assert.equal(track.style.transform, 'translate3d(240.00px, 0, 0)');
    for (const property of articulatedProperties) {
      assert.equal(
        Number.parseFloat(track.style[property]),
        0,
        `${property} must return to neutral after the preference changes`,
      );
    }

    renderer.destroy();
    assert.equal(reducedMotion.listenerCount('change'), 0);
  });

  it('five-layer: keeps idle fallback until all five rigid layers load', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [, track] = stage.children;
    const layers = track.querySelectorAll('.avatar-layer');

    assert.equal(layers.length, FIVE_LAYER_CONTRACT.length);
    assert.equal(track.dataset.rigState, 'loading');
    assert.ok(layers.every((image) => image.listenerCount('load') === 1));
    assert.ok(layers.every((image) => image.listenerCount('error') === 1));

    for (const image of layers.slice(0, -1)) {
      image.dispatch('load');
      assert.equal(track.dataset.rigState, 'loading');
    }
    layers.at(-1).dispatch('load');
    assert.equal(track.dataset.rigState, 'ready');
    assert.ok(layers.every((image) => image.listenerCount('load') === 0));
    assert.ok(layers.every((image) => image.listenerCount('error') === 1));

    renderer.destroy();
    assert.ok(layers.every((image) => image.listenerCount('load') === 0));
    assert.ok(layers.every((image) => image.listenerCount('error') === 0));
  });

  for (const avatarLayerCount of [4, 6]) {
    it(`five-layer: keeps fallback permanent when ${avatarLayerCount} rig rasters are rendered`, () => {
      const mount = new FakeElement('main');
      const document = new FakeDocument(mount, { avatarLayerCount });
      const renderer = createRenderer({ document, mount });
      const [stage] = mount.children;
      const [, track] = stage.children;
      const layers = track.querySelectorAll('.avatar-layer');

      assert.equal(layers.length, avatarLayerCount);
      assert.equal(track.dataset.rigState, 'fallback');
      assert.ok(layers.every((image) => image.listenerCount('load') === 0));
      assert.ok(layers.every((image) => image.listenerCount('error') === 0));

      for (const image of layers) {
        image.dispatch('load');
      }
      assert.equal(
        track.dataset.rigState,
        'fallback',
        'later load events must not recover an invalid layer cardinality',
      );

      renderer.destroy();
    });
  }

  it('five-layer: makes any rigid raster error a persistent fallback', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [, track] = stage.children;
    const layers = track.querySelectorAll('.avatar-layer');

    layers[4].dispatch('error');
    assert.equal(track.dataset.rigState, 'fallback');
    for (const image of layers) {
      image.dispatch('load');
    }
    assert.equal(track.dataset.rigState, 'fallback');

    renderer.destroy();
  });

  it('five-layer: cached completion is ready only when all five rigid rasters have pixels', () => {
    const successMount = new FakeElement('main');
    const successDocument = new FakeDocument(successMount, {
      imageState: { complete: true, naturalWidth: AVATAR_RIG.canvas.width },
    });
    const successRenderer = createRenderer({
      document: successDocument,
      mount: successMount,
    });
    const [, successTrack] = successMount.children[0].children;
    const successLayers = successTrack.querySelectorAll('.avatar-layer');

    assert.equal(successLayers.length, FIVE_LAYER_CONTRACT.length);
    assert.equal(successTrack.dataset.rigState, 'ready');
    assert.ok(successLayers.every((image) => image.listenerCount('load') === 0));
    assert.ok(successLayers.every((image) => image.listenerCount('error') === 0));
    successRenderer.destroy();

    const failureMount = new FakeElement('main');
    const failureDocument = new FakeDocument(failureMount, {
      imageState: { complete: true, naturalWidth: 0 },
    });
    const failureRenderer = createRenderer({
      document: failureDocument,
      mount: failureMount,
    });
    const [, failureTrack] = failureMount.children[0].children;

    assert.equal(failureTrack.dataset.rigState, 'fallback');
    failureRenderer.destroy();
  });

  it('handles lifecycle events through app wiring and pagehide performs complete cleanup', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const window = new FakeEventTarget();
    const frames = new Map();
    const cancelledFrames = [];
    const rendered = [];
    let nextFrameId = 1;
    let now = 0;
    let layout = { viewportWidth: 1000, avatarWidth: 200 };
    let measureCount = 0;
    let destroyCount = 0;

    const app = createAvatarApp({
      document,
      window,
      performance: { now: () => now },
      requestAnimationFrame(callback) {
        const id = nextFrameId;
        nextFrameId += 1;
        frames.set(id, callback);
        return id;
      },
      cancelAnimationFrame(id) {
        cancelledFrames.push(id);
        frames.delete(id);
      },
      rendererFactory() {
        return {
          measure() {
            measureCount += 1;
            return layout;
          },
          render(viewModel) {
            rendered.push(viewModel);
          },
          destroy() {
            destroyCount += 1;
          },
        };
      },
    });

    assert.equal(window.listenerCount('blur'), 1);
    assert.equal(window.listenerCount('resize'), 1);
    assert.equal(window.listenerCount('pagehide'), 1);
    assert.equal(document.listenerCount('visibilitychange'), 1);

    window.dispatch('keydown', createKeyboardEvent('ArrowRight'));
    assert.deepEqual(app.getState().held, { left: false, right: true });
    window.dispatch('blur');
    assert.deepEqual(app.getState().held, { left: false, right: false });

    window.dispatch('keydown', createKeyboardEvent('ArrowLeft'));
    assert.deepEqual(app.getState().held, { left: true, right: false });
    document.visibilityState = 'visible';
    document.dispatch('visibilitychange');
    assert.deepEqual(app.getState().held, { left: true, right: false });
    document.visibilityState = 'hidden';
    document.dispatch('visibilitychange');
    assert.deepEqual(app.getState().held, { left: false, right: false });

    layout = { viewportWidth: 400, avatarWidth: 200 };
    window.dispatch('resize');
    assert.equal(measureCount, 2);
    assert.equal(app.getState().layout.viewportWidth, 400);
    assert.equal(app.getState().x, 176);

    const stateBeforePagehide = app.getState();
    assert.equal(frames.size, 1);
    window.dispatch('pagehide');
    assert.equal(destroyCount, 1);
    assert.equal(frames.size, 0);
    assert.equal(cancelledFrames.length, 1);
    assert.equal(window.listenerCount('keydown'), 0);
    assert.equal(window.listenerCount('keyup'), 0);
    assert.equal(window.listenerCount('resize'), 0);
    assert.equal(window.listenerCount('blur'), 0);
    assert.equal(window.listenerCount('pagehide'), 0);
    assert.equal(document.listenerCount('visibilitychange'), 0);

    now = 100;
    window.dispatch('keydown', createKeyboardEvent('ArrowRight'));
    window.dispatch('resize');
    document.dispatch('visibilitychange');
    assert.strictEqual(app.getState(), stateBeforePagehide);
    app.stop();
    assert.equal(destroyCount, 1);
    assert.ok(rendered.length >= 6);
  });

  it('auto-bootstraps on browser module import and can be cleaned up through pagehide', async () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const window = new FakeEventTarget();
    const frames = new Map();
    const cancelledFrames = [];
    let nextFrameId = 1;

    const originalDescriptors = new Map(
      ['document', 'window', 'performance', 'requestAnimationFrame', 'cancelAnimationFrame']
        .map((name) => [name, Object.getOwnPropertyDescriptor(globalThis, name)]),
    );

    Object.defineProperties(globalThis, {
      document: {
        configurable: true,
        value: document,
        writable: true,
      },
      window: {
        configurable: true,
        value: window,
        writable: true,
      },
      performance: {
        configurable: true,
        value: { now: () => 0 },
        writable: true,
      },
      requestAnimationFrame: {
        configurable: true,
        value(callback) {
          const id = nextFrameId;
          nextFrameId += 1;
          frames.set(id, callback);
          return id;
        },
        writable: true,
      },
      cancelAnimationFrame: {
        configurable: true,
        value(id) {
          cancelledFrames.push(id);
          frames.delete(id);
        },
        writable: true,
      },
    });

    try {
      const moduleUrl = new URL('../src/app.js', import.meta.url);
      moduleUrl.searchParams.set('bootstrap-test', String(Date.now()));
      await import(moduleUrl.href);

      assert.equal(mount.children.length, 1);
      assert.equal(window.listenerCount('keydown'), 1);
      assert.equal(window.listenerCount('pagehide'), 1);
      assert.equal(document.listenerCount('visibilitychange'), 1);
      assert.equal(frames.size, 1);

      const [stage] = mount.children;
      window.dispatch('pagehide');
      assert.equal(stage.removed, true);
      assert.equal(frames.size, 0);
      assert.equal(cancelledFrames.length, 1);
      assert.equal(window.listenerCount('keydown'), 0);
      assert.equal(window.listenerCount('keyup'), 0);
      assert.equal(window.listenerCount('resize'), 0);
      assert.equal(window.listenerCount('blur'), 0);
      assert.equal(window.listenerCount('pagehide'), 0);
      assert.equal(document.listenerCount('visibilitychange'), 0);
    } finally {
      for (const [name, descriptor] of originalDescriptors) {
        if (descriptor === undefined) {
          delete globalThis[name];
        } else {
          Object.defineProperty(globalThis, name, descriptor);
        }
      }
    }
  });

  it('renders movement and direction separately with transition-based live announcements', async () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [bubbleTarget, track, instructions, liveStatus] = stage.children;
    const [bubble] = bubbleTarget.children;
    const visible = {
      x: 125.5,
      moving: true,
      movementDirection: 1,
      velocityX: 180,
      speedNormalized: 0.5,
      gaitPhase: 0.25,
      facing: -1,
      bubbleVisible: true,
      bubbleText: BUBBLE_TEXT,
      clapping: false,
    };

    assert.equal(bubble.attributes.has('role'), false);
    assert.equal(bubble.attributes.has('aria-live'), false);
    assert.equal(bubbleTarget.attributes.get('aria-hidden'), 'true');
    assert.equal(instructions.className, 'screen-reader-only');
    assert.equal(liveStatus.className, 'screen-reader-only live-status');
    assert.equal(liveStatus.attributes.get('role'), 'status');
    assert.equal(liveStatus.attributes.get('aria-live'), 'polite');
    assert.equal(liveStatus.attributes.get('aria-atomic'), 'true');
    assert.equal(liveStatus.hidden, false);
    assert.equal(liveStatus.textContentWrites, 0);

    renderer.render(visible);
    assert.equal(track.style.transform, 'translate3d(125.50px, 0, 0)');
    assert.equal(track.dataset.direction, '-1');
    assert.equal(track.classList.contains('is-walking'), true);
    assert.match(track.innerHTML, /class="avatar-facing"/);
    assert.equal(bubble.textContentWrites, 1);
    assert.equal(liveStatus.textContent, BUBBLE_TEXT);
    assert.equal(liveStatus.textContentWrites, 1);

    for (let index = 0; index < 20; index += 1) {
      renderer.render(visible);
    }
    assert.equal(bubble.textContentWrites, 1);
    assert.equal(liveStatus.textContentWrites, 1);

    renderer.render({ ...visible, bubbleVisible: false });
    assert.equal(bubble.textContentWrites, 2);
    assert.equal(liveStatus.textContent, '');
    assert.equal(liveStatus.textContentWrites, 2);
    for (let index = 0; index < 20; index += 1) {
      renderer.render({ ...visible, bubbleVisible: false });
    }
    assert.equal(bubble.textContentWrites, 2);
    assert.equal(liveStatus.textContentWrites, 2);

    renderer.render(visible);
    assert.equal(
      liveStatus.textContent,
      BUBBLE_TEXT,
      'the same welcome must announce again after dismissal',
    );
    assert.equal(liveStatus.textContentWrites, 3);

    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    assert.match(
      styles,
      /\.avatar-track\[data-direction="-1"\] \.avatar-facing\s*\{\s*transform: scaleX\(-1\)/,
    );
    assert.doesNotMatch(
      styles,
      /\.avatar-track\[data-direction="-1"\] \.avatar-body\s*\{/,
    );
  });

  it('five-layer: exports deterministic kinematics with only five rigid rotations', () => {
    const computePoseKinematics = getPoseHelper();
    const sampleViewModel = createPoseViewModel({
      x: 240,
      moving: true,
      movementDirection: 1,
      facing: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND * 0.75,
      speedNormalized: 0.75,
      gaitPhase: 0.375,
      bubbleVisible: true,
      clapping: true,
      clapProgress: 0.1,
    });

    const first = computePoseKinematics(sampleViewModel);
    const second = computePoseKinematics(sampleViewModel);

    assert.deepEqual(
      first,
      second,
      'the pose helper must stay pure and deterministic for tests',
    );
    assert.deepEqual(
      Object.keys(first.cssVariables)
        .filter((name) => name.endsWith('RotateDeg'))
        .sort(),
      [...FIVE_LAYER_ROTATION_PROPERTIES].sort(),
      'pose kinematics must publish exactly torsoHead/leftArm/rightArm/leftLeg/rightLeg rotations',
    );
    for (const property of LEGACY_ROTATION_PROPERTIES) {
      assert.equal(
        Object.hasOwn(first.cssVariables, property),
        false,
        `${property} must be removed from the rigid pose contract`,
      );
    }
  });

  it('five-layer: keeps idle rigid rotations neutral regardless of gait phase', () => {
    for (const gaitPhase of [0, 0.125, 0.25, 0.5, 0.875]) {
      const pose = computePose({
        moving: false,
        movementDirection: 1,
        facing: 1,
        velocityX: 0,
        speedNormalized: 0,
        gaitPhase,
      });

      assertNeutralPoseCss(pose.cssVariables, `gaitPhase=${gaitPhase}`);
    }
  });

  it('five-layer: keeps stopped rigid rotations neutral at a frozen mid-stride phase', () => {
    let state = createInitialState({
      now: 0,
      viewportWidth: 1800,
      avatarWidth: 200,
      edgePadding: 0,
      x: 400,
    });
    state = keyDown(state, 'ArrowRight');
    state = runFrames(state, [50, 100, 150, 200, 250, 300, 350, 400, 450, 500]);
    state = keyUp(state, 'ArrowRight');
    state = runFrames(state, [550, 600, 650, 700, 750, 800, 850, 900, 950, 1000]);

    const stoppedViewModel = selectViewModel(state);
    assert.ok(stoppedViewModel.gaitPhase > 0 && stoppedViewModel.gaitPhase < 1);
    assert.equal(stoppedViewModel.speedNormalized, 0);

    const pose = getPoseHelper()(stoppedViewModel);
    assertNeutralPoseCss(pose.cssVariables, 'stopped pose');
  });

  it('five-layer: mirrors the canonical torsoHead pose through the facing wrapper only', () => {
    const rightPose = computePose({
      movementDirection: 1,
      facing: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.25,
    });
    const leftPose = computePose({
      movementDirection: -1,
      facing: -1,
      velocityX: -MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.25,
    });

    for (const name of FIVE_LAYER_ROTATION_PROPERTIES) {
      assertApprox(
        rightPose.cssVariables[name],
        leftPose.cssVariables[name],
        1e-6,
        `${name} must stay canonical while the facing wrapper mirrors the rendered pose`,
      );
    }
    for (const pose of [rightPose, leftPose]) {
      for (const removedProperty of ['shadowScaleX', 'shadowShiftXPx']) {
        assert.equal(
          Object.hasOwn(pose.cssVariables, removedProperty),
          false,
          `${removedProperty} must not return after the floor shadow was removed`,
        );
      }
    }

    for (const pointName of ['bodyReference', 'headReference']) {
      const rightPoint = rightPose.worldPoints[pointName];
      const leftPoint = leftPose.worldPoints[pointName];

      assertApprox(
        rightPoint.x + leftPoint.x,
        rightPose.sourceCenterX * 2,
        1e-6,
        `${pointName} must mirror about source x=${rightPose.sourceCenterX}`,
      );
      assertApprox(
        rightPoint.y,
        leftPoint.y,
        1e-6,
        `${pointName} mirrored poses must retain the same world-space height`,
      );
      assert.ok(
        (rightPoint.x - rightPose.sourceCenterX)
          * (leftPoint.x - leftPose.sourceCenterX) < 0,
        `${pointName} must displace onto opposite sides instead of leaning the same way`,
      );
    }
  });

  it('five-layer: rigid arms preserve centered clap closure without crossing', () => {
    const separatedPose = computePose({
      movementDirection: 1,
      facing: 1,
      speedNormalized: 0,
      velocityX: 0,
      clapping: true,
      clapProgress: 0,
    });
    const contactPose = computePose({
      movementDirection: 1,
      facing: 1,
      speedNormalized: 0,
      velocityX: 0,
      clapping: true,
      clapProgress: 0.1,
    });

    const separatedGap = separatedPose.points.rightPalm.x - separatedPose.points.leftPalm.x;
    const contactGap = contactPose.points.rightPalm.x - contactPose.points.leftPalm.x;
    const separatedMidpoint = (separatedPose.points.leftPalm.x + separatedPose.points.rightPalm.x) / 2;
    const contactMidpoint = (contactPose.points.leftPalm.x + contactPose.points.rightPalm.x) / 2;

    assert.ok(separatedGap > 0, `separated clap phase must keep palms apart, received ${separatedGap}`);
    assert.ok(contactGap >= 0, `contact palms must not cross, received ${contactGap}`);
    assert.ok(
      contactGap <= separatedGap * 0.45,
      `contact palms must close most of the separated gap; separated=${separatedGap}, contact=${contactGap}`,
    );
    assertApprox(
      contactMidpoint,
      separatedMidpoint,
      Math.max(2, separatedGap * 0.1),
      'clapping must stay centered on the same local body axis',
    );
    assertApprox(
      contactMidpoint,
      contactPose.sourceCenterX,
      2,
      'clap contact must meet at the 896px artwork center near x=448',
    );
    assert.ok(
      contactPose.points.leftPalm.x <= contactPose.sourceCenterX
        && contactPose.points.rightPalm.x >= contactPose.sourceCenterX,
      'attached palms must approach the chest center from opposite sides without crossing',
    );
    assert.ok(
      contactPose.cssVariables.clapAccentOpacity > separatedPose.cssVariables.clapAccentOpacity,
      'clap accent should strengthen only near contact',
    );
  });

  it('five-layer: rigid limbs preserve gait contact, passing poses, and counter-swing', () => {
    const neutral = computePose({
      moving: true,
      movementDirection: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0,
    });
    const firstPose = computePose({
      moving: true,
      movementDirection: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.25,
    });
    const passingPose = computePose({
      moving: true,
      movementDirection: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.5,
    });
    const secondPose = computePose({
      moving: true,
      movementDirection: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.75,
    });
    const firstHalf = firstPose.cssVariables;
    const secondHalf = secondPose.cssVariables;

    assert.ok(firstHalf.leftLegRotateDeg > firstHalf.rightLegRotateDeg);
    assert.ok(secondHalf.rightLegRotateDeg < secondHalf.leftLegRotateDeg);
    assert.ok(firstHalf.leftArmRotateDeg * firstHalf.leftLegRotateDeg < 0);
    assert.ok(secondHalf.leftArmRotateDeg * secondHalf.rightLegRotateDeg < 0);
    assert.ok(firstHalf.leftArmRotateDeg * firstHalf.rightArmRotateDeg < 0);
    assert.ok(secondHalf.leftArmRotateDeg * secondHalf.rightArmRotateDeg < 0);
    assertApprox(
      firstPose.worldPoints.rightSole.y,
      firstPose.groundPlaneY,
      1e-6,
      'first contact phase must plant the right foot on the common world ground',
    );
    assertApprox(
      secondPose.worldPoints.leftSole.y,
      secondPose.groundPlaneY,
      1e-6,
      'opposite contact phase must plant the left foot on the common world ground',
    );
    assert.ok(
      firstPose.worldPoints.leftSole.y < firstPose.groundPlaneY - 10,
      'first lift phase must visibly clear the planted right shoe',
    );
    assert.ok(
      secondPose.worldPoints.rightSole.y < secondPose.groundPlaneY - 10,
      'opposite lift phase must visibly clear the planted left shoe',
    );
    for (const name of FIVE_LAYER_ROTATION_PROPERTIES) {
      assertApprox(
        neutral.cssVariables[name],
        0,
        1e-6,
        `gaitPhase=0 contact pose must start neutral at ${name}`,
      );
    }
    const passingRotationMagnitude = Math.max(
      ...FIVE_LAYER_ROTATION_PROPERTIES.map((name) => (
        Math.abs(passingPose.cssVariables[name])
      )),
    );
    assert.ok(
      passingRotationMagnitude >= 1.5,
      `gaitPhase=0.5 must retain a distinct passing pose; maximum rotation=${passingRotationMagnitude}`,
    );
    assert.ok(
      passingPose.cssVariables.torsoHeadRotateDeg
        * passingPose.cssVariables.leftArmRotateDeg < 0,
      'the passing pose must counter-swing the left arm against the torso',
    );
    assert.ok(
      passingPose.cssVariables.torsoHeadRotateDeg
        * passingPose.cssVariables.leftLegRotateDeg < 0,
      'the passing pose must counter-rotate the left leg to preserve grounded support',
    );
    assert.ok(
      passingPose.worldPoints.leftSole.y <= passingPose.groundPlaneY + 1e-6
        && passingPose.worldPoints.rightSole.y <= passingPose.groundPlaneY + 1e-6,
      'passing-pose soles must not penetrate the common ground',
    );
    assert.ok(
      Math.min(
        Math.abs(passingPose.worldPoints.leftSole.y - passingPose.groundPlaneY),
        Math.abs(passingPose.worldPoints.rightSole.y - passingPose.groundPlaneY),
      ) <= 1e-6,
      'the passing pose must keep at least one sole planted on the common ground',
    );
    for (const pose of [firstPose, passingPose, secondPose]) {
      for (const name of FIVE_LAYER_ROTATION_PROPERTIES) {
        const value = pose.cssVariables[name];
        assert.ok(
          Math.abs(value) <= 16,
          `${name} must remain restrained during walking; received ${value}`,
        );
      }
    }

    for (let sample = 0; sample <= 256; sample += 1) {
      const pose = computePose({
        moving: true,
        movementDirection: 1,
        velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
        speedNormalized: 1,
        gaitPhase: sample / 256,
      });
      const leftGroundError = Math.abs(
        pose.worldPoints.leftSole.y - pose.groundPlaneY,
      );
      const rightGroundError = Math.abs(
        pose.worldPoints.rightSole.y - pose.groundPlaneY,
      );

      assert.ok(
        pose.worldPoints.leftSole.y <= pose.groundPlaneY + 1e-6,
        `sample ${sample}: left sole must not pass below the common ground`,
      );
      assert.ok(
        pose.worldPoints.rightSole.y <= pose.groundPlaneY + 1e-6,
        `sample ${sample}: right sole must not pass below the common ground`,
      );
      assert.ok(
        Math.min(leftGroundError, rightGroundError) <= 1e-6,
        `sample ${sample}: at least one sole must remain planted on the common ground`,
      );
    }
  });

  it('five-layer: moving gaitPhase=0.5 remains visibly distinct from idle at 1080p', () => {
    const sourceToScreenScale = (1080 * 0.67) / AVATAR_RIG.canvas.height;
    const neutralPose = computePose({
      moving: false,
      movementDirection: 1,
      facing: 1,
      velocityX: 0,
      speedNormalized: 0,
      gaitPhase: 0.5,
    });
    const passingPose = computePose({
      moving: true,
      movementDirection: 1,
      facing: 1,
      velocityX: MOVEMENT_SPEED_PX_PER_SECOND,
      speedNormalized: 1,
      gaitPhase: 0.5,
    });
    const landmarkDisplacements = Object.fromEntries(
      ['leftPalm', 'rightPalm', 'leftSole', 'rightSole'].map((name) => {
        const neutral = neutralPose.points[name];
        const passing = passingPose.points[name];
        return [
          name,
          Math.hypot(passing.x - neutral.x, passing.y - neutral.y)
            * sourceToScreenScale,
        ];
      }),
    );
    const maximumLandmarkDisplacementPx = Math.max(
      ...Object.values(landmarkDisplacements),
    );
    const bodyTranslationPx = Math.abs(passingPose.cssVariables.bodyBobPx)
      * sourceToScreenScale;
    const rigidRotations = Object.fromEntries(
      FIVE_LAYER_ROTATION_PROPERTIES.map((name) => [
        name,
        passingPose.cssVariables[name],
      ]),
    );
    const maximumRigidRotationDeg = Math.max(
      ...Object.values(rigidRotations).map(Math.abs),
    );
    const failures = [];

    if (
      maximumLandmarkDisplacementPx < 4
      && bodyTranslationPx < 3
    ) {
      failures.push({
        bodyTranslationPx: Number(bodyTranslationPx.toFixed(3)),
        maximumLandmarkDisplacementPx: Number(
          maximumLandmarkDisplacementPx.toFixed(3),
        ),
        minimumBodyTranslationPx: 3,
        minimumLandmarkDisplacementPx: 4,
        reason: 'moving passing pose is screen-space indistinguishable from idle',
      });
    }
    if (maximumRigidRotationDeg < 1.5 && bodyTranslationPx < 3) {
      failures.push({
        bodyTranslationPx: Number(bodyTranslationPx.toFixed(3)),
        maximumRigidRotationDeg: Number(maximumRigidRotationDeg.toFixed(3)),
        minimumBodyTranslationPx: 3,
        minimumRigidRotationDeg: 1.5,
        reason: 'moving passing pose publishes no meaningful rigid/body motion channel',
      });
    }

    for (const [name, sole] of Object.entries({
      leftSole: passingPose.worldPoints.leftSole,
      rightSole: passingPose.worldPoints.rightSole,
    })) {
      assert.ok(
        sole.y <= passingPose.groundPlaneY + 1e-6,
        `${name} must not pass below the common ground at gaitPhase=0.5`,
      );
    }
    assert.ok(
      Math.min(
        Math.abs(passingPose.worldPoints.leftSole.y - passingPose.groundPlaneY),
        Math.abs(passingPose.worldPoints.rightSole.y - passingPose.groundPlaneY),
      ) <= 1e-6,
      'at least one passing-pose sole must remain planted on the common ground',
    );

    console.log(`PASSING_GAIT_VISUAL_METRICS=${JSON.stringify({
      bodyTranslationPx: Number(bodyTranslationPx.toFixed(3)),
      gaitPhase: 0.5,
      landmarkDisplacementsPx: Object.fromEntries(
        Object.entries(landmarkDisplacements).map(([name, value]) => [
          name,
          Number(value.toFixed(3)),
        ]),
      ),
      maximumLandmarkDisplacementPx: Number(
        maximumLandmarkDisplacementPx.toFixed(3),
      ),
      maximumRigidRotationDeg: Number(maximumRigidRotationDeg.toFixed(3)),
      rigidRotations,
      sourceToScreenScale: Number(sourceToScreenScale.toFixed(6)),
      thresholds: {
        minimumBodyTranslationPx: 3,
        minimumLandmarkDisplacementPx: 4,
        minimumRigidRotationDeg: 1.5,
      },
    })}`);
    assert.deepEqual(
      failures,
      [],
      'gaitPhase=0.5 at full nonzero speed must not render as the neutral idle silhouette; '
        + `failures=${JSON.stringify(failures)}`,
    );
  });

  it('retains opt-in local background rendering without a default request', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    createRenderer({
      document,
      mount,
      backgroundSrc: './operator-background.jpg',
    });

    const [stage] = mount.children;
    assert.equal(
      stage.style['--operator-background'],
      'url("http://localhost/operator-background.jpg")',
    );
  });

  it('rejects non-local or CSS-active background values before interpolation', () => {
    for (const backgroundSrc of [
      'https://attacker.example/pixel.jpg',
      '//attacker.example/pixel.jpg',
      'data:image/png;base64,AAAA',
      '../background.jpg',
      '%2e%2e/background.jpg',
      'placeholder"),url("https://attacker.example/pixel.jpg',
      'background(1).jpg',
      'background,second.jpg',
      'background.jpg\nurl(https://attacker.example/pixel.jpg)',
    ]) {
      const mount = new FakeElement('main');
      const document = new FakeDocument(mount);
      const renderer = createRenderer({ document, mount, backgroundSrc });
      const [stage] = mount.children;

      assert.equal(
        stage.style['--operator-background'],
        undefined,
        `${backgroundSrc} must not enter a CSS url() value`,
      );
      renderer.destroy();
    }
  });

  it('uses optional background.jpg through the production app only when the local probe succeeds', async () => {
    async function createBackgroundScenario(ok) {
      const mount = new FakeElement('main');
      const document = new FakeDocument(mount);
      const window = new FakeEventTarget();
      const frames = new Map();
      const requests = [];
      let nextFrameId = 1;

      document.defaultView = {
        AbortController,
        async fetch(url, options) {
          requests.push({ url, options });
          return { ok };
        },
      };

      const app = createAvatarApp({
        document,
        window,
        performance: { now: () => 0 },
        requestAnimationFrame(callback) {
          const id = nextFrameId;
          nextFrameId += 1;
          frames.set(id, callback);
          return id;
        },
        cancelAnimationFrame(id) {
          frames.delete(id);
        },
      });
      await Promise.resolve();
      await Promise.resolve();

      return {
        app,
        requests,
        stage: mount.children[0],
      };
    }

    const missing = await createBackgroundScenario(false);
    assert.deepEqual(
      missing.requests.map(({ url, options }) => ({
        cache: options.cache,
        method: options.method,
        url,
      })),
      [{ cache: 'no-store', method: 'HEAD', url: 'http://localhost/background.jpg' }],
    );
    assert.equal(missing.stage.style['--operator-background'], undefined);
    missing.app.stop();

    const available = await createBackgroundScenario(true);
    assert.equal(
      available.stage.style['--operator-background'],
      'url("http://localhost/background.jpg")',
    );
    available.app.stop();
  });

  it('five-layer: clap rendering uses rigid arm rotations while bubble targeting still tracks x', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [bubbleTarget, track] = stage.children;

    renderer.render(createPoseViewModel({
      x: 80,
      bubbleVisible: true,
      clapping: true,
      clapProgress: 0,
    }));
    const leftTailPercent = Number.parseFloat(
      bubbleTarget.style['--bubble-tail-left-pct'],
    );
    const leftArmRotation = track.style['--left-arm-rotate'];

    renderer.render(createPoseViewModel({
      x: 680,
      bubbleVisible: true,
      clapping: true,
      clapProgress: 0.1,
    }));
    const rightTailPercent = Number.parseFloat(
      bubbleTarget.style['--bubble-tail-left-pct'],
    );
    const rightArmRotation = track.style['--right-arm-rotate'];

    assert.ok(Number.isFinite(leftTailPercent), 'bubble tail must receive a numeric CSS percent');
    assert.ok(Number.isFinite(rightTailPercent), 'bubble tail must receive a numeric CSS percent');
    assert.ok(rightTailPercent > leftTailPercent, 'bubble tail should shift toward the avatar as x increases');
    assert.match(leftArmRotation, /^-?\d+(?:\.\d+)?deg$/);
    assert.match(rightArmRotation, /^-?\d+(?:\.\d+)?deg$/);
    assert.equal(track.style['--left-hand-transform'], undefined);
    assert.equal(track.style['--right-hand-transform'], undefined);
  });

  it('five-layer: renders exactly the registered rigid PNGs inside one facing wrapper', () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    createRenderer({ document, mount });

    const [stage] = mount.children;
    const [, track] = stage.children;
    const layerSources = rasterLayerSources(track);

    assert.deepEqual([...layerSources].sort(), FIVE_LAYER_RASTER_URLS);
    assert.match(track.innerHTML, /class=["'][^"']*avatar-facing[^"']*["']/i);
    assert.ok(
      layerSources.every((source) => source.startsWith('./assets/') || source.startsWith('/assets/')),
      'every raster layer must use a local asset URL',
    );
    assert.doesNotMatch(track.innerHTML, /<(?:svg|path|ellipse|circle)\b/i);
  });

  it('removes the legacy fixed CSS walk gait from the distance-driven renderer', async () => {
    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    assert.doesNotMatch(styles, /animation:\s*walk-bob\s+0\.44s/);
    assert.doesNotMatch(styles, /animation:\s*leg-stride\s+0\.44s/);
    assert.doesNotMatch(styles, /animation:\s*arm-swing\s+0\.44s/);
    assert.doesNotMatch(styles, /@keyframes\s+walk-bob\b/);
    assert.doesNotMatch(styles, /@keyframes\s+leg-stride\b/);
    assert.doesNotMatch(styles, /@keyframes\s+arm-swing\b/);
  });

  it('five-layer: rejects alternate CSS rotation, animation, and transition mechanisms', () => {
    const track = new FakeElement('div');
    track.setAttribute('class', 'avatar-track');
    const rogue = new FakeElement('div');
    rogue.setAttribute('class', 'avatar-joint rogue');
    track.appendChild(rogue);
    const rules = parseAllCssRules(`
      .rogue {
        rotate: 4deg;
        transition: 120ms linear all;
        animation: wobble 1s infinite;
      }
      @media (min-width: 1px) {
        .avatar-track[data-action="walk"] > .rogue {
          transform: rotateZ(6deg);
        }
      }
      @keyframes wobble {
        from { transform: rotate3d(0, 0, 1, 0deg); }
        to { transform: rotate3d(0, 0, 1, 8deg); }
      }
    `);
    const mechanisms = new Set(
      avatarMotionViolations({ rules, track }).map(({ mechanism }) => mechanism),
    );

    assert.deepEqual(
      [...mechanisms].sort(),
      ['animation', 'rotate property', 'rotation transform', 'transition'].sort(),
      'the CSS motion audit must catch individual rotate, alternate rotate functions, '
        + 'animation, and transition: all across state/media rules',
    );
  });

  it('five-layer: CSS exposes only the five rigid joint rotations and frozen pivots', async () => {
    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [, track] = mount.children[0].children;
    const rules = parseCssRules(styles);
    const allRules = parseAllCssRules(styles);
    const rootValues = new Map();
    const customPropertyNames = new Set();
    for (const rule of rules) {
      for (const [property, declaration] of rule.declarations) {
        if (!property.startsWith('--')) {
          continue;
        }
        customPropertyNames.add(property);
        if (rule.selector === ':root') {
          rootValues.set(property, declaration.value);
        }
      }
    }
    for (const element of descendantsAndSelf(track)) {
      for (const property of inlineCssDeclarations(element).keys()) {
        if (property.startsWith('--')) {
          customPropertyNames.add(property);
        }
      }
    }

    function splitCssValues(value) {
      const values = [];
      let current = '';
      let depth = 0;
      for (const character of value.trim()) {
        if (character === '(') {
          depth += 1;
        } else if (character === ')') {
          depth -= 1;
        }
        if (/\s/.test(character) && depth === 0) {
          if (current !== '') {
            values.push(current);
            current = '';
          }
        } else {
          current += character;
        }
      }
      if (current !== '') {
        values.push(current);
      }
      return values;
    }
    function resolveCssLength(value, axisSize, customProperties, seen = new Set()) {
      let expression = value.trim();
      expression = expression.replace(/var\(\s*(--[\w-]+)\s*\)/g, (match, property) => {
        assert.equal(seen.has(property), false, `CSS variable cycle at ${property}`);
        const replacement = customProperties.get(property);
        assert.ok(replacement, `missing CSS variable ${property}`);
        return resolveCssLength(
          replacement,
          axisSize,
          customProperties,
          new Set([...seen, property]),
        );
      });
      if (expression === 'center') {
        return axisSize / 2;
      }
      if (expression === 'left' || expression === 'top') {
        return 0;
      }
      if (expression === 'right' || expression === 'bottom') {
        return axisSize;
      }
      expression = expression.replace(/^calc\((.*)\)$/s, '$1');
      expression = expression.replace(
        /(-?\d+(?:\.\d+)?)%/g,
        (match, number) => String((Number(number) / 100) * axisSize),
      );
      expression = expression.replace(
        /(-?\d+(?:\.\d+)?)px\b/g,
        (match, number) => number,
      );
      assert.match(
        expression,
        /^[\d+\-*/().\s]+$/,
        `unsupported transform-origin expression ${value}`,
      );
      return Function(`"use strict"; return (${expression});`)();
    }

    const jointByRole = new Map(FIVE_LAYER_CONTRACT.map((layer) => {
      const renderedRole = track.querySelector(`[data-avatar-role="${layer.role}"]`);
      const renderedJoint = layer.role === 'torsoHead'
        ? renderedRole?.parentElement
        : renderedRole;
      assert.ok(renderedJoint, `${layer.role} must have a rendered joint`);
      return [layer.role, renderedJoint];
    }));
    const roleByJoint = new Map(
      [...jointByRole].map(([role, joint]) => [joint, role]),
    );
    const rotationFunctionPattern = /\brotate(?:[xyz]|3d)?\s*\(/i;
    const rotatingElements = descendantsAndSelf(track).flatMap((element) => {
      const transform = cascadedCssValue(element, rules, 'transform');
      const rotate = cascadedCssValue(element, rules, 'rotate');
      return (
        rotationFunctionPattern.test(transform ?? '')
        || (rotate !== undefined && rotate !== 'none')
      )
        ? [{ element, rotate, transform }]
        : [];
    });
    assert.deepEqual(
      rotatingElements.map(({ element }) => (
        roleByJoint.get(element)
        ?? `${element.tagName.toLowerCase()}.${element.className.replace(/\s+/g, '.')}`
      )).sort(),
      FIVE_LAYER_CONTRACT.map(({ role }) => role).sort(),
      'the effective DOM/CSS cascade must rotate exactly the five registered role joints',
    );

    const motionViolations = avatarMotionViolations({
      jointByRole,
      rules: allRules,
      track,
    });
    assert.deepEqual(
      motionViolations,
      [],
      'all responsive and state-specific CSS rules must keep avatar rotation exclusive to the '
        + 'five rigid role variables, with no rotate property, alternate rotate functions, '
        + 'CSS animation, or transform/all transition on the avatar subtree',
    );

    const trackCustomProperties = computedCssCustomProperties(
      track,
      rules,
      customPropertyNames,
      rootValues,
    );
    assert.deepEqual(
      FIVE_LAYER_CONTRACT.map(({ cssProperty }) => [
        cssProperty,
        trackCustomProperties.get(cssProperty),
      ]),
      FIVE_LAYER_CONTRACT.map(({ cssProperty }) => [cssProperty, '0deg']),
      '.avatar-track must initialize all five inherited rigid rotation variables to zero',
    );

    for (const layer of FIVE_LAYER_CONTRACT) {
      const renderedJoint = jointByRole.get(layer.role);
      const transform = cascadedCssValue(renderedJoint, rules, 'transform');
      assert.match(
        transform ?? '',
        new RegExp(
          `^\\s*rotate\\(\\s*var\\(\\s*${layer.cssProperty.replaceAll('-', '\\-')}\\s*\\)\\s*\\)\\s*$`,
        ),
        `${layer.role} must consume only ${layer.cssProperty} as its rigid transform`,
      );
      const customProperties = computedCssCustomProperties(
        renderedJoint,
        rules,
        customPropertyNames,
        rootValues,
      );
      const origin = cascadedCssValue(renderedJoint, rules, 'transform-origin');
      assert.ok(origin, `${layer.role} must have an effective transform-origin`);
      const originValues = splitCssValues(origin);
      if (originValues.length === 1) {
        originValues.push('center');
      }
      assert.equal(originValues.length, 2, `${layer.role} transform-origin must have x and y values`);
      assertApprox(
        resolveCssLength(
          originValues[0],
          AVATAR_RIG.canvas.width,
          customProperties,
        ),
        layer.pivot.x,
        0.01,
      );
      assertApprox(
        resolveCssLength(
          originValues[1],
          AVATAR_RIG.canvas.height,
          customProperties,
        ),
        layer.pivot.y,
        0.01,
      );
      assert.doesNotMatch(
        cascadedCssValue(renderedJoint, rules, 'transition') ?? '',
        /(?:^|,)\s*transform\b/i,
        `${layer.role} must not transition RAF-driven transforms`,
      );
    }

    for (const className of [
      '.floor-shadow',
      '.avatar-facing',
      '.avatar-body',
      '.clap-accent',
    ]) {
      const escapedClassName = className.replace('.', '\\.');
      assert.doesNotMatch(
        styles,
        new RegExp(`${escapedClassName}\\s*\\{[^}]*transition\\s*:`, 'm'),
        `${className} must not animate RAF-driven transforms through CSS transitions`,
      );
    }

    for (const obsoleteProperty of [
      '--head-tilt-deg',
      '--left-upper-arm-rotate',
      '--right-upper-arm-rotate',
      '--left-forearm-rotate',
      '--right-forearm-rotate',
      '--left-hand-rotate',
      '--right-hand-rotate',
      '--left-upper-leg-rotate',
      '--right-upper-leg-rotate',
      '--left-lower-leg-rotate',
      '--right-lower-leg-rotate',
      '--left-shoe-rotate',
      '--right-shoe-rotate',
    ]) {
      assert.doesNotMatch(
        styles,
        new RegExp(obsoleteProperty.replaceAll('-', '\\-')),
        `${obsoleteProperty} must be removed with the 14-layer topology`,
      );
    }

    for (const obsoleteClass of [
      'head-group',
      'upper-arm-left',
      'upper-arm-right',
      'forearm-left',
      'forearm-right',
      'hand-left',
      'hand-right',
      'upper-leg-left',
      'upper-leg-right',
      'lower-leg-left',
      'lower-leg-right',
      'shoe-left',
      'shoe-right',
    ]) {
      assert.doesNotMatch(
        styles,
        new RegExp(`\\.${obsoleteClass}\\b`),
        `.${obsoleteClass} must be removed with the 14-layer topology`,
      );
    }

    const reducedRules = parseCssRules(extractCssAtRuleBody(
      styles,
      '@media (prefers-reduced-motion: reduce)',
    ));
    for (const { role } of FIVE_LAYER_CONTRACT) {
      const renderedJoint = jointByRole.get(role);
      const reducedTransition = cascadedCssValue(renderedJoint, reducedRules, 'transition');
      const reducedAnimation = cascadedCssValue(renderedJoint, reducedRules, 'animation');
      if (reducedTransition !== undefined) {
        assert.equal(reducedTransition, 'none', `reduced motion must disable ${role} transitions`);
      }
      if (reducedAnimation !== undefined) {
        assert.equal(reducedAnimation, 'none', `reduced motion must disable ${role} animation`);
      }
    }
    renderer.destroy();
  });

  it('keeps silhouette effects off the avatar body so transparent garment gaps stay background-clean', async () => {
    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    const avatarBodyRule = /\.avatar-body\s*\{([^}]*)\}/s.exec(styles);

    assert.ok(avatarBodyRule, 'styles.css must define .avatar-body');
    assert.doesNotMatch(
      avatarBodyRule[1],
      /\bfilter\s*:|drop-shadow\s*\(/i,
      '.avatar-body must not apply a silhouette filter or drop-shadow into transparent gaps',
    );
    assert.doesNotMatch(
      styles,
      /\.floor-shadow\b/i,
      'the removed floor shadow must not retain a DOM-facing CSS selector',
    );
  });

  it('renders no prohibited horizon, perspective floor, reflection plane, or avatar floor shadow', async () => {
    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    const rules = parseAllCssRules(styles);
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const stage = mount.children[0];
    const track = stage.children.find((element) => elementHasClass(element, 'avatar-track'));
    const stageRule = rules.find(({ selector }) => selector.trim() === '.stage');
    const stageBackgroundImage = stageRule?.declarations.get('background-image')?.value ?? '';
    const floorShadowElements = track?.querySelectorAll('.floor-shadow').length ?? 0;
    const prohibitedPseudoTreatments = rules.flatMap((rule) => {
      const selectors = rule.selector.split(',').map((selector) => selector.trim());
      if (!selectors.includes('.stage::before')) {
        return [];
      }

      const declarationValue = (name) => rule.declarations.get(name)?.value;
      const disabled = declarationValue('content') === 'none'
        || declarationValue('display') === 'none'
        || declarationValue('visibility') === 'hidden'
        || declarationValue('opacity') === '0';
      if (disabled) {
        return [];
      }

      const activeProperties = [
        'background',
        'background-image',
        'border-top',
        'box-shadow',
        'filter',
        'transform',
      ].flatMap((name) => {
        const value = declarationValue(name);
        return value !== undefined && value !== 'none'
          ? [{ name, value }]
          : [];
      });
      return activeProperties.length === 0
        ? []
        : [{ activeProperties, selector: '.stage::before' }];
    });
    const activeFloorShadowRules = rules.flatMap((rule) => {
      const selectors = rule.selector.split(',').map((selector) => selector.trim());
      if (!selectors.includes('.floor-shadow')) {
        return [];
      }
      const disabled = rule.declarations.get('display')?.value === 'none'
        || rule.declarations.get('visibility')?.value === 'hidden'
        || rule.declarations.get('opacity')?.value === '0';
      if (disabled) {
        return [];
      }
      const visibleProperties = [
        'background',
        'background-image',
        'box-shadow',
        'filter',
        'opacity',
      ].flatMap((name) => {
        const value = rule.declarations.get(name)?.value;
        return value !== undefined && value !== 'none' && value !== '0'
          ? [{ name, value }]
          : [];
      });
      return visibleProperties.length === 0
        ? []
        : [{ selector: '.floor-shadow', visibleProperties }];
    });
    const failures = [];

    if (/\b(?:linear|conic)-gradient\s*\(/i.test(stageBackgroundImage)) {
      failures.push({
        property: 'background-image',
        reason: 'stage background contains a directional floor/horizon layer',
        selector: '.stage',
        value: stageBackgroundImage,
      });
    }
    if (prohibitedPseudoTreatments.length > 0) {
      failures.push(...prohibitedPseudoTreatments);
    }
    if (floorShadowElements > 0) {
      failures.push({
        count: floorShadowElements,
        reason: 'production avatar DOM renders a floor-shadow element',
        selector: '.floor-shadow',
      });
    }
    if (floorShadowElements > 0 && activeFloorShadowRules.length > 0) {
      failures.push(...activeFloorShadowRules);
    }

    console.log(`PROHIBITED_FLOOR_TREATMENT_METRICS=${JSON.stringify({
      activeFloorShadowRules,
      floorShadowElements,
      prohibitedPseudoTreatments,
      stageBackgroundImage,
    })}`);
    assert.deepEqual(
      failures,
      [],
      'production CSS/DOM must not render the prohibited horizon line, perspective/reflection '
        + `plane, or oval avatar floor shadow; failures=${JSON.stringify(failures)}`,
    );
    renderer.destroy();
  });

});

describe('bubble-local tail targeting regression', () => {
  it('targets safely on an untransformed bubble-local axis while the visual scales and rotates', async () => {
    const mount = new FakeElement('main');
    const document = new FakeDocument(mount);
    const renderer = createRenderer({ document, mount });
    const [stage] = mount.children;
    const [bubbleTarget, track] = stage.children;
    const [bubbleVisual] = bubbleTarget.children;
    const stageRect = { left: 100, top: 20, width: 1200, height: 720 };
    const bubbleTargetRect = { left: 400, top: 80, width: 600, height: 180 };
    const trackRect = { left: 100, top: 140, width: 180, height: 520 };
    const safeCornerInset = 56;
    const tolerance = 0.001;

    assert.equal(bubbleTarget.className, 'speech-bubble-target');
    assert.equal(bubbleVisual.className, 'speech-bubble');

    stage.clientWidth = stageRect.width;
    stage.setBoundingClientRect(stageRect);
    bubbleTarget.setBoundingClientRect(bubbleTargetRect);
    track.setBoundingClientRect(trackRect);

    for (const motion of [
      { name: 'arrival', scale: 0.78, rotationDeg: -3 },
      { name: 'settled', scale: 1, rotationDeg: -1 },
    ]) {
      const rotationRadians = motion.rotationDeg * (Math.PI / 180);
      const transformedWidth = motion.scale * (
        (bubbleTargetRect.width * Math.abs(Math.cos(rotationRadians)))
        + (bubbleTargetRect.height * Math.abs(Math.sin(rotationRadians)))
      );
      const transformedHeight = motion.scale * (
        (bubbleTargetRect.width * Math.abs(Math.sin(rotationRadians)))
        + (bubbleTargetRect.height * Math.abs(Math.cos(rotationRadians)))
      );
      bubbleVisual.setBoundingClientRect({
        left: bubbleTargetRect.left + ((bubbleTargetRect.width - transformedWidth) / 2),
        top: bubbleTargetRect.top + ((bubbleTargetRect.height - transformedHeight) / 2),
        width: transformedWidth,
        height: transformedHeight,
      });

      for (const scenario of [
        { name: 'left clamped', x: 0 },
        { name: 'center', x: 510 },
        { name: 'right clamped', x: 1020 },
      ]) {
        renderer.render(createPoseViewModel({
          x: scenario.x,
          bubbleVisible: true,
        }));

        const caseName = `${motion.name} ${scenario.name}`;
        assert.equal(
          bubbleTarget.hidden,
          false,
          `${caseName}: bubble must be visible before its measured rectangle is consumed`,
        );

        const measuredStageRect = stage.getBoundingClientRect();
        const measuredTrackRect = track.getBoundingClientRect();
        const measuredBubbleTargetRect = bubbleTarget.getBoundingClientRect();
        const avatarCenterStageX = (
          measuredTrackRect.left
          - measuredStageRect.left
          + (measuredTrackRect.width / 2)
        );
        const bubbleLeftStageX = measuredBubbleTargetRect.left - measuredStageRect.left;
        const avatarCenterBubbleLocalX = avatarCenterStageX - bubbleLeftStageX;
        const expectedLocalTailX = Math.min(
          measuredBubbleTargetRect.width - safeCornerInset,
          Math.max(safeCornerInset, avatarCenterBubbleLocalX),
        );
        const actualLocalTailX = Number.parseFloat(
          bubbleTarget.style['--bubble-tail-left-px'],
        );
        const tailTipStageX = bubbleLeftStageX + actualLocalTailX;
        const expectedTailTipStageX = bubbleLeftStageX + expectedLocalTailX;
        const stageSpaceTailError = Math.abs(tailTipStageX - avatarCenterStageX);
        const unavoidableClampError = Math.abs(expectedTailTipStageX - avatarCenterStageX);

        assert.ok(
          Number.isFinite(actualLocalTailX),
          `${caseName}: bubble tail must receive a measured bubble-local pixel coordinate`,
        );
        assertApprox(
          actualLocalTailX,
          expectedLocalTailX,
          tolerance,
          `${caseName}: conversion must ignore the transformed visual bounding box`,
        );
        assert.ok(
          actualLocalTailX >= safeCornerInset
            && actualLocalTailX <= measuredBubbleTargetRect.width - safeCornerInset,
          `${caseName}: tail must stay clamped away from rounded bubble corners`,
        );
        assertApprox(
          tailTipStageX,
          expectedTailTipStageX,
          tolerance,
          `${caseName}: tail tip must land on the expected stage-space target`,
        );
        assertApprox(
          stageSpaceTailError,
          unavoidableClampError,
          tolerance,
          `${caseName}: tail error must be zero when covered and only the clamp distance otherwise`,
        );
      }
    }

    assert.equal(
      bubbleVisual.boundingClientRectReads,
      0,
      'targeting must never consume the transformed visual bounding box',
    );

    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    assert.match(
      styles,
      /\.speech-bubble-target::after\s*\{[^}]*left:\s*var\(--bubble-tail-left-px\)/s,
      'the untransformed targeting container pseudo-element must consume the local pixel coordinate',
    );
    assert.doesNotMatch(
      styles,
      /\.speech-bubble-target\s*\{[^}]*(?:animation|transform):/s,
      'the targeting container must not scale or rotate',
    );
    assert.match(
      styles,
      /\.speech-bubble\s*\{[^}]*transform:\s*rotate\(-1deg\)[^}]*animation:\s*bubble-arrive/s,
      'only the inner bubble visual should own settled rotation and arrival animation',
    );
    assert.match(
      styles,
      /\.speech-bubble-target::after\s*\{[^}]*animation:\s*bubble-tail-arrive 220ms cubic-bezier\(\.18,\s*\.9,\s*\.3,\s*1\.35\) both/s,
      'the wrapper-owned tail must fade in with the same duration and timing as the inner bubble',
    );

    const tailArrivalKeyframes = /@keyframes bubble-tail-arrive\s*\{\s*from\s*\{([^}]*)\}\s*to\s*\{([^}]*)\}\s*\}/s.exec(styles);
    assert.ok(tailArrivalKeyframes, 'the tail must define a dedicated arrival animation');
    assert.match(tailArrivalKeyframes[1], /opacity:\s*0/, 'the tail arrival must start transparent');
    assert.match(tailArrivalKeyframes[2], /opacity:\s*1/, 'the tail arrival must finish opaque');
    assert.doesNotMatch(
      tailArrivalKeyframes.slice(1).join('\n'),
      /(?:transform|scale|rotate)\s*:/,
      'the tail arrival must animate opacity only so targeting geometry stays stable',
    );
  });

  it('keeps the bubble tail above the source-pinned scalp at 720p, 1080p, and 4K', async () => {
    const styles = await readFile(new URL('../styles.css', import.meta.url), 'utf8');
    const targetRule = /\.speech-bubble-target\s*\{([^}]*)\}/s.exec(styles);
    const tailRule = /\.speech-bubble-target::after\s*\{([^}]*)\}/s.exec(styles);

    assert.ok(targetRule, 'styles.css must define .speech-bubble-target');
    assert.ok(tailRule, 'styles.css must define the speech-bubble tail');
    assert.match(
      targetRule[1],
      /\btop:\s*clamp\(0\.5rem,\s*1\.5vh,\s*1\.5rem\)/,
      'the bubble must use the deterministic high-clearance vertical inset',
    );
    assert.match(
      targetRule[1],
      /--bubble-tail-size:\s*2rem/,
      'the targeting container must define the shortened deterministic tail size',
    );
    assert.match(
      targetRule[1],
      /--bubble-tail-drop:\s*0\.9rem/,
      'the targeting container must define the shortened deterministic tail drop',
    );
    assert.match(
      tailRule[1],
      /\bwidth:\s*var\(--bubble-tail-size\)/,
      'the tail width must consume the deterministic size',
    );
    assert.match(
      tailRule[1],
      /\bheight:\s*var\(--bubble-tail-size\)/,
      'the tail height must consume the deterministic size',
    );
    assert.match(
      tailRule[1],
      /\bbottom:\s*calc\(-1\s*\*\s*var\(--bubble-tail-drop\)\)/,
      'the tail must consume the deterministic shortened drop',
    );

    const rootFontSize = 16;
    const sourceScalpY = 62;
    const sourceHeight = AVATAR_RIG.canvas.height;
    const minimumClearancePx = 12;
    const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));

    for (const { name, width, height } of [
      { name: '720p', width: 1280, height: 720 },
      { name: '1080p', width: 1920, height: 1080 },
      { name: '4K', width: 3840, height: 2160 },
    ]) {
      const bubbleTopPx = clamp(height * 0.015, rootFontSize * 0.5, rootFontSize * 1.5);
      const fontSizePx = clamp(width * 0.051, rootFontSize * 2, rootFontSize * 5.5);
      const verticalPaddingPx = clamp(width * 0.025, rootFontSize, rootFontSize * 1.75);
      const borderWidthPx = clamp(width * 0.0045, 4, 8);
      const bubbleHeightPx = (fontSizePx * 2) + (verticalPaddingPx * 2) + (borderWidthPx * 2);
      const tailSizePx = rootFontSize * 2;
      const tailDropPx = rootFontSize * 0.9;
      const rotatedTailOverflowPx = tailSizePx * ((Math.SQRT2 - 1) / 2);
      const tailTipY = bubbleTopPx
        + bubbleHeightPx
        + tailDropPx
        + rotatedTailOverflowPx;
      const avatarTopPx = height - (height * 0.025) - (height * 0.67);
      const scalpY = avatarTopPx + ((sourceScalpY / sourceHeight) * height * 0.67);
      const clearancePx = scalpY - tailTipY;

      assert.ok(bubbleTopPx >= 0, `${name}: bubble must stay on-screen`);
      assert.ok(
        clearancePx >= minimumClearancePx,
        `${name}: bubble tail must clear the scalp by at least ${minimumClearancePx}px; `
          + `received ${clearancePx.toFixed(2)}px`,
      );
    }
  });
});
