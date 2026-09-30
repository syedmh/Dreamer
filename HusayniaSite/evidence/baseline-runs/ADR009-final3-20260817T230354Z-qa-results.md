# QA RESULT

Environment: Windows 10 Enterprise build 26200 x64, non-administrative
`REDMOND\syedhu`, .NET SDK `10.0.400`, repository root
`C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`.

## Scenario 1: Official preflight and pinned Release binary [PASS]

Steps:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
```

The Release DLL/PDB pair was then checked with
`System.Reflection.Metadata.MetadataReaderProvider` and
`System.Reflection.PortableExecutable.PEReader`; every PDB document checksum was recomputed from
the current source, and the DLL CodeView GUID was compared with the portable-PDB ID GUID.

Expected: Record the unsuppressed official restore/build result. If external NU1900 prevents a
fresh build, use the existing Release binary only if its PDB proves that it matches every current
source document. Install and verify the exact pinned browser.

Actual:

- Restore exit `1`, `7.355` seconds: only `NU1900`, because the vulnerability service index at
  `https://api.nuget.org/v3/index.json` was unavailable.
- Strict build exit `1`, `0.760` seconds: only the same warning-as-error `NU1900`.
- Browser install exit `0`, `1.883` seconds.
- Playwright requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`.
- Chromium revision/version `1234` / `151.0.7922.34`.
- Installed Chromium SHA-256
  `409805A16D6416087E6B2F778DF1CF8F7BBB267D6B99F6B5BB0A618EACE234F2`.
- Release DLL SHA-256
  `E92CE5FA1BCC7464B08B7A12137857C60444A6CB67E22053636B62BDE3F843DA`.
- Release PDB SHA-256
  `865A058967A9EF5E19F771E0B0006DD1C87F0E90E0E338876D2F934B73ECE95E`.
- PDB result: `28/28` current-source documents matched, with `0` mismatches, `0` missing,
  and `0` unknown algorithms.
- DLL/PDB identity matched:
  `4275d676-185d-48d7-9dad-28005d41f749`.

Evidence: project hash
`48356DF325C6A69C4B529584DD1C131C1992F08764787A375AAA2C933F75D580`;
tool lock hash
`9D737D7BFB7FFE174866AC35FFB42A3FC2F02F5BCD77ACC571B8142E265575F3`;
browser profile hash
`BA0BCBA67ACF062411321438E1FE15022B86C1FA04F88E700AE0053289A521F1`.

## Scenario 2: Approved-host DNS preflight [PASS]

Steps: Resolve each approved host with a five-second bound, wait two seconds, and resolve every
host again.

Expected: Every answer is public and each complete set is unchanged before capture.

Actual:

| Host | Round 1 | Round 2 |
|---|---|---|
| `www.husaynia.org` | `104.21.76.77,172.67.191.103` | unchanged |
| `fonts.googleapis.com` | `74.125.135.95` | unchanged |
| `fonts.gstatic.com` | `142.250.73.99` | unchanged |
| `cdnjs.cloudflare.com` | `104.17.24.14,104.17.25.14` | unchanged |
| `cdn.jsdelivr.net` | `104.17.207.5,104.17.208.5` | unchanged |

Evidence: all ten resolutions returned successfully in `0-36 ms`.

## Scenario 3: Final documented 60-minute Run A [FAIL]

Steps:

```powershell
tools/Husaynia.BaselineCapture/bin/Release/net10.0/Husaynia.BaselineCapture.exe capture `
  --no-submit `
  --base-url https://www.husaynia.org:443/ `
  --max-duration-minutes 60 `
  --output evidence/baseline-runs/ADR009-final3-20260817T230354Z-run-a
```

Expected: Exit `0` with `36/36` captured and `36/36` quality-pass, within the 60-minute global
bound.

Actual:

- Process exit `1` after `1397.656` seconds.
- Capture summary duration `1397.296` seconds, from
  `2026-08-17T23:04:07.9739074Z` through `2026-08-17T23:27:25.2697018Z`.
- Status `failed`; stage `route-capture`; reason `capture-error:socketexception`.
- Console reported all 36 screenshot keys failed, then `159` public URLs and `3` sitemaps
  discovered, followed by `command-failed:SocketException`.

Evidence: `capture-summary.json`, `README.md`, and the command output.

## Scenario 4: Run A diagnostics, counts, checksums, and visuals [FAIL]

Steps: Parse all Run A JSON, verify unique screenshot keys and route counts, inspect network
decisions and overflow fields, recompute every SHA-256 entry, check gated artifacts, and look for
fresh representative PNGs.

Expected: `36` unique captured/pass rows, coherent route/assets/screenshots evidence, complete
network terminals, no overflow, valid checksums, and fresh representative PNGs. Before verify,
there must be no seal.

Actual:

- Screenshot rows: `36`; unique keys: `36`; duplicates: `0`.
- Six templates and six viewports are each represented exactly six times.
- Status: `36 failed`, `0 captured`; quality: `36 fail`, `0 pass`.
- Errors: `33 dns-set-changed`, `3 navigation-timeout`.
- The three timeout rows used three attempts each; the remaining 33 rows stopped on their first
  DNS check.
- Ready rows `0`; PNG rows/files `0`; true-overflow rows `0`. Overflow is therefore unassessed,
  not proven absent.
- Screenshot network ledger: `9` unique requests across three capture keys, all safe primary-origin
  `GET` allows. It contains `9` request events but `0` response/failure terminal events.
- Summary routes `159`; `route-manifest.json` contains `158` unique route IDs/legacy paths.
- Successful route observations `0`; `http-inventory.json` is empty.
- Assets discovered/retained `0/0`; metadata `0`; forms `0`; religious fixtures `0`; dynamic
  regions `0`.
- Retained raw diagnostics: one `robots.txt` and three sitemap files.
- Run tree: `20` files. `checksums.sha256` has `19` entries and covers every other retained file;
  all recomputed hashes passed, with `0` missing, mismatched, or unlisted files.
- `screenshot-determinism.json`, `screenshot-visual-review.json`, and
  `baseline-verification.json` are absent as required for an unsuccessful candidate.
- Fresh home desktop/mobile, donation, event tablet, and religious-content PNGs do not exist, so
  pixel, loading, sensitive-content, and overflow inspection could not be performed.

Post-run DNS evidence:

- `fonts.googleapis.com` rotated from pinned `74.125.135.95` to `173.194.203.95`.
- `fonts.gstatic.com` rotated from pinned `142.250.73.99` to `142.251.46.67`.
- Primary origin, Cloudflare, and jsDelivr sets remained unchanged.

This confirms the screenshot path failed closed on approved-host DNS rotation. No TLS failure was
reported. The later route-capture `SocketException` is not attributed to a specific URL in the
retained inventory.

Evidence: `screenshots.json`, `screenshot-network-decisions.json`,
`screenshot-capture-provenance.json`, `route-manifest.json`, `residual-risks.json`,
`checksums.sha256`, and empty `screenshots/` and `assets/` directories.

## Scenario 5: Conditional Run B/review/verify gate [PASS]

Steps:

```text
predicate = Run A status complete
            AND captured rows == 36
            AND quality-pass rows == 36
```

Expected: Continue only when the predicate is true. Never promote.

Actual: Predicate was false: status `failed`, captured `0`, quality-pass `0`.
Run B, comparison, visual review, and verify/seal were not run. The reserved Run B path does not
exist. Promotion was not invoked.

Evidence:

```text
RUN_A_GATE status=failed captured=0 qualityPass=0 predicate=False
RUN_B_EXISTS=False
DETERMINISM_EXISTS=False
VISUAL_REVIEW_EXISTS=False
SEAL_EXISTS=False
```

## Scenario 6: Protected state and no promotion [PASS]

Steps: Recheck source/test/config timestamps, protected hashes, approved baseline writes, active
mission metadata, and promotion residue after the failed run.

Expected: Only new final3 run-specific evidence changes; baseline, source, tests, configuration,
active mission, and Git state remain untouched; no promotion residue exists.

Actual:

- Source writes after QA start: `0`.
- Test writes after QA start: `0`.
- Contract/config writes after QA start: `0`.
- Approved baseline writes after QA start: `0`.
- Active mission writes after QA start: `0`.
- Promotion residue count: `0`.
- `evidence/baseline/checksums.sha256` remained
  `F2D185FF4E18C859A9A5F14A78CE7765BEE8935F56B129F002C8EA410D8ABF06`.
- `.ai-org/active-mission.json` remained
  `1E77273D12648E2C1715D457BE9F7BE1CA2B0FBA4F663F5A21C2DA2377B63C9C`.

Evidence: post-run hash/timestamp and residue checks.

Scenarios: 6 run, 4 passed, 2 failed

Conclusion: **FAIL**

---

STATUS: FAIL

SUMMARY: Final documented Run A was executed with the exact 60-minute bound and fixed origin. It
failed closed after approved static-host DNS rotation and later ended in route capture with a
`SocketException`. The candidate has `0/36` captured and `0/36` quality-pass and is not sealed.
Run B/review/verify were correctly not run. Promotion was never invoked.

WORK_COMPLETED:

- Read the current Release README, architecture section 17, ADR-009, duration amendment, and current
  baseline README.
- Recorded the official restore/build results and exact package/browser/binary identities.
- Proved the existing Release DLL/PDB pair matches all current source documents.
- Installed and hashed the pinned browser.
- Ran two stable preflight DNS rounds.
- Executed one new exact 60-minute Run A.
- Inspected complete diagnostic artifacts, all 36 unique rows, route/asset/PNG counts, network
  reasons, overflow state, status, checksums, absent seal, and downstream predicate.
- Confirmed no protected-state write and no promotion residue.

EVIDENCE: The six scenarios above.

ARTIFACTS:

- `evidence/baseline-runs/ADR009-final3-20260817T230354Z-run-a/`
- `evidence/baseline-runs/ADR009-final3-20260817T230354Z-qa-results.md`
- Run B: not created
- Visual review: not created
- Seal: not created

FINDINGS:

- Legitimate Google Fonts DNS rotation caused the intended fail-closed
  `dns-set-changed` result for 33 matrix rows.
- The three earlier navigation-timeout attempts retained nine request decisions but no corresponding
  response/failure terminal events, contrary to ADR-009's one-terminal-per-request evidence
  contract.
- `capture-summary.json` reports 159 routes while `route-manifest.json` retains 158 unique routes.
- The terminal route-capture `SocketException` is not associated with a URL or lower-level reason
  in retained evidence, limiting operator diagnosis.
- No fresh PNG existed, so visual quality and overflow could not be assessed.

RISKS:

- No fresh live visual, route-response, asset, metadata, form, or religious-content evidence is
  available.
- DNS-sensitive approval runs can fail during normal CDN rotation; weakening this safety check is
  not acceptable.
- NuGet vulnerability audit remains externally unavailable through NU1900.

BLOCKERS:

- Run A did not exit `0` and has `0/36` captured and `0/36` quality-pass.
- Approved-host DNS changed during capture.
- Route capture ended with `capture-error:socketexception`.

NEXT_ACTION: Retain this immutable diagnostic run. Diagnose the missing network terminal evidence,
route-count mismatch, and unlocalized route `SocketException`. After DNS is stable, execute a new
UTC Run A/Run B pair from a new empty path. Do not reuse, seal, verify, or promote this failed run.
