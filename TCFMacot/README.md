# TCF Event Avatar

A dependency-free, offline presentation scene built from a locally generated, illustrated avatar.
The browser animates exactly five source-registered PNG body layers produced from the pinned
`Avatar.jpg`, with `idle.png` as its fallback; it does not receive the source image or private
build-time manifest at runtime.

## Requirements

- Node.js with `npm`
- Windows PowerShell 5.1 (`powershell.exe`)
- The Windows `System.Drawing` assembly

No `npm install` step is required. The application has no package dependencies and requests no
remote assets, fonts, analytics, or services.

## Launch

Open PowerShell in this folder and run:

```powershell
npm start
```

Open the URL printed by the server, normally `http://127.0.0.1:4173`, and use **F11** if the
presentation should fill the display.

To select another local port:

```powershell
$env:PORT = "5000"
npm start
```

`PORT` must be an integer from 1 through 65535. The server always binds to `127.0.0.1`, accepts
only loopback Host headers for its active port, and serves only an explicit allowlist of runtime
files.

## Generate the avatar assets

The checked-in runtime assets under `assets/avatar` are deterministic derivatives of the pinned
root-level `Avatar.jpg`. Regenerate them after an intentional generator or source update:

```powershell
npm run build:avatar
```

The Node entry point `tools/generate-avatar-assets.mjs` validates the source and invokes the local
PowerShell/System.Drawing compiler. Before generating anything, it requires:

- SHA-256:
  `04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF`
- dimensions: `896x1195`
- JPEG input at `Avatar.jpg`

Generation fails instead of accepting a different image or size. A successful build writes six
full-canvas PNGs—`idle.png` plus five articulated layers—and the private build-time
`assets/avatar/manifest.json`.

The compiler:

- removes only edge-connected near-black background pixels;
- uses alpha at foreground edges and decontaminates the black matte;
- preserves interior dark illustration detail;
- removes the source floor/reflection with a source-space mask while preserving the right-shoe
  contour;
- uses overlapping anatomical masks at shoulders, elbows, wrists, hips, knees, and ankles to
  reduce rotation seams; and
- creates new PNG pixel data rather than copying JPEG metadata.

All layer PNGs remain registered to the original `896x1195` coordinate space. The manifest records
the pinned source identity, canvas and coordinate space, the idle fallback, and each layer's URL,
parent, pivot, z-index, source rectangle, and generated SHA-256.

## Verify generated assets

Verify the pinned source, compiler output, checked-in PNG hashes, layer count, and exact manifest
without rewriting files:

```powershell
npm run build:avatar -- --verify
```

Expected success output:

```text
Verified 6 deterministic avatar PNGs and manifest from 04665D7D9B164B00CA55011C02E6814A318D3B45074BE68402CA0C5507EDA1CF.
```

If a generated PNG is missing or differs from the compiler output, verification reports the file
as missing or stale. If only the manifest differs, it reports:
`assets/avatar/manifest.json is stale; run npm run build:avatar`.

## Runtime and source privacy

The browser receives only the allowlisted runtime HTML, CSS, JavaScript, background image, and
generated avatar PNGs explicitly listed in `server.mjs`.
`assets/avatar/manifest.json` remains local build-time/test metadata, is not in `PUBLIC_FILES`, and requests for it return HTTP `404 Not Found`.
The server does not publish arbitrary repository files or directory listings.

The local source images are not public runtime assets. Requests for each of these paths return
HTTP `404 Not Found`:

- `/Avatar.jpg`
- `/Me.jpg`
- `/assets/avatar-face.jpg`

Do not add source images to the server allowlist. Regeneration is a local build operation; normal
launch and animation use only the generated PNG derivatives.

## Avatar layers and sizing

`src/avatar-rig.js` registers exactly five runtime layers in source space:

- `leftLeg.png` and `rightLeg.png`;
- `torsoHead.png`; and
- `leftArm.png` and `rightArm.png`.

Together with the allowlisted `idle.png` fallback, the public avatar inventory is exactly six
PNGs. `assets/avatar/manifest.json` describes the same five-layer rig for local build and test
work, but remains private build-time metadata and is not served to the browser.

The rig displays at `67vh` with the source aspect ratio (`896 / 1195`), so it scales with viewport
height while retaining its proportions. It sits `2.5vh` above the bottom of typical displays and
`3vh` above the bottom on narrow portrait layouts. The movement bounds are recalculated from the
viewport and rendered avatar width.

Until the five articulated layers are ready, the page displays the generated `idle.png` fallback.

## Controls

- **Left Arrow / Right Arrow** — walk horizontally.
- **Space** — show `Welcome to TCF` for three seconds; press again to restart the duration.
- **1** on the main keyboard or number pad — clap for five seconds; press again to restart.

Movement, clapping, and the welcome bubble can run at the same time. Losing browser focus clears
held movement keys so the avatar cannot remain stuck walking. The presentation also honors the
operating system's reduced-motion preference.

## Optional local background

The default stage is black with subtle generic lighting. To use an event background, put a
browser-readable JPEG named `background.jpg` beside `index.html`, then refresh the browser. Remove
or rename the file and refresh to return to the black stage. The background remains a local,
explicitly allowed runtime file; no code or CSS change is required.

## Validation

Run both deterministic asset verification and the Node test suite:

```powershell
npm run build:avatar -- --verify
npm test
```

The tests cover generator validation and failure paths, layer registration and loading, movement
and timed actions, browser event/render integration, responsive layout behavior, optional
background handling, and the loopback server. Server coverage includes Host validation, the
public-file allowlist, source-image `404` responses, HTTP methods, MIME types, missing files,
directory-listing denial, and traversal protection.

## Troubleshooting

- **The page does not open:** keep `npm start` running and use the exact loopback URL printed by the
  server.
- **The port is rejected:** use an integer from 1 through 65535. Invalid values report
  `PORT must be an integer from 1 through 65535`.
- **The port is already in use:** set `$env:PORT` to another valid port and relaunch.
- **Avatar generation reports a SHA-256 or dimensions mismatch:** restore the pinned `Avatar.jpg`.
  Do not regenerate derivatives from a different or edited source unless the generator's pinned
  source contract is intentionally updated with the implementation.
- **Avatar generation reports a System.Drawing compiler failure:** run on Windows PowerShell with
  the Windows `System.Drawing` assembly available.
- **A generated asset is missing or stale:** run `npm run build:avatar`, then rerun
  `npm run build:avatar -- --verify`.
- **The avatar stays on its idle image:** regenerate and verify `assets/avatar`, then reload the
  page; the idle PNG is the fallback used when the five-layer rig cannot become ready.
- **Keys do not respond:** click once on the presentation page, then try the controls again.
- **The background remains black:** confirm `background.jpg` is beside `index.html`, is a valid
  browser-readable JPEG, and refresh or force-refresh the page.
- **Animation is restrained:** the app honors the operating system's reduced-motion preference.
