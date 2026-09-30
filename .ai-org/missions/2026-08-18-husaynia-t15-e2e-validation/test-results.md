# T15 AC-6 False-Positive Remediation Test Gate

Date: 2026-08-18

## Result

**PASS** — the approve/decline/waitlist integration scenario now authenticates each
actual target primary member and proves exact agreement across the decision response,
that member's `/api/v1/signups/mine` projection, the operator roster, and PostgreSQL.

Production code changed: **No**.

## Red-before proof

The corrected positive member-projection assertion was first run while the scenario
still used the original seeded member token.

```text
Failed: 1, Passed: 0, Skipped: 0, Total: 1
System.InvalidOperationException: Sequence contains no matching element
SignupDecisionIntegrationTests.cs:85
```

This proves the former `DoesNotContain` assertion was a false positive and that the
corrected assertion detects the identity/setup defect.

## Executed gates

Environment: fresh isolated PostgreSQL 18.6 cluster; `tabruk_app NOLOGIN`;
`TABRUK_TEST_POSTGRES_CONNECTION` and `TABRUK_TEST_PSQL_PATH` supplied.

| Gate | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Corrected AC-6 scenario | 1 | 0 | 0 |
| Focused `Category=T15` | 19 | 0 | 0 |
| Operator/member `Category=T15QA` | 2 | 0 | 0 |
| OpenAPI checked-in snapshot drift | 1 | 0 | 0 |
| Full solution | 967 | 0 | 0 |

Additional gates:

- `dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror`: succeeded,
  0 warnings, 0 errors.
- `dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes
  --verbosity minimal`: exit 0.
- `git diff --check`: exit 0.

## Acceptance coverage

- AC-6 approve: exact response/member/roster/database identity, status, child
  version, root signup version, and waitlist order agreement — PASS.
- AC-6 decline: same exact agreement — PASS.
- AC-6 waitlist: same exact agreement — PASS.
- In-app notification and push intent: existing focused T15 atomic-effect scenario
  remains green — PASS.

## Durable gate todo

- [x] Correct T15 AC-6 member projection false positive and rerun current gates.
- [ ] T16 readiness remains pending independent review/judge.
