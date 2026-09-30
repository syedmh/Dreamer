# T11 consolidated independent-review rework

Date: 2026-08-28  
Status: superseded by `final-durability-rework.md`; independent re-review pending

## Remediations

1. **Retention fence:** `FormSubmissionRetentionTarget` now discovers only eligible, due,
   non-held rows and re-authorizes every destructive apply through current T19 run/item lease and
   fence state. Detached/expired permits, active T19/entity holds, wrong status, and future due
   timestamps cannot anonymize.
2. **CAS:** definition publish and retention commands require SQL rowversion tokens. Store
   comparisons plus EF concurrency originals prevent last-write-wins. Definition/retention
   responses emit ETags and refreshed tokens; stale HTTP/store requests return 409.
3. **Requeue audit safety:** raw reasons are replaced with bounded `sha256:` fingerprints. Final
   durability rework moved Forms audit before T19 requeue, so audit failure leaves the job
   DeadLettered rather than relying on cancellation compensation.
4. **Exception/malformed boundaries:** all five Forms admin endpoint handlers invoke the
   cancellation-safe exception-audit wrapper. Required nested JSON members are enforced;
   null/missing/invalid field DTOs return one audited 400. Throwing service/store paths return one
   audited bounded 500; finalizer failure returns 503.
5. **Delivery identity:** persisted `DeliveryJobInstanceId` must equal the executing T19
   `JobInstanceId` before values load, attempt creation, or sender invocation.
6. **Unknown partitions:** active definition lookup precedes rate partition creation; 25 distinct
   unknown keys produced zero `FormRateLimits` rows and no submissions.
7. **Trusted environment:** `Forms:EnvironmentName` is ignored. Production pickup rejection uses
   `IHostEnvironment`, including when Forms is disabled. Disabled Forms always resolves the
   disabled sender and validates configured delivery mode/unsupported live secret keys.
8. **Stale cookie:** current user, disabled/deleted state, ordinal security stamp, exact current
   role set, and current MFA state are checked before policy/audit/validation/store.

## Causal tests

- stale/detached/expired/held/future/wrong-status/current retention permits;
- stale definition and submission rowversions at store and HTTP boundaries;
- Forms-audit failure after requeue with T19 cancellation compensation and reason-hash inspection;
- null/missing/invalid nested DTOs plus throwing service/store matrices;
- wrong delivery job identity with zero sender calls/attempts;
- unknown-form partition growth;
- Production host versus spoofed configured environment;
- disabled configured pickup and unsupported delivery secret settings;
- 48 stale-cookie requests and eight current-session admin operations.

## Changed files

- `src/Husaynia.Application/Forms/FormContracts.cs`
- `src/Husaynia.Application/Forms/FormServices.cs`
- `src/Husaynia.Infrastructure/Forms/EfFormsStore.cs`
- `src/Husaynia.Infrastructure/Forms/FormSubmissionRetentionTarget.cs`
- `src/Husaynia.Infrastructure/Forms/FormsConfiguration.cs`
- `src/Husaynia.Infrastructure/Forms/FormsModule.cs`
- `src/Husaynia.Web/Areas/Admin/Forms/FormsEndpointModule.cs`
- `src/Husaynia.Web/Areas/Admin/Forms/FormsEndpoints.cs`
- `tests/Husaynia.Application.Tests/Forms/FormApplicationBoundaryTests.cs`
- `tests/Husaynia.Application.Tests/Forms/FormSubmissionValidationTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsConfigurationValidatorTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsPersistenceAndDeliveryTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsWebBoundaryTests.cs`

## Results

- Strict Release build: 0 warnings, 0 errors.
- Forms Domain/Application/Integration: 60 passed, 0 failed, 0 skipped.
- Architecture/frozen contracts: 13 passed, 0 failed, 0 skipped.
- Relevant T19 retention/jobs: 71 passed, 0 failed, 0 skipped.
- Relevant Identity cookie/security: 17 passed, 0 failed, 0 skipped.
- Total reported by the final retention-race checkpoint: 161 passed, 0 failed, 0 skipped.

No shared Identity/Operations source, migration/snapshot, project/package/lock, `Program.cs`,
deployment, commit, or push change was made. This is implementation evidence, not self-approval.
