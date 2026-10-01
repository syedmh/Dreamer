TCFBuild for macOS
==================

Requirements
------------
- macOS on an Apple silicon (arm64) or Intel (x86_64) computer.
- No Node.js installation or PATH configuration is required.

Start
-----
Extract the entire ZIP archive, open Terminal, change to the extracted
TCFBuild folder, and run:

  sh ./setup-tcfbuild.command

Optional custom ports:

  sh ./setup-tcfbuild.command --display-port=8080 --control-port=8081

Then open:

  Display:           http://127.0.0.1:8080
  Control dashboard: http://127.0.0.1:8081

Press Ctrl+C in Terminal to stop TCFBuild.

Optional convenience for direct Terminal launch:

  chmod +x setup-tcfbuild.command
  ./setup-tcfbuild.command

The supported launch command is sh ./setup-tcfbuild.command because ZIP files
created on Windows may not preserve the executable permission.

Before each launch, TCFBuild checks the main branch of syedmh/Dreamer for the
latest TCFBuild revision. If a different revision is available, it downloads
and validates all application files as one complete generation before making
that generation active. Embedded runtimes, this launcher, the updater, policy,
and documentation are never replaced by the updater.

If GitHub is offline, rate-limited, unavailable, or returns files that fail
validation, TCFBuild prints a warning and starts the last known-good local
generation. The packaged snapshot remains available as the permanent fallback.

For private-repository access or a higher GitHub API rate limit, launch with an
optional read-only GITHUB_TOKEN scoped only to repository contents:

  GITHUB_TOKEN=YOUR_READ_ONLY_TOKEN sh ./setup-tcfbuild.command

The updater sends the token only to api.github.com and does not print it.

TCFBuild does not install software, require administrator rights, or expose its
servers beyond this computer. Both servers listen only on 127.0.0.1. The
archive includes the official Node.js v22.23.3 runtime for both supported
macOS architectures, so system Node.js is not needed.
