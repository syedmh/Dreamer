# Independent Post-Fix Code Review

**Date:** 2026-09-04
**Repository HEAD:** `7192e3930656eb09bb0ac63c4b7322d903982f00` (`main`)
**Verdict:** APPROVED

REVIEW RESULT: APPROVED

Scope reviewed: Complete current uncommitted `TCFAnimation` working-tree state: 49 tracked changed files, empty staged diff, and 31 untracked project files. The review covered runtime/input/dialogue/state-machine/capture code; ControllerProbe; all four extractors plus shared cutout and release validator; 33 changed runtime frame PNGs; project/export/dependency configuration; launch/export/release tooling; package manifest/provenance; and current release artifacts. Intentionally unrelated untracked root sheets and sidecars were treated as protected inputs and confirmed absent from the selected-resource release.

Current post-fix source identity:
- `GlobalInputPolicy.cs` SHA-256 `7E43F301D7639B8D642CFAE752E0003A4B16AB0273660FAAB9C374178960DF9A`
- `TurnController.cs` SHA-256 `909A4B0815243CAED2483F092E8498BD413D24E0572DA29DD2C80A407AB19515`
- `DialogueUi.cs` SHA-256 `2553E13303A2C7899FD3D678425BEBFAEA10376B69F50AA64CB29552D4E20D4C`
- `DialogueModel.cs` SHA-256 `0B1DEBED776BC20D04F4ECAA1E6AFEC09F18B852A378AFA3D079200AB8FEF3A0`
- `release-stage-manifest.txt` SHA-256 `10B1649E8CC9C84A46A7C0465E1B282B71EDDB2546013EBF0F3132BD04508BDF`
- `export_presets.cfg` SHA-256 `6EFA1CF66C58137586C5CCBDD364697B047E38A6C5B12A3BD0D6762F0CCF0EF4`

Release identity:
- EXE: `Build/TCFAnimation.exe`, 100,185,624 bytes, SHA-256 `6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F`
- DLL: `Build/data_TCFAnimation_windows_x86_64/TCFAnimation.dll`, 78,848 bytes, SHA-256 `1C62F905EF43B76BD5C5E068ED5737F563F0F574E9CFE3E7539A19FF35AD2518`
- Both identities exactly match the expected post-fix release supplied for this gate.

Issues:

None. No high-confidence correctness, reliability, architecture, API-compatibility, release-identity, or performance defect was found.

Positive notes:
- The Alt+Enter remediation is correctly placed in the early `_Input` phase (`TurnController.cs:180-239`) and is limited to pressed, non-echo key events by `GlobalInputPolicy.Resolve` (`GlobalInputPolicy.cs:31-68`). Handled global actions call `SetInputAsHandled`, while `_UnhandledKeyInput` retains only gameplay/speed handling, preventing a second fullscreen route.
- Dialogue ownership remains intact: `DialogueUi._Input` explicitly leaves Alt+Enter untouched, plain Enter remains submit/open, and editing Escape remains cancel (`DialogueUi.cs:36-81`). The global policy suppresses fullscreen Escape while editing, yielding the required two-stage Escape behavior.
- The focused foreground regression is outcome-based rather than policy-only: both Alt+Enter directions toggle exactly once while preserving `fullscreen w/W 😀`, the LineEdit, and focus. The canonical JSON independently reports 51/51 assertions, 104/104 focus checks, 12/12 clean processes, and 85 screenshots.
- Release validation found exactly 33 runtime textures and 34 selected resources, no source content/debug symbols/denied payloads, and no PDB or stale stage remainder.

Evidence commands inspected/run:
- `git status --short`, `git diff -- TCFAnimation`, `git diff --staged -- TCFAnimation`, `git diff --stat/--numstat`, `git diff --name-only`, and `git ls-files --others --exclude-standard -- TCFAnimation`.
- `dotnet build TCFAnimation.sln -c Debug` — PASS, 0 warnings, 0 errors.
- `dotnet build TCFAnimation.sln -c Release` — PASS, 0 warnings, 0 errors.
- `dotnet run --project ControllerProbe/ControllerProbe.csproj -c Release` — PASS, `assertions=489`; named global-input checks include early-only routing, focused Alt+Enter preservation, plain Enter ownership, and two-stage Escape.
- `dotnet format TCFAnimation.sln --verify-no-changes --no-restore` — PASS.
- `git --no-pager diff --check -- TCFAnimation` — PASS.
- `python -B FrameExtraction/validate_release.py` — PASS: `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present`.
- Exported process invocation with `--headless -- --verify-runtime` — exit 0, empty stderr, `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true viewport_fit=1920x1080:CanvasItems:Keep`.
- `Get-FileHash -Algorithm SHA256` and byte-length inspection for the EXE and DLL — exact expected values above.
- Parsed `evidence/qa-final/qa-foreground-fixed/foreground-e2e-results.json` — status PASS; 51 assertions, 0 failed; 104 focus checks, 0 failed; 12 processes, 0 nonzero; release hash exact.
- Build hygiene check — `PDB_COUNT=0`, `STAGE_REMAINDER_COUNT=0`, `PROCESS_COUNT=0`.

---

STATUS:          PASS
SUMMARY:         Current post-fix source and exact release identity are approved. The Alt+Enter early-input fix resolves the focused LineEdit failure without duplicate handling, text/focus loss, dialogue submission, gameplay leakage, or changed Escape semantics.
WORK_COMPLETED:  Inspected the complete scoped diff and current untracked release-relevant files; traced input callback ordering and ownership; reviewed runtime, capture, extraction, validation, packaging, and launcher paths; ran builds, probe, format/diff, asset/package validator, runtime smoke, hash, QA-evidence, and cleanup checks.
EVIDENCE:        Debug/Release builds 0 warnings/errors; ControllerProbe 489/489; validator PASS for 33 frames/6 sources/34 resources; runtime smoke exit 0; foreground QA 51/51 and 12/12; exact EXE/DLL hashes above.
ARTIFACTS:       .ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/code-review-postfix.md
FINDINGS:        None.
RISKS:           Existing accepted residuals remain: same-user capture TOCTOU, one Windows/NVIDIA/125%-DPI foreground environment, unsigned executable, and platform-specific fullscreen client sizing. None is introduced or worsened by the post-fix change.
BLOCKERS:        None.
NEXT_ACTION:     Proceed to the remaining independent post-fix release gates/final judgment. Do not commit or push until all required gates approve.
