import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { after, before, describe, it } from 'node:test';

import {
  createStaticServer,
  DEFAULT_HOST,
  DEFAULT_PORT,
  parsePort,
  startStaticServer,
} from '../server.mjs';

const projectRoot = fileURLToPath(new URL('..', import.meta.url));
const packageJsonPath = fileURLToPath(new URL('../package.json', import.meta.url));
const indexHtmlPath = fileURLToPath(new URL('../index.html', import.meta.url));
const readmePath = fileURLToPath(new URL('../README.md', import.meta.url));
const stylesPath = fileURLToPath(new URL('../styles.css', import.meta.url));
const publicSourceRoot = fileURLToPath(new URL('../src', import.meta.url));
const AVATAR_SOURCE_SHA256 = '04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF';
const FIVE_LAYER_PUBLIC_PATHS = Object.freeze([
  '/assets/avatar/idle.png',
  '/assets/avatar/leftLeg.png',
  '/assets/avatar/rightLeg.png',
  '/assets/avatar/torsoHead.png',
  '/assets/avatar/leftArm.png',
  '/assets/avatar/rightArm.png',
]);
const OBSOLETE_LAYER_PATHS = Object.freeze([
  '/assets/avatar/leftShoe.png',
  '/assets/avatar/rightShoe.png',
  '/assets/avatar/leftLowerLeg.png',
  '/assets/avatar/rightLowerLeg.png',
  '/assets/avatar/leftUpperLeg.png',
  '/assets/avatar/rightUpperLeg.png',
  '/assets/avatar/torso.png',
  '/assets/avatar/leftUpperArm.png',
  '/assets/avatar/rightUpperArm.png',
  '/assets/avatar/leftForearm.png',
  '/assets/avatar/rightForearm.png',
  '/assets/avatar/leftHand.png',
  '/assets/avatar/rightHand.png',
  '/assets/avatar/head.png',
]);
const LOCAL_ONLY_DEPENDENCY_FIELDS = [
  'dependencies',
  'devDependencies',
  'peerDependencies',
  'optionalDependencies',
  'bundleDependencies',
  'bundledDependencies',
];
let server;
let origin;

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex').toUpperCase();
}

function listFilesRecursively(rootPath) {
  const filePaths = [];

  for (const entry of readdirSync(rootPath, { withFileTypes: true })) {
    const entryPath = path.join(rootPath, entry.name);

    if (entry.isDirectory()) {
      filePaths.push(...listFilesRecursively(entryPath));
      continue;
    }

    filePaths.push(entryPath);
  }

  return filePaths.sort();
}

function stripHtmlComments(source) {
  return source.replace(/<!--[\s\S]*?-->/g, '');
}

function stripCssComments(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '');
}

function stripJavaScriptComments(source) {
  let result = '';
  let index = 0;
  let state = 'code';

  while (index < source.length) {
    const character = source[index];
    const nextCharacter = source[index + 1];

    if (state === 'line-comment') {
      if (character === '\n') {
        state = 'code';
        result += character;
      }
      index += 1;
      continue;
    }

    if (state === 'block-comment') {
      if (character === '*' && nextCharacter === '/') {
        state = 'code';
        index += 2;
        continue;
      }
      if (character === '\n') {
        result += '\n';
      }
      index += 1;
      continue;
    }

    if (state === 'single-quote' || state === 'double-quote' || state === 'template') {
      result += character;
      if (character === '\\') {
        result += nextCharacter ?? '';
        index += 2;
        continue;
      }
      if (
        (state === 'single-quote' && character === '\'')
        || (state === 'double-quote' && character === '"')
        || (state === 'template' && character === '`')
      ) {
        state = 'code';
      }
      index += 1;
      continue;
    }

    if (character === '/' && nextCharacter === '/') {
      state = 'line-comment';
      index += 2;
      continue;
    }

    if (character === '/' && nextCharacter === '*') {
      state = 'block-comment';
      index += 2;
      continue;
    }

    if (character === '\'') {
      state = 'single-quote';
      result += character;
      index += 1;
      continue;
    }

    if (character === '"') {
      state = 'double-quote';
      result += character;
      index += 1;
      continue;
    }

    if (character === '`') {
      state = 'template';
      result += character;
      index += 1;
      continue;
    }

    result += character;
    index += 1;
  }

  return result;
}

function assertLocalRuntimeReference(reference, message) {
  assert.doesNotMatch(reference, /^(?:https?:)?\/\//, message);
}

function request(pathname, { host, method = 'GET' } = {}) {
  return new Promise((resolve, reject) => {
    const target = new URL(pathname, origin);
    const headers = host === undefined ? undefined : { Host: host };
    const requestInstance = http.request({
      hostname: target.hostname,
      port: target.port,
      path: `${target.pathname}${target.search}`,
      method,
      headers,
    }, (response) => {
      const chunks = [];
      response.on('data', (chunk) => chunks.push(chunk));
      response.on('end', () => {
        resolve({
          statusCode: response.statusCode,
          headers: response.headers,
          body: Buffer.concat(chunks),
        });
      });
    });
    requestInstance.on('error', reject);
    requestInstance.end();
  });
}

function requestPort(port, pathname = '/', timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    let responseInstance;
    let settled = false;
    const requestInstance = http.request({
      hostname: DEFAULT_HOST,
      port,
      path: pathname,
      method: 'GET',
    }, (response) => {
      if (settled) {
        response.destroy();
        return;
      }

      responseInstance = response;
      const chunks = [];
      const onData = (chunk) => chunks.push(chunk);
      const onEnd = () => {
        settle(resolve, {
          statusCode: response.statusCode,
          headers: response.headers,
          body: Buffer.concat(chunks),
        });
      };
      const onError = (error) => settle(reject, error);
      const onAborted = () => settle(
        reject,
        new Error(`Response aborted while requesting http://${DEFAULT_HOST}:${port}${pathname}`),
      );
      const onClose = () => {
        response.off('data', onData);
        response.off('end', onEnd);
        response.off('error', onError);
        response.off('aborted', onAborted);
      };

      response.on('data', onData);
      response.once('end', onEnd);
      response.once('error', onError);
      response.once('aborted', onAborted);
      response.once('close', onClose);
    });

    const timeout = setTimeout(() => {
      const error = new Error(
        `Timed out after ${timeoutMs}ms requesting http://${DEFAULT_HOST}:${port}${pathname}`,
      );
      settle(reject, error);
      if (responseInstance && !responseInstance.destroyed) {
        responseInstance.destroy(error);
      }
      if (!requestInstance.destroyed) {
        requestInstance.destroy(error);
      }
    }, timeoutMs);

    function settle(callback, value) {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeout);
      callback(value);
    }

    const onRequestError = (error) => settle(reject, error);
    requestInstance.once('error', onRequestError);
    requestInstance.once('close', () => {
      requestInstance.off('error', onRequestError);
    });
    requestInstance.end();
  });
}

async function getAvailablePort() {
  const portServer = createStaticServer({ root: projectRoot });
  portServer.listen(0, DEFAULT_HOST);
  await once(portServer, 'listening');
  const { port } = portServer.address();
  portServer.close();
  await once(portServer, 'close');
  return port;
}

function waitForStartup(child, expectedPort, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    let stdout = '';
    let stderr = '';
    const expectedMessage = `TCF Event Avatar available at http://${DEFAULT_HOST}:${expectedPort}`;

    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error(
        `Timed out waiting for CLI startup. stdout=${JSON.stringify(stdout)} stderr=${JSON.stringify(stderr)}`,
      ));
    }, timeoutMs);

    function cleanup() {
      clearTimeout(timeout);
      child.stdout.off('data', onStdout);
      child.stderr.off('data', onStderr);
      child.off('error', onError);
      child.off('exit', onExit);
    }

    function onStdout(chunk) {
      stdout += chunk;
      if (stdout.includes(expectedMessage)) {
        cleanup();
        resolve({ stdout, stderr });
      }
    }

    function onStderr(chunk) {
      stderr += chunk;
    }

    function onError(error) {
      cleanup();
      reject(error);
    }

    function onExit(code, signal) {
      cleanup();
      reject(new Error(
        `CLI exited before startup with code ${code} and signal ${signal}. `
        + `stdout=${JSON.stringify(stdout)} stderr=${JSON.stringify(stderr)}`,
      ));
    }

    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', onStdout);
    child.stderr.on('data', onStderr);
    child.on('error', onError);
    child.on('exit', onExit);
  });
}

async function stopChild(child, timeoutMs = 5000) {
  if (child.exitCode !== null || child.signalCode !== null) {
    return;
  }

  await new Promise((resolve, reject) => {
    const forceKillTimer = setTimeout(() => {
      if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL');
      }
    }, Math.floor(timeoutMs / 2));
    const failureTimer = setTimeout(() => {
      cleanup();
      reject(new Error(`CLI child ${child.pid} did not exit within ${timeoutMs}ms`));
    }, timeoutMs);

    function cleanup() {
      clearTimeout(forceKillTimer);
      clearTimeout(failureTimer);
      child.off('exit', onExit);
      child.off('error', onError);
    }

    function onExit() {
      cleanup();
      resolve();
    }

    function onError(error) {
      cleanup();
      reject(error);
    }

    child.once('exit', onExit);
    child.once('error', onError);
    if (child.exitCode !== null || child.signalCode !== null) {
      onExit();
      return;
    }
    child.kill();
  });
}

async function assertPortReusable(port) {
  const probe = createStaticServer({ root: projectRoot });
  probe.listen(port, DEFAULT_HOST);
  await once(probe, 'listening');
  probe.close();
  await once(probe, 'close');
}

async function verifyCliStartup({ port, probe = requestPort }) {
  const { PORT: ignoredPort, ...baseEnv } = process.env;
  const child = spawn(process.execPath, ['server.mjs'], {
    cwd: projectRoot,
    env: { ...baseEnv, PORT: String(port) },
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  try {
    const startup = await waitForStartup(child, port);
    assert.match(
      startup.stdout,
      new RegExp(`TCF Event Avatar available at http://127\\.0\\.0\\.1:${port}`),
    );
    assert.equal(startup.stderr, '');

    const response = await probe(port);
    assert.equal(response.statusCode, 200);
    assert.match(response.headers['content-type'], /^text\/html/);
    assert.match(response.body.toString('utf8'), /TCF Event Avatar/);
  } finally {
    await stopChild(child);
    assert.notEqual(child.exitCode === null && child.signalCode === null, true);
    await assertPortReusable(port);
  }
}

function requestWithLocalPort({ host, localPort }) {
  const testServer = createStaticServer({ root: projectRoot });
  const [handleRequest] = testServer.listeners('request');

  return new Promise((resolve, reject) => {
    const headers = new Map();
    const chunks = [];
    const response = {
      headersSent: false,
      setHeader(name, value) {
        headers.set(name.toLowerCase(), String(value));
      },
      writeHead(statusCode, responseHeaders = {}) {
        this.statusCode = statusCode;
        this.headersSent = true;
        for (const [name, value] of Object.entries(responseHeaders)) {
          headers.set(name.toLowerCase(), String(value));
        }
        return this;
      },
      end(chunk) {
        if (chunk !== undefined) {
          chunks.push(Buffer.from(chunk));
        }
        resolve({
          statusCode: this.statusCode,
          headers: Object.fromEntries(headers),
          body: Buffer.concat(chunks),
        });
      },
      destroy(error) {
        reject(error);
      },
    };

    void Promise.resolve(handleRequest({
      headers: { host },
      socket: { localPort },
      method: 'GET',
      url: '/not-here.txt',
    }, response)).catch(reject);
  });
}

before(async () => {
  server = createStaticServer({ root: projectRoot });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  origin = `http://127.0.0.1:${server.address().port}`;
});

after(async () => {
  if (server?.listening) {
    server.close();
    await once(server, 'close');
  }
});

describe('loopback static server', () => {
  it('uses the frozen default and validates custom PORT boundaries', () => {
    assert.equal(DEFAULT_HOST, '127.0.0.1');
    assert.equal(DEFAULT_PORT, 4173);
    assert.equal(parsePort(undefined), 4173);
    assert.equal(parsePort(''), 4173);
    assert.equal(parsePort('5000'), 5000);
    assert.equal(parsePort('1'), 1);
    assert.equal(parsePort('65535'), 65535);

    for (const value of ['0', '65536', '-1', '1.5', ' 4173', '4173 ', 'abc']) {
      assert.throws(
        () => parsePort(value),
        /PORT must be an integer from 1 through 65535/,
      );
    }
  });

  it('uses the production startup path and binds only to IPv4 loopback', async () => {
    const productionServer = startStaticServer({ port: 0, root: projectRoot });
    await once(productionServer, 'listening');

    try {
      const address = productionServer.address();
      assert.equal(address.address, '127.0.0.1');
      assert.equal(address.family, 'IPv4');
      assert.ok(address.port > 0);
    } finally {
      productionServer.close();
      await once(productionServer, 'close');
    }
  });

  it('starts the actual CLI entrypoint only on an available custom PORT', async () => {
    let customPort = await getAvailablePort();
    while (customPort === DEFAULT_PORT) {
      customPort = await getAvailablePort();
    }
    await verifyCliStartup({ port: customPort });
  });

  it('stops the actual CLI when its HTTP probe times out', { timeout: 10000 }, async () => {
    const stalledServer = http.createServer((requestInstance, response) => {
      requestInstance.resume();
      response.writeHead(200, { 'Content-Type': 'text/plain; charset=utf-8' });
      response.write('partial response');
    });
    stalledServer.listen(0, DEFAULT_HOST);
    await once(stalledServer, 'listening');

    const stalledPort = stalledServer.address().port;
    let cliPort = await getAvailablePort();
    while (cliPort === DEFAULT_PORT || cliPort === stalledPort) {
      cliPort = await getAvailablePort();
    }

    try {
      await assert.rejects(
        verifyCliStartup({
          port: cliPort,
          probe: () => requestPort(stalledPort, '/', 100),
        }),
        /Timed out after 100ms requesting/,
      );
    } finally {
      stalledServer.close();
      await once(stalledServer, 'close');
    }
  });

  it('serves the application root with HTML and no caching', async () => {
    const response = await request('/');

    assert.equal(response.statusCode, 200);
    assert.match(response.headers['content-type'], /^text\/html/);
    assert.equal(response.headers['cache-control'], 'no-store');
    assert.equal(response.headers['cross-origin-resource-policy'], 'same-origin');
    assert.equal(response.headers['x-content-type-options'], 'nosniff');
    assert.match(response.body.toString('utf8'), /TCF Event Avatar/);
  });

  it('supports HEAD without sending a response body', async () => {
    const getResponse = await request('/styles.css');
    const headResponse = await request('/styles.css', { method: 'HEAD' });

    assert.equal(headResponse.statusCode, 200);
    assert.match(headResponse.headers['content-type'], /^text\/css/);
    assert.equal(headResponse.headers['content-length'], String(getResponse.body.length));
    assert.equal(headResponse.body.length, 0);
  });

  it('serves JavaScript and CSS with correct MIME types', async () => {
    const scriptResponses = await Promise.all([
      request('/src/app.js'),
      request('/src/constants.js'),
      request('/src/render.js'),
      request('/src/state.js'),
    ]);
    const styleResponse = await request('/styles.css');

    for (const scriptResponse of scriptResponses) {
      assert.equal(scriptResponse.statusCode, 200);
      assert.match(scriptResponse.headers['content-type'], /^text\/javascript/);
    }
    assert.equal(styleResponse.statusCode, 200);
    assert.match(styleResponse.headers['content-type'], /^text\/css/);
  });

  it('keeps package/runtime assets fully local and dependency-free', () => {
    const manifest = JSON.parse(readFileSync(packageJsonPath, 'utf8'));

    for (const field of LOCAL_ONLY_DEPENDENCY_FIELDS) {
      if (manifest[field] === undefined) {
        continue;
      }

      if (Array.isArray(manifest[field])) {
        assert.deepEqual(
          manifest[field],
          [],
          `${field} must stay empty to preserve offline/local-only execution`,
        );
        continue;
      }

      assert.deepEqual(
        manifest[field],
        {},
        `${field} must stay empty to preserve offline/local-only execution`,
      );
    }

    const html = stripHtmlComments(readFileSync(indexHtmlPath, 'utf8'));
    const htmlRuntimeTargets = [
      ...html.matchAll(/<(?:link|script)\b[^>]*\b(?:href|src)=["']([^"']+)["']/g),
    ].map(([, target]) => target);
    assert.ok(htmlRuntimeTargets.length > 0, 'index.html must load local runtime assets');
    for (const target of htmlRuntimeTargets) {
      assertLocalRuntimeReference(target, `index.html asset ${target} must remain local`);
    }

    const styles = stripCssComments(readFileSync(stylesPath, 'utf8'));
    const stylesheetTargets = [
      ...styles.matchAll(/url\(\s*['"]?([^"'()]+)['"]?\s*\)/g),
      ...styles.matchAll(/@import\s+(?:url\(\s*)?['"]?([^"'()\s;]+)['"]?\s*\)?/g),
    ].map(([, target]) => target);
    for (const target of stylesheetTargets) {
      assertLocalRuntimeReference(target, `styles.css asset ${target} must remain local`);
    }

    for (const filePath of listFilesRecursively(publicSourceRoot)) {
      const source = stripJavaScriptComments(readFileSync(filePath, 'utf8'));
      assert.doesNotMatch(
        source,
        /(["'`])(?:https?:)?\/\/[^"'`\r\n]+\1/g,
        `${path.relative(projectRoot, filePath)} must not introduce remote runtime URLs`,
      );
    }
  });

  it('five-layer: keeps source portraits and private manifest unreachable', async () => {
    for (const pathname of [
      '/Avatar.jpg',
      '/Me.jpg',
      '/assets/avatar-face.jpg',
      '/assets/avatar/manifest.json',
    ]) {
      const response = await request(pathname);
      assert.equal(response.statusCode, 404, `${pathname} must not be publicly served`);
      assert.equal(response.headers['content-type'], 'text/plain; charset=utf-8');
      assert.equal(
        response.body.toString('utf8'),
        'Not Found\n',
        `${pathname} must return only the generic not-found response without private metadata`,
      );
    }
  });

  it('five-layer: serves exactly six avatar PNG URLs and rejects every obsolete layer URL', async () => {
    const requiredResponses = await Promise.all(
      FIVE_LAYER_PUBLIC_PATHS.map(async (pathname) => ({
        pathname,
        response: await request(pathname),
      })),
    );
    const obsoleteResponses = await Promise.all(
      OBSOLETE_LAYER_PATHS.map(async (pathname) => ({
        pathname,
        response: await request(pathname),
      })),
    );
    const observed = {
      required: requiredResponses.map(({ pathname, response }) => ({
        contentType: response.headers['content-type'] ?? null,
        pathname,
        statusCode: response.statusCode,
      })),
      obsolete: obsoleteResponses.map(({ pathname, response }) => ({
        pathname,
        statusCode: response.statusCode,
      })),
    };
    const expected = {
      required: FIVE_LAYER_PUBLIC_PATHS.map((pathname) => ({
        contentType: 'image/png',
        pathname,
        statusCode: 200,
      })),
      obsolete: OBSOLETE_LAYER_PATHS.map((pathname) => ({
        pathname,
        statusCode: 404,
      })),
    };

    assert.deepEqual(
      observed,
      expected,
      'the server allowlist must expose idle plus five rigid PNGs and no obsolete 14-part URL',
    );
  });

  it('keeps Avatar.jpg ignored as private build input', () => {
    const result = spawnSync('git', ['check-ignore', '--quiet', '--', 'Avatar.jpg'], {
      cwd: projectRoot,
      encoding: 'utf8',
    });

    assert.equal(
      result.status,
      0,
      `git check-ignore must recognize Avatar.jpg as private build input; `
        + `status=${result.status}, stdout=${result.stdout}, stderr=${result.stderr}`,
    );
  });

  it('five-layer: does not serve private manifest v2 metadata', async () => {
    const response = await request('/assets/avatar/manifest.json');

    assert.equal(
      response.statusCode,
      404,
      '/assets/avatar/manifest.json must remain build-time metadata rather than public content',
    );
    assert.equal(response.headers['content-type'], 'text/plain; charset=utf-8');
    assert.equal(response.body.toString('utf8'), 'Not Found\n');
  });

  it('documents the avatar manifest as build-time-only and not publicly served', () => {
    const readme = readFileSync(readmePath, 'utf8');
    const servedClaims = [
      ...readme.matchAll(
        /browser receives[\s\S]{0,200}?\bincluding\b[\s\S]{0,120}?assets\/avatar\/manifest\.json/gi,
      ),
    ].map(([claim]) => claim.replace(/\s+/g, ' ').trim());
    const documentsBuildTimeOnly = (
      /assets\/avatar\/manifest\.json[^\n]*(?:build-time|not (?:publicly )?served|404 Not Found)/i
        .test(readme)
      || /(?:build-time|not (?:publicly )?served|404 Not Found)[^\n]*assets\/avatar\/manifest\.json/i
        .test(readme)
    );

    console.log(`README_MANIFEST_CONTRACT=${JSON.stringify({
      documentsBuildTimeOnly,
      servedClaims,
    })}`);
    assert.deepEqual(
      { documentsBuildTimeOnly, servedClaims },
      { documentsBuildTimeOnly: true, servedClaims: [] },
      'README must agree with server.mjs: assets/avatar/manifest.json is private build-time '
        + 'metadata whose HTTP request returns 404, not a file the browser receives',
    );
  });

  it('five-layer: public runtime text exposes no private source or manifest metadata', () => {
    const publicRuntimeTextPaths = [
      indexHtmlPath,
      stylesPath,
      ...listFilesRecursively(publicSourceRoot),
    ];
    const exposures = [];

    for (const filePath of publicRuntimeTextPaths) {
      const source = readFileSync(filePath, 'utf8');
      for (const [label, pattern] of [
        ['Avatar.jpg', /Avatar\.jpg/i],
        ['private source SHA-256', new RegExp(AVATAR_SOURCE_SHA256, 'i')],
        ['private manifest path', /assets\/avatar\/manifest\.json/i],
      ]) {
        if (pattern.test(source)) {
          exposures.push({
            file: path.relative(projectRoot, filePath),
            metadata: label,
          });
        }
      }
    }
    assert.deepEqual(
      exposures,
      [],
      'public HTML, CSS, and JavaScript must expose neither private source identity nor '
        + `manifest metadata; exposed=${JSON.stringify(exposures)}`,
    );
  });

  it('accepts only loopback Host values for the actual bound port', async () => {
    const port = server.address().port;
    const localhostResponse = await request('/', { host: `localhost:${port}` });
    const attackerResponse = await request('/', { host: 'attacker.example' });
    const wrongPortResponse = await request('/', { host: `127.0.0.1:${port + 1}` });

    assert.equal(localhostResponse.statusCode, 200);
    assert.equal(attackerResponse.statusCode, 403);
    assert.match(attackerResponse.body.toString('utf8'), /Forbidden Host/);
    assert.equal(attackerResponse.headers['cross-origin-resource-policy'], 'same-origin');
    assert.equal(wrongPortResponse.statusCode, 403);
  });

  it('accepts bare loopback Hosts only when the actual local port is 80', async () => {
    for (const host of ['127.0.0.1', 'localhost', '127.0.0.1:80', 'localhost:80']) {
      const response = await requestWithLocalPort({ host, localPort: 80 });
      assert.equal(response.statusCode, 404, host);
      assert.match(response.body.toString('utf8'), /Not Found/);
    }

    for (const { host, localPort } of [
      { host: '127.0.0.1', localPort: 4173 },
      { host: 'localhost', localPort: 4173 },
      { host: '127.0.0.1:81', localPort: 80 },
      { host: 'localhost:81', localPort: 80 },
      { host: 'attacker.example', localPort: 80 },
    ]) {
      const response = await requestWithLocalPort({ host, localPort });
      assert.equal(response.statusCode, 403, `${host} on ${localPort}`);
      assert.match(response.body.toString('utf8'), /Forbidden Host/);
    }
  });

  it('exposes only the explicit public asset allowlist', async () => {
    for (const pathname of [
      '/server.mjs',
      '/package.json',
      '/README.md',
      '/test/state.test.mjs',
      '/test/server.test.mjs',
    ]) {
      const response = await request(pathname);
      assert.equal(response.statusCode, 404, pathname);
    }

    const backgroundResponse = await request('/background.jpg');
    const backgroundExists = existsSync(new URL('../background.jpg', import.meta.url));
    assert.equal(backgroundResponse.statusCode, backgroundExists ? 200 : 404);
  });

  it('returns 404 for missing files and refuses directory listings', async () => {
    const missingResponse = await request('/not-here.txt');
    const directoryResponse = await request('/src/');

    assert.equal(missingResponse.statusCode, 404);
    assert.equal(directoryResponse.statusCode, 404);
    assert.doesNotMatch(directoryResponse.body.toString('utf8'), /app\.js/);
  });

  it('returns 405 with Allow for unsupported methods', async () => {
    const response = await request('/', { method: 'POST' });

    assert.equal(response.statusCode, 405);
    assert.equal(response.headers.allow, 'GET, HEAD');
    assert.equal(response.headers['cache-control'], 'no-store');
    assert.equal(response.headers['cross-origin-resource-policy'], 'same-origin');
  });

  for (const pathname of [
    '/..%2Fpackage.json',
    '/%2e%2e%2fpackage.json',
    '/src%5capp.js',
    '/%00',
  ]) {
    it(`rejects unsafe request path ${pathname}`, async () => {
      const response = await request(pathname);
      assert.equal(response.statusCode, 400);
    });
  }

  it('rejects invalid PORT values before listening', async () => {
    const child = spawn(process.execPath, ['server.mjs'], {
      cwd: projectRoot,
      env: { ...process.env, PORT: '4173oops' },
      stdio: ['ignore', 'pipe', 'pipe'],
    });
    let stderr = '';
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', (chunk) => {
      stderr += chunk;
    });

    const [exitCode] = await once(child, 'exit');
    assert.equal(exitCode, 1);
    assert.match(stderr, /PORT must be an integer from 1 through 65535/);
  });
});
