# Husaynia Tabruk Focused Architecture Security Re-review

Date: 2026-08-14  
Review type: Independent pre-dispatch re-review superseding the prior failed review

## SECURITY RESULT

Scope: Remediated `requirements.md`, `architecture.md`, `decisions.md`, `definition-of-done.md`, `implementation-plan.md`, and `task-plan.md`; focused on the prior one High/four Medium findings and the implementation/test controls that touch those trust boundaries.

Critical: 0   High: 0   Medium: 1   Low: 2   Informational: 0

Blocking findings:
None

## Prior finding disposition

| Prior finding | Disposition | Exact evidence |
|---|---|---|
| High — signup states bypass private-thread approval boundary | **CLOSED** | `architecture.md:204-215` defines a default-deny matrix restricted to approved primary contacts and the active managing Food Incharge, with revocation for ineligible transitions and queued-post reauthorization. `architecture.md:298`, `definition-of-done.md:8`, and `implementation-plan.md:193,202,288,316,381` require state/transition tests. |
| Medium — ordinary administrators bypass audited moderation | **CLOSED** | `architecture.md:196-197,215,266-267` excludes ordinary administrators and defines a separate POST read requiring `PrivilegedThreadRead`, purpose-bound step-up, reason/purpose/case ID, and an immutable access event per returned page. `implementation-plan.md:316,381` requires denial and audit-failure tests. |
| Medium — named minors/non-members enter participant storage | **PARTIALLY REMEDIATED; residual Medium below** | Participant display-name input is removed and member participants must be eligible membership references (`architecture.md:112,171,221`; `implementation-plan.md:193,213,249,267`). However, the retained free-text label can still contain a person's name. |
| Medium — privileged-role propagation lacks governance | **CLOSED** | `architecture.md:109,151-152,244-245`, `decisions.md:159`, and `implementation-plan.md:171-175,227-231` specify dual control, proposer/approver/target separation, no self-targeting, five-minute single-use step-up, minimum two administrators, sealed bootstrap, atomic audit, notification, and immediate revocation tests. |
| Medium — messaging/reporting lacks size and abuse controls | **CLOSED ARCHITECTURALLY** | `architecture.md:218-229` defines payload, page, account, and organization limits with `413`, `429`, `Retry-After`, duplicate-report behavior, and suppression of rejected side effects. `definition-of-done.md:10` and `implementation-plan.md:40,202,249,316,381` require contract/integration coverage. |

## All findings

### [MEDIUM] Free-text signup label can still collect non-member or minor names

Location:     `architecture.md:112,171,221`; conflicting requirement at `requirements.md:50`

Issue:        The architecture retains an arbitrary user-supplied `label`, persists it with the signup, and constrains only its length. Calling the field “non-identifying” does not enforce the requirement that no non-member/minor name is accepted. Planned tests reject participant display-name fields but retain bounded label input (`implementation-plan.md:193,249,267`).

Attack path:  Authenticated primary contact submits a household/team signup -> enters a child's or other non-member's full name in `label` -> API accepts and persists the free text -> roster/signup DTO renders the label to authorized operational users.

Impact:       Collection and disclosure of identity data that the approved MVP privacy contract says will not be accepted, with retention/deletion policy still unresolved.

Fix:          Remove free-text labels from MVP, or replace them with a server-defined enum/generated description that cannot carry names. Do not rely on warnings or name-detection heuristics.

Confidence:   High

### [LOW] Offline cached thread display lacks a defined entitlement lease

Location:     `architecture.md:215,246,253`; `implementation-plan.md:334`

Issue:        Server authorization is revoked on the next request, but mobile purge is triggered by an authorization-loss response and the cache TTL is not specified. An offline device can therefore continue displaying previously cached messages after the user becomes ineligible until it reconnects or the unspecified TTL expires.

Attack path:  Approved contact caches a thread -> signup is withdrawn/cancelled or membership is disabled while the device is offline -> user continues opening cached thread content locally before the device receives a denial response.

Impact:       Time-bounded residual access on a device that had previously legitimate access; no new server data is disclosed.

Fix:          Define a short thread-entitlement lease/TTL and require online reauthorization after expiry before cached private messages are displayed.

Confidence:   High

### [LOW] Administrator hide/lock request schema omits required case linkage

Location:     `architecture.md:194-195,268`

Issue:        The frozen hide and lock endpoint examples accept only `{ reason }` plus step-up for a moderator, while the security rule says administrator actions require the privileged moderation policy, step-up, reason, and case ID. The request contract does not show `purpose` or `caseId`, creating implementation ambiguity for destructive privileged moderation.

Attack path:  Administrator invokes hide/lock through the documented endpoint -> implementation follows the shown request DTO -> action succeeds with step-up and reason but without a support/moderation case linkage.

Impact:       Reduced accountability and investigation quality for privileged destructive actions; the control does not create an authorization bypass by itself.

Fix:          Add `purpose` and `caseId` to administrator hide/lock request contracts, validate them server-side, and require atomic audit insertion before the action commits.

Confidence:   High

## Threat-boundary conclusion

The prior blocking object-authorization path is closed in the architecture: pending, waitlisted, declined, withdrawn, cancelled, disabled, revoked, cross-organization, unrelated, and post-date-cancellation actors are denied ordinary thread access. Administrator reads are separated into an audited step-up path. Privileged-role propagation and write-amplification risks now have explicit controls and planned tests.

The residual Medium label issue does not create a Critical/High exploit path and therefore does not block implementation dispatch, but it must be resolved before the signup/roster implementation is accepted. The Low findings must be carried into the relevant mobile and moderation contracts.

Conclusion: **PASS**

Implementation may begin under the dependency plan, starting with **T1 only**. This is an architecture/pre-dispatch verdict, not approval of unimplemented code or unexecuted tests.

---

STATUS:          PASS

SUMMARY:         Focused re-review closes the prior High and three prior Medium findings; the participant-name finding is reduced but remains Medium because arbitrary label text can still identify a non-member. Two Low contract-hardening risks remain. Zero unresolved Critical/High findings.

WORK_COMPLETED:  Re-threat-modeled the remediated thread, moderation, participant privacy, administrator governance, abuse-control, offline cache, and audit boundaries; compared every prior finding to exact updated architecture and delivery-plan evidence; verified transition-level tests are specified but still pending.

EVIDENCE:        Reviewed `requirements.md:8,50`, `architecture.md:28,109,112,122,151-152,171,189-229,244-268,298`, `decisions.md:120-175`, `definition-of-done.md:4-10,16`, `implementation-plan.md:1-65,171-175,193,202,213,227-231,249,267,288,306-334,373-382,555-578`, and `task-plan.md:1-70`. Ran `git status --short`, mission-artifact diff inspection, targeted numbered `rg` searches, recursive implementation-directory inspection, and SHA-256 hashing of the reviewed architecture/plan artifacts. `HusayniaTabruk` contains no implementation files, all implementation tasks are `PENDING`, and no dependency manifest exists; dependency audit was therefore not applicable or run.

ARTIFACTS:       `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/security-review.md`

FINDINGS:        0 Critical, 0 High, 1 Medium, 2 Low, 0 Informational.

RISKS:           Free-text signup labels may collect prohibited names; offline cached thread data may remain visible until reauthorization; administrator hide/lock requests have ambiguous case-linkage fields.

BLOCKERS:        None for beginning implementation. Production/signup acceptance must not pass until the residual Medium label issue is removed or constrained by a non-free-text contract.

NEXT_ACTION:     Dispatch T1 only. Amend the signup label contract before T6/T12 acceptance, define the private-thread cache entitlement TTL before T22, and freeze purpose/case ID fields for administrator hide/lock before T19 implementation.
# T10 Security Re-review — 2026-08-16

Result: **FAIL**

- Critical: 0
- High: 1
- Medium: 0
- Low: 1

Blocking High: the issuing administrator receives the live invite token and can self-redeem an
unused victim email without proving mailbox control. Acceptance success versus conflict also
reveals whether the global email identity already exists.

Prior bearer-only invite/disable and request-host URL findings are remediated. Residual Low:
`InvitationBaseUrl` must reject insecure schemes.

---

# T12 Independent Security Review — 2026-08-17

## SECURITY RESULT

Scope: T12 pending-signup and privacy-safe roster implementation only: HTTP submission/query/roster
entry points, application services, PostgreSQL signup/idempotency persistence, active actor and role
reload, tenant/record authorization, participant projections, request limits, quotas, CAS,
transactional audit/notification/outbox effects, safe errors/logging, T12 tests, and NuGet
dependencies. T13+, deployment, and history operations were excluded.

Critical: 0   High: 0   Medium: 1   Low: 0   Informational: 0

Blocking findings:

- No Critical or High vulnerability finding.
- Validation blocker (not a vulnerability severity): all eight T12 PostgreSQL integration tests
  were skipped because `TABRUK_TEST_POSTGRES_CONNECTION` was unavailable and no Docker,
  PostgreSQL, or Podman runtime exists on this host. The required real-database race, CAS,
  close-during-submit, active-role revalidation, and atomic rollback evidence therefore remains
  unexecuted in this review environment.

## Threat model

- Entry points: `POST /api/v1/needs/{needId}/signups`, `GET /api/v1/signups/mine`, and
  `GET /api/v1/dates/{dateId}/roster`.
- Actors: anonymous/expired/disabled users, active members, revoked or unrelated Food Incharges,
  cross-organization members, and an abusive authenticated member.
- Assets: tenant signup state, participant identity, roster data, manager notifications, audit
  records, outbox events, and idempotency receipts.
- Trust boundaries and sinks: bearer claims to active database membership/roles; route/body IDs to
  organization-scoped EF/PostgreSQL queries; JSON body to domain composition; aggregate state to
  CAS persistence; and signup state to roster/notification/audit/outbox projections.

## All findings

### [MEDIUM] Free-text label accepts and rediscloses prohibited participant contact data

Location:     `src/HusayniaTabruk.Application/Signups/Submit/SubmitSignupService.cs:278-294`;
              `src/HusayniaTabruk.Api/Endpoints/V1/Signups/Submit/SignupEndpointSupport.cs:171,234`

Issue:        `NormalizeLabel` trims the caller-controlled label and enforces only Unicode-scalar
              and UTF-8 byte limits. It does not make the field non-identifying. The accepted value
              is persisted and mapped unchanged into submission, mine, and managed-roster
              responses. This conflicts with the frozen privacy contract at
              `implementation-plan.md:304`.

Attack path:  Active member submits a household/team signup -> places a non-member's name, email,
              or phone number in `label` -> length-only validation accepts it -> persistence stores
              it -> `ToResponse` returns it to the primary contact and managing Food Incharge.
              Local reflection execution against the built application assembly confirmed that a
              synthetic email-shaped value returns `IsSuccess=True` from `NormalizeLabel`.

Impact:       The API collects, retains, and discloses direct contact or minor/non-member identity
              data that AC-4/AC-13 and the frozen interface say must not be accepted or exposed.
              Exposure is tenant-scoped and attacker-supplied, so this is Medium rather than High.

Fix:          Remove free-text `label` from T12, or replace it with a server-defined enum/generated
              non-identifying description. Add value-based API tests proving email-, phone-, and
              person-name-shaped input cannot be stored or returned; property-name-only checks are
              insufficient.

Confidence:   High

## Verified controls

- Access-token middleware reloads the exact active membership and current roles from PostgreSQL;
  T12 services reload the actor again. Roster authorization additionally rechecks and locks the
  active Food Incharge assignment and managed date before returning records.
- Submission and query persistence consistently scope entry queries by organization and actor.
  Unauthorized/unrelated roster access receives the same concealed `404`.
- Submission JSON uses an allow-list, rejects unknown/duplicate properties, applies depth and
  8-KiB body bounds, validates participant IDs/counts, and uses parameterized EF/interpolated SQL.
- Signup writes use complete aggregate hydration, expected-version CAS, omission detection, and a
  shared transaction for signup, idempotency completion, notification, audit, and outbox effects.
- Account and organization submission quotas and bounded in-memory bucket retention are present.
  The process-local nature of the limiter was not evaluated as a deployment issue.
- Error responses are sanitized RFC-style problem details; T12 code does not log request bodies,
  participant names, direct contact fields, or tokens.
- Central NuGet versions are pinned; the vulnerability audit reported no vulnerable direct or
  transitive packages from the configured sources.

Conclusion: **FAIL** — no Critical/High vulnerability was found, but safe-to-ship approval is
blocked until the mandatory PostgreSQL security/concurrency suite is actually executed.

---

STATUS:          BLOCKED

SUMMARY:         T12 has strong active database-backed authorization, organization/record scoping,
                 strict request shape, bounded payloads, CAS, and transactional effects. One
                 Medium privacy defect remains: arbitrary label text can carry and redisclose
                 prohibited identity/contact data. Zero Critical/High findings. Approval is
                 blocked because the real PostgreSQL T12 race/atomicity suite could not run.

WORK_COMPLETED:  Threat-modeled all T12 entry points and sinks; reviewed the frozen AC-4/5/7/13/
                 16/17 and architecture contracts; traced active actor/role reload, tenant and
                 record authorization, strict parsing, quotas, complete hydration/CAS,
                 idempotency, and audit/notification/outbox transactionality; scanned dependencies
                 and suspicious sinks/secrets; executed available application, domain, API
                 contract, and label-validation probes.

EVIDENCE:        Reviewed `SubmitSignupService.cs`, `SubmitSignupContracts.cs`,
                 `SignupPersistencePorts.cs`, `SignupQueryService.cs`,
                 `SignupQueryContracts.cs`, `RosterService.cs`, `RosterContracts.cs`,
                 `RosterPersistencePorts.cs`, T12 API endpoints/support,
                 `PostgresAggregateRepositories.cs:26-1029`,
                 `PostgresUnitOfWorkAndStores.cs:12-219`,
                 `PersistenceWriteSupport.cs:1-208`, `TabrukDbContext.cs:285-385,514-540`,
                 bearer/current-actor membership reload code, and T12 tests.
                 `dotnet test` application filter: 3 passed, 0 failed.
                 API security/roster/limit/problem filters: 46 passed, 0 failed.
                 Domain test filter run: 149 passed, 0 failed.
                 T12 PostgreSQL integration filter: 8 skipped, connection unavailable.
                 `dotnet list HusayniaTabruk.sln package --vulnerable --include-transitive`:
                 zero vulnerable packages in all eight projects.
                 Local runtime probe: Docker, Podman, `psql`, and `pg_isready` unavailable.
                 Reflection probe: synthetic email-shaped label accepted.

ARTIFACTS:       `.ai-org/missions/2026-08-14-husaynia-tabarruk-signup/security-review.md`

FINDINGS:        0 Critical, 0 High, 1 Medium, 0 Low, 0 Informational.

RISKS:           Prohibited identity/contact data remains representable in `label`. PostgreSQL-only
                 race and rollback behavior is supported by code and tests but was not executed in
                 this environment.

BLOCKERS:        Provide a local PostgreSQL 18.6-compatible test connection and execute all eight
                 `SignupSubmissionIntegrationTests` with zero skips/failures before approval.

NEXT_ACTION:     Remove or structurally constrain the free-text label, add value-based privacy
                 regression coverage, run the T12 PostgreSQL integration suite, and return for the
                 final security gate.
