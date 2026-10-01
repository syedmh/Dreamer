TCFBuild for Windows
====================

Requirements
------------
- Windows on an AMD64 or ARM64 computer.
- No Node.js installation or PATH configuration is required.

Start
-----
Extract the entire ZIP archive before running it.

Double-click setup-tcfbuild.bat, or open Command Prompt in this folder and run:

  setup-tcfbuild.bat

Optional custom ports:

  setup-tcfbuild.bat --display-port=8080 --control-port=8081

Then open:

  Display:           http://127.0.0.1:8080
  Control dashboard: http://127.0.0.1:8081

Press Ctrl+C in the Command Prompt window to stop TCFBuild.

Before each launch, TCFBuild checks the main branch of syedmh/Dreamer for the
latest TCFBuild revision. If a different revision is available, it downloads
and validates all application files as one complete generation before making
that generation active. Embedded runtimes, this launcher, the updater, policy,
and documentation are never replaced by the updater.

If GitHub is offline, rate-limited, unavailable, or returns files that fail
validation, TCFBuild prints a warning and starts the last known-good local
generation. The packaged snapshot remains available as the permanent fallback.

For private-repository access or a higher GitHub API rate limit, define an
optional GITHUB_TOKEN before launch:

  set GITHUB_TOKEN=YOUR_READ_ONLY_TOKEN
  setup-tcfbuild.bat

Use a read-only token scoped only to repository contents. The updater sends it
only to api.github.com and does not print it.

TCFBuild does not install software, require administrator rights, or expose its
servers beyond this computer. Both servers listen only on 127.0.0.1. The
archive includes the official Node.js v22.23.3 runtime for both supported
Windows architectures, so system Node.js is not needed.
