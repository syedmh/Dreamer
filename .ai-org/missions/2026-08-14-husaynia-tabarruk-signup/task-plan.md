# Tabruk Task Plan

The canonical dependency graph, exact file ownership, frozen interfaces, exit criteria, validation commands, and execution waves are in [implementation-plan.md](implementation-plan.md).

T1  Scaffold repository and deterministic toolchain
    owner:        developer
    objective:    Create buildable .NET/mobile/test shells and local PostgreSQL.
    files:        root manifests, all project files, mobile manifests, initial route shell, README
    depends_on:   -
    parallel_ok:  no
    exit_criteria: restore/build/mobile typecheck/docker-compose config pass
    status:       PENDING

T2  Implement shared primitives, ports, and boundary tests
    owner:        backend-specialist
    objective:    Freeze executable cross-module contracts, step-up/privileged-audit ports, limits, and dependency direction.
    files:        `Domain/Common/**`, `Application/Abstractions/**`, architecture tests
    depends_on:   T1
    parallel_ok:  no
    exit_criteria: architecture and primitive tests pass
    status:       PENDING

T3  Establish API conventions and contract harness
    owner:        api-specialist
    objective:    Freeze API conventions, bounded pagination/body/rate semantics, and deterministic OpenAPI generation.
    files:        API composition/conventions/middleware/OpenAPI, contract tests, OpenAPI artifact
    depends_on:   T2
    parallel_ok:  no
    exit_criteria: convention and OpenAPI snapshot tests pass
    status:       PENDING

T4-T7  Implement disjoint domain cores
    owner:        backend-specialist
    objective:    Implement dual-control roles, dates, minimized signups, approved-only thread eligibility, and notifications in separate folders.
    files:        exact disjoint paths in `implementation-plan.md`
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: domain gate passes
    status:       PENDING

T6R Remediate signup authority with per-help-need aggregate
    owner:        developer
    objective:    Make `HelpNeedSignups` the sole authority for the complete per-need signup set, capacity, waitlist, canonical date context, chronology, and aggregate version.
    files:        `src/HusayniaTabruk.Domain/Signups/HelpNeedSignups.cs` (add); `src/HusayniaTabruk.Domain/Signups/Signup.cs`; `src/HusayniaTabruk.Domain/Signups/SignupErrorCodes.cs`; `src/HusayniaTabruk.Domain/Signups/SignupCapacity.cs` (delete); `src/HusayniaTabruk.Domain/Signups/HelpNeedSignupExtensions.cs` (delete); `tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/T6IndependentSignupTests.cs`
    depends_on:   T6
    parallel_ok:  yes, only with tasks outside `Domain/Signups/**` and its tests
    exit_criteria: frozen interface and all acceptance attacks in `implementation-plan.md` pass; unsafe public child/collection/date-context APIs and both obsolete helper files are absent; focused Signups and full Domain gates pass; no non-Domain production code is added
    status:       FAILED

T6R-HW Complete durable waitlist high-water remediation
    owner:        developer
    objective:    Persist monotonic waitlist allocation history in the aggregate so removed orders are never reused across rehydration.
    files:        `src/HusayniaTabruk.Domain/Signups/HelpNeedSignups.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Signups/T6IndependentSignupTests.cs`
    depends_on:   -
    parallel_ok:  no
    exit_criteria: exact `WaitlistOrderHighWater` property and five-argument `Rehydrate` signature compile; tests prove non-negative/high-enough hydration, retained gaps, removed-maximum non-reuse after rehydration, deterministic ordering, unchanged Food-Incharge-selected reassignment, and atomic `long.MaxValue` exhaustion; focused Signups and full Domain gates pass
    status:       PENDING

T4R  Remediate account-governance invariant boundary
    owner:        developer
    objective:    Replace caller-controlled governance with the frozen versioned aggregate and migrate/delete every unsafe T4 entry point and test.
    files:        `src/HusayniaTabruk.Domain/Accounts/OrganizationAccountGovernance.cs`; `src/HusayniaTabruk.Domain/Accounts/AccountErrorCodes.cs`; `src/HusayniaTabruk.Domain/Accounts/Membership.cs`; `src/HusayniaTabruk.Domain/Accounts/RoleChangeRequest.cs`; `src/HusayniaTabruk.Domain/Accounts/AdministratorBootstrap.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/OrganizationAccountGovernanceTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/MembershipTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/RoleChangeRequestTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/T4IndependentTests.cs`; `tests/HusayniaTabruk.Domain.Tests/Accounts/AdministratorBootstrapTests.cs` (delete after migrating coverage)
    depends_on:   T2
    parallel_ok:  yes
    exit_criteria: exact T4R acceptance suite and Domain gate in `implementation-plan.md` pass; obsolete public actor-ID/count/bootstrap/propose/approve methods are absent; no persistence/API code is added
    status:       PENDING

T8-T14  Deliver authenticated first vertical slice
    owner:        database-specialist | backend-specialist | frontend-specialist
    objective:    Persist/authenticate/step-up/publish/submit minimized pending signup and privacy-safe roster. T8 persists `date_threads.version` and makes post/report/hide/lock saves use an explicit atomic thread-version compare-and-swap.
    files:        exact serialized/disjoint paths in `implementation-plan.md`
    depends_on:   T3-T7, T4R, T6R-HW
    parallel_ok:  only by published waves
    exit_criteria: first vertical slice gate passes; T8 schema and PostgreSQL integration tests prove `date_threads.version bigint NOT NULL`, exact Domain version-formula hydration, stale post/report/hide/lock rollback with no partial child/event/outbox writes, identical lock-retry no-op without duplicate events, and conflicting lock-retry atomic failure
    status:       PENDING

T8M Remediate production T8 migration orchestration
    owner:        developer
    objective:    Implement the approved owner-script-only production Up/Down contract, session
                  advisory lock, OID/schema/default-ACL attestation, compensating history-last Up,
                  logical non-destructive Down, disposable-only EF guard, seven-entry manifest,
                  tests, and operator safety guidance.
    files:        exact ten-file ownership in
                  `.ai-org/missions/2026-08-15-husaynia-t8-migration-orchestration/task-plan.md`
    depends_on:   T8
    parallel_ok:  no
    exit_criteria: complete T8M DoD passes; production Down preserves index, validated constraint,
                  schema/default ACLs, and least privilege; independent test/security/review gates
                  pass; Engineering Judge approves.
    status:       PENDING

T15-T24  Complete coordination, communication, offline, accessibility, and operations
    owner:        backend-specialist | frontend-specialist | devops-sre-specialist
    objective:    Complete AC-6 through AC-18 plus transition-level thread authorization, audited moderation, governance, abuse controls, offline purge, and operations.
    files:        exact disjoint paths in `implementation-plan.md`
    depends_on:   T14, T6R-HW
    parallel_ok:  only by published waves
    exit_criteria: full implementation gate passes
    status:       PENDING

T25-T31  Execute CI, independent tests, documentation, security, review, QA, and judgment
    owner:        devops-sre-specialist | test-engineer | documentation-specialist | security-engineer | code-reviewer | qa-engineer | engineering-judge
    objective:    Prove the approved Definition of Done and closure of every prior High/Medium security finding with independent evidence.
    files:        scripts/workflow, acceptance tests, mission reports, documentation
    depends_on:   T24
    parallel_ok:  only by published waves
    exit_criteria: Engineering Judge verdict is APPROVED
    status:       PENDING

Pre-dispatch gate: focused security re-review of `architecture.md`, `decisions.md`, and the delivery plans must PASS.

Execution waves:

`... -> T8 -> T8M -> independent T8M test/security/review (parallel) -> T8M judgment -> T9 -> remaining published waves`

Next dispatch: **T8M only**. T9 and all transitive dependants remain blocked until the real
PostgreSQL matrix, independent security/review gates, and Engineering Judge pass.

## T10 execution log

- 2026-08-16: CTO confirmed T1-T9 approved and authorized T10 only. T10 moved to IN_PROGRESS.
- Scope remains limited to the exact T10 ownership and contracts in `implementation-plan.md`.
- 2026-08-16: T10 implementation and remediation reached 837 passing tests with build/format green.
- 2026-08-16: T10 moved to BLOCKED. Independent security re-review found one unresolved High in
  the frozen single-admin invitation contract. Recommended unblock: CTO approval for a second-admin,
  request-bound countersign before returning the redeemable invite URL.
