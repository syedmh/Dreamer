# TCF Fundraiser Progress Experience

A dependency-free, event-ready 16:9 visualization that constructs an original red-brick school as fundraising advances from 0% to 100%. From 100% to 125%, students and teachers arrive while a hillside swing set grows into a finished playground. From 125% to 135%, a long conventional yellow school bus facing right arrives in the lower-left, completing the campus at 135%.

A transparent blueprint of the completed school remains visible behind the construction until the fundraising goal is reached, illustrating what incoming donations will complete.

The four `Building*.jpg` files are local-only visual references. The webpage does not load, embed, copy, display, or redistribute them.

## Run

Requires Node.js 18 or newer (verified with Node.js 24).

One Node process starts two loopback-only servers. The display and dashboard share in-memory state and synchronize over same-origin server-sent events.

```cmd
run.bat --display-port=8080 --control-port=8081
```

Open:

- Display: `http://127.0.0.1:8080`
- Control dashboard: `http://127.0.0.1:8081`

The state resets whenever the Node process restarts. Both listeners bind only to `127.0.0.1`; the tool is not exposed to other computers on the network.

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
- `S` — drop the next distant school onto the left hillside, up to four
- `X` — remove the most recently dropped distant school
- `D` — start or pause the 97.2-second demo (125% still occurs at 90 seconds)
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

The distant-school controls use transient `/api/actions` events. Each `S` press fills the next fixed hillside slot until all four are present; `X` removes the most recently filled slot, and adding it again replays its drop animation.

URL parameter `motion=auto|reduce|full` controls the display animation preference. The server is authoritative for live fundraiser and effect state.

Raised amounts are capped at 135% of the current goal across URL configuration, keyboard adjustments, the dashboard slider, demo playback, and the server API. The dashboard and display retain whole-number progress copy while preserving precise amount and ratio calculations internally.

The shared `PATCH /api/state` endpoint validates the merged raised/goal pair atomically. Values above 135% receive HTTP 400 with `{"error":"raised must be no more than 135% of goal."}` and do not change shared state or emit an SSE state update. This intentionally narrows the former accepted range while preserving the existing state and event schemas.

The bus is rendered as one dependency-free SVG group. Its final scene bounds are `x=165..530`, `y=630..775`; full motion starts fully offscreen-left at offset `(-600,+25)` and follows cubic ease-out, while reduced motion keeps the bus at `translate(0 0)` and reveals it using linear opacity only. The reveal remains exact at 0% bus visibility at 125% fundraising, 50% at 130%, and 100% at 135%.

Continuous mode distributes classic radial, ring, star, chrysanthemum, and willow bursts across safe left, center, upper, and right sky regions. It pauses while the page is hidden, resumes when visible, limits active effects, and automatically slows to single bursts when reduced motion is requested.

## Build

Build a validated, runnable distribution in `dist`:

```cmd
build.bat
```

`run.bat` rebuilds and then launches `dist/server.mjs`, forwarding all arguments.

## Automated tests

```powershell
node --test tests/server.test.mjs tests/model.test.mjs tests/currency.test.mjs tests/scene.test.mjs
```

The server tests cover the isolated display/control surfaces, security headers, strict state/action APIs, SSE delivery, host and Origin checks, and port parsing.

The older browser-unit harness contains coverage for the original embedded operator panel and may require migration to a browser-capable test harness. For a lightweight visual smoke check, open the display and dashboard URLs together.

## Architecture

- `index.html`, `styles.css`, `src/app.mjs` — display-only experience and keyboard interactions
- `control.html`, `control.css`, `src/control.mjs` — separate operator dashboard
- `src/config.mjs` — amount limits, defaults, and display URL parsing
- `src/model.mjs` — progress, reveal, and animation math
- `src/scene.mjs` — deterministic school and student geometry
- `src/render.mjs` — original SVG scene construction and rendering
- `server.mjs` — two Node built-in HTTP listeners, shared in-memory state, strict JSON APIs, and SSE

No packages, frameworks, CDNs, network services, CORS, or downloaded assets are used.
