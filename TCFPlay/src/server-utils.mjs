import path from "node:path";

export const SECURITY_HEADERS = Object.freeze({
  "Cache-Control": "no-store",
  "Content-Security-Policy": "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'",
  "Cross-Origin-Opener-Policy": "same-origin",
  "Referrer-Policy": "no-referrer",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY"
});

export const MIME_TYPES = Object.freeze({
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".mjs": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".md": "text/markdown; charset=utf-8"
});

export const ALLOWED_FILES = new Set([
  "/index.html",
  "/styles.css",
  "/data/playground.v1.json",
  "/data/playground.v2.json",
  "/src/app.js",
  "/src/background.js",
  "/src/config.js",
  "/src/controls.js",
  "/src/prompt-compiler.js",
  "/src/renderer.js",
  "/src/sprites.js",
  "/src/timeline.js",
  ...Array.from(
    { length: 9 },
    (_, index) => `/assets/boy-turn/turn_${String(index + 1).padStart(2, "0")}.png`
  ),
  ...Array.from(
    { length: 12 },
    (_, index) => `/assets/boy-walk/walk_${String(index + 1).padStart(2, "0")}.png`
  )
]);

export function resolveRequestPath(rawUrl, rootDirectory) {
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(rawUrl, "http://127.0.0.1").pathname);
  } catch {
    return null;
  }
  if (pathname.includes("\0") || pathname.includes("\\") || pathname.split("/").includes("..")) {
    return null;
  }
  if (pathname === "/") {
    pathname = "/index.html";
  }
  if (!ALLOWED_FILES.has(pathname)) {
    return null;
  }
  const relative = pathname.slice(1);
  const resolved = path.resolve(rootDirectory, relative);
  const root = path.resolve(rootDirectory);
  if (resolved !== root && !resolved.startsWith(`${root}${path.sep}`)) {
    return null;
  }
  return { pathname, filePath: resolved, mimeType: MIME_TYPES[path.extname(resolved)] || "application/octet-stream" };
}
