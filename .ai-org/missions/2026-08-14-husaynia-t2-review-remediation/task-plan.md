# Task Plan

| Task | Owner | Status | Exit criteria | Dependencies |
|---|---|---|---|---|
| A1 Design T2 remediations | architect | DONE | Code-grounded contract design and file-level plan | - |
| I1 Implement findings and tests | developer | DONE | All scoped changes self-tested with evidence | A1 |
| R1 Fix mismatch-at-expiry precedence | developer | DONE | Request mismatch wins at expiry boundary and regression passes | V1 |
| V1 Independent validation | test-engineer | DONE | Full test/build/format/mobile plus mutations pass | R1 |
| V2 Independent code review | code-reviewer | DONE | APPROVED with no remaining T2 findings | R1 |
| J1 Final judgment | engineering-judge | DONE | APPROVED against mission DoD | V1, V2 |

## Evidence log

- A1 DONE — `architecture.md` and ADRs define the scoped contract changes and mutation strategy.
- I1 DONE — production contracts and focused regressions implemented; developer evidence reports 13 Domain + 23 Application tests passing, build/format clean, and three imported dependency mutations detected.
- V1 FAIL — all executable suites passed, but a direct probe reproduced `MismatchAtExpiry=Expired`; expected `RequestMismatch`.
- R1 IN_PROGRESS — reorder identity/expiry classification and add the missing boundary regression.
- R1 DONE — runtime probe now returns RequestMismatch for operation/fingerprint mismatches at expiry and Expired for a matching request.
- V1 DONE — targeted 16/16 and full 36/36 .NET tests passed; build/format clean; prior Docker/mobile/mutation evidence remains valid because rework touched only Application source/tests.
- V2 DONE — fresh independent review APPROVED with no findings.
- J1 DONE — Engineering Judge APPROVED all mission DoD items; T3 is contract-ready only and was not started.
