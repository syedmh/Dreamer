# RC4 Canonical Visual-Evidence Generator Self-Verification

Date: 2026-09-04  
Scope: canonical mission evidence tooling and generated evidence only

STATUS: PASS

SUMMARY:

The sole RC3 failure was reproduced exactly and fixed at its evidence-tooling
root cause. The canonical generator now owns row 640 independently, rejects
production seam drift before writing output, and regenerated deterministic
current-tree evidence without changing production code, assets, or frames.

WORK_COMPLETED:

- Updated
  `evidence/rework2/developer/generate_t3_t4_evidence.py` to define
  `EXPECTED_STATIONARY_PLATE_SEAM_Y = 640`.
- Added a pre-generation check that both imported production seam constants
  equal the independent evidence contract.
- Replaced both removed `LOWER_BODY_SEAM_Y` slices with the local row-640
  constant.
- Added `evidence/rework4/developer/verify_visual_evidence.py` to verify the
  complete generated inventory, metrics, summary, and row-640 RGB/visibility
  equality.
- Regenerated `evidence/rework2/developer/after/` twice and compared every
  output hash.

EVIDENCE:

Failing-before reproduction from `TCFAnimation`:

```powershell
python -B ..\.ai-org\missions\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\rework2\developer\generate_t3_t4_evidence.py
```

```text
AttributeError: module 'extract_cross_arm' has no attribute
'LOWER_BODY_SEAM_Y'
exit=1
```

Passing regeneration:

```text
EVIDENCE_PASS frames=33 boundaries=45 boundary_pass=True
negatives_black=True front_identity=True shared_plate=True
```

Second-pass output comparison:

```text
VISUAL_OUTPUT_SNAPSHOT pass=2 files=85
VISUAL_OUTPUT_DETERMINISM_PASS files=85 mismatches=0
```

Inventory and row-640 verification:

```text
VISUAL_EVIDENCE_VERIFY_PASS seam_y=640 frames=33 boundaries=45
contact_sheets=21 individual_boundary_sheets=45
aggregate_boundary_sheets=5 plate_consumers=7
```

Production-drift negative control:

```text
SEAM_DRIFT_CONTROL_PASS rejected=True
message=Production stationary plate seam does not match independent evidence
contract 640: {'extract_cross_arm': 641}
```

Independent continuity:

```text
RC3_INDEPENDENT_PLATE seam_y=640 consumers=7
rgb_exact=7/7 visibility_exact=7/7
RC3_INDEPENDENT_BOUNDARIES total=45 passed=45 failed=0
stationary=27 moving=18
RC3_INDEPENDENT_CONTINUITY_PASS plate=7 boundaries=45
threshold_cases=20 adversarial=7
```

Production validator and read-only proof:

```text
ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
export_resources=34 artifact_manifest=checked_if_present
RC3_SNAPSHOT_COMPARE kind=tree files=3116 changed=0
```

Preservation:

```text
RC3_SNAPSHOT_COMPARE kind=release files=39 changed=0
RC3_SNAPSHOT_COMPARE kind=protected files=18 changed=0
```

Scoped diff and cleanup:

```text
PROJECT_DIFF_CHECK_EXIT=0 status_entries=79
CLEANUP_PASS release_stages=0 staging_files=0 capture_files=0
evidence_pycache_dirs=0
```

The 79 project status entries exactly match the authoritative dirty-tree
baseline captured before this change; no production path was added, removed,
or changed by this task.

ARTIFACTS:

- `evidence/rework2/developer/generate_t3_t4_evidence.py`
- `evidence/rework2/developer/after/visual-summary.json`
- `evidence/rework2/developer/after/frame-metrics.json`
- `evidence/rework2/developer/after/frame-metrics.csv`
- `evidence/rework2/developer/after/boundary-metrics.json`
- `evidence/rework2/developer/after/boundary-metrics.csv`
- `evidence/rework2/developer/after/artifact-negative-results.json`
- `evidence/rework2/developer/after/contact-sheets-native/`
- `evidence/rework2/developer/after/contact-sheets-2x/`
- `evidence/rework2/developer/after/contact-sheets-feet/`
- `evidence/rework2/developer/after/boundaries/`
- `evidence/rework2/developer/after/boundary-sheets/`
- `evidence/rework4/developer/verify_visual_evidence.py`
- `evidence/rework4/developer/visual-evidence-verification.json`
- `evidence/rework4/developer/visual-output-pass1.json`
- `evidence/rework4/developer/visual-output-pass2.json`
- `evidence/rework4/developer/visual-output-comparison.json`
- `evidence/rework4/developer/independent-continuity.json`
- `evidence/rework4/developer/release-before.json`
- `evidence/rework4/developer/release-after.json`
- `evidence/rework4/developer/protected-before.json`
- `evidence/rework4/developer/protected-after.json`
- `evidence/rework4/developer/validator-tree-before.json`
- `evidence/rework4/developer/validator-tree-after.json`

RISKS:

- This is developer self-verification, not independent gate approval.
- Final human visual/dialogue QA remains outside this task.

BLOCKERS:

None.

NEXT_ACTION:

Run the focused independent RC3 gate rerun for the repaired visual-evidence
surface plus preservation, validator, diff, and cleanup checks.
