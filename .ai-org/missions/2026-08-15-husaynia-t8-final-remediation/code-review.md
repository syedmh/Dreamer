# Code Review — Rework Required

## Verdict
CHANGES_REQUIRED

## Blocking finding
**High — EF idempotent corrective upgrade script is not executable on PostgreSQL.**

`20260815102612_T8CorrectivePostgresHardening.cs:54-60` uses a transaction-suppressed
`CREATE UNIQUE INDEX CONCURRENTLY`. EF places that statement inside its `DO $EF$` guard in
a `dotnet ef migrations script --idempotent` script. PostgreSQL 18.6 rejects it because
concurrent index creation cannot execute in a function/transaction context.

Independent reproduction:

```text
dotnet ef migrations script 20260815075156_InitialPostgresSchema
  20260815102612_T8CorrectivePostgresHardening --idempotent
psql < generated-script.sql
ERROR: CREATE INDEX CONCURRENTLY cannot be executed from a function
```

Remediation: provide and exercise an owner-run idempotent corrective upgrade path whose
concurrent DDL is outside EF's `DO` wrapper.

## Non-blocking follow-up
**Medium — no disposal-failure regression.**

`PostgresOwnedTransactionCleanup.cs:45-54` handles `DisposeAsync` failure, while
`PostgresTransactionCleanupTests.cs` only injects rollback failure. Add a disposal-failure
test proving the original cancellation/domain exception remains primary and records cleanup
failure data.

## Positive evidence
- Isolated PostgreSQL 18.6 persistence suite: 52 passed, 0 skipped.
- Full .NET suite: 564 passed, 0 skipped.
- Initial artifacts showed no T8 references and match the manifest.

## Rework 1 re-review

The original missing disposal-failure regression is resolved. The owner script happy path is
executable and idempotent, but two new findings require rework:

1. **High — guard exits falsely report success.**
   `20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql:16,32,56,72`
   uses `\quit 3`. PostgreSQL 18.6 `psql` ignores the argument, so a rejected preflight exits
   zero under automation. Reproduction emitted `warning: \quit: extra argument "3" ignored`
   and `FIRST_SCRIPT_EXIT=0`. Use a real SQL error under `ON_ERROR_STOP` and test the
   nonzero exit with history still absent.
2. **Medium — public-schema documentation leaves `search_path` ambient.**
   The public schema example tells the operator to omit an explicit search path while the script
   uses unqualified objects. Require a single explicit intended schema even for `public`, and
   add script-side/assertion coverage sufficient to prevent silently hardening another schema.

## Rework 2 final re-review

## Verdict
APPROVED

The final independent reviewer found no issues. The owner script now raises real SQL errors under
`psql -X -v ON_ERROR_STOP=1`, producing nonzero exit code 3 for missing-initial and
mismatched-schema guards. It requires matching explicit `tabruk.target_schema` and a
single-schema `search_path`; focused real PostgreSQL 18.6 tests prove alternate-schema
hardening is rejected. The six-file manifest matches and disposal-failure regressions preserve
the original exception.
