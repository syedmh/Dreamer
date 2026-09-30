# TCF Event Avatar Requirements

- Launch directly into one full-screen presentation scene.
- Default to a black background; use `background.jpg` when an operator places one in the app folder.
- Keep `Me.jpg` as the private, local source portrait; render and serve only the sanitized, metadata-free public derivative at `assets/avatar-face.jpg` as the recognizable face of an original avatar wearing a white shalwar qameez and green waistcoat.
- Keep the avatar between 45% and 55% of viewport height at 1280x720 and 1920x1080.
- Move left and right only with the arrow keys, using delta-time movement and viewport bounds.
- Show a comic bubble reading exactly `Welcome to TCF` for three seconds when Space is pressed.
- Start or restart a visible five-second clapping animation with `Digit1` or `Numpad1`.
- Keep movement and the speech bubble usable while clapping.
- Clear held movement keys when browser focus is lost.
- Use no remote assets, analytics, telemetry, cloud service, copyrighted game assets, or runtime dependencies.

## Validation

- State and local-server behavior are covered by automated Node tests.
- The complete stage journey is checked at 720p and 1080p, including focus loss and fullscreen.
- Independent test, review, security, QA, and judgment gates must pass.
