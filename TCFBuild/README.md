# TCF Fundraiser Progress Experience

A dependency-free, event-ready 16:9 visualization that constructs an original red-brick school as fundraising advances from 0% to 100%. From 100% to 125%, students and teachers arrive while a hillside swing set grows into a finished playground.

The four `Building*.jpg` files are local-only visual references. The webpage does not load, embed, copy, display, or redistribute them.

## Run

Requires Node.js 18 or newer (verified with Node.js 24).

```powershell
node server.mjs
```

Open `http://127.0.0.1:8080`.

If port 8080 is already in use, choose another local port:

```powershell
node server.mjs --port=8081
```

Open the port printed by the server.

### Windows command line

Build a validated, runnable distribution in `dist`:

```cmd
build.bat
```

Build and launch the distribution:

```cmd
run.bat
```

Server arguments are forwarded by `run.bat`, so an alternate port can be used:

```cmd
run.bat --port=8081
```

## Configure

URL parameters:

- `raised=50000` — current amount raised
- `goal=100000` — fundraiser goal
- `controls=1` — show operator controls initially
- `demo=1` — start the automatic 0–125% demonstration
- `motion=auto|reduce|full` — animation preference override

Keyboard controls:

- `Space` — show or hide both keyboard legends
- `C` — show or hide the operator controls
- `D` — start or pause the 90-second demo
- `N` — switch between day and night
- `O` — start or stop visible students clapping
- `P` — show or hide student “Thank You” messages after the goal
- `K` — add a kite to the sky
- `L` — remove all kites
- Arrow keys — adjust by 1% of goal
- Shift + Arrow keys — adjust by 5% of goal
- `Home` — set 0%
- `End` — set 100%
- `Q` — launch a firework
- `W` — start or stop continuous randomized fireworks
- `F` — enter or leave fullscreen

Continuous mode distributes classic radial, ring, star, chrysanthemum, and willow bursts across safe left, center, upper, and right sky regions. It pauses while the page is hidden, resumes when visible, limits active effects, and automatically slows to single bursts when reduced motion is requested.

The operator panel includes raised and goal inputs, an amount slider spanning 0 through 125% of the current goal, and an Apply button. Screen-reader announcements occur only for committed operator or keyboard changes; automatic fireworks are decorative and silent.

## Automated tests

```powershell
node --test tests/*.test.mjs
```

The suite verifies frozen configuration, progress math, exact fractional reveal sums, deterministic geometry and limits, omitted facade masks, HTTP behavior, security headers, MIME types, HEAD handling, explicit 404 responses, and traversal prevention.

For a lightweight in-browser module smoke check, open `http://127.0.0.1:8080/tests/harness.html`.

## Architecture

- `src/config.mjs` — defaults and URL parsing
- `src/model.mjs` — progress, reveal, and animation math
- `src/scene.mjs` — deterministic school and student geometry
- `src/render.mjs` — original SVG scene construction and rendering
- `src/app.mjs` — controls and the single animation loop
- `server.mjs` — Node built-in static server bound to localhost

No packages, frameworks, CDNs, network services, or downloaded assets are used.
