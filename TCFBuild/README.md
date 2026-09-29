# TCF Fundraiser Progress Experience

A dependency-free, event-ready 16:9 visualization that constructs an original red-brick school as fundraising advances from 0% to 100%. From 100% to 125%, two rows of students arrive while a hillside swing set grows into a finished playground. From 125% to 135%, a long conventional yellow school bus facing right arrives in the lower-left, completing the campus at 135%.

A transparent blueprint of the completed school remains visible behind the construction until the fundraising goal is reached, illustrating what incoming donations will complete.

The two student rows alternate the local `Boy.png` and `Girl.png` artwork. These production assets are served only on the display surface and are copied into `dist` by `build.bat`.

The visible summary card reads “Together We Build” and shows dashboard-controlled Seattle Schools and Operation Cost values. The original raised/goal progress remains available on the dashboard and continues to drive the school animation, but its display card is hidden.

The control dashboard includes a live 16:9 preview of the actual display server. The preview resolves the configured display port at runtime, so it also works when custom display and control ports are used.

The dashboard Celebration control combines night mode, continuous fireworks, student thank-you bubbles, and a special five-second TCF firework. Stopping Celebration returns those three ongoing modes to their inactive state.

The dashboard can disable or re-enable all display keyboard shortcuts. Dashboard controls remain active while display keypress handling is disabled.

The first distant-school drop initializes Seattle Schools to 47. When each 6.5-second drop finishes, the shared count increases by one. Removing a completed dropped school decreases the count by one; removing a school before its landing completes cancels its pending increment.

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

The display page intentionally contains no operator form. Use the separate control dashboard for Raised, Goal, quick progress, demo, day/night, clapping, thank-you bubbles, kites, and fireworks.

Display-local keyboard controls:

- `Space` — show or hide the keyboard legend
- `S` — drop the next distant school onto the left hillside, up to seven
- `X` — remove the most recently dropped distant school
- `D` — start or pause the server-driven 144-second demo (125% occurs at 90 seconds, the campus is complete at 97.2 seconds, and 200% occurs at 144 seconds)
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

The distant-school controls use `/api/actions`, but the accepted action first mutates the authoritative `distantSchools` state. Each `S` press fills the next fixed hillside slot until all seven are present; `X` removes the most recently filled slot, and adding it again uses a new generation so every client replays the drop animation. Reconnecting clients resume a pending 6.5-second drop from its elapsed time or render an already completed school immediately.

URL parameter `motion=auto|reduce|full` controls the display animation preference. The server is authoritative for live fundraiser and effect state.

Valid, explicitly supplied `goal` and `raised` URL parameters are applied as a one-time authoritative initialization after the display receives its first server snapshot. The browser sends one absolute state patch containing only the fundraiser parameters that were explicitly supplied, renders the server response, and then follows normal revision ordering. Automatic SSE reconnects do not resend the initialization; reloading the page may initialize once again. A dashboard preview URL with no `goal` or `raised` parameter never initializes fundraiser state.

Raised amounts are capped at 200% of the current goal across URL configuration, keyboard adjustments, the dashboard slider, demo playback, and the server API. The campus remains fully complete at 135%; amounts from 135% through 200% continue to display as additional fundraising progress. The dashboard and display retain whole-number progress copy while preserving precise amount and ratio calculations internally.

The shared `PATCH /api/state` endpoint validates the merged raised/goal pair atomically. Values above 200% receive HTTP 400 with `{"error":"raised must be no more than 200% of goal."}` and do not change shared state or emit an SSE state update.

Relative and toggle operations use strict `POST /api/commands` requests (`raised.add`, `raised.step`, `raised.setRatio`, `state.toggle`, and `celebration.toggle`) so simultaneous clients cannot lose updates. Raised commands clamp to 0–200% and stop demo playback. Clients cannot patch the server-owned `revision` or `distantSchools` fields, and a failed mutation is followed by an authoritative state refetch.

SSE clients identify their role explicitly: the main display uses `role=presentation`, the embedded dashboard preview uses `role=preview`, and the dashboard uses `role=control`. Preview presence does not make one-time display actions ready; `/api/actions` requires a connected presentation display.

Each listener accepts up to eight concurrent SSE clients for each supported role. Additional streams receive HTTP 503 with a short retry hint; disconnected, failed, or persistently backpressured clients are cleaned up so normal presentation, preview, and control usage remains available.

The bus is rendered as one dependency-free SVG group. Its final scene bounds are `x=150..515`, `y=675..820`; full motion starts fully offscreen-left at offset `(-600,+25)` and follows cubic ease-out, while reduced motion keeps the bus at `translate(0 0)` and reveals it using linear opacity only. Once parked, the flat-roofed bus labeled “TCF School Bus” gently rolls forward and backward. The lower-left playground swing seats move independently; both idle animations stop when reduced motion is requested. The reveal remains exact at 0% bus visibility at 125% fundraising, 50% at 130%, and 100% at 135%.

Continuous mode distributes classic radial, ring, star, chrysanthemum, and willow bursts across safe left, center, upper, and right sky regions. It pauses while the page is hidden, resumes when visible, limits active effects, and automatically slows to single bursts when reduced motion is requested.

## Build

Build a validated, runnable distribution in `dist`:

```cmd
build.bat
```

`run.bat` rebuilds and then launches `dist/server.mjs`, forwarding all arguments.

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
