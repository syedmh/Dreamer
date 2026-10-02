# TCF Fundraiser Progress Experience

A dependency-free, event-ready 16:9 visualization with six fundraising milestones: 20% completes the first four block rows and the door; 40% adds the next four rows and swings; 60% adds the next four rows and the school bus; 80% adds the next four rows, slide, and seesaw; 100% completes the school, raises the Pakistan flag, and completes the first student row; and 120% completes the full campus.

## Dashboard stage buttons

The dashboard labels its seven quick-progress presets **Stage 0** through
**Stage 6**. Selecting a stage sets Raised to the corresponding percentage of
the current Goal and stops demo playback. The underlying percentages remain
available in each button's tooltip and accessible label.

| Dashboard button | Goal percentage | Result at that exact stage |
|---|---:|---|
| **Stage 0** | 0% | Resets fundraiser progress. No school blocks, swings, bus, students, flag, or playground enhancements are revealed. |
| **Stage 1** | 20% | Reveals the first 4 block rows and the complete entrance door: 100 of 308 blocks. |
| **Stage 2** | 40% | Reveals the next 4 block rows and the complete swing set: 160 of 308 blocks. |
| **Stage 3** | 60% | Reveals the next 4 block rows and completes the school-bus arrival: 226 of 308 blocks. |
| **Stage 4** | 80% | Reveals the next 4 block rows, slide, and seesaw: 244 of 308 blocks. |
| **Stage 5** | 100% | Completes all 308 school blocks and architectural finishes, raises the Pakistan flag, shows the goal message, triggers the goal celebration, and reveals 12 of 24 students. This starts the enhanced-playground phase. |
| **Stage 6** | 120% | Completes the over-goal campus with all 24 students and all swing/playground enhancements visible. |

Progress is continuous between presets. The swing set reveals from 20% through
40%, the bus enters from 40% through 60%, the slide and seesaw reveal from 60%
through 80%, students appear from 80% through 120%, and the remaining
playground enhancements reveal from 100% through 120%.

A transparent blueprint of the completed school remains visible behind the construction until the fundraising goal is reached, illustrating what incoming donations will complete.

The two student rows alternate the local `Boy.png` and `Girl.png` artwork. These production assets are served only on the display surface and are copied into `dist` by `build.bat`.

The compact summary card reads “Seattle Schools” and presents the dashboard-controlled school count in large type. The dashboard can show or hide the entire card. The original raised/goal progress remains available on the dashboard and continues to drive the school animation, but its display card is hidden.

The control dashboard includes a live 16:9 preview of the actual display server. The preview resolves the configured display port at runtime, so it also works when custom display and control ports are used.

The display starts in a fitted 16:9 layout. Enable **Wide screen / fill monitor** under Display state to expand the scene canvas to an ultrawide or other non-16:9 monitor without stretching or cropping the school and other scene elements. The extended hills include additional trees and playground equipment while the standard 16:9 composition remains unchanged; disable the option to restore the fitted layout. The selection is shared live with every connected display.

The dashboard Celebration control combines night mode, continuous fireworks, student thank-you bubbles, and a special five-second TCF firework. Stopping Celebration returns those three ongoing modes to their inactive state.

Display keyboard shortcuts start disabled. Thank-you bubbles also start hidden. The keyboard-control state and dashboard handler remain implemented, but the individual **Enable keyboard controls** button is currently hidden. Dashboard controls remain active while display keypress handling is disabled.

The dashboard can drop a large live fundraising-total box into the center of the display. It continues a gentle drop-and-bounce motion while visible, with glowing green-and-white bulbs inspired by the Pakistan flag around the full border. Separate dashboard controls can hide or show the box without replaying the entrance.

The center box displays Override Raised Amount. Normal fundraiser progress changes copy Raised into Override so both move together. The **Ultimate Total Override** group can then replace only the center-box total: check **Edit override**, enter the value, and select **Apply Override**. Applying an override does not change Raised, Goal, percentage milestones, or scene progress.

Up to ten distant schools can be dropped. The Seattle Schools counter starts at 47 and increases when each 6.5-second drop finishes, reaching 57 when all schools have landed. Removing a completed school decreases the count; removing one before its landing completes cancels its pending increment.

## Hidden dashboard buttons

The following 13 buttons remain implemented and wired, but currently have the
HTML `hidden` attribute so they do not appear on the operator dashboard:

| Hidden button | Implemented action | Current visible or keyboard route |
|---|---|---|
| **Apply Raised and Goal** | Validates and submits manually edited Raised and Goal values, then stops demo playback. | The hidden submit remains available when the progress form is submitted with Enter; Stage buttons provide the normal quick-progress path. |
| **Start demo / Pause demo** | Starts or pauses the server-driven 0–120% demonstration. | `D`, when display keyboard handling is enabled. |
| **Switch to night / Switch to day** | Toggles the display between daytime and nighttime. | **Start celebration** enables night as part of the combined celebration; `N` toggles it independently when keyboard handling is enabled. |
| **Start clapping / Stop clapping** | Toggles the visible students' clapping animation. | `O`, when display keyboard handling is enabled. |
| **Show thank-you bubbles / Hide thank-you bubbles** | Toggles student thank-you messages after the goal. | **Start celebration** enables them as part of the combined celebration; `P` toggles them independently when keyboard handling is enabled. |
| **Start continuous fireworks / Stop continuous fireworks** | Toggles recurring randomized fireworks. | **Start celebration** enables them as part of the combined celebration; `W` toggles them independently when keyboard handling is enabled. |
| **Show key legend / Hide key legend** | Shows or hides the display keyboard-shortcut legend. | `Space`, when display keyboard handling is enabled. |
| **Enable keyboard controls / Disable keyboard controls** | Enables or disables display-local shortcut processing. | No visible dashboard replacement is currently shown; the shared state and handler remain implemented. |
| **Add kite** | Adds one kite to the display sky. | `K`, when display keyboard handling is enabled. |
| **Clear kites** | Removes every kite from the sky. | `L`, when display keyboard handling is enabled. |
| **Launch firework** | Launches one manual firework. | `Q`, when display keyboard handling is enabled. |
| **Clear fireworks** | Removes all active fireworks. | No visible dashboard replacement is currently shown. |
| **Drop Total Box** | Replays the large center total-box drop animation. | **Show Total Box** controls persistent visibility but does not expose this one-time replay action. |

The **Apply school count** button is not itself marked hidden. Its entire
Seattle Schools form starts hidden and is shown by the visible **Show Seattle
Schools** control.

The four `Building*.jpg` files are local-only visual references. The webpage does not load, embed, copy, display, or redistribute them.

## Run

Requires Node.js 18 or newer (verified with Node.js 24).

One Node process starts two loopback-only servers. The display and dashboard share server-authoritative in-memory state and synchronize over same-origin server-sent events. Every logical state mutation increments the server-owned `revision`, and the server also owns the complete distant-school lifecycle.

```cmd
run.bat --display-port=8080 --control-port=8081
```

Open:

- Display: `http://127.0.0.1:8080`
- Control dashboard: `http://127.0.0.1:8081`

The state resets whenever the Node process restarts. Both listeners bind only to `127.0.0.1`; the tool is not exposed to other computers on the network.

This is a trusted, single-user loopback tool. It intentionally has no authentication or access tokens; do not place either listener behind a network proxy or otherwise expose it beyond the local machine.

You can also run the source directly:

```powershell
node server.mjs --display-port=8080 --control-port=8081
```

Port configuration:

- `--display-port=<port>` or `DISPLAY_PORT` selects the display port.
- `--control-port=<port>` or `CONTROL_PORT` selects the dashboard port.
- Legacy `--port=<port>` and `PORT` remain aliases for the display port.
- Display and control ports must be different integers from 1 through 65535.

If either listener cannot bind, the process closes the other listener and exits with an error.

## Display controls

The display page intentionally contains no operator form. Use the separate control dashboard for Raised, Goal, Stage quick progress, widescreen mode, the combined Celebration mode, Seattle Schools, the total box, and distant-school actions. Additional demo, day/night, clapping, thank-you, kite, firework, and keyboard controls remain wired but are hidden as documented above.

Display-local keyboard controls:

- `Space` — show or hide the keyboard legend
- `S` — drop the next distant school onto the left hillside, up to ten
- `X` — remove the most recently dropped distant school
- `D` — start or pause the server-driven 86.4-second demo, which finishes at the 120% fundraising ceiling
- `N` — switch between day and night
- `O` — start or stop visible students clapping
- `P` — show or hide student “Thank You” messages after the goal
- `K` — add a kite
- `L` — remove all kites
- Arrow keys — adjust by 1% of goal
- Shift + Arrow keys — adjust by 5% of goal
- `Home` — set 0%
- `End` — set 100%
- `Q` — launch a firework
- `W` — start or stop continuous randomized fireworks
- `F` — enter or leave fullscreen

Fullscreen remains local to the display browser and is not remotely controlled.

The distant-school controls use `/api/actions`, but the accepted action first mutates the authoritative `distantSchools` state. Each `S` press fills the next fixed hillside slot until all ten are present; `X` removes the most recently filled slot, and adding it again uses a new generation so every client replays the drop animation. Reconnecting clients resume a pending 6.5-second drop from its elapsed time or render an already completed school immediately.

URL parameter `motion=auto|reduce|full` controls the display animation preference. The server is authoritative for live fundraiser and effect state.

Valid, explicitly supplied `goal` and `raised` URL parameters are applied as a one-time authoritative initialization after the display receives its first server snapshot. The browser sends one absolute state patch containing only the fundraiser parameters that were explicitly supplied, renders the server response, and then follows normal revision ordering. Automatic SSE reconnects do not resend the initialization; reloading the page may initialize once again. A dashboard preview URL with no `goal` or `raised` parameter never initializes fundraiser state.

Raised amounts are capped at 120% of the current goal across URL configuration, keyboard adjustments, dashboard controls, demo playback, and the server API. The campus is fully complete at the 120% ceiling. The dashboard and display retain whole-number progress copy while preserving precise amount and ratio calculations internally.

The shared `PATCH /api/state` endpoint validates the merged raised/goal pair atomically. Values above 120% receive HTTP 400 with `{"error":"raised must be no more than 120% of goal."}` and do not change shared state or emit an SSE state update.

Relative and toggle operations use strict `POST /api/commands` requests (`raised.add`, `raised.step`, `raised.setRatio`, `state.toggle`, and `celebration.toggle`) so simultaneous clients cannot lose updates. Raised commands clamp to 0–120% and stop demo playback. Clients cannot patch the server-owned `revision` or `distantSchools` fields, and a failed mutation is followed by an authoritative state refetch.

SSE clients identify their role explicitly: the main display uses `role=presentation`, the embedded dashboard preview uses `role=preview`, and the dashboard uses `role=control`. Preview presence does not make one-time display actions ready; `/api/actions` requires a connected presentation display.

Each listener accepts up to eight concurrent SSE clients for each supported role. Additional streams receive HTTP 503 with a short retry hint; disconnected, failed, or persistently backpressured clients are cleaned up so normal presentation, preview, and control usage remains available.

The bus is rendered as one dependency-free SVG group. Its final scene bounds are `x=150..515`, `y=675..820`; full motion starts fully offscreen-left at offset `(-600,+25)` and follows cubic ease-out, while reduced motion keeps the bus at `translate(0 0)` and reveals it using linear opacity only. Once parked, the flat-roofed bus labeled “TCF School Bus” gently rolls forward and backward. The lower-left playground swing seats move independently; both idle animations stop when reduced motion is requested. The bus reveal runs from 40% through 60% fundraising.

Continuous mode distributes classic radial, ring, star, chrysanthemum, and willow bursts across safe left, center, upper, and right sky regions. It pauses while the page is hidden, resumes when visible, limits active effects, and automatically slows to single bursts when reduced motion is requested.

## Build

Build a validated, runnable distribution in `dist`:

```cmd
build.bat
```

`run.bat` rebuilds and then launches `dist/server.mjs`, forwarding all arguments.

## Release packaging

Create validated Windows and macOS release ZIP archives under `artifacts`:

```cmd
package-release.bat 1.0.0
```

The version argument is optional and defaults to `1.0.0`. Packaging invokes
`build.bat`, copies the exact 15-file application runtime into
`app/generations/packaged`, adds the immutable updater, generated update policy,
platform-specific launch instructions, and official Node.js v22.23.3 LTS
runtimes, then validates that each archive contains no extra files. The policy
records the latest Git commit affecting `TCFBuild` as the packaged base while
marking the application bytes as a working-tree snapshot. Runtime downloads
are cached under `artifacts\runtime-cache\v22.23.3` and verified against
Node.js's official `SHASUMS256.txt` before every extraction.

After extracting the Windows archive, run:

```cmd
setup-tcfbuild.bat --display-port=8080 --control-port=8081
```

After extracting the macOS archive, open Terminal in the extracted folder and
run:

```sh
sh ./setup-tcfbuild.command --display-port=8080 --control-port=8081
```

No Node.js installation or `PATH` configuration is required. Each archive
bundles Node.js for both supported architectures, and the launcher selects the
correct embedded runtime. The macOS launcher restores executable permission on
its selected runtime, so the `sh` command remains supported even when
Windows-created ZIP archives do not preserve executable permissions.

Before starting the local server, the immutable updater asks GitHub for the
latest commit on `main` that affects `TCFBuild`. An equal commit is a strict
no-op, preserving the packaged working-tree snapshot. A different commit is
downloaded as a complete 15-file generation, checked against Git tree sizes and
blob identities, validated for PNG/text/JavaScript correctness, and activated
only after every file passes. Prior generations remain available for fallback.

Network, authentication, rate-limit, timeout, validation, and filesystem
failures print a sanitized warning and start the last known-good local
generation. Offline startup therefore continues to work. `GITHUB_TOKEN` is
optional for private-repository access or higher API rate limits; use a
read-only token scoped only to repository contents. It is sent only to
`api.github.com`, never to `raw.githubusercontent.com`, and is not logged.

## Automated tests

```powershell
node --test tests\*.test.mjs
```

The tests cover the isolated display/control surfaces, security headers, strict state/command/action APIs, deterministic demo and school scheduling, revision behavior, SSE roles and readiness, client failure reconciliation, non-optimistic actions, and renderer school snapshots.

## Architecture

- `index.html`, `styles.css`, `src/app.mjs` — display-only experience and keyboard interactions
- `control.html`, `control.css`, `src/control.mjs` — separate operator dashboard
- `src/config.mjs` — amount limits, defaults, and display URL parsing
- `src/model.mjs` — progress, reveal, and animation math
- `src/scene.mjs` — deterministic school geometry, student routes, and alternating artwork selection
- `src/render.mjs` — SVG scene construction, including PNG-backed SVG student image elements
- `server.mjs` — two Node built-in HTTP listeners, shared in-memory state, strict JSON APIs, and SSE

No packages, frameworks, CDNs, network services, CORS, or downloaded assets are used.
