# Definition of Done — Husaynia.org Replica

Mission: `2026-08-14-husaynia-site-modernization`

## Gate 1 — Requirements / Product

- [x] CTO decisions D-01..D-04 resolve OQ-B1..OQ-B4 and are incorporated as explicit requirements/decisions.
- [ ] Approved timestamped live-site visual/content/form/menu/integration baseline exists.
- [ ] URL/content/media/metadata/redirect manifest is complete.
- [ ] AC-01..AC-35 remain traceable to tests or named evidence.

## Gate 2 — Architecture

- [ ] Independent Architect: **APPROVED**.
- [ ] Design satisfies R-01..R-54 without changing the public contract.
- [ ] Azure stages, migration/rollback, content model, authorization, integration isolation, observability, privacy, and recovery are covered.
- [ ] Design includes idempotent public-crawl/media import and later WordPress DB/XML/media/configuration export adapters with dry-run, conflict reporting, and non-destructive replay.
- [ ] Design enforces the six-role least-privilege matrix and audits every privileged action.
- [ ] Design supports configurable baseline-proven payment modes and deterministic configurable prayer calculation with overrides/cached fallback.
- [ ] Design defaults non-essential trackers denied until consent while essential site/payment functions remain available.
- [ ] Architecture does not freeze an Azure SKU without a separate approved decision.

## Gate 3 — Implementation and code review

- [ ] Supported Visual Studio solution opens and clean restore/build/publish exits zero.
- [ ] Code implements the approved URL, visual, functional, admin, migration, and deployment contracts.
- [ ] No production secrets or live payment credentials exist in source/artifacts/logs.
- [ ] Independent principal code reviewer: **APPROVED**, with no unresolved correctness or maintainability blocker.

## Gate 4 — Independent automated test

- [ ] Independent Test Engineer maps every AC-01..AC-35 to one or more named tests/evidence.
- [ ] Unit, integration, contract, migration, redirect/crawl, accessibility, visual-regression, security-control, and sandbox integration suites pass.
- [ ] Donation duplicate/concurrency, webhook replay/signature, declined/cancelled/3DS, invalid form, upload abuse, dependency timeout, empty state, DST, Unicode, rollback, and restore paths pass.
- [ ] Idempotent import replay, later-export reconciliation, six-role authorization boundaries, prayer override/cache behavior, configurable payment modes, and consent grant/withdrawal tests pass.
- [ ] Commands, environment, counts, failures/skips, reports, and artifact checksums are retained; unexplained skips are zero.

## Gate 5 — Security and privacy

- [ ] Independent Security Engineer: **APPROVED**.
- [ ] No unresolved Critical/High vulnerability.
- [ ] Authorization matrix, CSRF, CSP/security headers, upload handling, injection, secrets, webhook verification, dependency/supply-chain, logging redaction, retention, and consent are verified.
- [ ] GA4, Clarity/session recording, marketing, and non-essential social tracking are absent before consent and stop after consent withdrawal.
- [ ] Production payment validation uses processor sandbox/test evidence only.

## Gate 6 — QA / end-to-end

- [ ] Independent QA Engineer executes home/navigation, prayer, programs/event/iCal, religious text/audio, announcements/media, search, contact/pledge, sandbox donation, editor publish, operator diagnosis, and rollback journeys.
- [ ] Browser/device matrix in R-49 passes.
- [ ] WCAG 2.2 AA automated and manual journeys pass.
- [ ] AC-01 visual thresholds pass at all named viewports.
- [ ] No open severity-1 or severity-2 functional, accessibility, security, data-loss, or visual-parity defect.

## Gate 7 — Azure release readiness

- [ ] One immutable artifact is promoted through Development and Staging with identical checksum.
- [ ] PR/build/deploy stages enforce R-34..R-40 and retain evidence.
- [ ] Staging migration dry run, backup/restore, smoke, crawl/redirect, visual, accessibility, performance, sandbox integrations, and rollback rehearsal pass.
- [ ] If an authorized WordPress export is available, exact export-backed reconciliation passes before production approval.
- [ ] If no authorized export is available, the release packet quantifies crawl/widget/configuration fidelity gaps for final judgment; absence does not retroactively block architecture.
- [ ] Prayer baseline values/tolerance and observable donation modes/categories are captured and approved before production approval.
- [ ] Production deployment plan includes approval, DNS/TLS, backups, monitoring, cutover reconciliation, rollback criteria, and owner/runbook links.
- [ ] Production deployment itself remains separately authorized and is not implied by mission approval.

## Gate 8 — Documentation

- [ ] README covers prerequisites, Visual Studio/CLI setup, local run, tests, sandbox configuration, and repository map.
- [ ] Developer/agent guide covers contracts, safe change boundaries, conventions, test evidence, ADRs, and prohibited secret/live-operation behavior.
- [ ] Deployment guide covers all Azure stages, configuration, migrations, promotion, rollback, and evidence retention.
- [ ] Operations runbooks cover alerts, feed outage, prayer/calendar failure, form/email failure, payment webhook failure, content restore, backup restore, credential rotation, and emergency unpublish.
- [ ] Content/editor guide covers roles, preview/publish/archive, media/alt text, SEO, redirects, and audit history.
- [ ] Privacy/security documentation matches implemented trackers, forms, retention, and subprocessors.
- [ ] Role/permission documentation exactly covers `SiteAdministrator`, `ContentEditor`, `EventEditor`, `MediaEditor`, `DonationOperator`, and `ReadOnlyAuditor`.

## Gate 9 — Final judgment

- [ ] Engineering Judge independently verifies the original objective and this contract against real evidence.
- [ ] All applicable prior gates are **APPROVED**; exceptions identify owner, expiry, risk, and explicit CTO acceptance.
- [ ] Any lack of authorized WordPress export is evaluated and recorded as residual fidelity risk with crawl-based reconciliation evidence.
- [ ] Engineering Judge verdict: **APPROVED**.
