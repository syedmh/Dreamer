# Development evidence: secure browser-assisted login

Date: 2026-09-29

## Outcome

Implemented explicit Windows-only `--browser-login` for TCFUploader. Existing environment and
redirected-stdin behavior remains unchanged without the flag. With the flag, state validation and
locking complete before token acquisition; a valid environment token wins, then valid redirected
stdin, and browser login is used only for `token_missing`.

## Security and lifecycle

- Locates Microsoft Edge before Google Chrome, with exact existing-file override through
  `TCFUPLOADER_BROWSER_PATH`.
- Creates a unique hardened profile below `%LOCALAPPDATA%\TCFUploader\browser-auth`.
- Launches InPrivate/Incognito with a unique user-data directory, loopback-only remote debugging,
  an ephemeral Chromium-selected port, and the exact event upload URL.
- Filters CDP targets to the default HTTPS `dash.lumabooth.com` origin and the expected
  `127.0.0.1` WebSocket port.
- Evaluates only the `user-settings` Fotoshare token field, including the documented compatibility
  shape, and applies the existing 16 KiB/control-character validation.
- Cancels browser login on first or immediate Ctrl+C, detects browser closure, times out after
  10 minutes, terminates only the launched process tree, waits, retries exact profile deletion,
  and withholds the token if cleanup cannot be confirmed.
- No token is printed, persisted, logged, included in exceptions, or placed on a process command
  line.

## Verification

- Targeted Release tests: 23 passed, 0 failed, 0 skipped.
- Full Release tests: 111 passed, 0 failed, 0 skipped.
- Release build: succeeded with 0 warnings and 0 errors.
- Final win-x64 framework-dependent publish: succeeded.
- Production package count: 0.
- Production project `<PackageReference>` count: 0.
- Publish files: 5; ZIP files: 5; SHA-256 matches: 5.
- Published help smoke:
  `Usage: TCFUploader --folder <existing-directory> [--browser-login]`

Artifacts:
`TCFUploader/artifacts/browser-login-validation-20260929-final`

No live dashboard authentication or production upload call was executed.

Mission state intentionally remains `IMPLEMENTATION`; tests, security, review, E2E, and judgment
gates remain `paused_by_cto`.
