# Husaynia deterministic baseline capture

This .NET 10 tool captures public, read-only evidence from the fixed trust boundary
`https://www.husaynia.org:443`. It never submits forms, follows payment/checkout actions, passes
credentials, or mutates the live site.

## Browser and least privilege

- Microsoft.Playwright is pinned exactly to `1.62.0`; package-matched Chromium is revision `1234`,
  version `151.0.7922.34`.
- The Windows x64 executable is SHA-256 checked against
  `chromium-executable-sha256.json` before launch.
- Run from a non-administrative account. `ChromiumSandbox=true` is mandatory.
- The browser cache needs read/execute access only. Chromium starts from an empty directory beneath
  the operating-system temp location and receives only allowlisted OS/runtime/temp environment
  variables.
- Preflight, elevation, executable identity, version, and SHA-256 checks complete before
  `Playwright.CreateAsync`. The Playwright Node driver and Chromium are then started inside one
  serialized process-wide sanitized environment and explicit temp working directory.
- Chromium does not receive repository/workspace paths, proxies, credentials, cookies, storage
  state, permissions, client certificates, secrets, or extra headers. Screenshot bytes return to
  .NET, which alone writes the run staging directory.
- Retained provenance records only stable browser identity/hash values plus structural isolation
  attestations. Absolute browser-cache, workspace, user-profile, and temporary-directory paths are
  never serialized.
- The primary-origin DNS answer set remains immutable for the run. Immediately before every fresh
  context, approved static hosts are resolved into an immutable public-address epoch; all-public CDN
  rotation is allowed, the ordinal-first address is pinned, and a changed pin forces a newly
  hash-verified sandboxed browser before context creation. Revalidation cannot alter an active pin,
  TLS still validates the hostname, and final `MAP * ~NOTFOUND` denies every unmapped hostname.
- Fresh contexts have no imported storage state and are checked for zero cookies before navigation,
  before screenshot, and after screenshot. `document.cookie`, Cookie Store `get()`, and `getAll()`
  may only return empty results and are terminally logged as `cookie-read-empty-context`; cookie
  writes/deletes, outbound Cookie headers, and any nonzero checkpoint fail closed.
- WebSocket, EventSource, sendBeacon, service workers, WebRTC, WebTransport, payment requests,
  downloads, popups, and form submission are disabled and ledgered. Persisted response headers use
  an explicit safe allowlist; query values are never retained.

## Required flow

The whole-capture deadline defaults to 60 minutes and accepts
`--max-duration-minutes 1..90`. This global bound includes the sequential 36-row screenshot matrix,
crawl, assets, and final capture work; ADR-009's per-attempt readiness/capture bounds remain 30/45
seconds with at most three attempts.

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium

dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --max-duration-minutes 60 --output evidence/baseline-runs/<run-a>
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- recapture-screenshots --no-submit --evidence evidence/baseline-runs/<run-a> --output evidence/baseline-runs/<run-b>
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- compare-screenshot-metrics --first-screenshots evidence/baseline-runs/<run-a>/screenshots.json --first-provenance evidence/baseline-runs/<run-a>/screenshot-capture-provenance.json --second-evidence evidence/baseline-runs/<run-b> --output evidence/baseline-runs/<run-a>/screenshot-determinism.json
```

Exit `3` means independent hash-bound visual review is required. Capture or comparison quality
failures exit `1`; trust, usage, non-empty-output, or lock refusals exit `2`; restore failure exits
`4` and prints recovery paths.

Retained sitemap membership is derived only from exact canonical `<urlset><url><loc>` values whose
raw XML is bound to successful HTTP evidence by path, length, and SHA-256; sitemap-index locations
do not count. Visual-review JSON is parsed case-sensitively with exactly ten entry properties and
strict recursive mask validation. Masks supplement—but never replace—the unmasked diff, quality
gate, or independent hash-bound acceptance.

Browser startup, caller cancellation, and mid-matrix failures retain one unique diagnostic row for
each of the 36 approved keys, preserve completed rows and collected request decisions, record the
failure stage/reason, and exit nonzero.

Capture summaries keep the established `routeCount` meaning (discovered public URLs) and frozen
route-manifest semantics. Additive diagnostics label `discoveredUrlCount`, `manifestPathCount`, and
`queryEndpointExcludedByFrozenSchemaCount`; the expected count delta is only query endpoints such
as `/events/?ical=1`, which cannot enter the query-free frozen route schema. Failed browser attempts
dispose the page and context before snapshotting decisions, retain real browser terminals when they
arrive, and synthesize only still-pending allowed requests as
`attempt-aborted-before-browser-terminal` / `capture-attempt-aborted`.

Route socket failures are retained per URL as stable `socket-<SocketErrorCode>` diagnostics so
successful sibling routes remain available. Summary `failureAffectedUrl` values redact every query
value, `failureDetailReason` contains only a stable reason code, and DNS-set changes still refuse
the capture rather than being converted into route failures.

Only after independent test, security, code, and any visual review may a reviewer seal and promote:

```powershell
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- verify --evidence evidence/baseline-runs/<run-a>
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- promote --approved --from evidence/baseline-runs/<run-a> --to evidence/baseline
```

Promotion verifies the sealed source, copies to a sibling candidate, verifies again, swaps under an
exclusive lock, post-verifies, and restores the exact prior baseline on failure. Implementation
work must not promote or edit `evidence/baseline`.

Same-process failures through `AfterPostVerify` roll back from in-memory source/prior manifests and
stable file identities; the diagnostic journal is never reread to authorize rollback. After a
process restart, any sibling `transaction`, journal temporary, `prior`, `candidate`, `failed`, or
`failed-prior` residue is ambiguous: the tool acquires the lock, reports logical sibling names,
leaves every artifact byte-identical, and exits `4` for manual recovery.
On Windows, a case-equivalent CLI destination is normalized to the repository-cased
`evidence/baseline` path before the lease opens, and every residue prefix plus the temporary-journal
suffix is matched case-insensitively so casing cannot bypass refusal.

Candidate and journal create operations own their new artifact until they return its stable
identity. Copy, write, flush, or cancellation failures self-clean through the pinned handle; if
cleanup fails or its result is ambiguous, the artifact is preserved, the logical sibling name is
reported, and promotion exits `4`. A later invocation treats that name as cross-process residue and
refuses without mutation.

The journal is an unsigned, create-new, flushed operator diagnostic only. Promotion creates no
DPAPI/HMAC key, replay marker, or Local Application Data signing store. Existing legacy signed
journals are untrusted residue and are preserved without parsing.

Windows promotion opens only the volume root by absolute path, then walks every source and
destination-parent segment with relative `NtCreateFile` no-follow opens. Each opened component is
checked by handle for identity, owner, effective ACL, and unsafe inheritable/inherit-only ACEs;
all sibling creates, opens, renames, snapshots, and cleanup remain relative to the pinned handles.
Candidate trees, lock/journal files, and other newly created transaction artifacts receive
protected current-user/SYSTEM/Administrators ACLs in the create call, before they are visible.
Existing destination/prior ACLs are validated but never rewritten.

Reparse points, ancestor replacement, authority or identity changes, unsafe hard links, and trees
deeper than 64 directories fail closed. A rename is considered attempted before the kernel call;
post-rename exceptions are reconciled from pinned identities, with ambiguous state preserved for
manual recovery at exit `4`. Recovery diagnostics escape control/delimiter characters and cap
reported logical names. The crash-only test harness additionally requires its sentinel plus a
nonce-bound, unique operating-system-temp root and refuses paths outside that root. Unsupported
operating systems refuse promotion unless an equivalent handle-relative implementation is
provided. `AfterPostVerify` remains pre-commit. Durable commit is marked only after that fault point
returns and the final cancellation check succeeds, and prior deletion starts only after durable
commit.

After transaction state is settled and the promotion lease is released, the tool best-effort emits
exactly one bounded JSON line to standard output for exit `0` or standard error for exits `1`, `2`,
and `4`. The `husaynia-promotion-exit` event contains `phase`, stable `reason`, numeric `exit`,
logical `destination`, and bounded escaped `priors`, `candidates`, `failed`, and `journals` arrays.
It never contains absolute paths. Writer or serialization failure is ignored and cannot block
rollback/cleanup, alter the returned exit, or undo a verified committed baseline.
