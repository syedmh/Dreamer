# Task Plan

- [x] Create the app shell, constants, responsive stage, and operator documentation.
- [x] Implement deterministic state transitions and automated state tests.
- [x] Build the original SVG avatar, bubble, walking, and clapping presentation.
- [x] Wire keyboard, focus, resize, and animation-frame behavior.
- [x] Implement and self-test the loopback-only static server.
- [x] Run independent test, code review, security, and end-to-end QA gates.
- [x] Remediate any gate failures and obtain final engineering judgment.

## Implementation evidence

- `npm test`: 22 passed, 0 failed, 0 skipped.
- Loopback smoke: `/` and `/Me.jpg` returned HTTP 200 with `Cache-Control: no-store`.
- Headless Chrome loaded the generated avatar DOM at 1280x720 and 1920x1080.
- `Me.jpg` SHA256 remains `A9D37E52A7ABA927C06046A241D63EE8514C6B95B28508750885275773C98860`.
- Application source scan found no remote asset URLs and no timer-based expiration APIs.

At initial implementation completion, the next mission state was `VALIDATION` and independent validation gates remained pending.

## Rework evidence — August 29, 2026

- Initial independent test and code-review gates identified repeated-key timing, competing mirror/animation transforms, stale chord facing, repeated live-region writes, missing durable integration coverage, and incomplete server privacy coverage.
- Rework made repeat keydown idempotent without losing required Arrow/Space default prevention, moved mirroring to a non-animated wrapper, reconciled facing after chord release, made live-region writes conditional, and separated action deadlines from movement-frame integration.
- Server rework added actual-port loopback Host validation, port-80 Host normalization, an explicit public-file allowlist, and `Cross-Origin-Resource-Policy: same-origin`.
- Durable regression coverage now includes the real default app/renderer path, literal frozen constants, repeat and fresh-press behavior, action presses between movement frames, chord-facing recovery, live-region semantics/mutation stability, production startup, PORT boundaries, loopback binding, Host rejection, port-80 normalization, security headers, and public-asset exposure.
- Maintained test gate command `npm test`: 35 passed, 0 failed, 0 skipped in 331.9979 ms.
- Independent code review verdict: `APPROVED`; no reportable findings.
- Independent security result: `PASS`; 0 Critical, 0 High, 0 Medium, and 0 Low findings after re-audit.
- Independent browser QA result: 13 scenarios passed at 1280x720 and 1920x1080, including no-scroll sizing, 50% avatar height, local portrait and composition, reflection during animation, focus/visibility movement stop, exact repeat/restart behavior, live-region stability, HTTP allowlist/Host checks, zero remote requests, and zero runtime exceptions.
- HTTP smoke returned 200 for `/` and `/Me.jpg`, 404 for protected project files, and 403 for an attacker Host.
- `Me.jpg` remained byte-identical with SHA256 `A9D37E52A7ABA927C06046A241D63EE8514C6B95B28508750885275773C98860`.
- No dependencies were added, no timer-based action expiration or remote application URLs were introduced, and `.ai-org/active-mission.json` was not modified.

Rework validation is complete. Final independent engineering judgment: `APPROVED` on August 29, 2026, after an independent 35/35 test rerun, source and artifact inspection, live HTTP probes, hash verification, and review of the 13/13 browser QA evidence. Mission outcome: `COMPLETED`.

## Maintained-test gap rework — August 29, 2026

- Added real `createAvatarApp` event-wiring coverage for blur, visible/hidden `visibilitychange`, resize remeasurement/reclamping, and pagehide RAF cancellation, listener removal, renderer destruction, and idempotent cleanup.
- Added application-level `Numpad1` coverage proving repeat does not restart clapping and a keyup followed by a fresh physical press does restart it.
- Added serialized actual-entrypoint CLI coverage for the default `127.0.0.1:4173` listener and a valid dynamically selected custom `PORT`, including HTTP 200 verification, bounded child shutdown, and post-exit port reuse.
- Added a cache-busted browser-environment import that executes `src/app.js` top-level auto-bootstrap and cleans up its DOM, RAF, listeners, and temporary globals through `pagehide` and `finally`.
- Final maintained command `npm test`: 38 passed, 0 failed, 0 skipped in 420.1435 ms. Separate file runs passed 22/22 app/state tests and 16/16 server tests; port 4173 had no listener afterward.
- `node --test --experimental-test-coverage` also passed 38/38. Node's built-in report merges the cache-busted `app.js` URL with the ordinary module path and does not instrument the separately spawned CLI child, so its line table still marks those paths uncovered; the named integration tests above directly execute and assert both seams.
- Production source, package dependencies, public exports, and `Me.jpg` were unchanged; `Me.jpg` SHA256 remains `A9D37E52A7ABA927C06046A241D63EE8514C6B95B28508750885275773C98860`. The active mission pointer was not read or modified.

### Focused cleanup rework

- Follow-up review found that the CLI HTTP probe could wait indefinitely before reaching child cleanup if a response never ended. The maintained helper now applies a finite aborting timeout with single settlement, and a regression test stalls a real HTTP response while verifying that the actual `server.mjs` child exits and its port becomes reusable.
- Current maintained command `npm test`: 39 passed, 0 failed, 0 skipped. The timeout regression passed 10/10 focused repetitions, five repeated full-suite runs passed 39/39, and post-run checks found no port 4173 listener or surviving `server.mjs` process.
- Focused independent code re-review verdict: `APPROVED`. Production source, dependencies, `Me.jpg`, and `.ai-org/active-mission.json` remained unchanged.

## Gate-remediation evidence — August 29, 2026

- Corrected the requirements and architecture contract: `Me.jpg` is the private, local source portrait, while `assets/avatar-face.jpg` is the sanitized, metadata-free public derivative used for rendering and serving.
- Added the path-local `TCFMacot/.gitignore` rule `/Me.jpg`. `git check-ignore -v -- TCFMacot/Me.jpg` resolves to `TCFMacot/.gitignore:1:/Me.jpg`, while `git check-ignore -v -- TCFMacot/assets/avatar-face.jpg` exits 1 and confirms the public derivative is not ignored.
- The original private portrait remains byte-identical: `Me.jpg` SHA256 is `A9D37E52A7ABA927C06046A241D63EE8514C6B95B28508750885275773C98860`.
- Maintained gate command `npm test` passed 42 tests, 0 failed, 0 cancelled, 0 skipped, and 0 todo in 526.0686 ms.
- The earlier `/Me.jpg` HTTP 200 statements above are retained as historical evidence but are explicitly superseded. The current privacy contract and maintained server tests require `/Me.jpg` to return HTTP 404; only `/assets/avatar-face.jpg` is publicly served.
- The earlier 22-, 35-, 38-, and 39-test results above are retained as historical snapshots. Any earlier statement describing one of those counts as “current” is explicitly superseded by the maintained 42/42 result recorded in this section.
- This remediation changed documentation and source-control privacy configuration only; application runtime source, behavior, dependencies, timing, controls, and `.ai-org/active-mission.json` were not changed.
