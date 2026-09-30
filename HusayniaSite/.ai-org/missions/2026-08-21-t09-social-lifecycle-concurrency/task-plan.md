# Task plan

| Task | Owner | Status | Exit criteria | Evidence |
|---|---|---|---|---|
| T1 Minimal Social-only design | VP (architect unavailable at agent-depth limit) | DONE | Freeze coordinator generation contract, expiry basis, and bounded startup retry semantics without T19 edits | `architecture.md` |
| T2 Implement and self-test | VP (developer unavailable at agent-depth limit) | DONE | Production changes plus causal tests; focused self-test evidence | `test-results.md` |
| T3 Independent validation | VP execution; independent agent unavailable | DONE_WITH_LIMITATION | Execute causal/focused regression suites and strict solution build with exact counts | `test-results.md` |
| T4 Independent code review | code-reviewer | BLOCKED | APPROVED with file/line evidence or actionable findings | dispatch failed at maximum agent depth; supporting review in `code-review.md` |
| T5 Definition-of-Done judgment | engineering-judge | BLOCKED | APPROVED only from repository evidence and gate artifacts | `final-verdict.md` rejected solely for missing independent gates |

Dependencies: T1 -> T2 -> parallel(T3, T4) -> T5.

Security gate: N/A; this changes internal scheduling/idempotency and startup retry behavior without adding an auth, secret, input, or external trust-boundary surface.

E2E gate: N/A; these lifecycle defects are deterministically covered at Application and Infrastructure integration boundaries without a user-facing runtime journey.
