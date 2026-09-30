# Architecture Decision Records

Date: 2026-08-15

| ADR | Decision | Status |
|---|---|---|
| [ADR-001](adr/ADR-001-modular-monolith.md) | .NET 10 server-rendered modular monolith | Accepted |
| [ADR-002](adr/ADR-002-azure-baseline.md) | Minimal isolated Azure PaaS baseline | Accepted |
| [ADR-003](adr/ADR-003-versioned-content.md) | Custom versioned editorial content model | Accepted |
| [ADR-004](adr/ADR-004-identity-audit.md) | ASP.NET Core Identity, six policies, append-only audit | Accepted |
| [ADR-005](adr/ADR-005-integrations.md) | Provider ports with persisted snapshots and durable DB jobs | Accepted |
| [ADR-006](adr/ADR-006-payments.md) | Stripe webhook is the only completion authority | Accepted |
| [ADR-007](adr/ADR-007-migration-url-contract.md) | Manifest-driven idempotent migration and URL compatibility | Accepted |
| [ADR-008](adr/ADR-008-immutable-promotion.md) | One immutable artifact promoted through isolated stages | Accepted |
| [ADR-009](adr/ADR-009-deterministic-browser-capture.md) | Fail-closed deterministic capture and transactional baseline promotion | Accepted |

Requirements decisions D-01..D-04 remain binding (`requirements.md:160-163`). Costed Azure
enhancements remain CTO-reserved and are not prerequisites for implementation of the baseline.

The 2026-08-15 T01 amendment is bounded to capture, comparison, evidence verification, and baseline
promotion. Approved navigation and AC-35 migration/media-reference evidence remain unchanged.

## ADR-009 consolidated decision

### Context
Current T01 evidence can pass changed PNG hashes on DOM metrics alone, permits an overflowing
event-tablet row to remain quality-pass, trusts caller/inventory URLs, and replaces the approved
baseline by delete-then-copy.

### Decision
Bind capture to canonical `https://www.husaynia.org:443`, public pinned DNS, sandboxed/hash-verified
Playwright Chromium `151.0.7922.34` from package `1.62.0`, fresh context/page per attempt, complete
request terminals, and truthful separate completeness/quality states. Overflow is captured but
quality-fail; commands return nonzero and promotion refuses it. Pixel changes require per-key visual
diff review, and promotion uses verified sibling copy/swap with prior-baseline restore.

### Consequences
The current event-tablet evidence is not promotable under the consolidated gate. T01 becomes safer
and recoverable at the cost of more reruns/review when DNS, pixels, or live quality vary. No public
schema, navigation, migration, `mediaRef`, production, or infrastructure decision changes.

### Alternatives rejected
DOM-only comparison, overflow waiver, arbitrary base URLs, ambient proxy/DNS trust, direct overwrite,
capture-time masking, and a new image dependency were rejected because each weakens evidence
integrity, security, or recoverability.

## ADR-009 amendment: Refuse all cross-process promotion residue

Date: 2026-08-17     Status: Accepted

### Context

The implemented promotion journal uses an HMAC key protected for Windows DPAPI `CurrentUser`.
Another process or workspace under that same user can use the same signing authority, so a valid
signature cannot prove that a residual journal/tree came from the interrupted promoter. The
destination is an owner-writable workspace, and no production service, separate principal, hardware
signer, package, or infrastructure change is allowed.

### Decision

Automatic rollback remains mandatory for failures handled inside the same `PromoteAsync` process and
uses only in-memory manifests, stable identities, and phase. After restart, every recognized
journal/prior/candidate/failed residual is treated as ambiguous, preserved without mutation, reported
by logical name, and refused with exit `4`.

The journal becomes untrusted operator diagnostics and grants no recovery authority. Remove the
DPAPI/HMAC key store and require handle-anchored path operations plus rejection of reparse points,
identity changes, and files with hard-link count other than one. No external signer is required
unless unattended cross-process recovery against a malicious same-user writer becomes a future
requirement.

`AfterPostVerify` remains pre-commit. Durable commit occurs only after that fault point returns and
the final cancellation check succeeds, and it occurs before prior-baseline deletion. A fault at
`AfterPostVerify` therefore rolls back; a cleanup failure after durable commit preserves the
verified destination and exits `4`.

### Consequences

Same-process faults still restore the exact prior tree. A crash is recoverable by explicit operator
action, not automatically; stale or attacker-created residue can cause denial of service but cannot
cause the tool to restore or delete a chosen tree. Existing signed journals are preserved/refused,
and the obsolete per-user key store is not read or automatically deleted.

### Alternatives considered

- Keep DPAPI `CurrentUser` plus HMAC/ACL checks - rejected because it does not separate same-user
  processes or workspaces.
- Validate manifests and auto-restore without a signature - rejected because attacker-controlled
  content can satisfy self-authored metadata.
- Add a separate principal/service/hardware signer - technically sufficient for unattended
  hostile-writer recovery, but outside T01 constraints and unnecessary for preserve-and-refuse.
- Delete or quarantine suspicious residuals automatically - rejected because that is still a
  destructive decision based on unauthenticated cross-process state.

## ADR-009 amendment: Context-pinned CDN rotation and empty-context cookie reads

Date: 2026-08-18     Status: Accepted

The capture policy advances to `adr-009-v3`. Primary-origin DNS remains immutable for the run.
Approved static-CDN answers may rotate only when every answer is public; each fresh context receives
an immutable selected pin, and Chromium is relaunched when the pin map changes. TLS hostname
validation and deny-all resolver fallback remain mandatory.

Reads from `document.cookie` and Cookie Store are allowed only as empty-result reads in a fresh,
nonpersistent, zero-cookie context and are fully logged. Cookie writes, cookie-bearing requests,
state import/export, credentials, proxy inheritance, payment/form mutation, and storage inheritance
remain denied.

The same amendment requires independently reconstructed retained-sitemap membership and exact,
case-sensitive visual-review/mask validation. This is a compatibility and evidence-integrity
correction, not a security exception. No CTO-reserved decision is required.
