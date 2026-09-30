# ADR-009: Use fail-closed deterministic browser capture and transactional baseline promotion
Date: 2026-08-15     Status: Accepted

## Context
T01 uses package-pinned Playwright capture, but current promotion deletes the approved baseline
before direct copy, pixel comparison can pass changed PNGs on DOM metrics alone, trust is derived
from caller/inventory URLs, browser launch lacks explicit sandbox/environment/executable-hash
controls, retries reuse page state, and overflow is recorded without failing quality. The retained
event-detail 768px tablet row overflows and the retained comparison reports five changed PNG hashes
while passing. R-06/AC-03 prohibit page-level overflow, while the original ADR correctly requires
real source defects to remain visible.

## Decision
Keep `Microsoft.Playwright` exactly `1.62.0` and package-matched Chromium `151.0.7922.34`; add no
NuGet dependency. Fix the trust boundary at `https://www.husaynia.org:443`, validate and pin only
public DNS endpoints, reject credentials/IP/non-443/private-reserved/rebinding/outside navigation,
disable proxy/cookies/credentials, explicitly enable the Chromium sandbox, sanitize the child
environment, and verify a checked-in executable SHA-256 profile before launch.

Reuse one verified browser per run, but create a fresh non-persistent context/page/request ledger
for each bounded attempt. Require complete interception with exactly one decision and terminal per
request, drain/quiesce the ledger before snapshot, retain every attempt, and preserve safe
GET/HEAD-only donation observation without interaction or payment traffic.

Separate capture completeness from quality. A safely complete row remains `status=captured`;
horizontal overflow or another quality defect sets `qualityStatus=fail`. The command completes the
36-row diagnostic matrix and exits nonzero on any quality failure, and promotion requires both
36/36 captured and 36/36 quality-pass. Visual review may accept hash-bound dynamic pixel variance
but cannot waive overflow.

Compare two separate runs with the exact same unique 36 keys. Changed PNGs require retained
unmasked per-key visual diffs and independent hash-bound review; equal DOM metrics alone never pass.
Promote only a completely verified, checksummed staging tree through an exclusive-lock,
sibling-copy, copy-verify, prior-baseline rename, swap, post-verify, and automatic restore protocol.
Navigation hierarchy, route/import schemas, migration logic, and `mediaRefs` do not change.

## Consequences
Evidence and promotion become fail-closed, reproducible, independently reviewable, and recoverable.
The current event-tablet baseline cannot pass promotion until a truthful capture has no unwaived
overflow. DNS rotation, browser binary changes, or pixel variance can stop a run and require a
rerun/review; this is intentional.

Playwright/Chromium remain tooling-only. New evidence fields/artifacts are additive; frozen
route/import contracts and production topology are unchanged.

## Alternatives considered
- Permit overflow in an approved baseline - rejected because it contradicts R-06/AC-03 and the
  mandated fail-closed quality gate; a future exception is CTO-reserved.
- Treat 36 PNG files as success regardless of quality - rejected because completeness is not
  correctness.
- Pass changed pixels on DOM metrics alone - rejected because it can hide visible regressions.
- Overwrite `evidence/baseline` directly - rejected because copy/swap faults can destroy the last
  approved tree.
- Add an image-diff package - rejected because Playwright canvas can produce the required per-key
  comparison without a second dependency.
- Trust inventory/base-url hosts or ambient DNS/proxy state - rejected because attacker-controlled
  evidence could expand network trust or reach local/reserved endpoints.
- Use capture-time CSS/DOM masks - rejected because they falsify the source layout.

## 2026-08-17 promotion recovery/security amendment

The automatic-restore decision above applies only to failures handled by the same
`PromoteAsync` invocation, using its in-memory source/prior manifests and stable artifact identities.
It no longer applies after process termination.

On a later invocation, any sibling promotion journal, journal temporary, prior, candidate, failed,
or failed-prior artifact is ambiguous because an owner-writable workspace and DPAPI `CurrentUser`
cannot distinguish same-user processes. The tool must preserve all such artifacts, perform no
restore/delete/quarantine/commit action, report logical recovery names, and exit `4` for manual
operator recovery.

The journal is untrusted diagnostics only; remove its DPAPI/HMAC signing authority. Promotion
mutations must be anchored to stable non-following directory/file handles, and reparse points,
identity changes, and hard-linked files must fail closed. No external signer is required for this
preserve-and-refuse contract; one becomes CTO-reserved only if unattended hostile-writer
cross-process recovery is required later.

`AfterPostVerify` is a pre-commit fault point. Durable commit is marked only after that fault point
returns and the final cancellation check succeeds, and before deletion of the prior baseline.
Therefore a same-process `AfterPostVerify` fault restores the exact prior; cleanup failures after
the later durable commit preserve the verified destination and return `4`.

## 2026-08-18 DNS epoch, cookie capability, and evidence-integrity amendment

The network policy version is `adr-009-v3`.

### DNS epochs

`TrustedEndpointPolicy` creates an immutable `TrustedContextNetworkPolicy` immediately before each
attempt. It contains an epoch and a `DnsPinBinding` for every approved host: host class, selected
public address, complete observed public answer set, and observation time. The run's primary-origin
set must remain exactly unchanged. Approved static hosts may rotate among entirely public answers;
the ordinal-first sorted address is pinned for the context. A changed selected pin requires browser
relaunch before context creation. Reauthorization and pre/post screenshot checks reject empty,
failed, mixed, or non-public answers but cannot alter the active pin. Resolver rules end with
deny-all and TLS continues to validate the hostname.

### Cookie and storage capabilities

Each attempt uses a new context with zero cookies and no supplied storage state. Empty reads are
compatible behavior: `document.cookie` returns `""`, Cookie Store `get()` returns `null`, and
`getAll()` returns `[]`. Each read records one allowed capability decision and terminal with
`cookie-read-empty-context`. Setters and Cookie Store mutation fail
`capability-cookie-write-blocked`; outbound Cookie headers fail `cookie-request-blocked`; any
nonzero cookie count at pre-navigation, pre-screenshot, or post-screenshot checkpoints fails
`cookies-created`. Attempt-local Web Storage is destroyed with the context and is never exported or
logged.

### Sitemap provenance

The producer and validator independently derive route membership only from exact normalized
canonical URLs in retained `<urlset><url><loc>` entries. `<sitemapindex>` child locations and
general discovery do not create membership. Every raw sitemap is bound to a successful retained
HTTP record by saved path, size, and SHA-256. Asset aliases remain excluded.

### Visual-review contract

Review JSON uses case-sensitive parsing with unknown members disallowed. Each entry contains exactly
`key`, `runASha256`, `runBSha256`, `unmaskedDiffPath`, `maskVersion`, `rectangles`, `reviewer`,
`reviewedUtc`, `decision`, and `rationale`. The nullable mask pair is either both null or version
`1` with 1-64 positive, bounded rectangles inside the screenshot, each with rationale. Masks are
supplemental only: unmasked evidence and independent hash-bound acceptance remain required.

### Required gates

Negative tests cover origin change, public CDN rotation, mixed/non-public answers, pin refresh,
redirect escape, empty cookie reads, cookie writes/headers/state inheritance, sitemap-index versus
URL-set semantics, stale/missing sitemap provenance, strict JSON casing/unknown fields, and every
mask boundary. Then all non-live tests, two fresh 36/36 live runs, comparison/review, security,
code review, verification, atomic promotion, post-promotion tests, and judgment must pass.
