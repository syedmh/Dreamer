# T11 final durability rework

Date: 2026-08-28  
Status: implemented and causally exercised; official strict gate blocked by external `NU1900`;
independent re-review pending

## Exact guarantees implemented

### Retention sink

`FormSubmissionRetentionTarget.ApplyAsync` now holds one serializable Forms-owned transaction and
the same target application lock used by T19. Inside that transaction it:

- captures the single authoritative UTC decision time only after the target lock is acquired;
- reads the exact T19 run/item join with `UPDLOCK,HOLDLOCK`;
- requires the permit run ID, current lease token, unexpired lease, incomplete run, exact
  target/subject/due binding, and `Applying` item state;
- checks current T19 wildcard/subject holds;
- reads the exact submission with `UPDLOCK,HOLDLOCK`;
- requires `Eligible`, due timestamp equality, no entity legal hold, and a valid current rowversion;
- anonymizes values/submission and commits atomically.

The existing T19 workflow callback then marks the still-`Applying` item `Applied`. A current permit
is idempotent after a committed anonymization so crash recovery can finish bookkeeping. A reclaimed
old lease mutates zero rows; the new permit mutates once and preserves rowversion on replay.

### Dead-letter retry

Forms now finalizes one bounded `retry_authorized` T04 audit, including only a `sha256:` reason
fingerprint, before calling T19 requeue. Audit failure returns 503 while the job remains
`DeadLettered` and no T19 operational audit exists. A blocking-finalizer test proves the job remains
non-runnable until audit completion. Invalid preflight state receives one truthful failure audit.
A post-audit requeue failure leaves the truthful authorization-attempt audit and returns failure; it
is never reported as successful.

### Outermost privileged boundary

Each Forms admin handler is entered through one outer wrapper that begins correlation before and
encompasses explicit authentication/current-user lookup, security-stamp/role/MFA validation,
policy, antiforgery, body length/read/deserialization, Application/store calls, and cancellation.
Unexpected failures receive one sanitized exception audit and bounded 500. Request cancellation/
client disconnect receives one shutdown-bounded sanitized cancellation audit and 499 when writable.
Audit-finalization failure remains 503. No cancellation or dependency exception is rethrown
unaudited by the handler.

## Causal coverage

- reclaimed old retention permit: zero mutation;
- current retention permit: one mutation, idempotent replay, T19 `Applied` callback succeeds;
- global T19 hold, entity legal hold, future due, wrong status, detached/expired permit;
- audit finalizer blocked/fails before requeue, proving `DeadLettered` until audit completion;
- raw retry reason absent from durable operational audit;
- throwing endpoint authentication, UserManager lookup, policy, antiforgery, body stream, service,
  and store;
- actual request cancellation after service entry, with exactly one persisted cancellation audit.
- a real LocalDB target-lock waiter whose lease expires and is taken over before lock release:
  old permit mutates zero and time is not read before the lock;
- a real LocalDB target-lock waiter where a hold with a later `StartsAtUtc` commits while blocked:
  post-lock time observes the hold and mutation remains zero;
- an unexpired, no-hold current permit: one rowversion-changing anonymization and idempotent replay.

## Results and blocker

- Forms Domain/Application/Integration: 60 passed, 0 failed, 0 skipped.
- Architecture/frozen contracts: 13 passed, 0 failed, 0 skipped.
- Relevant T19 retention/jobs: 71 passed, 0 failed, 0 skipped.
- Relevant Identity security: 17 passed, 0 failed, 0 skipped.
- Total: 161 passed, 0 failed, 0 skipped.
- Current Infrastructure/Web/Integration compiler warning-as-error diagnostic: passed.
- Required official strict `--no-restore` build: blocked before compilation by cached `NU1900`
  because `https://api.nuget.org/v3/index.json` is unreachable.

No shared T19/Identity source, migration/snapshot, package/project/lock, `Program.cs`, commit, or push
change was made. This is implementation evidence, not self-approval.
