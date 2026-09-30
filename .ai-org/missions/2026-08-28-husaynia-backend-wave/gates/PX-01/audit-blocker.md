# PX-01 NuGet audit blocker

Date: 2026-08-28

## Result

**BLOCKED_EXTERNAL — fail-closed audit metadata access remains unavailable.**

`dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive` exited `1` during
restore with 11 unsuppressed `NU1900` errors. Every error reported that the vulnerability service
index at `https://api.nuget.org/v3/index.json` could not be loaded.

## Network diagnosis

- DNS resolved `api.nuget.org` to a public address.
- TCP port 443 connectivity succeeded.
- `curl.exe` failed Schannel negotiation with `SEC_E_ILLEGAL_MESSAGE`.
- `Invoke-WebRequest` failed with TLS alert `HandshakeFailure`.
- The only enabled NuGet source is the canonical
  `https://api.nuget.org/v3/index.json`.

This is a TLS/service-path failure rather than a package, source-configuration, DNS, or TCP defect.
No audit suppression, failed-source ignore, alternate registry, package guess, manifest/lock edit,
deployment, migration, commit, or push was performed.

## Consequence

PX-01 remains blocked. T05 Content, T06 Calendar, T08 Media, T12 Donations, and WG-01 must not
start until current vulnerability metadata is reachable and the complete select/edit/lock/audit/
proof gate passes.
