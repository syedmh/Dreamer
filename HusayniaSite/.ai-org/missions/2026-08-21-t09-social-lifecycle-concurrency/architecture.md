# Social restart/successor deduplication

## Current state — verified facts

- **FACT:** A Social handler persists refresh state, enqueues its successor, and only then returns for
  T19 to complete/fail the current job
  (`src/Husaynia.Application/Social/SocialRefreshJobs.cs:113-160`;
  `src/Husaynia.Application/Operations/Jobs/DurableJobProcessor.cs:216-250`).
- **FACT:** Handler scheduling currently keys the successor by the current `JobInstanceId`, while
  startup omits that value and keys from snapshot/recovery state. The two paths therefore cannot
  deduplicate after a restart
  (`src/Husaynia.Application/Social/SocialRefreshJobs.cs:128-137,150-159,248-282,302-330`).
- **FACT:** The durable payload is strictly bounded and contains only the normalized provider
  (`src/Husaynia.Application/Social/SocialRefreshJobs.cs:36-81`).
- **FACT:** A non-rate-limited failure currently writes `RetryAfterUtc = null`; this can erase the
  recovery generation while T19 is retrying the same job
  (`src/Husaynia.Domain/Social/SocialFeedEntities.cs:227-248`).
- **FACT:** Scheduling currently composes two separate reads. `ReadAsync` uses the provider shared
  lock, but `ReadRefreshStateAsync` is a separate unlocked query, so scheduling can observe a torn
  snapshot/state pair (`src/Husaynia.Application/Social/SocialRefreshJobs.cs:274-285`;
  `src/Husaynia.Infrastructure/Social/SocialPersistence.cs:90-174`).
- **FACT:** T19 supplies both `JobInstanceId` and the durable `IdempotencyKey` to the handler
  (`src/Husaynia.Application/Operations/Jobs/JobContracts.cs:168-172`;
  `src/Husaynia.Application/Operations/Jobs/DurableJobProcessor.cs:154-161`).
- **FACT:** T19 deduplicates on definition plus idempotency key, returns the existing job when the
  payload is identical, and does not compare correlation or `NotBeforeUtc`
  (`src/Husaynia.Infrastructure/Operations/Jobs/EfDurableJobStore.cs:71-145`;
  `src/Husaynia.Infrastructure/Operations/Persistence/OperationsEntityConfigurations.cs:25-35`).
- **FACT:** Social startup already retries only definition-registration `PersistenceFailure` with a
  fresh scope; this design does not change that behavior
  (`src/Husaynia.Infrastructure/Social/SocialRefreshJobStartupService.cs:9-61`).

## Binding invariant

For one provider and one logical due generation, startup, handler successor scheduling, handler
retry, and recovery use one canonical idempotency key. A handler may call the provider only while
its durable job key still denotes the current persisted Social generation; after state advances, a
retry only repairs/deduplicates the successor and reproduces the persisted outcome.

Logical generations remain local and event-based, not globally aligned:

- missing snapshot: `social-refresh:{provider}:missing`;
- active snapshot: `social-refresh:{provider}:v{version}:e{expiryUtcTicks}`;
- recovery: `social-refresh:{provider}:recovery:v2:not-before:{retryAfterUtcTicks}`.

The recovery key deliberately excludes mutable attempt timestamps. Its bounded retry timestamp is
retained as the generation anchor through nonterminal T19 retries, even after that timestamp is in
the past.

## Desired state and delta

1. Add `SocialRefreshSchedulingState` and
   `ISocialSnapshotStore.ReadSchedulingStateAsync(provider, cancellationToken)`. The EF
   implementation reads active snapshot version/expiry plus refresh state under one short shared
   provider-lock transaction; it never loads items.
2. Replace handler-only `schedulingGeneration: Guid?` with canonical state scheduling:
   `EnsureScheduledAsync(string provider, CancellationToken cancellationToken)`.
3. Add:

   ```csharp
   Task<SocialRefreshJobExecutionPlan> PrepareExecutionAsync(
       string provider,
       JobExecutionContext context,
       CancellationToken cancellationToken);
   ```

   `RefreshRequired=true` only when the current job still represents the persisted generation.
   Otherwise the coordinator enqueues the canonical current generation and returns the already
   persisted outcome for the handler to reproduce.
4. Extend the internal refresh contract without breaking existing callers:

   ```csharp
   Task<SocialRefreshReceipt> RefreshAsync(
       string provider,
       CancellationToken cancellationToken,
       TimeSpan? terminalRecoveryDelay = null);
   ```

   On the final T19 attempt, the handler passes the definition maximum backoff. Timeout,
   unavailable, and malformed failures then persist the new recovery timestamp in the same Social
   transaction that records the failure. Nonfinal failures carry forward the prior bounded
   recovery anchor; success clears it and rate-limit replaces it with the bounded provider delay.
   The handler no longer performs the later `DeferUntilAsync` repair.
5. Keep `SocialRefreshJobPayload` provider-only and keep definition/handler names, configuration,
   schema, deployment shape, correlation bounds, immediate catch-up, and T19 code unchanged.

## Execution and failure semantics

- Current generation: perform the remote refresh outside every transaction/lock, then schedule
  from the resulting persisted state.
- State already advanced by success or rate-limit: do not refresh; ensure the successor and return
  success.
- State already advanced by terminal timeout/unavailable/malformed recovery: do not refresh;
  ensure the recovery successor and return the matching failure so T19 still dead-letters.
- Nonterminal failure: the generation anchor is unchanged, so T19 retry and fresh startup both
  deduplicate to the current job.
- Enqueue failure: throw; T19 retries. The next preflight repairs the successor without repeating a
  refresh whose terminal state was already persisted.
- Cancellation continues through T19 release/retry behavior; no new catch or suppression is added.
- T19's first successful enqueue still wins correlation and `NotBeforeUtc`; all contenders derive
  the same future due time except immediate catch-up, where the first current time wins.

## Cross-cutting

- **Observability:** Existing Social dependency metrics occur only for a real provider attempt;
  replay suppression must not emit a false dependency call. Existing T19 job outcome/attempt
  metrics and correlation propagation remain authoritative.
- **Security/trust:** The payload remains the existing strictly validated provider-only JSON. Keys
  contain only normalized provider names, versions, and UTC ticks; no credentials, feed content, or
  unbounded values cross the durable-job boundary.
- **Concurrency:** Social obtains only a short shared provider lock to read scheduling state. It
  releases that lock before either the provider call or T19 enqueue, avoiding a cross-store
  transaction and remote work under lock.
- **Error semantics:** Scheduling-state or legacy T19 state lookup failure aborts the handler/startup
  attempt; enqueue failure still throws. T19 therefore applies its existing retry, lease-recovery,
  and dead-letter rules rather than Social inventing another retry loop.

## Compatibility and migration

- No database, configuration, dependency, service, or external contract migration is required.
- Existing snapshot/missing keys are already canonical.
- Existing old recovery keys are recognized as aliases, enqueue the v2 recovery key, and complete
  without a provider call.
- Existing `generation:{guid}` jobs use T19 `GetStateAsync(JobInstanceId)` only during transition:
  `NextRunAtUtc` is the durable scheduled marker. A success at/after it or a replacement recovery
  after it is treated as already processed; otherwise the legacy job executes once and emits only
  canonical successors.
- Payload compatibility is unchanged in both directions. Rollback is code-compatible, but should
  drain Social jobs first because old code does not preserve the v2 recovery invariant.

## Tests and validation required

- Causal success, rate-limit, and terminal-recovery tests: handler enqueues successor, a fresh
  startup runs before due, the second receipt is duplicate, and only one durable key/job remains.
- Crash replay test: after successor enqueue, execute the same current job again; provider-call and
  snapshot-version counts do not increase, and the successor remains singular.
- Recurrence test: execute that successor; it refreshes once and creates one distinct later
  generation.
- Recovery retry test: a due recovery job has a nonterminal failure, then fresh startup; both retain
  the same recovery key and no second chain appears.
- Persistence tests prove atomic scheduling-state reads, retained past recovery anchors, and atomic
  final-attempt recovery replacement.
- Run focused Social Application/Integration tests, focused Operations.Jobs
  Application/Integration regressions, then:

  `dotnet build .\HusayniaSite.sln --no-restore --configuration Release -warnaserror --nologo`

## Tradeoffs and risks

- This adds one short Social shared-lock read before refresh. It is preferred over torn scheduling
  state and never encloses a provider or T19 call.
- `RetryAfterUtc` may remain in the past while it anchors an in-flight generation. Suppression still
  occurs only when it is greater than `now`; tests must lock this semantic.
- A payload generation field was rejected: T19 already durably supplies the key, while adding a
  field would make old strict payload readers reject new queued jobs and can cause
  `DuplicateConflict` during mixed-version rollout.
- Job-instance-only keys, global aligned schedules, a new outbox/service, and a Social schema column
  were rejected as respectively restart-unsafe, catch-up delaying, disproportionate, and outside
  the minimal migration-free fix.
