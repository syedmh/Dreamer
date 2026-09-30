# T11 Forms implementation evidence

Date: 2026-08-28  
Scope: T11-owned paths only  
Status: **DONE; independent gates and final judgment approved**

## Outcome

Implemented the generic versioned Forms backend and HTTP boundaries without inventing live Husaynia
schemas or destinations. Production remains empty/disabled while T01 is blocked. Tests use only
`test-contact-v1`, `test-pledge-v1`, `.test` addresses, and a local non-public pickup directory.

## Delivered behavior

- Immutable definition versions/fields with bounded field kinds, labels, rules, choices, privacy
  class, ordering, consent version, and published pointer.
- Accessible field-error model and deterministic NFC canonical payload hashing.
- Secret/payment/attachment field keys prohibited; unknown, malformed, oversized, and invalid
  values rejected before persistence.
- Public boundary ordering: antiforgery, bounded strict JSON, honeypot, HMAC admission fingerprint,
  SQL fixed-window limit, definition validation, duplicate HMAC, Application service.
- Privacy-safe fixed-window partitions use only keyed 32-byte HMACs and SQL
  `sp_getapplock` plus `UPDLOCK,HOLDLOCK`; dependency failure is fail-closed.
- Serializable submission transaction creates exactly one submission/value set, invokes the frozen
  T19 `IDurableJobStore.EnqueueAsync`, and stages one redacted `AuditEvent` before commit.
- Authoritative unique duplicate fence returns the original receipt for the same canonical hash and
  conflict for a different hash.
- Strict IDs-only `forms.delivery.v1` payload and `forms.delivery` `IJobHandler`; values are reloaded
  only inside the handler and never placed in job payloads, audit, telemetry, or logs.
- Disabled and local pickup senders only; no SMTP/provider/HTTP/network adapter exists.
- Persisted sanitized attempts, retry/dead-letter compatibility, cancellation, manual dead-letter
  retry through T19, and submission survival on delivery failure.
- Retention status, anonymization, legal-hold fencing, and `IRetentionTarget` registration.
- `/admin/forms/**` performs explicit `SiteAdministration` policy plus Application capability checks,
  requires SiteAdministrator and MFA, validates antiforgery on mutations, and produces exactly one
  T04 redacted audit in tested allow/deny/transport outcomes.
- Admin admission authenticates the Identity cookie, validates current user/disabled state,
  ordinal security stamp, current role set, and current MFA state, replaces stale principals with
  anonymous, and only then builds actor/policy/audit state. Disabled, revoked-role, changed-stamp,
  deleted, missing-stamp, and revoked-MFA sessions cannot reach validation or stores.
- Definition publish and retention mutations require rowversion CAS tokens; stale tokens return
  conflict without mutation, and successful definition/retention responses emit refreshed ETags.
- T19 retention applies under one sink transaction/application lock that validates the locked
  current run/item lease, Applying state, exact permit binding, holds, Forms due/status, and
  submission rowversion immediately before anonymization. The authoritative UTC decision time is
  captured only after acquiring the target lock, so lock wait cannot preserve an expired lease or
  hide a newly effective hold. T19's stable callback marks Applied.
- Manual dead-letter retry hashes the admin reason and finalizes the Forms authorization audit
  before T19 requeue. Audit failure leaves the job DeadLettered and non-runnable.
- Every Forms admin operation uses an outer cancellation-safe exception audit wrapper. Malformed nested
  DTOs are audited 400 responses; throwing services/stores produce bounded exactly-once audited
  failures; authentication, current-user lookup, policy, antiforgery, body-stream, and client
  cancellation failures are also enclosed.
- Unknown form keys are resolved before SQL rate partition creation; delivery handlers verify the
  persisted job identity before loading values or invoking the sender.
- Host environment comes from `IHostEnvironment`; configured environment text cannot enable
  Production pickup, and disabled Forms cannot activate a configured pickup sink.
- Public T15-facing ports are additive; frozen C2/C4 files are unchanged.

## Production source files

- `src/Husaynia.Domain/Forms/FormEntities.cs`
- `src/Husaynia.Application/Forms/FormContracts.cs`
- `src/Husaynia.Application/Forms/FormSubmissionValidation.cs`
- `src/Husaynia.Application/Forms/FormServices.cs`
- `src/Husaynia.Application/Forms/FormDeliveryJobs.cs`
- `src/Husaynia.Infrastructure/Forms/FormsConfiguration.cs`
- `src/Husaynia.Infrastructure/Forms/FormsEntityConfigurations.cs`
- `src/Husaynia.Infrastructure/Forms/SqlFormsRateLimiter.cs`
- `src/Husaynia.Infrastructure/Forms/FormMessageSenders.cs`
- `src/Husaynia.Infrastructure/Forms/EfFormsStore.cs`
- `src/Husaynia.Infrastructure/Forms/FormSubmissionRetentionTarget.cs`
- `src/Husaynia.Infrastructure/Forms/FormsModule.cs`
- `src/Husaynia.Web/Areas/Admin/Forms/FormsAdmission.cs`
- `src/Husaynia.Web/Areas/Admin/Forms/FormsEndpointModule.cs`
- `src/Husaynia.Web/Areas/Admin/Forms/FormsEndpoints.cs`

## Test files

- `tests/Husaynia.Domain.Tests/Forms/FormDomainTests.cs`
- `tests/Husaynia.Application.Tests/Forms/FormSubmissionValidationTests.cs`
- `tests/Husaynia.Application.Tests/Forms/FormApplicationBoundaryTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsConfigurationValidatorTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsPersistenceAndDeliveryTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsWebBoundaryTests.cs`

## Validation

See `../../gates/T11/test-results.md`: the final independent gate reported 468 passed, 0 failed,
0 skipped. Current source passed the isolated compiler warning-as-error diagnostic. The required
official strict `--no-restore` build is externally blocked before compilation by cached `NU1900`;
no restore/audit suppression/cache edit was used. Final durability details are in
`../../gates/T11/final-durability-rework.md`.

## Ownership and safety

No project/package/lock, `Program.cs`, shared DbContext, migration/snapshot, Identity, Operations,
Social, deployment, secret, live endpoint, live form, commit, or push change was made by T11.
Runtime source contains no `EnsureCreated`, `EnsureDeleted`, `Migrate`, or DDL.

## Pending external gates/risks

- T01 must later supply approved form definitions, disclosures, acknowledgement text, and real
  destination configuration; no live fidelity is claimed.
- T18 alone must generate/apply the migration from `t18-schema-handoff.md`.
- Production delivery remains intentionally disabled; a future approved provider implementation
  must preserve the frozen `IOutboundMessageSender` boundary and idempotency caveat.
- Independent test, security, correctness-review, and engineering-judge verdicts approved T11.
