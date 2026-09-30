# T8M migration-orchestration implementation plan

Date: 2026-08-15
Status: READY FOR T8M DISPATCH

## Frozen production interface

Production/shared databases support only:

```text
psql -X --set ON_ERROR_STOP=1 --set target_schema=<schema> -f 20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql
psql -X --set ON_ERROR_STOP=1 --set target_schema=<schema> -f 20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql
```

Both scripts validate one schema, derive the same database/schema/T8 session advisory-lock key,
hold that session lock until disconnect, qualify every fixed object, and use namespace/object OID
attestation. Production Down deletes only the T8 history row; it never drops the index or
constraint and never broadens schema, relation, sequence, column, history, or default privileges.

EF is local/disposable-only. `TabrukDbContextFactory` and T8 SQL require
`Options=-c tabruk.disposable_ef=on`. EF destructive Down additionally proves every application
table is empty. Missing/false disposable mode fails before migration selection or mutation.

## Frozen failure and observability semantics

- Attestation mismatch: SQLSTATE `P0001`,
  `T8_ATTESTATION_FAILED:<schema|temp|owner|acl|history|object|disposable>`.
- Duplicate waitlist/index conflict: SQLSTATE `23505`.
- Chronology validation failure: SQLSTATE `23514`.
- Stages: `T8_STAGE=<preflight|chronology|index|finalize|compensation|down>`.
- Results: `T8_RESULT=<success|failed|compensation_failed>`.
- History is inserted last and is the only success marker.
- Compensation removes only canonical objects absent at entry and created by this invocation.
- Compensation failure emits `T8_COMPENSATION_FAILED`, leaves history absent, retains the session
  lock until disconnect, and requires the documented owner repair procedure.
- Scripts do not retry locks or DDL. Operator repair and retry are explicit.
- Output never contains connection strings, role memberships, or ACL contents.

## T8M — bounded remediation task

Objective: replace the remaining ambiguous EF/owner production paths with the approved single
session owner orchestration while preserving the existing EF model and least privilege.

Owned files:

- `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` (new)
- `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs`
- `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs`
- `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs`
- `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md`

Read-only inputs: initial migration source/designer, corrective designer, model snapshot, EF model,
API, Domain, Application, packages, deployment configuration, and unrelated tests.

Required implementation:

1. Add one stable session advisory-lock derivation used by both owner scripts and hold it across
   all commits, concurrent DDL, compensation, and final history mutation.
2. Bind a validated target namespace OID and replace trust in `search_path`, `current_schema()`, or
   unqualified name resolution with qualified identifiers and OID/canonical-definition checks.
3. Fail closed on temp shadows, wrong owner/kind/definition, hostile history, schema ownership,
   PUBLIC schema rights, app/inherited CREATE, grant options, target/global default ACL conflicts,
   and any deviation from exact runtime relation/sequence/column privileges.
4. Make owner Up use entry-state capture, staged chronology validation, concurrent unique index,
   compensation, full final re-attestation, and history-last commit.
5. Add logical owner Down that transactionally deletes only corrective history after and before
   full attestation. Preserve the index, validated constraint, schema/default ACL controls, and
   runtime least privilege.
6. Guard EF Up/Down as disposable/local-only; schema-qualify its fixed SQL; destructive EF Down
   fails unless all application tables are empty.
7. Expand the manifest to exactly seven entries, adding the corrective designer and owner Down.
8. Replace safety guidance that publishes production EF or destructive rollback commands with the
   two owner commands, semantic-atomicity limits, stage/error meanings, compensation repair,
   logical Down/re-up, lock monitoring, and least-privilege/default-ACL prerequisites.

## Required test matrix

| Gate | Evidence |
|---|---|
| Qualification/temp | Static fixed-identifier scan plus temp shadows for every fixed name; target schema is used or execution fails before mutation. |
| Serialization | Owner Up-vs-Up and Up-vs-Down overlap on the same lock across commits/concurrent DDL; one valid history outcome. |
| History race | Pre-opened/queued app writer and competing owner history insert cannot bypass hardening/finalization. |
| ACL/default ACL | Wrong schema/object owner, PUBLIC rights, direct/inherited app CREATE, grant option, target/global defaults, and hostile role posture fail closed. |
| Compensation | Chronology, concurrent-index, final-attestation, and forced-history failures restore entry objects when created by the run; history remains absent. |
| Retry | Exact entry objects are preserved; repaired retry succeeds once and writes one history row. |
| Production Down | Only history is deleted; safety objects and least privilege remain; rerun and re-up are idempotent. |
| Disposable EF | Missing flag and nonempty destructive Down fail; empty fresh apply/down/re-up passes. |
| Manifest | Exactly seven hashes, including corrective designer and both owner scripts. |
| Live PostgreSQL | PostgreSQL 18.6 proves concurrent progress, staged validation, lock timeout, cleanup, and zero persistence skips. |

## Exit criteria and gates

T8M is implementation-complete only when the focused and full persistence suites pass on real
PostgreSQL 18.6, the full .NET build/tests/format pass, the seven-entry manifest verifies, and the
safety document contains no supported production EF/destructive rollback command.

After developer completion, independent Test Engineer, Security Engineer, and Code Reviewer gates
run in parallel. Security requires zero Critical/High findings. Review must be APPROVED. The
Engineering Judge runs last. Any failure creates T8M rework and invalidates affected downstream
evidence.

T9 and every task depending on T9 remain BLOCKED until all T8M gates and final judgment pass.

## Execution waves

`wave 1: T8M -> wave 2: independent test, security, code review (parallel) -> wave 3: engineering judgment -> wave 4: unblock T9`
