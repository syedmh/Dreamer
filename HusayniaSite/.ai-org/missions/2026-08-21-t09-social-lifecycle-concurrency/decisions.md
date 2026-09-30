# ADR-1: Use persisted Social generations for every durable refresh key
Date: 2026-08-21     Status: Accepted

## Context

Handler successors use a job-instance generation while startup uses snapshot/recovery state, so a
restart can enqueue a second recurrence chain. T19 already gives handlers the durable idempotency
key and deduplicates identical payloads by that key.

## Decision

Every new Social job uses the canonical persisted-state generation key. Before any provider call,
Social compares that durable key with one atomic scheduling-state read; an advanced generation
repairs the canonical successor without refreshing. Existing key formats receive bounded
Social-owned transition handling.

## Consequences

Startup, retry, recovery, and handler scheduling converge without T19 changes. Scheduling requires
one additional short database read and a small legacy-key transition path.

## Alternatives considered

- Current `JobInstanceId` successor key — rejected because startup cannot derive it after restart.
- Add the generation to the JSON payload — rejected as redundant and mixed-version/rollback unsafe
  with the existing strict payload parser and T19 payload-conflict semantics.
- Globally aligned time buckets — rejected because they delay immediate catch-up and churn at
  boundaries.

# ADR-2: Persist terminal recovery atomically and retain the recovery anchor across retries
Date: 2026-08-21     Status: Accepted

## Context

Recovery identity currently includes mutable attempt time, and a non-rate-limited failure clears
`RetryAfterUtc`. A separate handler `DeferUntilAsync` leaves a crash window between recording the
final failure and making startup able to derive its successor.

## Decision

Recovery identity is the bounded retry timestamp only. Nonterminal failures retain the current
bounded timestamp as their generation anchor; final timeout/unavailable/malformed failures replace
it with `attemptedAt + maximumBackoff` in the same failure transaction. Success clears the anchor
and rate-limit replaces it with its bounded provider/fallback timestamp.

## Consequences

T19 retries and startup retain one generation, and final-attempt crashes remain recoverable without
a schema or cross-store transaction. `RetryAfterUtc` can be historical after it has served as a
delay, so consumers must continue comparing it to the current time before suppressing work.

## Alternatives considered

- Keep the post-refresh `DeferUntilAsync` repair — rejected because the crash window remains.
- Add a persisted scheduled-generation column/outbox — rejected because it requires a schema or
  global persistence migration disproportionate to this Social-only fix.
- Enqueue while holding the Social lock — rejected because it couples stores and increases
  deadlock/latency risk.
