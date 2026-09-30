# T6R Task Plan

| Task | Owner | Status | Dependencies | Exit evidence |
|---|---|---|---|---|
| I1 Implement frozen T6R contract | developer | DONE | frozen plan/architecture | 139/139 Signups; 337/337 Domain; build and format pass |
| V1 Independent test gate | test-engineer | DONE | I1 | 139 focused; 43 independent; 337 Domain; 450 solution; build/format/mobile/Compose pass |
| V2 Independent security gate | security-engineer | DONE | I1 | PASS; zero findings; authority attacks verified |
| V3 Independent code review | code-reviewer | FAILED | I1 | Removed maximum waitlist order is reused; CHANGES_REQUIRED |
| R1 Resolve monotonic waitlist allocation contract | architect | DONE | V3 | VP selected durable `WaitlistOrderHighWater`; authoritative artifacts and T8 contract amended |
| R2 Implement durable high-water rework | developer | PENDING | R1 | Exact three-file ownership; retained-gap/non-reuse/exhaustion tests plus focused and Domain gates pass |
| V4 Re-run invalidated independent gates | test-engineer, security-engineer, code-reviewer | PENDING | R2 | Test/security/code-review all PASS against amended contract |
| J1 Final judgment | engineering-judge | PENDING | V4 | APPROVED against amended DoD |

Execution: I1 -> parallel(V1,V2,V3) -> R1 -> R2 -> parallel(V4 test/security/code-review) -> J1.

Frozen R2 ownership:

- `src/HusayniaTabruk.Domain/Signups/HelpNeedSignups.cs`
- `tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs`
- `tests/HusayniaTabruk.Domain.Tests/Signups/T6IndependentSignupTests.cs`

No other task may edit `Domain/Signups/**` or its tests until R2 and V4 complete.

2026-08-14 22:33 PDT — Mission entered IMPLEMENTATION; frozen architecture already exists, so no new design phase is needed.
2026-08-14 23:18 PDT — I1 completed; mission entered VALIDATION and V1/V2/V3 dispatched in parallel.
2026-08-15 00:02 PDT — V1 and V2 passed. V3 failed with a reproduced monotonic waitlist-order defect; mission entered REWORK.
2026-08-15 00:24 PDT — Architect proved exact frozen invariants are mutually unsatisfiable across rehydration. Mission BLOCKED pending CTO approval of a minimal public/persistence contract amendment.
VP decision recorded — durable monotonic non-reuse with retained gaps is approved. R1 completed;
R2 is the next and only developer dispatch.
