# T16 Task Plan

Objective: implement only T16 from the canonical Husaynia Tabruk implementation plan.

1. Read canonical requirements, architecture, decisions, T6R/T6R-HW contracts, T15 patterns, and repository conventions.
2. Add failing contract tests proving identifier-only commands and absence of caller date/deadline/aggregate context.
3. Add application and API cancellation/reassignment behavior with server clock, current actor, idempotency, and stable errors.
4. Add strictly necessary full-set persistence/CAS/effect wiring and real PostgreSQL attack/race coverage.
5. Run focused tests, full solution build/test/format, OpenAPI/generated-client drift, and mobile regressions.
6. Dispatch independent test, security, code review, QA, and engineering judgment gates.

No T17 date editing/closure, T18 mobile behavior, commit, push, or deployment is in scope.
