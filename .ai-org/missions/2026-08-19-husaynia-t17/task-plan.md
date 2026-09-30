# Task plan

1. **T17-IMPLEMENT — IN PROGRESS** — backend specialist
   - Read authoritative plan, AC-7/AC-15, architecture/frozen contracts, T15/T16 patterns, and T8 canonical repository.
   - Add failing tests first, then implement application/API/domain/persistence/DI/contracts and generated artifacts.
   - Execute focused and regression verification.
2. **T17-VALIDATE — DONE** — independent test, security, code-review, and QA agents.
   - Tests: 1004/1004 .NET and 121/121 mobile, zero skips.
   - Security: PASS, zero Critical/High.
   - Code review: APPROVED.
   - QA: 8/8 stateful HTTP/PostgreSQL scenarios.
3. **T17-JUDGE — DONE** — independent engineering judge verified the mission-specific Definition of Done and returned APPROVED.

## Rework 1

- **T17-REWORK-1 — DONE** — developer
  - Add write-time active-manager revalidation under the same transaction/locks.
  - Prevent date-level saves from overwriting concurrent help-need edits; add explicit child CAS/race coverage.
  - Bind close/cancel idempotency fingerprints to expected date version.
  - Reject non-UTC patch timestamps as stable validation errors.
  - Add close-without-thread and stale/concurrent close/cancel coverage.
  - Diagnose and restore the mobile Jest regression without weakening tests.

Evidence: focused T17 PostgreSQL 25/25; full .NET 999/999; mobile 121/121; build, format, and generated-client drift passed.

## Rework 2

- **T17-REWORK-2 — DONE** — developer
  - Allow parent date cancellation to persist preserved active signup cancellations even after the date/needs were previously closed, while retaining manager auth, CAS, and atomicity.
  - Reject zone-less date-patch timestamps independent of host timezone.
  - Add close-then-cancel and missing-zone regressions.

Evidence: application 5/5, API contract 7/7, PostgreSQL T17 27/27, full .NET 1004/1004, mobile 121/121.
