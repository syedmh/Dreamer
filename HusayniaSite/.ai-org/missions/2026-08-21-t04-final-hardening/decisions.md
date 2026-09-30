# Decisions

- 2026-08-21: Preserve the pre-existing Social mission artifacts; repoint only `active-mission.json`.
- 2026-08-21: Treat the CTO requirements as frozen and authoritative.
- 2026-08-21: No migration/snapshot, runtime startup DDL, package/project-file, defaults/secrets, or non-Identity edits.

# ADR-T04-01: Centralize privileged audit finalization
Date: 2026-08-21     Status: Accepted

## Context
Privileged outcomes are selected in authorization middleware, Web endpoints, Application services,
and Infrastructure stores. Only selected Web denials currently use a shutdown-linked bounded token;
request cancellation can suppress other required audits, and overlapping layers can duplicate them.

## Decision
Use one scoped `IIdentityAuditFinalizer` for every privileged outcome. It claims one
`(correlationId, action)` attempt, creates a token linked only to application shutdown with a
five-second timeout, invokes the existing writer once, and optionally commits the surrounding
transaction with the same token.

## Consequences
Disconnects cannot cancel selected-outcome audits, duplicate layer writes are blocked, and audit
failure prevents transactional success. Callers must obey the explicit ownership matrix and must
not catch finalization failure as a reason to audit again.

## Alternatives considered
- Keep per-layer cancellation helpers - rejected because policy and exactly-once behavior would
  remain fragmented.
- Move every audit to the HTTP boundary - rejected because mutation and audit would no longer share
  the store transaction.
- Add a queue/outbox - rejected because it adds infrastructure and does not meet the smallest-change
  constraint.

# ADR-T04-02: Use a SQL fixed-window limiter with keyed client fingerprints
Date: 2026-08-21     Status: Accepted

## Context
The limit must be global across hosts, race-free, privacy preserving, and enforced before
credential/token/OTP verification. The repository already depends on SQL Server but has no shared
cache or trusted proxy configuration.

## Decision
Store one fixed-window row per endpoint family and HMAC-SHA-256 client fingerprint. Serialize each
partition with a SQL Server serializable transaction plus `UPDLOCK,HOLDLOCK`, fail closed on store
failure, and derive fingerprints only from the server-established remote address.

## Consequences
At most the configured limit passes across hosts and spoofed forwarding headers are irrelevant.
The design requires one externally supplied shared HMAC key and accepts fixed-window boundary burst
characteristics.

## Alternatives considered
- In-memory ASP.NET rate limiting - rejected because hosts would not share a limit.
- Redis/distributed cache - rejected because it introduces an external service and cost.
- Unkeyed SHA-256 of IP addresses - rejected because the IPv4 input space is cheaply reversible.
- Sliding-window request rows - rejected because extra storage and cleanup complexity provide no
  acceptance benefit.

# ADR-T04-03: Keep schema delivery in T18 with deterministic T04 SQL
Date: 2026-08-21     Status: Accepted

## Context
T04 must protect the bootstrap seal and describe the limiter schema, while T18 exclusively owns
migrations/snapshots and runtime startup must not need schema-alter permission.

## Decision
Extend `IdentityAuditDatabaseInvariant.InstallSql/DownSql` to own the audit trigger, permanent-seal
trigger, and limiter table/constraints/index. The permanent-seal trigger rejects UPDATE/DELETE only
when the statement's `deleted` rows include the authoritative singleton key `Id = 1`; it does not
turn the table into a general append-only table. T04 supplies matching EF configuration and
executable fixture coverage; only T18 applies the migration SQL outside tests.

## Consequences
The handoff is exact and repeatable without runtime DDL. Migration Down drops only T04-owned
ephemeral limiter state and the two triggers; it does not remove Identity account, seal, or audit
data. The predicate protects mutation, deletion, and key reassignment of the known seal while
allowing statements that affect only non-authoritative rows.

## Alternatives considered
- Execute SQL during host startup - rejected because it violates least privilege and AC-04.
- Let T18 independently invent object names/schema - rejected because implementation/tests and
  migration could drift.
- Add the migration in T04 - rejected because ownership is explicitly frozen to T18.
- Reject every UPDATE/DELETE on `IdentityBootstrapState` - rejected because AC-01 scopes the
  invariant to singleton `Id = 1`; table-wide blocking grants no additional protection to the
  authoritative seal and unnecessarily constrains unrelated rows.

## Amendment — 2026-08-21

The accepted exact SQL is clarified to include
`IF EXISTS (SELECT 1 FROM deleted WHERE [Id] = 1)` before error `51005`. This resolves the earlier
architecture-text omission and aligns the decision with AC-01, the landed deterministic constant,
the executable exact-SQL test, and the frozen T18 handoff. The amendment does not change T18
ownership, object names, error semantics, deployment sequencing, or rollback behavior.

# Completion

- 2026-08-21: Final judge APPROVED the mission after one rework cycle.
- The shared `.ai-org/active-mission.json` was not overwritten at completion because another
  concurrent mission was already in `JUDGMENT`; this mission's terminal state is persisted in
  `task-plan.md` and `final-verdict.md`.
