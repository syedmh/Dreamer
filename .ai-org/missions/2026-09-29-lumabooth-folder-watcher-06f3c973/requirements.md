# LumaBooth Folder Watcher Requirements

## Evidence and classification

- `FACT F-01` — `TCFUploader` contains no files and has no tracked implementation as of 2026-09-29. Evidence: filesystem glob and `git ls-files -- TCFUploader` returned no entries.
- `FACT F-02` — The required upstream contract is the two-step Fotoshare PUT then LumaBooth event POST described in the CTO directive; no other authentication or API discovery flow is authorized.
- `ASSUMPTION A-01` — The tool is a single-user Windows console application targeting .NET 8 and Windows 10/11.
- `ASSUMPTION A-02` — All regular files are eligible, regardless of extension or zero length. Unsupported content types use `application/octet-stream`.
- `ASSUMPTION A-03` — Delivery is at-least-once. Exactly-once registration cannot be guaranteed because the event POST contract has no idempotency key or query operation.

## Prioritized requirements

### P0 — Required for a safe usable release

- `REQ-01 Configuration` — The CLI shall require a watch-folder path and a Fotoshare bearer token. The event ID defaults to `-P1Y143qUTagjT1hDguA`. The token shall be accepted only from `LUMABOOTH_FOTOSHARE_TOKEN` or redirected standard input, never from a command-line argument, source file, checked-in configuration, or prompt that echoes input.
- `REQ-02 Startup validation` — Before monitoring starts, the CLI shall validate that the watch folder exists and is readable, the event ID is non-empty, a non-empty token is available, the state directory is writable and outside the watched tree, and no other process owns the same state store. Any failure shall produce a concise diagnostic and a nonzero exit code without starting uploads.
- `REQ-03 Recursive discovery` — The CLI shall recursively discover regular files already present at startup and files subsequently created, renamed, or changed under the watch root. It shall skip directories and Windows reparse points.
- `REQ-04 Reconciliation` — File-system notifications shall be treated as hints. The CLI shall perform a complete recursive startup scan, recover from watcher buffer overflow with a complete rescan, and run a periodic rescan at least every 60 seconds so notification loss does not permanently omit a file.
- `REQ-05 Stable-file gate` — A file shall not be uploaded until its length and UTC last-write timestamp are unchanged across three observations at least two seconds apart and it can be opened for reading. An unstable, locked, missing, or changing file remains pending without consuming an upload retry.
- `REQ-06 Streaming` — File content shall be streamed from disk to the PUT request; the application shall not buffer an entire file in memory and shall impose no undocumented file-size limit.
- `REQ-07 Remote key` — Each newly discovered file version shall receive a unique URL-safe remote key generated locally, retaining only a sanitized extension. The same persisted key shall be reused when retrying or resuming that file version.
- `REQ-08 Binary upload` — Step 1 shall PUT the exact file bytes to `https://w.fotoshare.co/upload_file/{remote-key}` with `Authorization: Bearer <token>` and the detected file `Content-Type`.
- `REQ-09 PUT response` — Step 1 succeeds only for a 2xx response whose JSON contains a non-empty `url`. A relative `url` shall be rooted at `https://fotoshare.s3.us-east-005.backblazeb2.com/`; an absolute URL shall be accepted only when it is HTTPS.
- `REQ-10 Event registration` — Step 2 shall POST `multipart/form-data` to `https://fotoshare.co/api/event/-P1Y143qUTagjT1hDguA/upload` with the same bearer token and `uploadFileField=<resolved uploaded URL>`. It shall send `imgSize=<exact byte length>` and may omit `thumbnailURL`, `imgWidth`, and `imgHeight`.
- `REQ-11 POST response` — A file is complete only when the POST returns a 2xx response and valid JSON containing boolean `success=true`. Any other status, malformed response, missing field, or `success=false` is a failure.
- `REQ-12 Bounded retries` — Each network step shall retry connection failures, timeouts, HTTP 408, HTTP 429, and HTTP 5xx at most five total attempts per processing cycle, using exponential backoff with jitter, honoring a valid `Retry-After`, and capping any delay at 60 seconds. Other HTTP 4xx responses shall not be retried in that cycle.
- `REQ-13 Durable state` — State shall be durably and atomically persisted outside the watched tree for each normalized relative path and file version, including file fingerprint, remote key, completed step, uploaded URL when available, attempt outcome, and timestamps.
- `REQ-14 Restart behavior` — On restart, completed file versions shall not be uploaded again. A version with a completed PUT shall resume at POST using its stored URL. Pending and failed versions shall be eligible for a new bounded processing cycle after passing the stable-file gate.
- `REQ-15 Version identity` — A version fingerprint shall include normalized relative path, byte length, UTC last-write timestamp, and SHA-256 content hash. A changed fingerprint is a new version; unchanged content at the same path is not.
- `REQ-16 State integrity` — Corrupt or unreadable state shall fail startup nonzero with a diagnostic and shall not be silently discarded, reset, or interpreted as an empty store.
- `REQ-17 Source preservation` — The application shall never delete, move, rename, truncate, or modify watched source files.
- `REQ-18 Secret safety` — The token and Authorization header shall never be logged, persisted, included in exception text emitted by the application, or sent to any host other than `w.fotoshare.co` and `fotoshare.co`.
- `REQ-19 Graceful shutdown` — On Ctrl+C, the CLI shall stop accepting new work, persist state, allow the active request up to 30 seconds to finish, then cancel it if necessary and exit without marking incomplete work successful.
- `REQ-20 Operable logging` — Console logs shall include timestamp, severity, relative file path, lifecycle stage, attempt number, and final outcome. Logs shall not contain secrets or full Authorization headers. Failures shall identify whether discovery, stability, PUT, response parsing, POST, or state persistence failed.

### P1 — Reliability and maintainability

- `REQ-21 Work bounds` — Upload processing shall use a bounded queue and a single upload worker by default. Bursts shall not create unbounded tasks or memory growth; a rescan shall recover work not admitted from notifications.
- `REQ-22 Timeouts` — Each HTTP attempt shall have a configurable timeout with a default of 10 minutes. Stability probe interval, periodic rescan interval, shutdown grace period, and retry limits may be configurable, with the defaults in these requirements.
- `REQ-23 Exit semantics` — Normal Ctrl+C termination exits `0`. Invalid startup configuration or state exits nonzero. Individual file failures do not terminate the watcher but are logged and persisted.
- `REQ-24 Windows path handling` — Path comparisons used for containment, deduplication, and state identity shall be case-insensitive and use canonical absolute paths while preserving relative paths for logs.
- `REQ-25 Dependency constraint` — Runtime implementation shall use .NET 8 platform libraries only unless a later approved requirement explicitly justifies a third-party runtime package.
- `REQ-26 Testability` — HTTP endpoints, clock/delay behavior, and filesystem observation shall be separable behind test seams so automated tests can prove retries, response handling, stability, restart, and shutdown without using production credentials.

### P2 — Documentation

- `REQ-27 Operator documentation` — Documentation shall provide Windows PowerShell examples for setting the token for the current process, starting the watcher, stopping it, interpreting exit codes, locating state, and recovering from invalid/corrupt state without exposing the token.
- `REQ-28 Build and publish` — Documentation shall provide .NET 8 restore, build, test, run, and Windows publish commands.

## Acceptance criteria

- `AC-01` GIVEN no token, a missing/non-directory watch path, an unwritable state location, a state location inside the watched tree, corrupt state, or an already-held state lock, WHEN the CLI starts, THEN it exits nonzero before creating a watcher or HTTP request and identifies the invalid setting without printing a token.
- `AC-02` GIVEN valid configuration and an empty folder, WHEN the CLI starts, THEN it remains running, reports that monitoring is active, sends no upload request, and exits `0` after Ctrl+C.
- `AC-03` GIVEN stable files in the root and nested folders before startup, WHEN the CLI starts, THEN each eligible version is processed exactly once in that run and completed versions remain skipped after restart.
- `AC-04` GIVEN a file arrives after startup in a nested folder, WHEN it becomes stable, THEN it is processed without requiring a restart.
- `AC-05` GIVEN a file's length or last-write time changes during stability checks or the file cannot be opened, WHEN probes run, THEN no PUT occurs until three qualifying observations and a successful read-open occur.
- `AC-06` GIVEN watcher notifications are dropped or an overflow is raised, WHEN the next recovery or periodic scan runs, THEN an otherwise stable unrecorded file is discovered and processed.
- `AC-07` GIVEN a file with a known extension, WHEN PUT is sent, THEN the request URL contains its persisted URL-safe remote key, the body bytes equal the source bytes, the content type matches the extension, and the bearer token is present only in the Authorization header.
- `AC-08` GIVEN an unknown extension, WHEN PUT is sent, THEN `Content-Type` is `application/octet-stream`.
- `AC-09` GIVEN PUT returns `{"url":"/bucket/object"}`, WHEN the POST is built, THEN `uploadFileField` is `https://fotoshare.s3.us-east-005.backblazeb2.com/bucket/object`.
- `AC-10` GIVEN PUT returns an absolute HTTPS URL, WHEN the POST is built, THEN that URL is used; GIVEN an absolute non-HTTPS URL, THEN the response is rejected and no POST is sent.
- `AC-11` GIVEN successful PUT response data, WHEN event registration is sent, THEN it is multipart POSTed to the exact event endpoint with the same bearer token, `uploadFileField`, and `imgSize` equal to the source byte length.
- `AC-12` GIVEN POST returns 2xx JSON with boolean `success=true`, WHEN state is committed, THEN the version is marked complete and is not sent again after process restart.
- `AC-13` GIVEN PUT or POST returns non-2xx, malformed JSON, missing required JSON, or POST `success=false`, WHEN processed, THEN the version is not marked complete and the precise stage is logged.
- `AC-14` GIVEN a transient network error, timeout, 408, 429, or 5xx, WHEN a step is processed, THEN no more than five attempts occur in that cycle with bounded backoff; GIVEN another 4xx, THEN exactly one attempt occurs in that cycle.
- `AC-15` GIVEN PUT succeeded and its URL was durably stored but the process stopped before POST, WHEN restarted, THEN the CLI sends no second PUT and resumes POST with the stored URL.
- `AC-16` GIVEN a completed path is replaced or changed so its fingerprint differs, WHEN stable, THEN it is processed as a new version; GIVEN the fingerprint is unchanged, THEN it is skipped.
- `AC-17` GIVEN any upload outcome, WHEN source files are compared before and after processing, THEN names, paths, contents, lengths, and timestamps are unchanged by the application.
- `AC-18` GIVEN logs from successful, retrying, failed, startup-error, and shutdown flows, WHEN searched for the supplied token or `Authorization: Bearer`, THEN neither appears.
- `AC-19` GIVEN Ctrl+C during an active upload, WHEN the request finishes within 30 seconds, THEN state reflects its real result; WHEN it exceeds 30 seconds, THEN it is canceled, remains incomplete, and is resumable next start.
- `AC-20` GIVEN a file larger than the test process memory budget, WHEN uploaded to a test server, THEN peak managed memory does not scale with the full file size and received bytes match the source.
- `AC-21` GIVEN a burst larger than queue capacity, WHEN notifications arrive, THEN memory/task count remains bounded and all stable files are eventually recovered by reconciliation.
- `AC-22` GIVEN mixed-case Windows paths and equivalent path spellings, WHEN deduplicated and checked for containment, THEN they resolve to one identity and cannot place state inside the watched tree.
- `AC-23` GIVEN the repository is restored and built on Windows with .NET 8, WHEN dependency assets are inspected, THEN no third-party runtime package is present.
- `AC-24` GIVEN a mock server implementing the documented contract, WHEN a complete end-to-end test runs, THEN one source file produces one byte-identical PUT followed by one valid multipart POST and durable completed state.
- `AC-25` GIVEN an authorized live smoke-test token, WHEN one uniquely named test file is added, THEN it appears on event `-P1Y143qUTagjT1hDguA`, the CLI records success, and the token is absent from captured logs. The test file is not deleted by the CLI.

## Edge cases

- Empty watch folder; empty file; extensionless or unknown-extension file; nested folders; very large file.
- Slow copy, paused copy, exclusive producer lock, repeated changes, rename into the tree, rename within the tree, disappearance before upload, and replacement at the same path.
- Duplicate/coalesced/missing watcher notifications and watcher buffer overflow.
- Network loss before PUT, during streaming, after PUT response, during POST, and after POST success but before local completion commit.
- 401/403 expired or invalid token, 404 event, 408, 413, 429 with/without `Retry-After`, 5xx, timeout, TLS failure, malformed JSON, and unexpected absolute URL.
- Restart with pending, PUT-complete, complete, failed, corrupt, or locked state.
- Ctrl+C while idle, probing, hashing, PUTing, POSTing, retry-delaying, or persisting state.
- Two instances targeting the same watch root/event; state disk full; state permission revoked.

## Assumptions and known limitation

- `A-04` Remote keys use a cryptographically random identifier plus sanitized extension; the upstream accepts URL-safe opaque keys.
- `A-05` Width, height, and thumbnail are optional as stated and will be omitted; `imgSize` is the source byte length.
- `A-06` Failed files remain in durable state and are retried in a fresh bounded cycle on a later startup or subsequent reconciliation, rather than retrying forever in one run.
- `A-07` The default state root is under `%LOCALAPPDATA%\TCFUploader`, partitioned by watch-root and event identity.
- `KNOWN LIMITATION` If the process or machine fails after the event POST succeeds upstream but before local success is durably committed, restart can repeat the POST. The documented API provides no idempotency key or status lookup to close this ambiguity.

## Out of scope

- `OUT OF SCOPE OOS-01` Discovering, refreshing, exchanging, or storing Fotoshare credentials beyond reading the supplied token.
- `OUT OF SCOPE OOS-02` Browser automation, LumaBooth dashboard scraping, or undocumented API/authentication flows.
- `OUT OF SCOPE OOS-03` Editing, resizing, transcoding, thumbnail generation, metadata extraction, or filtering by media type.
- `OUT OF SCOPE OOS-04` Deleting, moving, archiving, or renaming source files after upload.
- `OUT OF SCOPE OOS-05` A Windows service, GUI, installer, cloud deployment, multi-user server, or remote management API.
- `OUT OF SCOPE OOS-06` Guaranteed exactly-once upstream event registration without upstream idempotency support.
- `OUT OF SCOPE OOS-07` Uploading to arbitrary endpoints or events in the initial release, except test endpoint substitution through internal test seams.

## Open questions

- No blocking CTO decision is required for implementation under these defaults.
- `OPEN QUESTION Q-01 (non-blocking)` — Whether operators ultimately need selectable event IDs. Recommended default: keep the specified event as the default and allow an explicit `--event-id` override only if implementation scope later expands.

