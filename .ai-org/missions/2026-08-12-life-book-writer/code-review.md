# Independent Code Review — T10/T11 Remediation

Date: 2026-08-12

## Verdict

**APPROVED**

All five specified findings are remediated. The previously reproduced deletion-before-storage-event
race is closed: `forceOverwrite()` treats a missing memoir key as terminal erase handling and
returns before any write (`BookWriter/js/app.js:607-629`).

## Executed evidence

- Fresh Chrome browser suite: `46 passed, 0 failed, 46 total.`
- Fresh Edge browser suite: `46 passed, 0 failed, 46 total.`
- Static checks:
  - `REQUIRED_FILES: PASS 15/15`
  - `FIXTURES: PASS 6 valid JSON fixtures + malformed.json rejected as invalid JSON`
  - `APP_SCRIPT_ORDER: PASS 9 scripts`
  - `TEST_SCRIPT_ORDER: PASS 10 scripts`
  - `PROHIBITED_SCAN: PASS 0 hits`
  - `EXTERNAL_SCAN: PASS 0 hits`

Targeted executed regressions include prompt-count overflow atomic rejection, missing-key
force-overwrite before null-event delivery, null-event erase handling, export field fidelity and
escaping, erase timer cancellation, filtered creation visibility, and structured restore bounds.

## Findings

None.

## T12 post-review blocking finding and remediation

A later review found a **High** regression in `BookWriter/js/validation.js`: restore-defense
chapter, memory, reference-list, text-list, field-length, and aggregate-text limits were enforced
by general collection validation. Normal local save/load and backup serialization therefore failed
for locally authored collections above restore thresholds, including the emergency-backup path.

Developer remediation separates schema/referential invariants from untrusted-import resource
limits. Local persistence/load, backup serialization, and export retain invariant validation
without restore caps; `validateBackup()` applies byte and structural/entity/text limits before an
imported collection is cloned, previewed, rendered, or stored. Oversized files receive actionable
split-before-restore guidance and current data remains unchanged.

Executed developer evidence:

- Pre-fix Edge regression: `46 passed, 1 failed, 47 total`; the new local-fidelity test failed on
  the chapter restore limit during `Persistence.save()`.
- Post-fix Edge: `48 passed, 0 failed, 48 total`.
- Post-fix Chrome: `48 passed, 0 failed, 48 total`.
- Static scans: required files, fixtures, script order, prohibited patterns, and external
  references all passed.

The High finding is remediated in code and regression coverage.

## Independent T12 final re-review

**Verdict: APPROVED**

The independent code reviewer verified that:

- `validateCollection()` enforces local schema and reference invariants without restore caps.
- Persistence and backup serialization preserve oversized locally authored collections without
  truncation.
- `validateBackup()` applies resource limits only at the untrusted restore boundary.
- Oversized restores are rejected before preview, rendering, state mutation, or storage writes.
- Documentation clearly explains that oversized backups remain complete but may require splitting
  before restore.

Executed evidence:

- Edge: `48 passed, 0 failed, 48 total`.
- Chrome: `48 passed, 0 failed, 48 total`.
- Findings: none.

The current implementation is approved with no blocking review findings.
