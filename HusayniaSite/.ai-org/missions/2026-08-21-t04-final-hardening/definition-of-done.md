# Definition of Done

## Implementation gate

- [ ] AC-01..AC-04: seal invariant, restart causality, idempotent SQL, and zero runtime DDL proven.
- [ ] AC-05..AC-08: one central audit token/policy covers every privileged outcome exactly once,
  survives disconnect, is shutdown/timeout bounded, and has tested writer-failure behavior.
- [ ] AC-09..AC-12: complete MFA setup/enable audit matrix passes with no key/OTP/secret leakage and
  existing MFA secrecy/session behavior preserved.
- [ ] AC-13..AC-20: atomic DB-backed limiter passes multi-host global-limit, partition, expiry,
  retention, configuration, enumeration-safe 429, forwarded-header spoofing, and no-lockout-
  amplification tests.
- [ ] AC-21: T18 handoff exactly names both triggers, limiter table/constraints/indexes/retention,
  deterministic `InstallSql`/`DownSql`, and T18 migration ownership.
- [ ] Scoped diff contains no production/test changes outside T04 Identity-owned paths and no
  migrations, snapshots, project/package files, defaults, secrets, deployment, or infrastructure.

## Verification gate

- [ ] Every AC has at least one named automated test or explicit artifact inspection recorded in an
  acceptance-to-evidence matrix.
- [ ] Strict Release builds of owned Application, Infrastructure/Web as applicable, and Integration
  test projects pass with warnings as errors.
- [ ] Focused Application/Integration Identity suites pass with zero failed/skipped tests and real
  counts recorded.
- [ ] Concurrency, restart, delayed-writer, direct-SQL, timeout, expiry, and multi-host causal tests
  pass three consecutive executions with zero flakes.
- [ ] Strict full solution build and full solution test suite pass with zero failed/skipped tests and
  real counts recorded.

## Independent gates

- [ ] Test Engineer: APPROVED against AC-01..AC-22 using executed evidence.
- [ ] Security Engineer: APPROVED with no unresolved Critical/High finding.
- [ ] Code Reviewer: APPROVED with exact diff/file review.
- [ ] QA: APPROVED for bootstrap restart, privileged audit, MFA, and anonymous throttle journeys.
- [ ] Engineering Judge: APPROVED against this checklist and `requirements.md`.
