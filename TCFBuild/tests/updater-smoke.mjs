import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdtemp, mkdir, readFile, readdir, rm, stat, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, relative, resolve, sep } from "node:path";
import test from "node:test";

import { runUpdater } from "../release/updater.mjs";

const BASE_COMMIT = "1".repeat(40);
const REMOTE_COMMIT = "2".repeat(40);
const NEXT_COMMIT = "4".repeat(40);
const TREE_SHA = "3".repeat(40);
const RUNTIME_FILES = [
  "server.mjs",
  "index.html",
  "styles.css",
  "control.html",
  "control.css",
  "Logo.png",
  "Boy.png",
  "Girl.png",
  "src/app.mjs",
  "src/config.mjs",
  "src/control.mjs",
  "src/currency.mjs",
  "src/model.mjs",
  "src/render.mjs",
  "src/scene.mjs",
];
const PNG = Buffer.from([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x54, 0x43, 0x46,
]);

test("immutable runtime manifest contains every local JavaScript import", async () => {
  const projectRoot = resolve(import.meta.dirname, "..");
  for (const runtimePath of RUNTIME_FILES.filter((pathValue) => pathValue.endsWith(".mjs"))) {
    const source = await readFile(join(projectRoot, ...runtimePath.split("/")), "utf8");
    for (const match of source.matchAll(/from\s+["'](\.[^"']+)["']/g)) {
      const importedPath = relative(
        projectRoot,
        resolve(projectRoot, dirname(runtimePath), match[1])
      ).split(sep).join("/");
      assert.ok(
        RUNTIME_FILES.includes(importedPath),
        `${runtimePath} imports ${importedPath}, which is absent from the immutable runtime manifest`
      );
    }
  }
});

function gitBlobSha(bytes) {
  return createHash("sha1")
    .update(Buffer.from(`blob ${bytes.length}\0`))
    .update(bytes)
    .digest("hex");
}

function fixtureBytes(label, { invalidJs = false } = {}) {
  return new Map(
    RUNTIME_FILES.map((pathValue) => {
      if (pathValue.endsWith(".png")) {
        return [pathValue, Buffer.concat([PNG, Buffer.from(label)])];
      }
      if (pathValue === "server.mjs") {
        return [pathValue, Buffer.from(`console.log(${JSON.stringify(label)});\n`)];
      }
      if (pathValue.endsWith(".mjs")) {
        const source =
          invalidJs && pathValue === "src/app.mjs"
            ? "export const broken = ;\n"
            : `export const value = ${JSON.stringify(`${label}:${pathValue}`)};\n`;
        return [pathValue, Buffer.from(source)];
      }
      if (pathValue.endsWith(".css")) {
        return [pathValue, Buffer.from(`/* ${label}:${pathValue} */\nbody { color: #fff; }\n`)];
      }
      return [pathValue, Buffer.from(`<!doctype html><title>${label}:${pathValue}</title>\n`)];
    }),
  );
}

async function writeGeneration(root, name, bytesByPath) {
  const generationRoot = join(root, "app", "generations", name);
  for (const [pathValue, bytes] of bytesByPath) {
    const fullPath = join(generationRoot, ...pathValue.split("/"));
    await mkdir(dirname(fullPath), { recursive: true });
    await writeFile(fullPath, bytes);
  }
  return generationRoot;
}

async function createPackage() {
  const root = await mkdtemp(join(tmpdir(), "TCFBuild updater smoke path with spaces "));
  const packagedBytes = fixtureBytes("packaged");
  await writeGeneration(root, "packaged", packagedBytes);
  await writeFile(
    join(root, "update-policy.json"),
    `${JSON.stringify(
      {
        schemaVersion: 1,
        repository: {
          owner: "syedmh",
          name: "Dreamer",
          branch: "main",
          subdirectory: "TCFBuild",
        },
        packagedBaseCommit: BASE_COMMIT,
        packagedContent: "working-tree-snapshot",
        runtimeFiles: RUNTIME_FILES,
      },
      null,
      2,
    )}\n`,
  );
  return { root, packagedBytes };
}

function treeFor(bytesByPath) {
  return {
    truncated: false,
    tree: [...bytesByPath].map(([pathValue, bytes]) => ({
      path: `TCFBuild/${pathValue}`,
      mode: "100644",
      type: "blob",
      sha: gitBlobSha(bytes),
      size: bytes.length,
    })),
  };
}

function mockTransport({
  commitSha,
  bytesByPath,
  failPath = "",
}) {
  const tree = treeFor(bytesByPath);
  return async (url, options) => {
    assert.equal(options.headers.Authorization, undefined);
    if (url.includes("/commits?")) {
      return {
        status: 200,
        body: Buffer.from(
          JSON.stringify([
            {
              sha: commitSha,
              commit: { tree: { sha: TREE_SHA } },
            },
          ]),
        ),
      };
    }
    if (url.includes("/git/trees/")) {
      return { status: 200, body: Buffer.from(JSON.stringify(tree)) };
    }
    const marker = `/${commitSha}/TCFBuild/`;
    const markerIndex = url.indexOf(marker);
    assert.notEqual(markerIndex, -1, `Unexpected URL: ${url}`);
    const runtimePath = decodeURIComponent(url.slice(markerIndex + marker.length));
    if (runtimePath === failPath) {
      return { status: 503, body: Buffer.from("unavailable") };
    }
    return { status: 200, body: bytesByPath.get(runtimePath) };
  };
}

async function snapshotGeneration(root, name) {
  const generationRoot = join(root, "app", "generations", name);
  const snapshot = new Map();
  for (const pathValue of RUNTIME_FILES) {
    const fullPath = join(generationRoot, ...pathValue.split("/"));
    const bytes = await readFile(fullPath);
    const fileStat = await stat(fullPath);
    snapshot.set(pathValue, {
      sha256: createHash("sha256").update(bytes).digest("hex"),
      mtimeMs: fileStat.mtimeMs,
    });
  }
  return snapshot;
}

async function stagingEntries(root) {
  const generationRoot = join(root, "app", "generations");
  return (await readdir(generationRoot)).filter((name) => name.startsWith(".staging-"));
}

test("equal revision preserves packaged hashes and timestamps and selects packaged server", async (t) => {
  const { root, packagedBytes } = await createPackage();
  t.after(() => rm(root, { recursive: true, force: true }));
  const before = await snapshotGeneration(root, "packaged");
  let launchedPath = "";

  const exitCode = await runUpdater({
    packageRoot: root,
    token: "",
    request: mockTransport({
      commitSha: BASE_COMMIT,
      bytesByPath: packagedBytes,
    }),
    launch: async (serverPath) => {
      launchedPath = serverPath;
      return 0;
    },
  });

  assert.equal(exitCode, 0);
  assert.equal(
    relative(root, launchedPath).split(sep).join("/"),
    "app/generations/packaged/server.mjs",
  );
  assert.deepEqual(await snapshotGeneration(root, "packaged"), before);
  assert.deepEqual(await stagingEntries(root), []);
});

test("different revision downloads, validates, and atomically activates a complete generation", async (t) => {
  const { root } = await createPackage();
  t.after(() => rm(root, { recursive: true, force: true }));
  const remoteBytes = fixtureBytes("remote");
  let launchedPath = "";

  const exitCode = await runUpdater({
    packageRoot: root,
    token: "",
    request: mockTransport({
      commitSha: REMOTE_COMMIT,
      bytesByPath: remoteBytes,
    }),
    launch: async (serverPath) => {
      launchedPath = serverPath;
      return 0;
    },
  });

  assert.equal(exitCode, 0);
  assert.equal(
    relative(root, launchedPath).split(sep).join("/"),
    `app/generations/${REMOTE_COMMIT}/server.mjs`,
  );
  const state = JSON.parse(
    await readFile(join(root, ".tcfbuild-state", "current.json"), "utf8"),
  );
  assert.equal(state.activeGeneration, REMOTE_COMMIT);
  assert.equal(state.activeCommit, REMOTE_COMMIT);
  for (const [pathValue, expectedBytes] of remoteBytes) {
    assert.deepEqual(
      await readFile(
        join(root, "app", "generations", REMOTE_COMMIT, ...pathValue.split("/")),
      ),
      expectedBytes,
    );
  }
  assert.deepEqual(await stagingEntries(root), []);
});

test("malformed JavaScript leaves packaged generation active and removes staging", async (t) => {
  const { root } = await createPackage();
  t.after(() => rm(root, { recursive: true, force: true }));
  const malformedBytes = fixtureBytes("malformed", { invalidJs: true });
  let launchedPath = "";

  const exitCode = await runUpdater({
    packageRoot: root,
    token: "",
    request: mockTransport({
      commitSha: REMOTE_COMMIT,
      bytesByPath: malformedBytes,
    }),
    launch: async (serverPath) => {
      launchedPath = serverPath;
      return 0;
    },
  });

  assert.equal(exitCode, 0);
  assert.equal(
    relative(root, launchedPath).split(sep).join("/"),
    "app/generations/packaged/server.mjs",
  );
  await assert.rejects(
    stat(join(root, ".tcfbuild-state", "current.json")),
    /ENOENT/,
  );
  assert.deepEqual(await stagingEntries(root), []);
});

test("download failure leaves the prior remote generation active", async (t) => {
  const { root } = await createPackage();
  t.after(() => rm(root, { recursive: true, force: true }));
  const firstBytes = fixtureBytes("first remote");
  const nextBytes = fixtureBytes("next remote");

  await runUpdater({
    packageRoot: root,
    token: "",
    request: mockTransport({
      commitSha: REMOTE_COMMIT,
      bytesByPath: firstBytes,
    }),
    launch: async () => 0,
  });

  let launchedPath = "";
  const exitCode = await runUpdater({
    packageRoot: root,
    token: "",
    request: mockTransport({
      commitSha: NEXT_COMMIT,
      bytesByPath: nextBytes,
      failPath: "styles.css",
    }),
    launch: async (serverPath) => {
      launchedPath = serverPath;
      return 0;
    },
  });

  assert.equal(exitCode, 0);
  assert.equal(
    relative(root, launchedPath).split(sep).join("/"),
    `app/generations/${REMOTE_COMMIT}/server.mjs`,
  );
  const state = JSON.parse(
    await readFile(join(root, ".tcfbuild-state", "current.json"), "utf8"),
  );
  assert.equal(state.activeCommit, REMOTE_COMMIT);
  await assert.rejects(
    stat(join(root, "app", "generations", NEXT_COMMIT)),
    /ENOENT/,
  );
  assert.deepEqual(await stagingEntries(root), []);
});
