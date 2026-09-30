# TCFAnimation RC5 Turning-Landmark Independent Test Gate

Date: 2026-09-04  
Role: Independent Test Engineer  
Project: `C:\Users\syedhu\source\repos\Dreamer\TCFAnimation`  
Scope: final RC5 turning-landmark enforcement, `RightTurn/turn_1`
recalibration, and directly invalidated determinism, visual, build, export,
package, runtime-smoke, preservation, and hygiene surfaces.  
Final human visual/dialogue QA: explicitly not granted by this gate.

## TEST RESULT

### Command

The following commands were executed against the authoritative current tree.
Long commands are shown in their operational form; every Python invocation used
`-B` except the explicit syntax-compilation check.

```powershell
# Independent failing-before, classification-bypass, current-boundary, and
# expected-only asset-delta regression.
python -B `
  .\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\`
evidence\rework5\tests\rc5_independent_turning.py

# Production validator with whole-project snapshots immediately around it.
python -B ...\rc3_independent_continuity.py snapshot `
  --kind tree --output ...\rework5\tests\validator-tree-before.json
python -B .\TCFAnimation\FrameExtraction\validate_release.py
python -B ...\rc3_independent_continuity.py snapshot `
  --kind tree --output ...\rework5\tests\validator-tree-after.json
python -B ...\rc3_independent_continuity.py compare `
  --before ...\validator-tree-before.json `
  --after ...\validator-tree-after.json

# Independent continuity, exact-threshold, and adversarial controls.
python -B ...\rc3_independent_continuity.py continuity `
  --output ...\rework5\tests\independent-continuity.json

# Syntax check.
python -B -m py_compile `
  .\TCFAnimation\FrameExtraction\validate_release.py `
  .\TCFAnimation\FrameExtraction\extract_directional_turns.py `
  ...\rework3\tests\rc3_independent_continuity.py `
  ...\rework2\developer\generate_t3_t4_evidence.py `
  ...\rework4\developer\verify_visual_evidence.py `
  ...\rework5\tests\rc5_independent_turning.py

# Two complete fresh-process extractor passes. Each pass ran all four official
# extractors, then captured all six sources and all 33 frame byte/pixel hashes.
python -B .\TCFAnimation\FrameExtraction\extract_directional_turns.py
python -B .\TCFAnimation\FrameExtraction\extract_right_walk.py
python -B .\TCFAnimation\FrameExtraction\extract_clap.py
python -B .\TCFAnimation\FrameExtraction\extract_cross_arm.py
python -B ...\rc3_independent_continuity.py snapshot `
  --kind release --output ...\rework5\tests\extractor-pass1.json
# The same five commands were then run in fresh processes for pass 2.
python -B ...\rc3_independent_continuity.py compare `
  --before ...\release-before.json --after ...\extractor-pass1.json
python -B ...\rc3_independent_continuity.py compare `
  --before ...\extractor-pass1.json --after ...\extractor-pass2.json

# Two canonical visual-evidence generations, complete 85-file SHA-256 maps,
# comparison, and independent verifier.
python -B ...\rework2\developer\generate_t3_t4_evidence.py
# Snapshot every file below rework2/developer/after as visual-pass1.json.
python -B ...\rework2\developer\generate_t3_t4_evidence.py
# Snapshot as visual-pass2.json and compare every relative path/SHA-256.
python -B ...\rework4\developer\verify_visual_evidence.py

# Build, probe, format, and diff.
dotnet build .\TCFAnimation\TCFAnimation.sln --configuration Debug --nologo
dotnet build .\TCFAnimation\TCFAnimation.sln --configuration Release --nologo
dotnet run --project `
  .\TCFAnimation\ControllerProbe\ControllerProbe.csproj `
  --configuration Release --no-build
dotnet format .\TCFAnimation\TCFAnimation.sln `
  --verify-no-changes --no-restore --verbosity minimal
git -C C:\Users\syedhu\source\repos\Dreamer diff --check -- TCFAnimation

# Authenticated isolated export from an unrelated current directory.
Push-Location $env:TEMP
& C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\export-release.ps1
Pop-Location

# Post-export package validator and exported runtime smoke.
python -B .\TCFAnimation\FrameExtraction\validate_release.py
Start-Process `
  -FilePath .\TCFAnimation\Build\TCFAnimation.exe `
  -ArgumentList @("--headless", "--", "--verify-runtime") `
  -NoNewWindow -Wait -PassThru

# Final release/protected/project snapshots, package inventory, SHA-256,
# provenance, cache/stage/PDB hygiene, scoped diff, and final RC5 rerun.
python -B ...\rc3_independent_continuity.py snapshot `
  --kind release --output ...\rework5\tests\release-after.json
python -B ...\rc3_independent_continuity.py snapshot `
  --kind protected --output ...\rework5\tests\protected-after.json
python -B ...\rc3_independent_continuity.py compare `
  --before ...\release-before.json --after ...\release-after.json
python -B ...\rc3_independent_continuity.py compare `
  --before ...\protected-before.json --after ...\protected-after.json
python -B ...\rework5\tests\rc5_independent_turning.py
```

### Result

Real, unedited summary output from the executed gates:

```text
RC5_FAILING_BEFORE_PASS boundary=Right_turn1_to_turn2 shoulder=16 waist=6 old_label_gate=accepted explicit_turning_gate=rejected reason=shoulder
RC5_BOUNDARY_MATRIX_PASS total=45 stationary=27 turning=6 walking=12 head_max=5.898610 torso_max=13.796428 shoulder_max=10 waist_max=9 baseline_max=11 height_max=12
RC5_RIGHT_TRANSITION_PASS boundary=Right_turn1_to_turn2 head=3.391516 torso=6.488855 shoulder=10 waist=0 baseline=6 height=12
RC5_CLASSIFICATION_BYPASS_NEGATIVE_PASS mutation_shoulder=41 turning_with_walk_label=rejected walking_without_walk_label=rejected
RC5_EXPECTED_ASSET_DELTA_PASS changed=1 path=Frames/RightTurn/turn_1.png before_sha256=D891561FE5E5414A984AC3DFA061DE48050BC73B2E2C96500BDFDBDB0B3BA9D1 after_sha256=188D76A038B4AFD94DE02877CCF1A5F38417011070D5550E88253DCB4F867BC6 translation=(0,-6)
RC5_INDEPENDENT_TURNING_PASS checks=7

RC3_INDEPENDENT_PLATE seam_y=640 consumers=7 rgb_exact=7/7 visibility_exact=7/7
RC3_INDEPENDENT_BOUNDARIES total=45 passed=45 failed=0 stationary=27 moving=18
RC3_INDEPENDENT_THRESHOLDS cases=20 exact_inclusive=true epsilon_over_rejected=true
RC3_INDEPENDENT_ADVERSARIAL name=turning_landmark_shift_41px rejected=true reason=turning_landmark_continuity:shoulder
RC3_INDEPENDENT_CONTINUITY_PASS plate=7 boundaries=45 threshold_cases=20 adversarial=8

adversarial_control_ok=turning_landmark_shift_41px rejected=true reason=turning_landmark_continuity detail=Moving turning boundary turning_landmark_shift_41px exceeds actual shoulder/waist landmark continuity: shoulder=41.00/10 waist=2.00/10.
continuity_ok=boundaries:45 front_identity=bytes_and_pixels shared_lower_plate=7 seam_y=640 stationary=head4 torso4 baseline2 lower_binary_symmetric_difference_union12pct moving=head6 torso14 shoulder10 waist10 baseline16 height18
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true export_resources=34 artifact_manifest=checked_if_present
RC3_SNAPSHOT_COMPARE kind=tree files=3120 changed=0
RC5_PYTHON_SYNTAX_PASS files=6

RC5_EXTRACTOR_PASS pass=1 script=extract_directional_turns.py exit=0 terminal=canonical_front=RightTurn/turn_0 LeftTurn/turn_0 pixels_and_png_bytes_identical=true
RC5_EXTRACTOR_PASS pass=1 script=extract_right_walk.py exit=0 terminal=source_sha256=CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A source_unchanged=true
RC5_EXTRACTOR_PASS pass=1 script=extract_clap.py exit=0 terminal=runtime_assets=6 playback_steps=6 source_human_frames=(1, 2, 3, 4, 5, 6) playback_order=(0, 1, 2, 3, 4, 5) playback_fps=8 duration_seconds=0.750 contact_steps=(3,) repeated_contact_cycle=false source=Clapping2.png labels_removed=true dividers_removed=true source_unchanged=true
RC5_EXTRACTOR_PASS pass=1 script=extract_cross_arm.py exit=0 terminal=height_check=Frames\CrossArmRelease\release_05.png head_top=181 shoulder=293 waist=533 shoe=840 height=660 deltas=(+1,-5,-16,+0,-1)
RC5_EXTRACTOR_PASS pass=2 script=extract_directional_turns.py exit=0 terminal=canonical_front=RightTurn/turn_0 LeftTurn/turn_0 pixels_and_png_bytes_identical=true
RC5_EXTRACTOR_PASS pass=2 script=extract_right_walk.py exit=0 terminal=source_sha256=CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A source_unchanged=true
RC5_EXTRACTOR_PASS pass=2 script=extract_clap.py exit=0 terminal=runtime_assets=6 playback_steps=6 source_human_frames=(1, 2, 3, 4, 5, 6) playback_order=(0, 1, 2, 3, 4, 5) playback_fps=8 duration_seconds=0.750 contact_steps=(3,) repeated_contact_cycle=false source=Clapping2.png labels_removed=true dividers_removed=true source_unchanged=true
RC5_EXTRACTOR_PASS pass=2 script=extract_cross_arm.py exit=0 terminal=height_check=Frames\CrossArmRelease\release_05.png head_top=181 shoulder=293 waist=533 shoe=840 height=660 deltas=(+1,-5,-16,+0,-1)
RC3_SNAPSHOT_COMPARE kind=release files=39 changed=0
RC3_SNAPSHOT_COMPARE kind=release files=39 changed=0

EVIDENCE_PASS frames=33 boundaries=45 boundary_pass=True negatives_black=True front_identity=True shared_plate=True
RC5_VISUAL_OUTPUT_SNAPSHOT pass=1 files=85
EVIDENCE_PASS frames=33 boundaries=45 boundary_pass=True negatives_black=True front_identity=True shared_plate=True
RC5_VISUAL_OUTPUT_SNAPSHOT pass=2 files=85
RC5_VISUAL_OUTPUT_DETERMINISM_PASS files=85 mismatches=0
VISUAL_EVIDENCE_VERIFY_PASS seam_y=640 frames=33 boundaries=45 stationary=27 turning=6 walking=12 contact_sheets=21 individual_boundary_sheets=45 aggregate_boundary_sheets=5 plate_consumers=7

Build succeeded.
    0 Warning(s)
    0 Error(s)
Build succeeded.
    0 Warning(s)
    0 Error(s)
RC5_PROBE_ACCOUNTING observed_pass_lines=481 terminal=CONTROLLER_PROBE_PASS assertions=481 left_turn=true right_turn=true returns=true direct_reversal_both_ways=true both_held_neutral=true left_walk_6_frames=true right_walk_6_frames=true geometry_extrema=74,437 geometry_centers=227.5,1693.75 global_input=true capture_root=true fixed_runtime_frames=33 wave_assets=false
RC5_DOTNET_FORMAT_PASS solution=TCFAnimation.sln
RC5_SCOPED_DIFF_CHECK exit=0 project_status_entries=79

GODOT_SELECTED source=bundled version=4.5.1.stable.mono.official.f62fdbde1 executable=C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\.tools\godot-4.5.1-mono\Godot_v4.5.1-stable_mono_win64\Godot_v4.5.1-stable_mono_win64_console.exe sha256=FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F
RELEASE_STAGE_READY seed_files=50 preimport_files=51 solution_length=994 solution_sha256=FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79
RELEASE_EXPORT_PASS output=C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build\TCFAnimation.exe clean_build=true godot_version=4.5.1.stable.mono.official.f62fdbde1 isolated_stage=true import_exit=0 import_errors=0 export_exit=0 export_errors=0 exe_bytes=100185624 managed_dll_bytes=78336

pack_manifest_read=TCFAnimation.exe pack_version=3 engine=4.5.1 entries=73
pack_payload_ok=TCFAnimation.exe runtime_scene=1 runtime_scripts=2 runtime_textures=33 import_metadata=33 engine_metadata=4 denied_payloads=false source_content=false metadata_denied_tokens=0
managed_payload_ok=pdb_absent absolute_project_paths=false project_assemblies=1
artifact_validation=pass artifacts=1 manifest_only=true extraction_artifacts=false
RC5_PACKAGE_INVENTORY_PASS entries=73 textures=33 imports=33 engine_metadata=4 scenes=1 scripts=2
RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true viewport_fit=1920x1080:CanvasItems:Keep
RC5_RUNTIME_SMOKE_PROCESS_EXIT code=0

RC5_RELEASE_HASHES exe_bytes=100185624 exe_sha256=6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9 dll_bytes=78336 dll_sha256=EA9CFB0D15853B1340962E2F3E13630B7C739E9918D352C2E76B8FE8DED65A2A
RC5_PROVENANCE_ENTRY_PASS id=godot-console-win64 bytes=197640 sha256=FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F
RC5_PROVENANCE_ENTRY_PASS id=godot-editor-win64 bytes=163933704 sha256=C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B
RC5_PROVENANCE_TEMPLATE_PASS bytes=96965120 sha256=9186C4AA21D659035A6BCC33BEA644D7399DDA42AC24F9E0D3A9E5965E92E00F

RC3_SNAPSHOT_COMPARE kind=release files=39 changed=0
RC3_SNAPSHOT_COMPARE kind=protected files=18 changed=0
RC5_CLEANUP_PASS pycache_removed=5 pycache_remaining=0 transient_stages=0 release_stage=False staging=False captures=False
RC5_FINAL_HYGIENE diff_exit=0 project_status_entries=79 pycache=0 pdb=0 transient_stages=0
RC5_GENERATED_DRIFT_BOUNDED_PASS before_files=3118 after_files=3120 changed=7 unexpected=0
```

Focused mandatory surfaces:

Passed:   **35**  
Failed:   **0**  
Skipped:  **0**

Focused count basis:

1. Preserved failing-before canonical record.
2. Reproduction of the old label-based turning bypass.
3. Independent explicit-turning rejection of the preserved 16 px record.
4. Production turning kind rejects a 41 px mutation despite walk-like label text.
5. Production walking kind rejects the mutation despite no walk text.
6. Production validator-owned 41 px turning negative control.
7. Current `Right_turn1_to_turn2` shoulder/waist result.
8. Exact 45-boundary inventory and 27/6/12 class counts.
9. All moving maxima at or below their limits.
10. Independent plate, threshold, boundary, and eight-adversarial harness.
11. Production validator and its 3,120-file read-only comparison.
12. Six-file Python syntax check.
13. First complete four-extractor pass.
14. Second complete four-extractor pass.
15. Pass 1 source/frame byte and pixel hashes equal the pre-run snapshot.
16. Pass 2 source/frame byte and pixel hashes equal pass 1.
17. Exactly one frame differs from the captured RC4 baseline.
18. First canonical visual-evidence generation.
19. Second canonical visual-evidence generation.
20. Exact 85-file visual-output determinism.
21. Independent visual evidence verifier.
22. Right-turn native, feet, transition, and aggregate sheet inspection.
23. Debug solution build.
24. Release solution build.
25. ControllerProbe, including 481/481 accounting.
26. `dotnet format --verify-no-changes`.
27. Project-scoped diff check and unchanged 79-entry status.
28. Authenticated isolated export.
29. Post-export release/package validator.
30. Exact 73-entry package inventory.
31. Exported runtime smoke.
32. Release executable/DLL and pinned tool/template hashes.
33. Release and protected-asset preservation.
34. Cache/stage/PDB/capture cleanup and final hygiene.
35. Generated build-cache/log drift bounded to seven expected ignored outputs,
    with zero source, runtime-frame, user-asset, or release-resource drift.

Cumulative canonical gate:

Passed:   **682**  
Failed:   **0**  
Skipped:  **0**

The cumulative total remains 682 rather than adding the 35 focused supporting
surfaces again. RC5 replaces the invalidated RC4 continuity/visual evidence
with corrected explicit-classification evidence. The prior code-review finding
is disposed: the preserved 16 px transition now fails the explicit turning
contract, the current transition is exactly 10 px, and a turning label can no
longer bypass landmark enforcement.

### Failures

None on a mandatory surface.

One exploratory whole-project byte-for-byte comparison exited nonzero because
the required build/export commands updated seven ignored generated files:
five `.godot/mono/temp/obj` caches and two authenticated Godot
`editor_data/mono/build_logs` files. A subsequent bounded-drift assertion
proved those were the complete set (`changed=7`, `unexpected=0`). Release
sources/frames (`39/39`) and protected user assets (`18/18`) remained
byte-for-byte unchanged.

### Coverage of acceptance criteria

| Criterion | Executed evidence | Result |
|---|---|---|
| Turning classification cannot bypass landmarks | Preserved 16 px record accepted by reconstructed old label rule but rejected by explicit `TURNING`; production 41 px mutation rejected with both misleading label combinations | PASS |
| Current right transition is at most 10 px | `Right_turn1_to_turn2`: shoulder 10, waist 0 | PASS |
| Validator owns classification | Production `BoundaryKind`/`ContinuityBoundary`; 27 stationary, 6 turning, 12 walking | PASS |
| Every moving boundary enforces shoulder/waist | All 18 moving rows checked; maxima shoulder 10, waist 9 | PASS |
| Complete 45-boundary continuity | 45 passed, 0 failed; all moving maxima within 6/14/10/10/16/18 limits | PASS |
| Turning-specific negative | Independent and production 41 px mutations rejected for landmark continuity | PASS |
| Two-pass extraction determinism | Two fresh four-extractor passes; 6 sources and 33 frame byte/pixel hashes unchanged | PASS |
| Expected-only frame change | Relative to RC4 captured baseline, only `Frames/RightTurn/turn_1.png` changed; integer translation `(0,-6)` | PASS |
| Visual generation determinism | Two 85-file generations, zero hash mismatches | PASS |
| Visual evidence contract | 33 frames, 45 passing boundaries, 21 contact sheets, 45 individual and 5 aggregate sheets, seven plate consumers | PASS |
| Obvious right-turn visual regression | Native, feet, `turn_1 -> turn_2`, and complete right-directional sheets inspected; no obvious clipping, detached fragment, hard mask edge, floor residue, footwear loss, or discontinuous bob observed | PASS (automated/test-engineer inspection only) |
| Builds and probe | Debug/Release: 0 warnings/errors; probe 481 assertions and 481 observed PASS lines | PASS |
| Format and scoped diff | `dotnet format` and `git diff --check` passed; project status remained 79 entries | PASS |
| Secure export | Bundled pinned Godot 4.5.1 Mono; 50 seed/51 pre-import files; isolated import/export 0 errors | PASS |
| Package and provenance | 73 entries; 33 textures; 33 import metadata; 4 engine metadata; 0 denied payloads; executable/editor/template hashes exact | PASS |
| Exported runtime | `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true` and exit 0 | PASS |
| Preservation and cleanup | 39 release files and 18 protected assets unchanged; caches/stages/PDB/captures zero; generated drift bounded | PASS |

Conclusion: **PASS**

---

STATUS:          **PASS**

SUMMARY:

Independent RC5 validation proves the prior turning-classification defect is
closed. The old label-based rule accepted the preserved
`Right_turn1_to_turn2` shoulder delta of 16 px; the current explicit turning
classification rejects that record. Production and independent 41 px turning
mutations are rejected for shoulder/waist landmark continuity even when label
text is deliberately misleading. The current transition measures shoulder
10 px and waist 0 px.

All 45 boundaries pass with exact class counts 27 stationary, 6 turning, and
12 walking. Moving maxima are head 5.898610, torso 13.796428, shoulder 10,
waist 9, baseline 11, and height 12. Two complete extractor passes and two
85-file visual generations were deterministic. Debug/Release builds, the
481-assertion probe, format/diff, secure export, package validation,
provenance, exported runtime smoke, asset preservation, and hygiene passed.

WORK_COMPLETED:

- Read canonical code review, task plan, prior test result, RC5 developer
  evidence, production validator/extractor, independent continuity harness,
  visual generator, and visual verifier.
- Added and executed an independent RC5 regression harness without changing
  production code.
- Proved the old label bypass, explicit-kind enforcement, misleading-label
  negatives, current transition, exact class counts, moving maxima, integer
  translation, and expected-only asset delta.
- Executed production and independent validator negative/threshold matrices.
- Executed two full extractor passes and compared source/frame bytes and
  decoded RGBA pixels.
- Executed two visual generations, compared all 85 hashes, and ran the
  independent verifier.
- Inspected the right-turn native, feet, individual transition, and aggregate
  directional sheets for obvious regressions.
- Executed builds, probe accounting, format, scoped diff, isolated export,
  post-export package validation, runtime smoke, package inventory,
  provenance hashes, preservation snapshots, and cleanup.
- Updated this canonical test result with focused and cumulative disposition.

EVIDENCE:

- The `TEST RESULT` above.

ARTIFACTS:

- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/test-results.md`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/rc5_independent_turning.py`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/turning-validation.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/independent-continuity.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/extractor-pass1.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/extractor-pass2.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/visual-pass1.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/visual-pass2.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/visual-determinism.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/validator-tree-before.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/validator-tree-after.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/release-before.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/release-after.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/protected-before.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/protected-after.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/project-tree-before.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/project-tree-after.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework5/tests/generated-drift-analysis.json`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/after/contact-sheets-native/RightTurn.png`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/after/contact-sheets-feet/RightTurn.png`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/after/boundaries/10-Right_turn1_to_turn2.png`
- `.ai-org/missions/2026-09-04-tcfanimation-release-quality-cecc239c/evidence/rework2/developer/after/boundary-sheets/right-directional.png`
- `TCFAnimation/Build/TCFAnimation.exe`
- `TCFAnimation/Build/data_TCFAnimation_windows_x86_64/TCFAnimation.dll`

FINDINGS:

- Prior finding disposition: **resolved**. Turning/walking landmark
  enforcement is classification-owned and cannot be bypassed by label text.
- The current formerly failing boundary is exactly at the inclusive shoulder
  limit: 10 px.
- The only RC4 release-frame delta is `RightTurn/turn_1.png`, changed by the
  configured whole-pixel `(0,-6)` translation.
- Required builds/exports update seven ignored tool-generated cache/log files;
  no source, runtime frame, immutable sheet, protected user asset, or package
  resource drift was found.
- No mandatory check failed or was skipped.

RISKS:

- The corrected shoulder result is exactly at the 10 px limit and therefore
  has no numeric margin; future image-processing changes must retain the
  explicit turning negative and exact boundary matrix.
- Automated and Test Engineer sheet inspection cannot replace final unlocked
  human visual/dialogue QA.
- Previously accepted same-user capture TOCTOU residual remains unchanged and
  was not re-reviewed because security-sensitive code did not change.

BLOCKERS:

None.

NEXT_ACTION:

Proceed to independent final QA/judge. Do not interpret this PASS as code-review
approval or final human visual/dialogue approval.
