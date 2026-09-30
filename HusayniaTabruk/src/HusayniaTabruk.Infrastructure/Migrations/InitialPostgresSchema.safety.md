# PostgreSQL migration safety contract

T8 production migration is owned by the checked-in `psql` scripts.  EF is intentionally limited
to disposable databases and is never a production upgrade, rollback, or recovery mechanism.

## Production commands

Run each command in a fresh owner connection.  Do not use `--single-transaction`/`-1`; the session
advisory lock must survive the transaction boundaries and the concurrent index build.

```powershell
psql -X --set ON_ERROR_STOP=1 --set target_schema=tabruk_prod `
  --file src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql

psql -X --set ON_ERROR_STOP=1 --set target_schema=tabruk_prod `
  --file src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql
```

The connection must be the migration owner, must resolve the declared schema, and must not expose
application objects through an ambient `search_path`.  The scripts capture the target namespace
OID, set `search_path` to `pg_catalog`, and use `:"target_schema"."object"` for every fixed
application relation.  Owner setup normalizes only target-schema default privileges.  Unsafe
owner-global defaults fail closed before history mutation and are never rewritten by T8; no role
membership or hostile object is repaired implicitly.

## Lock and atomicity contract

Both scripts derive the same database/namespace/T8 advisory-lock key and acquire a **session**
advisory lock before preflight.  It remains held over:

1. preflight and least-privilege changes;
2. every committed transaction;
3. chronology creation and validation;
4. `CREATE INDEX CONCURRENTLY`;
5. compensation and re-attestation; and
6. the final history mutation.

The history row is written last and is the only success marker.  PostgreSQL transaction atomicity
does not span concurrent DDL; the scripts provide semantic atomicity by recording entry state and
removing only canonical objects created by the current invocation.  A failed compensation emits
`T8_COMPENSATION_FAILED`, leaves history absent, retains the session lock until disconnect, and
requires owner repair before retry.

Index compensation is exceptional and may briefly block writers.  It opens one transaction, takes
`ACCESS EXCLUSIVE` on the owning table, rechecks that the invocation-captured index OID still names
the exact canonical PostgreSQL object, and performs a non-concurrent schema-qualified drop before
releasing the lock.  A same-name index created or replaced by another session is never claimed or
dropped; the script fails closed with history absent for explicit owner recovery.

Constraint compensation follows the same ownership rule.  The invocation captures the newly
created constraint OID immediately, persists it in session state, and under `ACCESS EXCLUSIVE`
table lock drops only that exact OID after rechecking the canonical definition.  A same-name
replacement with a different OID survives every validation, index, and finalization cleanup path;
history remains absent until an owner repairs or accepts the preserved canonical replacement.

Scripts emit:

```text
T8_STAGE=preflight|chronology|index|finalize|compensation|down
T8_RESULT=success|failed|compensation_failed
```

They do not retry locks or DDL and never print connection strings, role membership, or ACL
contents.  `P0001` with `T8_ATTESTATION_FAILED:<schema|temp|owner|acl|history|object|disposable>`
is a fail-closed attestation error.  Duplicate waitlist data is `23505`; chronology validation is
`23514`.

## Required posture

Before mutation the owner script verifies:

- the target schema exists and is owned by `current_user`;
- every managed table, sequence, history object, and existing T8 object has the expected owner,
  namespace OID, relation kind, topology, and canonical definition;
- `pg_temp` cannot shadow a fixed name;
- `tabruk_app` exists as `NOLOGIN` and is not superuser, createdb, createrole, replication,
  bypass-RLS, or the migration owner;
- PUBLIC has neither schema `USAGE` nor `CREATE`;
- `tabruk_app` has direct schema `USAGE`, no effective schema `CREATE`, and no grant option;
- effective target and owner-global default ACLs, including PostgreSQL defaults when catalog rows
  are absent, grant nothing to PUBLIC or `tabruk_app`/inherited roles for tables, sequences,
  functions, or types; target-schema rows may be normalized, while unsafe global rows abort
  unchanged; and
- PostgreSQL 18 table, column, sequence, PUBLIC, inherited, and grant-option privileges exactly
  match the runtime allowlist.

The runtime allowlist is CRUD on catalogued application tables, `INSERT` only on
`audit_events`/`privileged_access_events`, `USAGE, SELECT` on the two identity sequences, and no
privilege on migration history or other target-schema relations.

## EF disposable path

EF refuses to create a migration context unless the connection has exactly one matching schema
and exactly one option:

```text
-c tabruk.target_schema=<schema> -c tabruk.disposable_ef=on
```

The T8 SQL repeats this guard.  EF destructive `Down` additionally proves every application table
is empty before dropping the T8 objects.  Use EF only for fresh/empty local or CI databases:

```powershell
dotnet ef database update --project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj `
  --startup-project src/HusayniaTabruk.Infrastructure/HusayniaTabruk.Infrastructure.csproj
```

The command above is not a production command.  Production rollback is the logical owner
downgrade below, not destructive object removal.

## Logical production Down and re-up

`owner-downgrade.sql` takes the same session lock, fully re-attests the hardened state, deletes only
the corrective history row, re-attests the index, validated chronology constraint, schema/default
ACLs, and least privilege, then commits.  It never drops the index or constraint, so older
application versions retain the safety checks.  Re-running Down is idempotent; re-running owner Up
reuses the exact objects and writes the history row last.

If a run fails, repair the reported cause and retry.  Duplicate waitlist positions must be fixed
before retry.  Invalid concurrent-index debris may be removed only after the owner verifies its
canonical T8 identity and confirms it was created by the failed invocation.  A wrong same-name
object, hostile history, ACL/default-ACL conflict, or compensation failure is preserved for
investigation and requires an explicit owner repair plan.

Before rollout, check disk/WAL headroom, clear unexpectedly long transactions, schedule a
low-DDL window, and monitor `pg_stat_progress_create_index`, locks, disk, WAL, and the stage/result
output.
