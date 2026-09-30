# Security Review — Final History-Writer Serialization

## SECURITY RESULT

Scope: Final PostgreSQL migration-history table-lock serialization and the T8 hostile-drift trust boundaries: transaction ordering, lock conflicts and timeout behavior, pre-opened/queued sessions, direct and inherited grants, malformed history, hostile objects, downgrade paths, schema injection, tests, documentation, and artifact manifest.

Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings:

None

All findings:

None. The prior HIGH uncommitted-history forgery race is resolved.

Conclusion: PASS

## STATUS

PASS

## SUMMARY

`LOCK TABLE ... IN SHARE ROW EXCLUSIVE MODE` is acquired before history ACL revocation in the same transaction and held through commit. PostgreSQL `SHARE ROW EXCLUSIVE` conflicts with the `ROW EXCLUSIVE` lock used by `INSERT`, so hardening waits for pre-existing writers and prevents later writers from crossing the revocation boundary. If a forged writer commits, the subsequent attestation sees the committed row and rejects the state; if it rolls back, hardening proceeds. Queued writers are denied after revocation. Lock acquisition failure aborts the transaction before revocation or attestation, which is fail-closed rather than a path to trust forged history.

Zero unresolved Critical or High findings remain.

## WORK_COMPLETED

- Independently reviewed the final lock-before-revoke transaction ordering and PostgreSQL table-lock conflict semantics.
- Reviewed pre-executed uncommitted writers, queued writers, pre-opened sessions, lock timeout/failure behavior, malformed history, privilege matrices, alternate grantors, hostile objects, downgrade paths, and schema injection.
- Reviewed the hostile-drift regression, catalog/factory controls, operational documentation, and SHA-256 manifest.
- Executed the focused race regression in the caller environment; it was skipped because `TABRUK_TEST_POSTGRES_CONNECTION` was unset.
- Executed catalog/manifest tests and independently recomputed all five manifest hashes.
- An isolated security-review execution exercised PostgreSQL 18.6 concurrency and timeout probes.
- Used no Git commands and made no production-code changes.

## EVIDENCE

Reviewed controls:

- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:126-167`
  - Begins one transaction, takes the advisory lock, takes the history-table `SHARE ROW EXCLUSIVE` lock, revokes ACLs, and commits before attestation.
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:63-67`
  - Emits the schema-qualified, identifier-quoted `LOCK TABLE ... IN SHARE ROW EXCLUSIVE MODE`.
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:209-227`
  - Revokes runtime and `PUBLIC` history-table privileges.
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs:416-740,988-1120`
  - Enforces privilege and exact migration-history attestation.
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs:42-75,243-286`
  - Validates and quotes the configured schema, preventing schema-name SQL injection.
- `HusayniaTabruk/tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs:1105-1210`
  - Executes the attacker INSERT before opening hardening, proves the owner waits, commits the forgery, and requires attestation rejection with ACLs revoked.
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md:49-135,150-230`
  - Documents the lock, failure, privilege, and operational contract.

Caller commands and outputs:

```text
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-restore --nologo --filter "FullyQualifiedName~EmptyEfBootstrapWaitsForPreRevocationWriterAndRejectsCommittedForgedHistory"
Failed: 0, Passed: 0, Skipped: 1, Total: 1
Reason: TABRUK_TEST_POSTGRES_CONNECTION was unset.
```

```text
dotnet test .\tests\HusayniaTabruk.IntegrationTests\HusayniaTabruk.IntegrationTests.csproj --no-restore --nologo --filter "FullyQualifiedName~PostgresLeastPrivilegeCatalogTests|FullyQualifiedName~PostgresMigrationArtifactManifestTests"
Failed: 0, Passed: 5, Skipped: 0, Total: 5
```

```text
PowerShell SHA-256 recomputation of every entry in T8MigrationArtifacts.sha256
Matched: 5 of 5
```

Isolated PostgreSQL 18.6 security-review evidence:

- Lock/catalog/manifest tests: 6 passed, 0 failed, 0 skipped.
- Forged-history race regression: 1 passed, 0 failed, 0 skipped.
- T8 inherited-privilege/semantic suite: 55 passed, 0 failed, 0 skipped.
- Alternate-grantor suite: 25 passed, 0 failed, 0 skipped.
- Queued-writer probe: owner completed; attacker INSERT was denied; forged-row count was 0; INSERT privilege was false.
- Timeout probe: history lock timed out and aborted before revocation; a successful retry revoked privileges; forged-row count was 0.
- PostgreSQL server version: 18.6; `tabruk_app` was `NOLOGIN`.

## ARTIFACTS

- `.ai-org/missions/2026-08-15-husaynia-t8-hostile-drift/security-review.md`
- `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`

## FINDINGS

None.

The previous HIGH attack path is closed:

1. A forged INSERT holds `ROW EXCLUSIVE` until its transaction ends.
2. Hardening cannot obtain `SHARE ROW EXCLUSIVE` while that writer remains open.
3. After the writer commits, hardening acquires the lock, revokes ACLs, and commits.
4. Pre-history attestation then observes the committed forged row and fails.
5. Writers queued behind hardening cannot proceed using the revoked privilege.

## RISKS

- A compromised writer can cause temporary hardening/migration denial of service by retaining its transaction until `lock_timeout`. The owner operation aborts before making or trusting partial state; operators must remove the blocker and retry. This does not restore the forged-history bypass.
- The caller environment did not expose PostgreSQL or Docker, so its focused live test skipped. The separate isolated PostgreSQL 18.6 probe supplied the live concurrency evidence.

## BLOCKERS

None.

## NEXT_ACTION

Proceed to the remaining independent release gates. Preserve the lock-before-revoke ordering and the pre-executed uncommitted-writer regression.
