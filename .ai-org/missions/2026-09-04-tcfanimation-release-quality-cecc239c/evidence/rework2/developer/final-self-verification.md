# RC2 Developer Final Self-Verification

Date: 2026-09-04  
Scope: RC2-T1 through RC2-T4 and developer-owned release rerun

## Status

PASS

## Automated evidence

- Debug build: exit 0, warnings 0, errors 0.
- Release build: exit 0, warnings 0, errors 0.
- Controller probe: 476 assertions, including capture-root selection and
  failure cases.
- Python compilation: six extraction/validation modules compiled.
- Determinism: two complete extraction passes; 6/6 sources and 33/33 frame
  hashes identical.
- Validator: 33 frames, 6 sources, exact regeneration, adversarial lower-body
  controls rejected, 45 continuity boundaries passed, viewport configuration
  passed, final package passed.
- Secure export: exact 50-file seed + fixed solution, import/export error
  counts zero, final EXE 100,192,168 bytes, managed DLL 75,264 bytes.
- Runtime smoke from `C:\Windows`: exit 0 with
  `viewport_fit=1920x1080:CanvasItems:Keep`.
- Capture process matrix: 8/8, including a 149,185-byte successful
  executable-adjacent capture, specific no-overwrite/path failures, bounded
  headless-renderer failure, no orphan staging, and cleanup.
- Viewport capture matrix: 6/6 actual exported captures. The 1536x960 and
  1000x800 requests were uniformly constrained to 1536x864 and 1000x562;
  right-edge character and dialogue remained visible without distortion.
- Package hygiene: 73 pack entries, 33 runtime textures, 33 import metadata
  entries, 4 engine metadata entries, no denied payloads, no source content,
  no PDBs, and no absolute project paths.
- Cleanup: zero release stages, zero `.staging` files, zero remaining
  `Build\Captures` entries, and no generated `GlobalInputPolicy.cs.uid`.

## Visual evidence

All 33 active frames were inspected in:

- `after/contact-sheets-native`
- `after/contact-sheets-2x`
- `after/contact-sheets-feet`

All 45 boundaries were inspected in:

- `after/boundaries`
- `after/boundary-sheets`

Results:

- LeftTurn 3/3 clean; front is byte-identical to RightTurn front.
- RightTurn 3/3 clean; reflection/detached fragments removed.
- LeftWalk and RightWalk 12/12 clean; six exact mirrors preserved.
- Clap 6/6 clean; repeated shoe reflection removed without recoloring.
- CrossArm 3/3 clean.
- CrossArmRelease 6/6 clean with the shared canonical lower plate.
- Boundaries 45/45 pass; stationary and moving maxima remain within the
  binding thresholds.

## Legacy self-test interpretation

RW1 reran cleanly at 13/13. RW2/RW3/RW4 passed all functional assertions, then
their original pre-RC2 protected-baseline check reported the 23 files that
this approved rework was required to change. No test was weakened or skipped.
`rc2-protected-comparison.json` reclassifies only that expected delta and
passes with zero unapproved changes.

## Final artifact

`TCFAnimation\Build\TCFAnimation.exe`

SHA-256:
`DE3A85361D9B9D80D6E0E9E9A1168D74F71551CD5FF1A3E3F597D7A407502209`
