# T09 Social lifecycle/concurrency hardening

- Objective: fix the three independent Social lifecycle/concurrency blockers without changing T19.
- Classification: bugfix.
- Size: medium.
- Scope: Social Domain/Application/Infrastructure and Social tests only.
- Excluded: T19 durable job store/processor, Operations/Persistence, global/project/Web, UI, network, credentials, git history.
- Baseline: strict build/tests were reported green before this mission; preserve unrelated worktree changes.

## Causal defects

1. Accepted historical provider `FetchedAtUtc` currently drives expiry and can make a new snapshot immediately stale.
2. Handler successor idempotency currently depends on persisted snapshot/recovery state, so retrying one `JobInstanceId` after an enqueue-before-completion crash can fork recurrence.
3. Social startup treats a transient T19 definition-registration unique-key race as fatal instead of boundedly retrying `PersistenceFailure`.
