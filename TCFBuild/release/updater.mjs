import { createHash, randomBytes } from "node:crypto";
import {
  lstat,
  mkdir,
  open,
  readFile,
  readdir,
  rename,
  rm,
  stat,
  writeFile,
} from "node:fs/promises";
import { request as httpsRequest } from "node:https";
import { basename, dirname, join, relative, resolve, sep } from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

const API_VERSION = "2026-03-10";
const CHECK_TIMEOUT_MS = 5_000;
const FILE_TIMEOUT_MS = 12_000;
const SYNTAX_TIMEOUT_MS = 5_000;
const TOTAL_UPDATE_TIMEOUT_MS = 45_000;
const MAX_CONCURRENT_DOWNLOADS = 4;
const MAX_JSON_BYTES = 10 * 1024 * 1024;
const MAX_FILE_BYTES = 8 * 1024 * 1024;
const MAX_GENERATION_BYTES = 25 * 1024 * 1024;
const SHA_PATTERN = /^[0-9a-f]{40}$/;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

class UpdateError extends Error {
  constructor(phase, message) {
    super(message);
    this.name = "UpdateError";
    this.phase = phase;
  }
}

function shortSha(value) {
  return SHA_PATTERN.test(value ?? "") ? value.slice(0, 8) : "packaged";
}

function sanitizeMessage(error, token = "") {
  let message = error instanceof Error ? error.message : String(error);
  if (token) {
    message = message.split(token).join("[redacted]");
  }
  return message
    .replace(/https?:\/\/\S+/gi, "[remote]")
    .replace(/[\r\n\t]+/g, " ")
    .slice(0, 300);
}

function warning(phase, error, generation, token = "") {
  console.warn(
    `[TCFBuild update] WARNING: Update skipped during ${phase}: ` +
      `${sanitizeMessage(error, token)}. Starting ${shortSha(generation.activeCommit)}.`,
  );
}

function ensureWithin(root, candidate, phase = "local paths") {
  const resolvedRoot = resolve(root);
  const resolvedCandidate = resolve(candidate);
  const prefix = resolvedRoot.endsWith(sep) ? resolvedRoot : `${resolvedRoot}${sep}`;
  if (resolvedCandidate !== resolvedRoot && !resolvedCandidate.startsWith(prefix)) {
    throw new UpdateError(phase, "A local path escaped the package directory.");
  }
  return resolvedCandidate;
}

function encodePath(pathValue) {
  return pathValue.split("/").map(encodeURIComponent).join("/");
}

function gitBlobSha(bytes) {
  const header = Buffer.from(`blob ${bytes.length}\0`, "utf8");
  return createHash("sha1").update(header).update(bytes).digest("hex");
}

function parseJson(bytes, phase) {
  try {
    return JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new UpdateError(phase, "GitHub returned invalid JSON.");
  }
}

function validatePolicy(policy) {
  if (
    policy?.schemaVersion !== 1 ||
    policy?.repository?.owner !== "syedmh" ||
    policy?.repository?.name !== "Dreamer" ||
    policy?.repository?.branch !== "main" ||
    policy?.repository?.subdirectory !== "TCFBuild" ||
    policy?.packagedContent !== "working-tree-snapshot" ||
    !SHA_PATTERN.test(policy?.packagedBaseCommit ?? "") ||
    !Array.isArray(policy?.runtimeFiles) ||
    policy.runtimeFiles.length !== 15
  ) {
    throw new UpdateError("policy validation", "The immutable update policy is invalid.");
  }

  const seen = new Set();
  for (const pathValue of policy.runtimeFiles) {
    if (
      typeof pathValue !== "string" ||
      pathValue.length === 0 ||
      pathValue.includes("\\") ||
      pathValue.startsWith("/") ||
      pathValue.split("/").some((part) => part === "" || part === "." || part === "..") ||
      seen.has(pathValue)
    ) {
      throw new UpdateError("policy validation", "The runtime allowlist is invalid.");
    }
    seen.add(pathValue);
  }

  if (!seen.has("server.mjs")) {
    throw new UpdateError("policy validation", "The runtime allowlist is missing server.mjs.");
  }
  return policy;
}

async function loadPolicy(packageRoot) {
  const policyPath = ensureWithin(packageRoot, join(packageRoot, "update-policy.json"));
  let bytes;
  try {
    bytes = await readFile(policyPath);
  } catch {
    throw new UpdateError("policy validation", "update-policy.json could not be read.");
  }
  return validatePolicy(parseJson(bytes, "policy validation"));
}

function generationDetails(packageRoot, name, commit) {
  if (name !== "packaged" && !SHA_PATTERN.test(name)) {
    throw new UpdateError("state validation", "The active generation name is invalid.");
  }
  const generationRoot = ensureWithin(
    packageRoot,
    join(packageRoot, "app", "generations", name),
    "state validation",
  );
  return {
    name,
    activeCommit: commit,
    root: generationRoot,
    serverPath: ensureWithin(generationRoot, join(generationRoot, "server.mjs")),
  };
}

async function readState(packageRoot, policy) {
  const stateRoot = ensureWithin(packageRoot, join(packageRoot, ".tcfbuild-state"));
  const statePath = ensureWithin(stateRoot, join(stateRoot, "current.json"));
  const packaged = generationDetails(
    packageRoot,
    "packaged",
    policy.packagedBaseCommit,
  );

  let bytes;
  try {
    bytes = await readFile(statePath);
  } catch (error) {
    if (error?.code === "ENOENT") {
      return { stateRoot, statePath, generation: packaged, state: null };
    }
    throw new UpdateError("state validation", "The active generation state could not be read.");
  }

  let stateValue;
  try {
    stateValue = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new UpdateError("state validation", "The active generation state is invalid JSON.");
  }
  if (
    stateValue?.schemaVersion !== 1 ||
    !SHA_PATTERN.test(stateValue?.activeCommit ?? "") ||
    stateValue?.activeGeneration !== stateValue.activeCommit
  ) {
    throw new UpdateError("state validation", "The active generation state is invalid.");
  }

  return {
    stateRoot,
    statePath,
    generation: generationDetails(
      packageRoot,
      stateValue.activeGeneration,
      stateValue.activeCommit,
    ),
    state: stateValue,
  };
}

async function listFiles(root) {
  const files = [];
  async function visit(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const fullPath = join(directory, entry.name);
      if (entry.isSymbolicLink()) {
        throw new UpdateError("generation validation", "A generation contains a symbolic link.");
      }
      if (entry.isDirectory()) {
        await visit(fullPath);
      } else if (entry.isFile()) {
        files.push(relative(root, fullPath).split(sep).join("/"));
      } else {
        throw new UpdateError("generation validation", "A generation contains an unsupported entry.");
      }
    }
  }
  await visit(root);
  return files.sort();
}

async function validateGenerationStructure(generation, policy) {
  let rootStat;
  try {
    rootStat = await lstat(generation.root);
  } catch {
    throw new UpdateError("generation validation", "The selected generation is missing.");
  }
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
    throw new UpdateError("generation validation", "The selected generation is not a directory.");
  }
  const actual = await listFiles(generation.root);
  const expected = [...policy.runtimeFiles].sort();
  if (
    actual.length !== expected.length ||
    actual.some((pathValue, index) => pathValue !== expected[index])
  ) {
    throw new UpdateError(
      "generation validation",
      "The selected generation does not match the runtime allowlist.",
    );
  }
  return generation;
}

async function selectLocalGeneration(packageRoot, policy) {
  const packaged = generationDetails(
    packageRoot,
    "packaged",
    policy.packagedBaseCommit,
  );
  let stateInfo;
  try {
    stateInfo = await readState(packageRoot, policy);
    await validateGenerationStructure(stateInfo.generation, policy);
    return stateInfo;
  } catch (error) {
    warning(error.phase ?? "state validation", error, packaged);
    await validateGenerationStructure(packaged, policy);
    const stateRoot = ensureWithin(packageRoot, join(packageRoot, ".tcfbuild-state"));
    return {
      stateRoot,
      statePath: ensureWithin(stateRoot, join(stateRoot, "current.json")),
      generation: packaged,
      state: null,
    };
  }
}

function remainingTimeout(deadline, requested, phase) {
  const remaining = deadline - Date.now();
  if (remaining <= 0) {
    throw new UpdateError(phase, "The update exceeded its 45-second deadline.");
  }
  return Math.max(1, Math.min(requested, remaining));
}

export function requestBuffer(
  url,
  { headers, timeoutMs, maxBytes = MAX_JSON_BYTES, signal },
) {
  return new Promise((resolveRequest, rejectRequest) => {
    let settled = false;
    const fail = (error) => {
      if (!settled) {
        settled = true;
        rejectRequest(error);
      }
    };
    const succeed = (value) => {
      if (!settled) {
        settled = true;
        resolveRequest(value);
      }
    };

    const request = httpsRequest(
      url,
      {
        method: "GET",
        headers,
        signal,
      },
      (response) => {
        const chunks = [];
        let length = 0;
        response.on("data", (chunk) => {
          length += chunk.length;
          if (length > maxBytes) {
            request.destroy(new Error("The response exceeded its allowed size."));
            return;
          }
          chunks.push(chunk);
        });
        response.on("end", () => {
          succeed({
            status: response.statusCode ?? 0,
            headers: response.headers,
            body: Buffer.concat(chunks, length),
          });
        });
        response.on("error", fail);
      },
    );
    request.setTimeout(timeoutMs, () => {
      request.destroy(new Error("The request timed out."));
    });
    request.on("error", fail);
    request.end();
  });
}

function apiHeaders(token, accept = "application/vnd.github+json") {
  const headers = {
    Accept: accept,
    "User-Agent": "TCFBuild-Updater/1",
    "X-GitHub-Api-Version": API_VERSION,
  };
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }
  return headers;
}

function rawHeaders() {
  return {
    Accept: "application/octet-stream",
    "User-Agent": "TCFBuild-Updater/1",
  };
}

async function requireOk(request, url, options, phase) {
  let response;
  try {
    response = await request(url, options);
  } catch (error) {
    throw new UpdateError(phase, sanitizeMessage(error));
  }
  if (response?.status !== 200 || !Buffer.isBuffer(response?.body)) {
    throw new UpdateError(phase, `GitHub returned HTTP ${response?.status ?? "unknown"}.`);
  }
  return response.body;
}

async function resolveRemoteCommit({ policy, request, token, deadline, signal }) {
  const { owner, name, branch, subdirectory } = policy.repository;
  const url =
    `https://api.github.com/repos/${encodeURIComponent(owner)}/` +
    `${encodeURIComponent(name)}/commits?sha=${encodeURIComponent(branch)}` +
    `&path=${encodeURIComponent(subdirectory)}&per_page=1`;
  const bytes = await requireOk(
    request,
    url,
    {
      headers: apiHeaders(token),
      timeoutMs: remainingTimeout(deadline, CHECK_TIMEOUT_MS, "commit check"),
      maxBytes: MAX_JSON_BYTES,
      signal,
    },
    "commit check",
  );
  const value = parseJson(bytes, "commit check");
  const commit = Array.isArray(value) ? value[0] : null;
  if (
    !SHA_PATTERN.test(commit?.sha ?? "") ||
    !SHA_PATTERN.test(commit?.commit?.tree?.sha ?? "")
  ) {
    throw new UpdateError("commit check", "GitHub returned invalid commit metadata.");
  }
  return { commitSha: commit.sha, treeSha: commit.commit.tree.sha };
}

async function resolveExpectedFiles({
  policy,
  request,
  token,
  treeSha,
  deadline,
  signal,
}) {
  const { owner, name, subdirectory } = policy.repository;
  const url =
    `https://api.github.com/repos/${encodeURIComponent(owner)}/` +
    `${encodeURIComponent(name)}/git/trees/${treeSha}?recursive=1`;
  const bytes = await requireOk(
    request,
    url,
    {
      headers: apiHeaders(token),
      timeoutMs: remainingTimeout(deadline, CHECK_TIMEOUT_MS, "tree check"),
      maxBytes: MAX_JSON_BYTES,
      signal,
    },
    "tree check",
  );
  const tree = parseJson(bytes, "tree check");
  if (tree?.truncated !== false || !Array.isArray(tree?.tree)) {
    throw new UpdateError("tree check", "GitHub returned an incomplete recursive tree.");
  }

  const wanted = new Map(
    policy.runtimeFiles.map((pathValue) => [`${subdirectory}/${pathValue}`, pathValue]),
  );
  const expected = new Map();
  let totalSize = 0;
  for (const entry of tree.tree) {
    const runtimePath = wanted.get(entry?.path);
    if (!runtimePath) {
      continue;
    }
    if (expected.has(runtimePath)) {
      throw new UpdateError("tree check", `Duplicate tree entry for ${runtimePath}.`);
    }
    if (
      entry?.type !== "blob" ||
      entry?.mode !== "100644" && entry?.mode !== "100755" ||
      !SHA_PATTERN.test(entry?.sha ?? "") ||
      !Number.isSafeInteger(entry?.size) ||
      entry.size <= 0 ||
      entry.size > MAX_FILE_BYTES
    ) {
      throw new UpdateError("tree check", `Invalid tree metadata for ${runtimePath}.`);
    }
    totalSize += entry.size;
    if (totalSize > MAX_GENERATION_BYTES) {
      throw new UpdateError("tree check", "The runtime generation is unreasonably large.");
    }
    expected.set(runtimePath, { sha: entry.sha, size: entry.size });
  }
  for (const pathValue of policy.runtimeFiles) {
    if (!expected.has(pathValue)) {
      throw new UpdateError("tree check", `Missing tree blob for ${pathValue}.`);
    }
  }
  return expected;
}

function fileUrl(policy, commitSha, runtimePath, token) {
  const { owner, name, subdirectory } = policy.repository;
  if (token) {
    return (
      `https://api.github.com/repos/${encodeURIComponent(owner)}/` +
      `${encodeURIComponent(name)}/contents/` +
      `${encodePath(`${subdirectory}/${runtimePath}`)}?ref=${commitSha}`
    );
  }
  return (
    `https://raw.githubusercontent.com/${encodeURIComponent(owner)}/` +
    `${encodeURIComponent(name)}/${commitSha}/` +
    encodePath(`${subdirectory}/${runtimePath}`)
  );
}

function validateFileBytes(pathValue, bytes, metadata) {
  if (bytes.length !== metadata.size) {
    throw new UpdateError("file validation", `Size mismatch for ${pathValue}.`);
  }
  if (gitBlobSha(bytes) !== metadata.sha) {
    throw new UpdateError("file validation", `Git blob mismatch for ${pathValue}.`);
  }
  if (pathValue.toLowerCase().endsWith(".png")) {
    if (bytes.length < PNG_SIGNATURE.length || !bytes.subarray(0, 8).equals(PNG_SIGNATURE)) {
      throw new UpdateError("file validation", `Invalid PNG signature for ${pathValue}.`);
    }
    return;
  }
  if (bytes.length === 0 || bytes.includes(0)) {
    throw new UpdateError("file validation", `Invalid text asset ${pathValue}.`);
  }
}

async function writeRuntimeFile(stagingRoot, pathValue, bytes) {
  const destination = ensureWithin(stagingRoot, join(stagingRoot, ...pathValue.split("/")));
  await mkdir(dirname(destination), { recursive: true });
  await writeFile(destination, bytes, { flag: "wx" });
}

async function downloadGeneration({
  packageRoot,
  policy,
  request,
  token,
  commitSha,
  expected,
  deadline,
  signal,
}) {
  const generationsRoot = ensureWithin(
    packageRoot,
    join(packageRoot, "app", "generations"),
  );
  const stagingName = `.staging-${process.pid}-${randomBytes(8).toString("hex")}`;
  const stagingRoot = ensureWithin(generationsRoot, join(generationsRoot, stagingName));
  await mkdir(stagingRoot, { recursive: false });

  let nextIndex = 0;
  const workers = Array.from(
    { length: Math.min(MAX_CONCURRENT_DOWNLOADS, policy.runtimeFiles.length) },
    async () => {
      while (nextIndex < policy.runtimeFiles.length) {
        const index = nextIndex;
        nextIndex += 1;
        const pathValue = policy.runtimeFiles[index];
        const metadata = expected.get(pathValue);
        const url = fileUrl(policy, commitSha, pathValue, token);
        const bytes = await requireOk(
          request,
          url,
          {
            headers: token
              ? apiHeaders(token, "application/vnd.github.raw+json")
              : rawHeaders(),
            timeoutMs: remainingTimeout(deadline, FILE_TIMEOUT_MS, "file download"),
            maxBytes: metadata.size,
            signal,
          },
          "file download",
        );
        validateFileBytes(pathValue, bytes, metadata);
        await writeRuntimeFile(stagingRoot, pathValue, bytes);
      }
    },
  );

  const results = await Promise.allSettled(workers);
  const failure = results.find((result) => result.status === "rejected");
  if (failure) {
    await rm(stagingRoot, { recursive: true, force: true }).catch(() => {});
    throw failure.reason;
  }
  return stagingRoot;
}

function checkSyntax(pathValue, executable, timeoutMs) {
  return new Promise((resolveCheck, rejectCheck) => {
    const child = spawn(executable, ["--check", pathValue], {
      stdio: ["ignore", "ignore", "pipe"],
      windowsHide: true,
    });
    const timer = setTimeout(() => {
      child.kill();
      rejectCheck(new UpdateError("JavaScript validation", "JavaScript validation timed out."));
    }, timeoutMs);
    child.stderr.resume();
    child.on("error", () => {
      clearTimeout(timer);
      rejectCheck(
        new UpdateError("JavaScript validation", "The embedded Node.js runtime could not validate JavaScript."),
      );
    });
    child.on("exit", (code) => {
      clearTimeout(timer);
      if (code === 0) {
        resolveCheck();
      } else {
        rejectCheck(
          new UpdateError(
            "JavaScript validation",
            `JavaScript syntax validation failed for ${basename(pathValue)}.`,
          ),
        );
      }
    });
  });
}

async function validateCompleteGeneration({
  generationRoot,
  policy,
  expected,
  deadline,
}) {
  const generation = {
    root: generationRoot,
    serverPath: join(generationRoot, "server.mjs"),
  };
  await validateGenerationStructure(generation, policy);
  let totalSize = 0;
  for (const pathValue of policy.runtimeFiles) {
    const fullPath = ensureWithin(generationRoot, join(generationRoot, ...pathValue.split("/")));
    const fileStat = await stat(fullPath);
    if (!fileStat.isFile()) {
      throw new UpdateError("generation validation", `Invalid file entry ${pathValue}.`);
    }
    const bytes = await readFile(fullPath);
    validateFileBytes(pathValue, bytes, expected.get(pathValue));
    totalSize += bytes.length;
  }
  if (totalSize > MAX_GENERATION_BYTES) {
    throw new UpdateError("generation validation", "The runtime generation is unreasonably large.");
  }

  const javascriptFiles = policy.runtimeFiles.filter(
    (pathValue) => pathValue === "server.mjs" || /^src\/[^/]+\.mjs$/.test(pathValue),
  );
  for (const pathValue of javascriptFiles) {
    await checkSyntax(
      join(generationRoot, ...pathValue.split("/")),
      process.execPath,
      remainingTimeout(deadline, SYNTAX_TIMEOUT_MS, "JavaScript validation"),
    );
  }
}

async function installGeneration({
  packageRoot,
  policy,
  expected,
  commitSha,
  stagingRoot,
  deadline,
}) {
  const destination = ensureWithin(
    packageRoot,
    join(packageRoot, "app", "generations", commitSha),
  );
  try {
    await lstat(destination);
    await validateCompleteGeneration({
      generationRoot: destination,
      policy,
      expected,
      deadline,
    });
    await rm(stagingRoot, { recursive: true, force: true });
    return destination;
  } catch (error) {
    if (error?.code !== "ENOENT" && error?.phase !== "generation validation") {
      throw error;
    }
    if (error?.phase === "generation validation") {
      throw new UpdateError(
        "generation validation",
        "An existing commit generation failed validation.",
      );
    }
  }

  await validateCompleteGeneration({
    generationRoot: stagingRoot,
    policy,
    expected,
    deadline,
  });
  try {
    await rename(stagingRoot, destination);
  } catch (error) {
    if (error?.code === "EEXIST" || error?.code === "ENOTEMPTY") {
      await validateCompleteGeneration({
        generationRoot: destination,
        policy,
        expected,
        deadline,
      });
      await rm(stagingRoot, { recursive: true, force: true });
      return destination;
    }
    throw new UpdateError("generation activation", "The validated generation could not be installed.");
  }
  return destination;
}

async function activateGeneration({
  stateInfo,
  commitSha,
  previousGeneration,
}) {
  await mkdir(stateInfo.stateRoot, { recursive: true });
  const tempPath = ensureWithin(
    stateInfo.stateRoot,
    join(
      stateInfo.stateRoot,
      `current.json.tmp-${process.pid}-${randomBytes(6).toString("hex")}`,
    ),
  );
  const stateValue = {
    schemaVersion: 1,
    activeGeneration: commitSha,
    activeCommit: commitSha,
    previousGeneration: previousGeneration.name,
    previousCommit: previousGeneration.activeCommit,
    activatedAt: new Date().toISOString(),
  };
  const handle = await open(tempPath, "wx");
  try {
    await handle.writeFile(`${JSON.stringify(stateValue, null, 2)}\n`, "utf8");
    await handle.sync();
  } finally {
    await handle.close();
  }
  try {
    await rename(tempPath, stateInfo.statePath);
  } catch {
    await rm(tempPath, { force: true }).catch(() => {});
    throw new UpdateError("state activation", "The active generation pointer could not be replaced.");
  }
  return stateValue;
}

async function acquireLock(stateRoot) {
  await mkdir(stateRoot, { recursive: true });
  const lockPath = ensureWithin(stateRoot, join(stateRoot, "update.lock"));
  try {
    const handle = await open(lockPath, "wx");
    await handle.writeFile(
      `${JSON.stringify({ pid: process.pid, createdAt: new Date().toISOString() })}\n`,
      "utf8",
    );
    await handle.sync();
    return { handle, lockPath };
  } catch (error) {
    if (error?.code === "EEXIST") {
      return null;
    }
    throw new UpdateError("update lock", "The update lock could not be created.");
  }
}

async function releaseLock(lock) {
  if (!lock) {
    return;
  }
  await lock.handle.close().catch(() => {});
  await rm(lock.lockPath, { force: true }).catch(() => {});
}

async function performUpdate({
  packageRoot,
  policy,
  stateInfo,
  request,
  token,
}) {
  const lock = await acquireLock(stateInfo.stateRoot);
  if (!lock) {
    console.log("[TCFBuild update] Another launch is checking for updates; starting local server.");
    return stateInfo.generation;
  }

  const deadline = Date.now() + TOTAL_UPDATE_TIMEOUT_MS;
  const abortController = new AbortController();
  const deadlineTimer = setTimeout(() => abortController.abort(), TOTAL_UPDATE_TIMEOUT_MS);
  let stagingRoot = null;
  try {
    console.log(
      `[TCFBuild update] Checking ${policy.repository.owner}/${policy.repository.name} ` +
        `${policy.repository.branch} (current ${shortSha(stateInfo.generation.activeCommit)})...`,
    );
    const { commitSha, treeSha } = await resolveRemoteCommit({
      policy,
      request,
      token,
      deadline,
      signal: abortController.signal,
    });
    if (commitSha === stateInfo.generation.activeCommit) {
      console.log(
        `[TCFBuild update] Up to date at ${shortSha(commitSha)}; starting local server.`,
      );
      return stateInfo.generation;
    }

    const expected = await resolveExpectedFiles({
      policy,
      request,
      token,
      treeSha,
      deadline,
      signal: abortController.signal,
    });
    console.log(
      `[TCFBuild update] Updating ${shortSha(stateInfo.generation.activeCommit)} -> ` +
        `${shortSha(commitSha)} (${policy.runtimeFiles.length} files)...`,
    );
    stagingRoot = await downloadGeneration({
      packageRoot,
      policy,
      request,
      token,
      commitSha,
      expected,
      deadline,
      signal: abortController.signal,
    });
    const installedRoot = await installGeneration({
      packageRoot,
      policy,
      expected,
      commitSha,
      stagingRoot,
      deadline,
    });
    stagingRoot = null;
    await activateGeneration({
      stateInfo,
      commitSha,
      previousGeneration: stateInfo.generation,
    });
    console.log(
      `[TCFBuild update] Validated and activated ${shortSha(commitSha)}; starting local server.`,
    );
    return {
      name: commitSha,
      activeCommit: commitSha,
      root: installedRoot,
      serverPath: ensureWithin(installedRoot, join(installedRoot, "server.mjs")),
    };
  } finally {
    clearTimeout(deadlineTimer);
    abortController.abort();
    if (stagingRoot) {
      await rm(stagingRoot, { recursive: true, force: true }).catch(() => {});
    }
    await releaseLock(lock);
  }
}

export function launchServer(serverPath, args) {
  return new Promise((resolveLaunch, rejectLaunch) => {
    const child = spawn(process.execPath, [serverPath, ...args], {
      stdio: "inherit",
      windowsHide: false,
    });
    const forwardSignal = (signal) => {
      if (!child.killed) {
        child.kill(signal);
      }
    };
    const signalHandlers = new Map();
    for (const signal of ["SIGINT", "SIGTERM"]) {
      const handler = () => forwardSignal(signal);
      signalHandlers.set(signal, handler);
      process.on(signal, handler);
    }
    const cleanup = () => {
      for (const [signal, handler] of signalHandlers) {
        process.off(signal, handler);
      }
    };
    child.on("error", (error) => {
      cleanup();
      rejectLaunch(error);
    });
    child.on("exit", (code, signal) => {
      cleanup();
      if (Number.isInteger(code)) {
        resolveLaunch(code);
      } else {
        resolveLaunch(signal === "SIGINT" ? 130 : 143);
      }
    });
  });
}

export async function runUpdater({
  packageRoot,
  args = [],
  request = requestBuffer,
  launch = launchServer,
  token = process.env.GITHUB_TOKEN?.trim() ?? "",
} = {}) {
  const resolvedPackageRoot = resolve(
    packageRoot ?? dirname(fileURLToPath(import.meta.url)),
  );
  let policy;
  let stateInfo;
  let selected;
  try {
    policy = await loadPolicy(resolvedPackageRoot);
    stateInfo = await selectLocalGeneration(resolvedPackageRoot, policy);
    try {
      selected = await performUpdate({
        packageRoot: resolvedPackageRoot,
        policy,
        stateInfo,
        request,
        token,
      });
    } catch (error) {
      warning(error.phase ?? "update", error, stateInfo.generation, token);
      selected = stateInfo.generation;
    }
  } catch (error) {
    const fallback = generationDetails(resolvedPackageRoot, "packaged", "");
    console.warn(
      `[TCFBuild update] WARNING: Update disabled during ${error.phase ?? "startup"}: ` +
        `${sanitizeMessage(error, token)}. Starting packaged.`,
    );
    selected = fallback;
  }

  try {
    return await launch(selected.serverPath, args);
  } catch (error) {
    console.error(
      `[TCFBuild update] ERROR: Local server launch failed: ${sanitizeMessage(error, token)}`,
    );
    return 1;
  }
}

const invokedPath = process.argv[1] ? pathToFileURL(resolve(process.argv[1])).href : "";
if (import.meta.url === invokedPath) {
  process.exitCode = await runUpdater({ args: process.argv.slice(2) });
}
