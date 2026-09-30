# T04 Final Hardening Requirements

## Grounded facts

- **FACT F-01:** The anonymous Identity surface includes login, invitation acceptance, MFA setup,
  and MFA enable endpoints (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs:42-47`).
- **FACT F-02:** The current database invariant exposes deterministic SQL only for audit-event
  UPDATE/DELETE protection; it does not protect the bootstrap seal
  (`src/Husaynia.Infrastructure/Identity/IdentityAuditDatabaseInvariant.cs:5-22`).
- **FACT F-03:** EF change tracking rejects bootstrap seal UPDATE/DELETE, but direct SQL bypasses
  that guard (`src/Husaynia.Infrastructure/Identity/HusayniaIdentityDbContext.cs:26-36`).
- **FACT F-04:** Bootstrap uses a serializable transaction and locks singleton `Id = 1`; the seal
  suppresses later bootstrap runs
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityBootstrapSeeder.cs:39-65`).
- **FACT F-05:** Current independent audit cancellation is used on selected denial paths and is
  bounded to five seconds; application-service audits still receive the request token
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs:916-1060`;
  `src/Husaynia.Application/Identity/IdentityAdministrationService.cs:235-296`).
- **FACT F-06:** MFA setup/enable handlers currently do not receive an audit writer and return
  several outcomes without MFA-specific audit emission
  (`src/Husaynia.Web/Areas/Admin/Identity/IdentityAdminEndpoints.cs:418-539`).
- **FACT F-07:** Current integration setup applies the audit invariant explicitly after
  `EnsureCreated`; runtime startup has a no-trigger-DDL test
  (`tests/Husaynia.IntegrationTests/Identity/IdentityTestHost.cs:59-67`;
  `tests/Husaynia.IntegrationTests/Identity/IdentityBootstrapAndConfigurationTests.cs:160-207`).
- **FACT F-08:** Existing tests prove enumeration-equivalent login/invitation failures and sanitized
  anonymous audit identifiers
  (`tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousEnumerationTests.cs:7-157`).
- **FACT F-09:** No Identity-owned DB-backed limiter, `Retry-After`, client fingerprint, or trusted
  forwarded-header handling was found in the scoped production paths (repository search performed
  2026-08-21).

## Scope contract

- **REQUIREMENT R-01:** Protect the permanent bootstrap seal singleton from database-level UPDATE
  and DELETE, including direct SQL outside EF.
- **REQUIREMENT R-02:** Make every privileged Identity outcome emit exactly one cancellation-safe,
  bounded audit attempt through one central policy.
- **REQUIREMENT R-03:** Audit the complete MFA setup/enable lifecycle with stable sanitized data.
- **REQUIREMENT R-04:** Apply atomic DB-backed anonymous throttling to login, invitation acceptance,
  and the MFA challenge performed by anonymous login.
- **REQUIREMENT R-05:** Supply deterministic schema SQL and an exact T18 handoff without adding a
  migration or executing runtime DDL.

## Acceptance matrix

| ID | Requirement | Verifiable acceptance criteria | Required evidence |
|---|---|---|---|
| AC-01 | Seal DB invariant | **GIVEN** the T18 SQL is installed and singleton `IdentityBootstrapState.Id = 1` exists, **WHEN** direct SQL attempts to UPDATE any seal column or DELETE the row, **THEN** SQL Server rejects each statement and the row remains byte-for-byte unchanged. | LocalDB integration test executing direct SQL, reading the row before/after, and naming the thrown SQL error. |
| AC-02 | Seal creation and restart | **GIVEN** an unsealed database and matching enabled bootstrap configuration, **WHEN** bootstrap runs, **THEN** administrator creation/assignment, two bootstrap audits, and insertion of the seal commit atomically. **GIVEN** later direct mutation attempts fail, **WHEN** a new host starts against the same database, **THEN** it does not recreate/reassign the administrator or duplicate the seal/audits. | Causal LocalDB test spanning initial host, direct SQL attempts, disposed host, and restarted host. |
| AC-03 | Deterministic invariant SQL | `InstallSql` is idempotent and installs both append-only audit protection and seal UPDATE/DELETE protection; `DownSql` is idempotent and removes exactly those owned invariants. Repeated install, down, and reinstall succeed. | Exact SQL assertions plus executable LocalDB install/down/reinstall test. |
| AC-04 | No runtime DDL | **GIVEN** schema objects already exist, **WHEN** any Identity host starts, including bootstrap-enabled startup, **THEN** no CREATE/ALTER/DROP trigger/table/index command is issued by runtime code and startup does not require schema-alter permission. | Command interceptor test covering ordinary and bootstrap-enabled startup; source search for runtime execution of handoff SQL. |
| AC-05 | Central audit token | **GIVEN** any privileged Identity path has selected an outcome, **WHEN** its audit is written, **THEN** the token is independent of `HttpContext.RequestAborted`, linked to application shutdown, and automatically cancels no later than five seconds. | Unit/integration tests inspecting token linkage and elapsed cancellation for representative middleware, endpoint, application-service, store, and MFA paths. |
| AC-06 | Exactly-once privileged audit | **GIVEN** a privileged request ends in framework denial, authorization denial, validation failure, conflict, dependency/business failure, exception, or success, **WHEN** processing completes or propagates the exception, **THEN** exactly one audit record/attempt exists for that action and correlation ID, with no duplicate from overlapping layers. | Outcome matrix test across every privileged route; delayed-writer tests assert count `1` per action/correlation. |
| AC-07 | Disconnect survival | **GIVEN** an audit writer is deliberately delayed after outcome selection, **WHEN** the client disconnects/cancels `RequestAborted`, **THEN** the audit continues and persists once. **WHEN** application shutdown or the internal timeout occurs, **THEN** the writer receives cancellation and the request does not wait without bound. | Deterministic delayed-writer tests for denial, validation/conflict, exception, and success, not denial only. |
| AC-08 | Audit failure isolation | **GIVEN** the audit writer throws or times out, **WHEN** a privileged outcome is being finalized, **THEN** there is no second audit attempt and no secret-bearing exception body is returned. The original operation is not falsely reported as successful if its required audit did not persist. | Negative tests with throwing and non-completing writers; response/exception and audit-attempt counts asserted. |
| AC-09 | MFA setup audit coverage | MFA setup emits exactly one audit for: unauthenticated/forbidden framework denial, antiforgery denial, key generation success, key-generation failure, already-enabled conflict, and unexpected exception. | Named integration tests for each outcome and a single-record assertion. |
| AC-10 | MFA enable audit coverage | MFA enable emits exactly one audit for: unauthenticated/forbidden framework denial, antiforgery denial, missing/malformed OTP, invalid OTP, lockout where applicable, framework enable failure, success, and unexpected exception. | Named integration tests for each outcome and a single-record assertion. |
| AC-11 | MFA audit sanitization | **GIVEN** setup/enable inputs and authenticator state contain a shared key and OTP, **WHEN** any MFA audit is persisted, **THEN** action, outcome, actor/target, correlation ID, and stable error/result metadata are present, while shared keys, OTPs, passwords, recovery material, invitation tokens, and raw email/IP values are absent from every audit field. | Audit-record assertions using sentinel secrets against all serialized fields and identifiers. |
| AC-12 | Existing MFA secrecy | Already-enabled setup remains conflict-safe: it does not reveal/reset the established key or downgrade an MFA-satisfied session. Missing antiforgery does not create/reset a key. | Existing MFA causal tests retained and passing, extended to assert the required audits. |
| AC-13 | Atomic DB-backed limiter | **GIVEN** two independently hosted applications share one LocalDB database and one partition, **WHEN** concurrent requests cross configured limit `N` within window `W`, **THEN** at most `N` requests pass the limiter globally; excess requests return 429 without race overshoot. | Barrier-synchronized multi-host LocalDB test with database count/window inspection. |
| AC-14 | Covered anonymous flows | The limiter is enforced before credential/token/OTP verification for login (including its MFA challenge) and invitation acceptance. Authenticated MFA setup/enable are not treated as anonymous endpoints. | Flow tests proving threshold behavior and proving throttled calls do not invoke password, invitation-token, OTP, or lockout mutation paths. |
| AC-15 | Partition isolation and privacy | Partitions separate endpoint family and privacy-preserving client fingerprint. Different endpoints or fingerprints do not consume one another's allowance. Stored keys and audits contain no raw IP, email, invitation token, password, or OTP; hashes are fixed-length and one-way. | DB row inspection and audit inspection using sentinel values; partition-isolation tests. |
| AC-16 | Window, expiry, retention | Counts and windows are finite and bounded by validated configuration. **GIVEN** a partition is exhausted, **WHEN** its window expires, **THEN** a new request can pass. Expired/stale rows are deleted or rendered non-counting within the configured retention bound. | Time-controlled tests for boundary `N`, `N+1`, expiry, and stale-row cleanup/non-counting. |
| AC-17 | Configuration safety | Missing, zero, negative, overflow, or otherwise out-of-policy limiter count/window/retention values fail configuration validation; valid finite values are observable in threshold tests. No secret or environment default file is changed by T04. | Configuration unit tests and scoped diff. |
| AC-18 | Enumeration-safe 429 | **GIVEN** the same exhausted partition, **WHEN** login or invitation requests vary account existence/state, token validity, password, or OTP validity, **THEN** each endpoint returns its own uniform 429 status/body/headers before state lookup. The response contains a valid `Retry-After` representing the remaining window and no account/token clues. | Equivalence matrix comparing status, body, headers, and absence of downstream state changes. |
| AC-19 | Forwarded-header trust | **GIVEN** this repository has no existing trusted forwarded-header configuration, **WHEN** a caller spoofs `X-Forwarded-For` or similar headers, **THEN** the limiter ignores them and uses the server-established remote address. T04 must not introduce proxy trust configuration. | Configuration/source assertion and request tests varying spoofed headers while retaining one partition. |
| AC-20 | No lockout amplification | **GIVEN** a partition is throttled, **WHEN** further login/MFA attempts arrive, **THEN** Identity access-failure counts and lockout timestamps do not change. Concurrency cannot cause more verifier/lockout mutations than the allowed request count. | Multi-host causal test reading user lockout/access-failure state before and after the burst. |
| AC-21 | T18 schema handoff | The handoff lists exact object names and deterministic `InstallSql`/`DownSql` for: audit append-only trigger, bootstrap-seal trigger, rate-limit table, primary/unique constraints, lookup/expiry indexes, and retention semantics. It explicitly states T18 owns migration application. | Reviewed handoff artifact plus executable SQL fixture use. |
| AC-22 | Compatibility and quality | Requests below the limiter threshold preserve existing route shapes, status/body contracts, MFA behavior, authorization, antiforgery, audit redaction, bootstrap concurrency, and enumeration equivalence. Strict owned builds, focused Identity tests, and full solution tests pass with zero failures/skips and recorded counts. | Named regression tests; `dotnet build ... -warnaserror`; focused and full `dotnet test` command logs. |

## Security and negative criteria

- **REQUIREMENT R-06:** No raw secret, credential, token, OTP, authenticator key, email, or IP address
  may be introduced into limiter storage, limiter/audit metadata, logs, exception bodies, or 429
  responses.
- **REQUIREMENT R-07:** No request cancellation, duplicate submit, concurrent host, direct SQL,
  spoofed forwarding header, stale row, or invalid configuration may bypass AC-01 through AC-20.
- **REQUIREMENT R-08:** All SQL identifiers and statements supplied to T18 are deterministic,
  idempotent where specified, and scoped only to T04-owned Identity objects.

## Assumptions and questions

- **ASSUMPTION A-01:** “Applicable MFA anonymous flow” means the OTP challenge inside
  `/admin/identity/login`; setup and enable remain authenticated privileged flows.
- **ASSUMPTION A-02:** Tests may use small configured `N`, `W`, and retention values for
  determinism; this contract does not invent production threshold defaults.
- **ASSUMPTION A-03:** SQL Server/LocalDB is the authoritative database behavior for this mission,
  consistent with the current Identity provider and test fixture.
- **OPEN QUESTION:** None blocking. Exact production limiter thresholds remain an operational
  configuration choice, provided AC-16/AC-17 are met without changing repository defaults.

## Explicitly out of scope

- **OUT OF SCOPE:** Architecture or implementation prescriptions beyond the frozen DB-backed,
  atomic, partitioned outcome.
- **OUT OF SCOPE:** EF migrations, model snapshots, runtime schema creation, deployment,
  infrastructure, package/project changes, secret/default configuration changes, and NU1900 work.
- **OUT OF SCOPE:** Changes outside the listed Identity production/test paths except mission
  artifacts and the T18 handoff artifact.
- **OUT OF SCOPE:** New registration, password-reset, proxy-trust, or non-Identity rate-limiting
  behavior.
