# RC2-T3/T4 Developer Visual Inspection

Date: 2026-09-04

## Sources

Inspected all six immutable source sheets directly:

- `LTurning.png`
- `RTurning.png`
- `RWalking2.png`
- `Clapping2.png`
- `CrossArm3.png`
- `CrossArm4.png`

The dark studio floor/reflection geometry is present in the immutable source
sheets. No source sheet was modified.

## Active frame groups

Inspected all 33 active frames at native canvas size, 2x nearest-neighbor
visible crop, and 2x lower-foot crop.

| Group | Frames | Result | Findings |
|---|---:|---|---|
| LeftTurn | 3 | PASS | `turn_0` is the canonical right-front image byte-for-byte. The former blocks below `turn_1` and detached fragments in `turn_2` are absent. Profile footwear remains intact. |
| RightTurn | 3 | PASS | The canonical front has no floor block or detached cool fragment. The profile frame retains both overlapping shoes without the former lower reflection band. |
| LeftWalk | 6 | PASS | Natural gait, intact shoes, no new mask damage, and exact mirrors of RightWalk. |
| RightWalk | 6 | PASS | Natural gait and footwear; unchanged visual source cadence. |
| Clap | 6 | PASS | The repeated left-shoe reflection block is absent in all six frames. Shoe uppers, soles, highlights, and stance remain visually intact. |
| CrossArm | 3 | PASS | Reflection blocks are absent in all three frames. `cross_02` uses the canonical lower plate without a visible floor remnant or detached fragment. |
| CrossArmRelease | 6 | PASS | All six upper-body poses remain distinct and aligned. The shared lower plate is visually stable across the release and returns continuously to front. |

Dark blue/gray tonal pixels that remain along trouser folds are attached source
shading, not detached floor components. Warm brown pixels retained on the shoe
uppers and soles are source leather detail.

## Boundary inspection

Inspected every generated pair sheet under `after/boundaries` and the five
aggregate boundary sheets:

- left directional: 9/9 PASS
- right directional: 9/9 PASS
- front handoff: 1/1 PASS
- clap entry/15-step/return: 16/16 PASS
- cross entry/hold/release/return: 10/10 PASS

Total: **45/45 visually accepted**.

Quantitative maxima:

| Class | Count | Head | Torso | Landmark | Baseline | Height | Lower-band difference |
|---|---:|---:|---:|---:|---:|---:|---:|
| Stationary | 27 | 3.07 px | 3.79 px | 3 px | 2 px | 3 px | 2.9593% |
| Moving | 18 | 5.90 px | 13.80 px | 4 px | 12 px | 12 px | 13.0916% (not a moving limit) |

The left/right front handoff is exactly identical. `cross_02 -> release_00`
has no lower-plate change and remains within the stationary upper-anchor
limits. No pair shows a new seam pop, clipped bound, detached lower component,
or shoe-color alteration.
