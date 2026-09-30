# Task Plan

T8M  Remediate production T8 migration orchestration
    owner:        developer
    objective:    Make checked-in owner psql scripts the sole production Up/Down path with one
                  session advisory lock, OID-qualified attestation, exact schema/default ACL
                  controls, compensating concurrent-index semantics, history-last success, logical
                  non-destructive production Down, and disposable-only EF execution.
    files:        `src/HusayniaTabruk.Infrastructure/Migrations/PostgresLeastPrivilegeCatalog.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-idempotent.sql`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.owner-downgrade.sql` (new);
                  `src/HusayniaTabruk.Infrastructure/Migrations/20260815102612_T8CorrectivePostgresHardening.cs`;
                  `src/HusayniaTabruk.Infrastructure/Persistence/TabrukDbContextFactory.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/T8MigrationArtifacts.sha256`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationArtifactManifestTests.cs`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresMigrationAndSchemaTests.cs`;
                  `tests/HusayniaTabruk.IntegrationTests/Persistence/PostgresLeastPrivilegeCatalogTests.cs`;
                  `src/HusayniaTabruk.Infrastructure/Migrations/InitialPostgresSchema.safety.md`
    depends_on:   T8 approved architecture; existing T8 remediation baseline
    parallel_ok:  no
    exit_criteria: all contracts and the full test matrix in `implementation-plan.md` pass on real
                  PostgreSQL 18.6; manifest contains exactly seven verified entries; production
                  Down deletes only T8 history and preserves safety objects/least privilege;
                  production documentation exposes no EF or destructive rollback path; independent
                  test/security/review gates pass and Engineering Judge approves.
    status:       PENDING

Execution waves:

`wave 1: T8M -> wave 2: independent test, security, code review (parallel) -> wave 3: engineering judgment -> wave 4: T9`

Frozen interface: `implementation-plan.md` is binding. Any change to script names, lock key,
`target_schema`, disposable flag, error/stage/result strings, seven-artifact manifest, or logical
Down semantics stops the wave and requires Tech Lead replan.

Next dispatch: **T8M only**. T9 and all transitive dependants remain blocked until the independent
gates and Engineering Judge pass.
