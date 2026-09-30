# RC2-T3/T4 Developer Verification Summary

Date: 2026-09-04

## Failing-before proof

Command:

```powershell
python -B evidence\rework2\developer\before\probe_before.py
```

Expected failing exit: `1`.

Observed:

```text
BEFORE_FRAME_COUNT=33
BEFORE_ALL_DEFECT_ROIS_BLACK=False
BEFORE_FRONT_BYTES_IDENTICAL=False
BEFORE_FRONT_PIXELS_IDENTICAL=False
BEFORE_DARK_RECTANGLE_ACCEPTED=True
BEFORE_COOL_FRAGMENT_ACCEPTED=True rect=(180, 730, 185, 734)
BEFORE_CROSS02_RELEASE00=... head_x_delta=-16.81 ...
torso_x_delta=-18.18 ... foot_x_delta=48.48 ...
```

The old validator separately returned
`ASSET_RELEASE_CHECK_PASS frames=33 sources=6` despite those failures.

## Deterministic regeneration

Executed the complete extractor order twice:

```powershell
python -B FrameExtraction\extract_directional_turns.py
python -B FrameExtraction\extract_right_walk.py
python -B FrameExtraction\extract_clap.py
python -B FrameExtraction\extract_cross_arm.py
```

Comparison:

```json
{
  "sources_identical": true,
  "frames_identical": true,
  "source_count": 6,
  "frame_count": 33,
  "source_mismatches": [],
  "frame_mismatches": []
}
```

Evidence:

- `determinism-pass1.json`
- `determinism-pass2.json`
- `determinism-comparison.json`

## Compile and import

```text
IMPORT_CHECK_PASS modules=5
```

Compiled/imported:

- `foreground_cutout`
- `extract_directional_turns`
- `extract_clap`
- `extract_cross_arm`
- `validate_release`

## Final validator

Command:

```powershell
python -B FrameExtraction\validate_release.py
```

Observed:

```text
negative_control_ok=in_envelope_dark_rectangle rejected=true
negative_control_ok=detached_cool_fragment rejected=true
regeneration_ok=pixels_and_png_bytes mirror=exact
continuity_ok=boundaries:45 front_identity=bytes_and_pixels
shared_lower_plate=7 seam_y=700
artifact_validation=pass artifacts=1
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
```

## Visual and metric evidence

Command:

```powershell
python -B evidence\rework2\developer\generate_t3_t4_evidence.py
```

Observed:

```text
EVIDENCE_PASS frames=33 boundaries=45 boundary_pass=True
negatives_black=True front_identity=True shared_plate=True
```

Generated:

- 7 native contact sheets
- 7 2x nearest-neighbor contact sheets
- 7 lower-foot contact sheets
- 45 individual boundary pair sheets
- 5 aggregate boundary sheets
- frame metrics JSON/CSV
- boundary metrics JSON/CSV
- floor-negative results JSON
- visual summary JSON
- `visual-inspection.md`
