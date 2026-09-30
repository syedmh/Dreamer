# Architecture

Use semantic HTML, CSS, browser-native ES modules, an original layered SVG avatar, and a single `requestAnimationFrame` loop. `Me.jpg` is retained only as the private, local source portrait; the face is rendered and served exclusively from the sanitized, metadata-free public derivative at `assets/avatar-face.jpg`. Clothing and articulated arms are vector artwork that stays sharp on large displays.

`src/state.js` owns deterministic input, timing, movement, and clamping. `src/render.js` owns DOM/SVG creation and transform-only presentation. `src/app.js` owns browser events, layout measurement, and the animation loop. `server.mjs` uses Node built-ins and binds only to `127.0.0.1`.

The app has no package dependencies. Node's built-in test runner validates state and HTTP behavior.
