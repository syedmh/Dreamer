# Definition of Done

- [ ] Historical but accepted provider fetch metadata is preserved while `ExpiresAtUtc` is based on the bounded local successful refresh time.
- [ ] A causal test proves the durable successor for an accepted historical provider timestamp is future-dated.
- [ ] Handler scheduling accepts a bounded canonical generation derived from `JobExecutionContext.JobInstanceId`.
- [ ] Re-executing the same job instance after different snapshot versions produces one idempotent successor.
- [ ] A distinct successor job instance produces the next single generation.
- [ ] Rate-limited and terminal-recovery branches use the same current-job generation without altering RetryAfter or dead-letter semantics.
- [ ] Startup retries only `JobStoreErrorCode.PersistenceFailure`, for a small fixed bounded attempt count.
- [ ] Startup cancellation is preserved; nontransient and exhausted failures still fail.
- [ ] No remote provider call occurs under a transaction or lock.
- [ ] No T19/Operations/Persistence/global/project/Web files are modified.
- [ ] Focused Social Application tests pass with exact count.
- [ ] Focused Social Integration tests pass with exact count.
- [ ] Focused Operations.Jobs regression tests pass with exact count.
- [ ] Unsuppressed strict solution build passes.
- [ ] NU1900 is reported separately if restore is attempted.
- [ ] Independent code review approves the implementation.
- [ ] Independent engineering judge approves all checklist items.
