# Definition of Done

- T17-only scope: versioned date/need editing, close, and cancel.
- Strong `If-Match` on all four mutations; close/cancel also require `Idempotency-Key`.
- Frozen editable fields and identifier/scalar command contracts are preserved.
- Capacity reduction uses complete canonical signup-root hydration plus signup-version CAS and has no-write rejection.
- Close preserves signup/help/thread history and locks only an existing thread.
- Cancel is fully atomic across signups, needs/date, thread, distinct-contact effects, audit, outbox, idempotency, and access-change signals.
- Focused application/API/PostgreSQL 18.6 tests pass with zero skips.
- OpenAPI/generated mobile client and full regressions pass.
- Independent test, security, code-review, QA, and judge gates approve.
