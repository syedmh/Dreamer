MISSION:              Resume and complete T09 Social remediation, including durable T19 refresh-job integration: close the malformed-snapshot, media/pagination, lifecycle, liveness, and security findings; provide a discoverable recurring/catch-up/retry/dead-letter/correlation Social job through stable T19 seams; keep public reads local-only; and make only Social backend/test changes, with no UI, live-provider, deployment, or commit claim.

REQUIREMENTS:         PASS — The prior malformed-payload finding is closed: a non-empty payload with no valid items returns `Malformed` and preserves the last-known-good snapshot (`SocialFeedServiceTests.NonEmptyPayloadWithNoValidItemsIsMalformedAndPreservesLastKnownGood`). Media metadata, consent-gated playback/lightbox descriptors, bounded versioned pagination, stale/empty/loading/error states, and persisted URL sanitization are implemented and exercised. The durable definition/handler is registered by `SocialModule`; canonical state-generation keys, catch-up, retries, dead-letter recovery, correlation propagation, and legacy transitions are covered by the 27 canonical tests and the liveness tests. No UI implementation is claimed.

IMPLEMENTATION:       PASS — Direct inspection verified local expiry is `now + SnapshotLifetime` while provider `FetchedAtUtc` is retained (`SocialFeedServices.cs:328-335,495-503`), malformed normalization records failure without replacing the snapshot, and `SocialFeedReader` reads only the snapshot store (`SocialFeedServices.cs:9-96`). `SocialRefreshJobs.cs:126-159,238-425,491-525` implements bounded canonical scheduling, T19 idempotency, persisted-outcome replay, final-attempt recovery, correlation validation, and dead-letter requeue; `SocialRefreshJobStartupService.cs:14-176` implements three `PersistenceFailure`-only startup attempts, fresh scopes, and the five-minute repair loop. `SocialHttpResilience.cs:45-203` enforces fixed-host DNS/public-address pinning, no redirects, bearer-token binding to the configured endpoint, bounded transport retry, and one circuit. `SocialFeedEntities.cs:107-128,325-422` sanitizes persisted URLs. Current T09 production/test paths are Social-only; no Web UI/provider live call was used and no commit exists for this worktree.

TESTS:                PASS — Independently executed against current Release assemblies, all with `--no-build --no-restore` except the listed build:
                       `dotnet build .\HusayniaSite.sln --no-restore --configuration Release -warnaserror --nologo` → 0 warnings, 0 errors.
                       Focused Application (`FullyQualifiedName~Social|FullyQualifiedName~Operations.Jobs`) → 85 passed, 0 failed, 0 skipped.
                       Focused Integration (same filter) → 87 passed, 0 failed, 0 skipped.
                       `SocialRefreshJobLivenessTests`, repeated twice → 5/5 and 5/5, 0 skipped.
                       `SocialRefreshJobTests` canonical scheduling suite → 27 passed, 0 failed, 0 skipped.
                       Full Application → 147 passed, 0 failed, 0 skipped; full Integration → 287 passed, 0 failed, 0 skipped.
                       The separate locked restore/audited strict-build gate remains externally blocked by 11 `NU1900` diagnostics because `api.nuget.org` vulnerability metadata is unreachable; no project/CI suppression was found. This is recorded as an external dependency, not a code/test failure.

SECURITY:             PASS — Latest independent security review reports 0 findings. Source and executable tests cover fixed HTTPS origin/port, host allowlists, DNS rebinding/private/IPv6 rejection, redirect refusal, token binding, bounded JSON, sensitive-query stripping, consent gating, bounded keys/payloads, and non-sensitive audit/correlation data.

CODE REVIEW:          PASS — Latest independent review approved the final-attempt enqueue repair loop and dead-letter requeue. The coordinator, startup lifecycle, T19 seam use, and causal tests were re-read against the current source; no code finding remains.

E2E:                  N/A — This is backend durable-job and snapshot lifecycle behavior; there is no user-facing journey in this scope, and the brief expressly does not require UI implementation. Application/integration boundaries provide the applicable executable coverage.

DEFINITION OF DONE:   PASS
                       1. PASS — Provider fetch metadata is retained; expiry uses bounded local successful-refresh time; future successor is proven.
                       2. PASS — Non-empty all-invalid payload is `Malformed` and preserves the good snapshot.
                       3. PASS — Canonical scheduling is bounded and tied to T19 execution/idempotency context; same-job replay deduplicates.
                       4. PASS — A distinct successor creates the next generation; rate-limit and terminal recovery preserve their canonical generation, RetryAfter, dead-letter, and correlation semantics.
                       5. PASS — Stable definition/handler discovery, recurring catch-up, retry, dead-letter, and final-attempt repair are proven; completed/cancelled/noncanonical duplicates are excluded from requeue.
                       6. PASS — Startup retries only `PersistenceFailure`, at most three times, with fresh scopes; cancellation, nontransient, and exhausted failures propagate.
                       7. PASS — No provider call occurs under a Social transaction/lock; public reads make zero provider/network calls.
                       8. PASS — Media/playback/lightbox/pagination descriptors, consent default-deny, stale/empty/error states, and URL sanitization are implemented at the backend contract boundary.
                       9. PASS — Transport has one bounded retry layer and one circuit; origin/DNS/rebinding/redirect/token controls remain present.
                       10. PASS — Scope is Social production/tests only for this remediation; no T19, Operations/Persistence, project/global, or Web edit is part of the reviewed T09 change, and no commit was made.
                       11. PASS — Focused gates: 85/85 Application and 87/87 Integration, zero skips; liveness repeated 5/5 twice; canonical scheduling 27/27.
                       12. PASS — Full Application 147/147 and full Integration 287/287, zero skips.
                       13. PASS with external dependency recorded — no-restore strict Release build is 0 warnings/0 errors; locked NuGet audit is blocked only by the 11 external `NU1900` metadata failures and is not suppressed.
                       14. PASS — Independent security review has 0 findings.
                       15. PASS — Independent code review approved.
                       16. PASS — E2E is justified N/A for this backend-only scope.
                       17. PASS — This independent Engineering Judge gate is complete.

RISKS:                Locked NuGet vulnerability metadata remains unverified until `api.nuget.org` is reachable; this is an external audit availability dependency, not an accepted vulnerability exception. The repair loop is intentionally polling-based and may lag by up to five minutes. Earlier Identity configuration failures reported in older runs are unrelated and were not present in the current 287/287 Integration run. No deployment or release authorization is implied.

REMAINING WORK:       Re-run the unsuppressed locked restore/audit and strict build when the NuGet vulnerability endpoint is available; no T09 code remediation remains.

STANDARD EVIDENCE:
STATUS:               APPROVED_WITH_EXTERNAL_NU1900_DEPENDENCY
COMMANDS:             Current Release build and test commands/results are listed under TESTS above; source/test evidence is in `SocialFeedServiceTests.cs`, `SocialRefreshJobTests.cs`, `SocialRefreshJobLivenessTests.cs`, `SocialModuleTests.cs`, `SocialHttpResilienceTests.cs`, and `SocialSnapshotPersistenceTests.cs`.
FINDINGS:             All prior T09 code findings are closed with executable evidence. The only remaining gate condition is external `NU1900` vulnerability-metadata reachability (11 diagnostics, no suppression).
UNRELATED:            Prior Identity configuration failures were outside T09 and are cleared in the latest full Integration execution (287/287).
BLOCKERS:             None for the T09 code objective; external locked NuGet audit availability remains a release-readiness dependency.

FINAL:                APPROVED