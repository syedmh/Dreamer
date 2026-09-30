# T19 task plan

Frozen interfaces: see `design.md`, section **Implementation-ready plan — 2026-08-19 (frozen for T19)**.

Execution waves:
- wave 1: `T19-IMPLEMENT-APP`, `T19-IMPLEMENT-API`, `T19-IMPLEMENT-INTEGRATION` (parallel; each must capture focused red evidence before production code goes green)
- wave 2: `T19-IMPLEMENT-CLIENT`
- wave 3: `T19-VALIDATE-TEST`, `T19-VALIDATE-SECURITY`, `T19-VALIDATE-REVIEW` (parallel)
- wave 4: `T19-QA`
- wave 5: `T19-JUDGE`

T19-ARCH
    owner:        architect
    objective:    Ground the approved T19 design in the real codebase, confirm no migration, and authorize only the minimal sender-display lookup scope expansion.
    files:        .ai-org/missions/2026-08-19-husaynia-t19/architecture.md; .ai-org/missions/2026-08-19-husaynia-t19/decisions.md
    depends_on:   -
    parallel_ok:  no
    exit_criteria: architecture.md and decisions.md prove existing thread/audit/step-up persistence, preserve T17 no-backfill behavior, and record D-001/D-002.
    status:       DONE

T19-PLAN
    owner:        tech lead
    objective:    Freeze exact T19 interfaces, algorithms, file ownership, red-first test order, generated-client/OpenAPI steps, and implementation waves without changing production/test code.
    files:        .ai-org/missions/2026-08-19-husaynia-t19/design.md; .ai-org/missions/2026-08-19-husaynia-t19/task-plan.md
    depends_on:   T19-ARCH
    parallel_ok:  no
    exit_criteria: design.md contains the frozen implementation-ready plan; task-plan.md contains dependency-ordered executable work; hidden interface conflicts are explicitly documented.
    status:       DONE

T19-IMPLEMENT
    owner:        developer
    objective:    Deliver T19 through the frozen APP/API/INTEGRATION/CLIENT sequence with red-first evidence, no migration, and no scope expansion beyond the approved identity partial and generated artifacts.
    files:        see T19-IMPLEMENT-APP, T19-IMPLEMENT-API, T19-IMPLEMENT-INTEGRATION, T19-IMPLEMENT-CLIENT
    depends_on:   T19-PLAN
    parallel_ok:  no
    exit_criteria: all T19-IMPLEMENT-* tasks are DONE and their focused suites are green after retained red evidence.
    status:       PENDING

T19-IMPLEMENT-APP
    owner:        developer
    objective:    Add red-first Application/Threads contracts/service plus the approved read-only sender-display lookup, preserving live ordinary authorization, exact duplicate semantics, and privileged audit hashing.
    files:        src/HusayniaTabruk.Application/Threads/ThreadContracts.cs; src/HusayniaTabruk.Application/Threads/ThreadMembershipLookupPorts.cs; src/HusayniaTabruk.Application/Threads/ThreadStepUpPurposes.cs; src/HusayniaTabruk.Application/Threads/ThreadService.cs; src/HusayniaTabruk.Infrastructure/Identity/Services/PostgresAuthenticationMembershipRepository.Threads.cs; tests/HusayniaTabruk.Application.Tests/Threads/ThreadServiceTests.cs
    depends_on:   T19-PLAN
    parallel_ok:  yes
    exit_criteria: focused application thread tests show saved red evidence first, then pass green; no migration exists; no notification/outbox/push behavior is added.
    status:       PENDING

T19-IMPLEMENT-API
    owner:        developer
    objective:    Add red-first `/dates/{dateId}/thread/*` and `/admin/moderation/thread-reads` endpoints, freeze status/header/body handling, and regenerate the OpenAPI snapshot without touching non-thread endpoint files.
    files:        src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpointSupport.cs; src/HusayniaTabruk.Api/Endpoints/V1/Threads/ThreadEndpoints.cs; tests/HusayniaTabruk.Api.ContractTests/Threads/ThreadEndpointContractTests.cs; docs/api/openapi.json
    depends_on:   T19-PLAN
    parallel_ok:  yes
    exit_criteria: focused contract tests show saved red evidence first, then pass green; OpenAPI snapshot contains the six frozen T19 operations and required headers.
    status:       PENDING

T19-IMPLEMENT-INTEGRATION
    owner:        developer
    objective:    Add red-first real PostgreSQL 18.6 thread integration coverage for live authorization transitions, privileged rollback, privacy/no-outbox guarantees, and the explicit T17 no-backfill proof.
    files:        tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTests.cs; tests/HusayniaTabruk.IntegrationTests/Threads/ThreadIntegrationTestSupport.cs
    depends_on:   T19-PLAN
    parallel_ok:  yes
    exit_criteria: focused PG thread tests show saved red evidence first, then pass green with TABRUK_TEST_POSTGRES_CONNECTION; the existing close-without-thread regression is rerun explicitly.
    status:       PENDING

T19-IMPLEMENT-CLIENT
    owner:        developer
    objective:    Regenerate the mobile API contract client for the six frozen T19 operations, including additive ETag/version envelopes for ordinary thread methods, without changing feature screens.
    files:        apps/mobile/src/core/api/generate-api-client.mjs; apps/mobile/src/core/api/generated/api-contract-client.ts; apps/mobile/src/core/api/auth-api.ts; apps/mobile/tests/unit/api/generated-api-client.test.ts
    depends_on:   T19-IMPLEMENT-API
    parallel_ok:  no
    exit_criteria: generated client/runtime tests pass, `npm --prefix .\\apps\\mobile run check:generated-client` is green, and existing T18 signatures remain source-compatible.
    status:       PENDING

T19-VALIDATE-TEST
    owner:        test-engineer
    objective:    Independently validate focused T19 suites, the explicit T17 regression rerun, and the full solution/mobile regression commands with real executed counts.
    files:        -
    depends_on:   T19-IMPLEMENT-APP, T19-IMPLEMENT-API, T19-IMPLEMENT-INTEGRATION, T19-IMPLEMENT-CLIENT
    parallel_ok:  yes
    exit_criteria: focused Application/API/PG thread suites, the explicit T17 regression, full solution build/test/format, generated-client drift check, and mobile lint/typecheck/jest all execute and pass with reported evidence.
    status:       PENDING

T19-VALIDATE-SECURITY
    owner:        security-engineer
    objective:    Independently review the T19 thread/privileged-read change for auth, step-up, data exposure, logging, and transaction fail-closed behavior.
    files:        -
    depends_on:   T19-IMPLEMENT-APP, T19-IMPLEMENT-API, T19-IMPLEMENT-INTEGRATION
    parallel_ok:  yes
    exit_criteria: zero unresolved Critical/High findings remain on the T19 scope.
    status:       PENDING

T19-VALIDATE-REVIEW
    owner:        code-reviewer
    objective:    Independently review the implemented T19 slices for correctness, architecture adherence, and regression risk.
    files:        -
    depends_on:   T19-IMPLEMENT-APP, T19-IMPLEMENT-API, T19-IMPLEMENT-INTEGRATION, T19-IMPLEMENT-CLIENT
    parallel_ok:  yes
    exit_criteria: independent review returns APPROVED with exact evidence or drives rework.
    status:       PENDING

T19-QA
    owner:        qa-engineer
    objective:    Validate the end-to-end ordinary and privileged thread journeys against the running system, including concealment, rollback, and privacy behavior.
    files:        -
    depends_on:   T19-VALIDATE-TEST, T19-VALIDATE-SECURITY, T19-VALIDATE-REVIEW
    parallel_ok:  no
    exit_criteria: ordinary participant, ordinary concealment, manager moderation, and privileged admin-read journeys pass with reproducible evidence.
    status:       PENDING

T19-JUDGE
    owner:        engineering-judge
    objective:    Verify the original T19 Definition of Done against the real evidence from implementation, validation, QA, and preserved red-first history.
    files:        -
    depends_on:   T19-QA
    parallel_ok:  no
    exit_criteria: final verdict proves every T19 requirement, including no migration, no notification/push expansion, and preserved T17 behavior.
    status:       PENDING
