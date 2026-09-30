# Rework Cycle 5 Developer Evidence

Date: 2026-09-04

## Failing-before evidence

- The preserved independent record in `failing-before.json`, sourced from the
  prior canonical `evidence/rework4/tests/independent-continuity.json`, shows
  `Right_turn1_to_turn2` was accepted with shoulder delta 16 px and waist
  delta 6 px.
- Before the correction, an in-memory turning-specific vest mutation measured
  shoulder delta 41 px and waist delta 4 px and was accepted because the
  validator conditioned landmark enforcement on `"walk" in label.casefold()`.

## Implementation

- `FrameExtraction/validate_release.py` now uses validator-owned
  `BoundaryKind` and `ContinuityBoundary` records. Every turning and walking
  boundary enforces shoulder and waist deltas at 10 px or less; labels are
  diagnostic text only.
- The validator now includes a calibrated 41 px turning-shoulder mutation and
  requires rejection specifically for turning landmark continuity.
- The independent continuity harness owns its own boundary kinds, records,
  thresholds, landmark extraction, and 41 px turning mutation.
- The canonical visual generator owns its own moving classifications and
  thresholds and reports independently measured green-vest shoulder and waist
  landmarks instead of the old top-row proxy.
- `RightTurn/turn_1` is translated upward by exactly 6 whole pixels after
  segmentation. No resampling, source edit, recoloring, scaling, rotation, or
  synthesized pixels are used.

## Boundary results

The final independent 45-boundary matrix contains 27 stationary, 6 turning,
and 12 walking boundaries. Moving maxima are:

| Metric | Maximum | Limit |
|---|---:|---:|
| Head | 5.898610 px | 6 px |
| Torso | 13.796428 px | 14 px |
| Shoulder | 10 px | 10 px |
| Waist | 9 px | 10 px |
| Shoe baseline | 11 px | 16 px |
| Body height | 12 px | 18 px |

The corrected right-directional boundaries are:

| Boundary | Head | Torso | Shoulder | Waist | Baseline | Height |
|---|---:|---:|---:|---:|---:|---:|
| `Right_front_to_turn1` | 3.67 | 1.34 | 7 | 6 | 1 | 5 |
| `Right_turn1_to_turn2` | 3.39 | 6.49 | 10 | 0 | 6 | 12 |
| `Right_turn2_to_walk0` | 5.90 | 3.96 | 10 | 1 | 3 | 7 |

## Determinism and source integrity

- Two complete extractor passes regenerated all 33 runtime frames from all six
  sources. `extractor-determinism.json` records zero source or frame hash
  mismatches.
- Two canonical visual-evidence passes produced 85 files each with zero hash
  mismatches in `visual-determinism.json`.
- Final `Frames/RightTurn/turn_1.png`: 181927 bytes, SHA-256
  `188D76A038B4AFD94DE02877CCF1A5F38417011070D5550E88253DCB4F867BC6`.
- Immutable `RTurning.png` remains SHA-256
  `2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840`.
- The other five immutable sources retain their approved hashes in
  `extractor-pass2.json`.
- All six left/right walk pairs remain exact mirrors.
- The 18 protected unrelated user assets compare byte-for-byte unchanged with
  the prior canonical protected snapshot.

## Validation and release

- Python compilation passed for the production validator/extractor,
  independent continuity harness, canonical visual generator, and visual
  verifier.
- Independent continuity passed: seven row-640 consumers, 45 boundaries,
  20 inclusive/epsilon threshold cases, and eight adversarial controls.
- Production validation passed and a 3120-file before/after snapshot proved it
  read-only.
- Debug and Release solution builds passed with zero warnings and zero errors.
- Controller probe passed 481 assertions and retained 33 runtime names, six
  frame walk groups, exact geometry, and no Waving runtime assets.
- Authenticated isolated export passed with 50 seed files and 51 pre-import
  files. Package validation found 33 textures, 33 import metadata entries,
  four engine metadata entries, and zero denied payloads.
- Exported runtime smoke passed.
- Final `Build/TCFAnimation.exe`: 100185624 bytes, SHA-256
  `6421D47A982DD97427C4F2DCAC0F79F9E06F4D411AE8CEB7F4606A8EFB5EF9E9`.
- Final managed `TCFAnimation.dll`: 78336 bytes, SHA-256
  `EA9CFB0D15853B1340962E2F3E13630B7C739E9918D352C2E76B8FE8DED65A2A`.

## Visual inspection

The regenerated native, feet, individual-boundary, and aggregate
right-directional sheets were inspected in forward order
front -> turn_1 -> turn_2 -> walk_00 and in reverse. The 6 px whole-frame
turn_1 correction removes the shoulder jump without introducing a visible
vertical bob, canvas clipping, hard mask edge, anatomy discontinuity, detached
fragment, floor/reflection residue, footwear loss, or black-background
contamination. The side-profile turn_2 and walking handoff remain visually
coherent.

Primary visual artifacts:

- `evidence/rework2/developer/after/contact-sheets-native/RightTurn.png`
- `evidence/rework2/developer/after/contact-sheets-feet/RightTurn.png`
- `evidence/rework2/developer/after/boundaries/10-Right_turn1_to_turn2.png`
- `evidence/rework2/developer/after/boundary-sheets/right-directional.png`

Independent approval, final QA, security re-review, and judge remain out of
scope for this developer slice.
