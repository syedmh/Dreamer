# Definition of Done

## Baseline and safety

- [ ] **Mission owner:** The recovered `main`/`cdcc5d5` working tree and intentional uncommitted TCFAnimation work are preserved; listed unrelated root sheets/Waving files and sidecars are untouched and un-staged.
- [ ] **Mission owner:** No sibling project is inspected/changed; no commit, push, checkout, reset, clean, rebase, or history mutation occurs.

## Automated proof

- [ ] **Developer/Test gate:** `dotnet build TCFAnimation.sln -c Debug` and `-c Release` pass.
- [ ] **Test gate:** ControllerProbe Release passes, prints a real positive assertion count plus named coverage summary, and covers AC-03/05/06/07/08/09/10/14.
- [ ] **Developer/Test gate:** `dotnet format TCFAnimation.sln --verify-no-changes --no-restore` and project-scoped `git diff --check` pass.
- [ ] **Asset gate:** Official extractors run in fresh processes; all six source sheets remain byte-identical; two regenerated 33-frame sets have identical pixel and PNG-byte hashes; normal runs write no evidence artifacts.
- [ ] **Asset gate:** `python -B FrameExtraction\validate_release.py` passes all positive/negative checks and ends with `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present`.
- [ ] **Runtime gate:** Godot 4.5.1 Mono headless import and startup pass.
- [ ] **Release gate:** From an arbitrary cwd, `export-release.ps1` cleans only project-local `Build`, creates `Build/TCFAnimation.exe`, and post-export manifest/content validation excludes source/development/evidence/obsolete/Waving resources, PDBs, debug symbols, and absolute local paths.
- [ ] **Runtime gate:** The actual exported executable launches on Windows and read-only `--verify-runtime` prints `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true`; incompatible capture arguments fail.
- [ ] **Launcher/capture gate:** `run-animation.bat` works from arbitrary cwd. Valid capture publishes atomically below `Captures`; invalid, escape, reserved-name, existing-target, permission, reparse-point, and publication-failure cases cannot escape or overwrite and leave no orphan staging file.

## Manual and visual proof

- [ ] **Visual QA gate:** All 33 active frames are inspected at runtime display size and zoom; the complete R-19 group/boundary matrix is recorded.
- [ ] **Visual QA gate:** No floor/panel/shadow/reflection/label/divider/halo/detached fragment/matte bite/hole/clipping/artificial charcoal plateau/unnatural gray footwear is visible; opaque exact-black background is independently confirmed.
- [ ] **Visual QA gate:** Apparent visible height, head anchor, torso anchor, and lowest-foot position/deltas are recorded for front-turn-walk, walk loops/edges, clap entry/order/return, cross-hold-release, and direct returns. Accepted source-art limitations cite the source pose and reviewer decision.
- [ ] **QA/E2E gate:** Realistic journeys run in `Build/TCFAnimation.exe` on Windows and cover left/right turn-walk-reverse/edges, speed limits, clap interruption, cross/hold/release with held-arrow arbitration, dialogue boundaries, fullscreen arbitration, no W behavior, and capture success/failure.

## Independent release gates

- [ ] **Test Engineer:** PASS with executed test counts and AC traceability.
- [ ] **Code Reviewer:** APPROVED with no unresolved correctness/reliability blocker.
- [ ] **Security Engineer:** PASS with no unresolved Critical/High finding.
- [ ] **QA:** PASS for exported-runtime journeys and the visual matrix.
- [ ] **Engineering Judge:** APPROVED against `requirements.md`, this checklist, and recorded evidence.
- [ ] **Mission owner:** No stale quality blocker remains; README/runtime/export evidence agree; no commit or push occurs.
