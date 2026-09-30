# QA RESULT

Environment: Windows 10.0.26200, non-administrative `REDMOND\syedhu`, .NET SDK 10.0.400.
The unsuppressed locked restore and strict no-restore build were attempted once. Both were externally
blocked by NU1900, so the current Release DLL/PDB pair was used only after all 28 portable-PDB source
checksums matched the current source.

## Scenario 1: Pin, restore, build, and browser identity [PASS]

Steps:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode --force-evaluate
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore -warnaserror
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
```

Expected: Attempt the official unsuppressed restore; build current source if possible; otherwise use
only a current-source/PDB-matched Release binary after reporting NU1900. Verify the exact browser pin.

Actual:

- Restore exit `1`, 7.496 seconds: only `NU1900`, vulnerability service index unavailable at
  `https://api.nuget.org/v3/index.json`.
- Build exit `1`, 0.691 seconds: only the same `NU1900`.
- Browser install exit `0`, 2.079 seconds.
- Playwright requested/resolved `[1.62.0, 1.62.0]` / `1.62.0`.
- Chromium revision `1234`, version `151.0.7922.34`.
- Chromium SHA-256
  `409805A16D6416087E6B2F778DF1CF8F7BBB267D6B99F6B5BB0A618EACE234F2`.
- Release DLL SHA-256
  `4EFEB751DA4596943F7AF18BE19A70A9154BF6D6166F9932C4785A1011FDE0BC`.
- Release PDB SHA-256
  `41935AAF77E606C699BE17F6655C240C142DA4EA272BF54C1B9DEEBA56B76113`.
- Portable PDB verification: `28/28` current source documents matched, `0` mismatches, `0` missing.

Evidence: restore/build output above; current project and lock hashes remained
`48356DF325C6A69C4B529584DD1C131C1992F08764787A375AAA2C933F75D580` and
`9D737D7BFB7FFE174866AC35FFB42A3FC2F02F5BCD77ACC571B8142E265575F3`.
The mandated `--force-evaluate` restore refreshed `packages.lock.json` file metadata at
`2026-08-17T21:11:20.5740073Z`, but its content hash remained exactly the approved pin.

## Scenario 2: Approved-host DNS stability probe [PASS]

Steps: Resolve each approved host with a five-second bound, wait two seconds, and resolve again.

Expected: Public answers are available and each complete answer set is unchanged.

Actual:

| Host | Round 1 | Round 2 | Result |
|---|---|---|---|
| `www.husaynia.org` | `104.21.76.77,172.67.191.103` | same | unchanged |
| `fonts.googleapis.com` | `74.125.135.95` | same | unchanged |
| `fonts.gstatic.com` | `142.251.46.67` | same | unchanged |
| `cdnjs.cloudflare.com` | `104.17.24.14,104.17.25.14` | same | unchanged |
| `cdn.jsdelivr.net` | `104.17.207.5,104.17.208.5` | same | unchanged |

Evidence: all ten probes returned `ok`; individual durations were 1-60 ms.

## Scenario 3: Clean bounded Run A capture [FAIL]

Steps:

```powershell
tools/Husaynia.BaselineCapture/bin/Release/net10.0/Husaynia.BaselineCapture.exe capture `
  --no-submit `
  --base-url https://www.husaynia.org:443/ `
  --max-duration-minutes 45 `
  --output evidence/baseline-runs/ADR009-final2-20260817T211328Z-run-a
```

Expected: One clean full capture runs for at most 45 minutes, retaining either a complete passing
36-row matrix or a complete failed 36-row diagnostic matrix.

Actual: Exit `2` in 1.031 seconds:

```text
Safety refusal: capture-option-out-of-range
```

Evidence: the CLI accepts `--max-duration-minutes` only through 25
(`Program.cs:107,315-318`), so it rejected the mandated value before creating tool evidence or
making any HTTP request.

## Scenario 4: Run A artifact completeness and visual inspection [FAIL]

Steps: Inspect the Run A path for summary, provenance, network decisions, screenshots, PNGs,
checksums, determinism, visual review, and verification seal.

Expected: A failed capture still retains exactly 36 unique diagnostic rows, complete diagnostics,
checksums, and no seal. Inspect fresh home desktop/mobile, donation, event tablet, and religious
content PNGs.

Actual:

- CLI-generated screenshot rows: `0`; required unique rows: `36`.
- Fresh PNGs: `0`.
- `capture-summary.json`, `screenshots.json`, network decisions, provenance, and checksums: absent.
- `baseline-verification.json`: absent.
- No fresh PNGs existed, so blank/loading/overflow/sensitive visual inspection could not be done.

Evidence: only this QA harness report was added after the refused command; it is not a CLI capture
artifact or verification seal.

## Scenario 5: Conditional downstream gate [PASS]

Steps: Evaluate the Run A predicate before Run B, comparison/review, and verify/seal.

Expected: Continue only when Run A exits `0` with `36/36` captured and `36/36` quality-pass.

Actual: Run A exited `2` with no matrix. Run B, comparison, visual-review JSON, verify/seal, and
promotion were not run. The reserved Run B path
`evidence/baseline-runs/ADR009-final2-20260817T211328Z-run-b` was not created.

Evidence: existing `evidence/baseline` had zero writes after QA start; no promotion transaction
residue was present. `src`, `contracts`, and `.ai-org/active-mission.json` also had zero writes.

Scenarios: 5 run, 3 passed, 2 failed

Conclusion: **FAIL**

---

STATUS: FAIL

SUMMARY: The exact mandated Run A command failed closed before capture because the current CLI
rejects `--max-duration-minutes 45`. No candidate was sealed. Downstream work was correctly stopped,
and no promotion occurred.

WORK_COMPLETED:

- Read the current tool README, architecture sections 16-17 and its binding amendment, ADR-009,
  and the T01 plan. The current architecture file has no section 18.
- Recorded package, browser, executable, source, DLL, PDB, and protected-input hashes.
- Attempted one unsuppressed locked restore and one strict current-source build.
- Installed and verified the pinned browser.
- Ran two short DNS-set probes.
- Invoked the exact clean Run A command once.
- Inspected artifact absence and enforced the Run A downstream predicate.

EVIDENCE: The five scenarios above.

ARTIFACTS:

- `evidence/baseline-runs/ADR009-final2-20260817T211328Z-run-a/qa-results.md`
- Run B: not created
- Visual review JSON: not created
- Seal: not created

FINDINGS:

- The CLI contract required by this QA run and the implementation disagree: the command-line parser
  caps whole-capture duration at 25 minutes, while this run required 45.
- The safety error is stable but not actionable: it does not state the accepted range.
- The unsuppressed force-evaluate restore touched `packages.lock.json` metadata despite leaving its
  bytes unchanged; no source, test, package, audit, TLS, or configuration content changed.
- No fresh visual evidence exists; event-tablet overflow and other visual quality predicates remain
  unassessed.

RISKS:

- There is no fresh live-site evidence for route, asset, network, provenance, visual quality, or
  determinism behavior.
- NuGet vulnerability audit remains externally unavailable through NU1900.

BLOCKERS:

- `--max-duration-minutes 45` is rejected before diagnostic capture.
- Official unsuppressed restore/build remains blocked by NU1900.

NEXT_ACTION: Align the CLI with the required bounded 45-minute Run A contract, or issue an
authoritative revised maximum. Then rerun from a new UTC Run A/Run B pair; do not reuse this failed
path.
