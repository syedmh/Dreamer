import { createReadStream } from 'node:fs';
import { realpath, stat } from 'node:fs/promises';
import { createServer } from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const DEFAULT_HOST = '127.0.0.1';
export const DEFAULT_PORT = 4173;
const PROJECT_ROOT = path.dirname(fileURLToPath(import.meta.url));
const PUBLIC_FILES = new Map([
  ['/', 'index.html'],
  ['/index.html', 'index.html'],
  ['/styles.css', 'styles.css'],
  ['/background.jpg', 'background.jpg'],
  ['/src/app.js', 'src/app.js'],
  ['/src/constants.js', 'src/constants.js'],
  ['/src/render.js', 'src/render.js'],
  ['/src/state.js', 'src/state.js'],
  ['/assets/sprites/idle.png', 'assets/sprites/idle.png'],
  ...Array.from(
    { length: 14 },
    (_, index) => [
      `/assets/sprites/walk-right-${index}.png`,
      `assets/sprites/walk-right-${index}.png`,
    ],
  ),
  ...Array.from(
    { length: 15 },
    (_, index) => [
      `/assets/sprites/walk-left-${index}.png`,
      `assets/sprites/walk-left-${index}.png`,
    ],
  ),
  ...Array.from(
    { length: 5 },
    (_, index) => [
      `/assets/sprites/clap-${index}.png`,
      `assets/sprites/clap-${index}.png`,
    ],
  ),
]);

const MIME_TYPES = new Map([
  ['.css', 'text/css; charset=utf-8'],
  ['.html', 'text/html; charset=utf-8'],
  ['.jpeg', 'image/jpeg'],
  ['.jpg', 'image/jpeg'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.mjs', 'text/javascript; charset=utf-8'],
  ['.png', 'image/png'],
  ['.svg', 'image/svg+xml; charset=utf-8'],
  ['.txt', 'text/plain; charset=utf-8'],
  ['.webp', 'image/webp'],
]);

export function parsePort(value) {
  if (value === undefined || value === '') {
    return DEFAULT_PORT;
  }

  if (typeof value !== 'string' || !/^\d+$/.test(value)) {
    throw new RangeError('PORT must be an integer from 1 through 65535');
  }

  const port = Number(value);
  if (!Number.isSafeInteger(port) || port < 1 || port > 65535) {
    throw new RangeError('PORT must be an integer from 1 through 65535');
  }

  return port;
}

function sendText(response, statusCode, message, extraHeaders = {}) {
  const body = Buffer.from(`${message}\n`, 'utf8');
  response.writeHead(statusCode, {
    'Cache-Control': 'no-store',
    'Content-Length': body.length,
    'Content-Type': 'text/plain; charset=utf-8',
    ...extraHeaders,
  });
  response.end(body);
}

function decodeRequestPath(requestUrl) {
  const rawPath = String(requestUrl ?? '/').split(/[?#]/, 1)[0] || '/';
  let decodedPath;

  try {
    decodedPath = decodeURIComponent(rawPath);
  } catch {
    return null;
  }

  if (
    decodedPath.includes('\0')
    || decodedPath.includes('\\')
    || decodedPath.split('/').some((segment) => segment === '..')
  ) {
    return null;
  }

  return decodedPath;
}

function isInsideRoot(candidatePath, rootPath) {
  const relative = path.relative(rootPath, candidatePath);
  return relative === '' || (!relative.startsWith('..') && !path.isAbsolute(relative));
}

function isAllowedHost(request) {
  const host = request.headers.host?.trim().toLowerCase();
  const localPort = request.socket.localPort;

  if (!host || !Number.isInteger(localPort)) {
    return false;
  }

  return host === `${DEFAULT_HOST}:${localPort}`
    || host === `localhost:${localPort}`
    || (
      localPort === 80
      && (host === DEFAULT_HOST || host === 'localhost')
    );
}

export function createStaticServer({ root = PROJECT_ROOT } = {}) {
  const absoluteRoot = path.resolve(root);

  return createServer(async (request, response) => {
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('Cross-Origin-Resource-Policy', 'same-origin');
    response.setHeader('X-Content-Type-Options', 'nosniff');

    if (!isAllowedHost(request)) {
      sendText(response, 403, 'Forbidden Host');
      return;
    }

    if (request.method !== 'GET' && request.method !== 'HEAD') {
      sendText(response, 405, 'Method Not Allowed', { Allow: 'GET, HEAD' });
      return;
    }

    const decodedPath = decodeRequestPath(request.url);
    if (decodedPath === null) {
      sendText(response, 400, 'Bad Request');
      return;
    }

    const relativePath = PUBLIC_FILES.get(decodedPath);
    if (relativePath === undefined) {
      sendText(response, 404, 'Not Found');
      return;
    }

    const candidatePath = path.resolve(absoluteRoot, relativePath);

    if (!isInsideRoot(candidatePath, absoluteRoot)) {
      sendText(response, 403, 'Forbidden');
      return;
    }

    try {
      const [canonicalRoot, canonicalFile, fileStats] = await Promise.all([
        realpath(absoluteRoot),
        realpath(candidatePath),
        stat(candidatePath),
      ]);

      if (!isInsideRoot(canonicalFile, canonicalRoot)) {
        sendText(response, 403, 'Forbidden');
        return;
      }

      if (!fileStats.isFile()) {
        sendText(response, 404, 'Not Found');
        return;
      }

      const contentType = MIME_TYPES.get(path.extname(canonicalFile).toLowerCase())
        ?? 'application/octet-stream';
      response.writeHead(200, {
        'Cache-Control': 'no-store',
        'Content-Length': fileStats.size,
        'Content-Type': contentType,
      });

      if (request.method === 'HEAD') {
        response.end();
        return;
      }

      const stream = createReadStream(canonicalFile);
      stream.on('error', (error) => {
        if (!response.headersSent) {
          sendText(response, 500, 'Internal Server Error');
        } else {
          response.destroy(error);
        }
      });
      stream.pipe(response);
    } catch (error) {
      if (error?.code === 'ENOENT' || error?.code === 'ENOTDIR') {
        sendText(response, 404, 'Not Found');
        return;
      }

      console.error('Static server request failed:', error);
      sendText(response, 500, 'Internal Server Error');
    }
  });
}

export function startStaticServer({
  port = DEFAULT_PORT,
  root = PROJECT_ROOT,
  onError,
  onListening,
} = {}) {
  const server = createStaticServer({ root });

  if (onError) {
    server.on('error', onError);
  }

  server.listen(port, DEFAULT_HOST, () => {
    onListening?.(server);
  });

  return server;
}

function isMainModule() {
  return process.argv[1] !== undefined
    && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}

if (isMainModule()) {
  try {
    const port = parsePort(process.env.PORT);
    startStaticServer({
      port,
      onError(error) {
        console.error(`Unable to start TCF Event Avatar server: ${error.message}`);
        process.exitCode = 1;
      },
      onListening() {
        console.log(`TCF Event Avatar available at http://${DEFAULT_HOST}:${port}`);
      },
    });
  } catch (error) {
    console.error(`Unable to start TCF Event Avatar server: ${error.message}`);
    process.exitCode = 1;
  }
}
