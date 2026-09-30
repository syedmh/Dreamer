# T02 Independent Test Result

## T09 Social final validation — 2026-08-21

**Verdict: APPROVED_WITH_EXTERNAL_NU1900_DEPENDENCY**

- Strict Release no-restore solution build: 0 warnings, 0 errors.
- Focused Social/Operations.Jobs Application: 85 passed, 0 failed, 0 skipped.
- Focused Social/Operations.Jobs Integration: 87 passed, 0 failed, 0 skipped.
- Social liveness suite: 5/5 passed twice.
- Canonical Social refresh-job suite: 27 passed, 0 failed, 0 skipped.
- Full Application: 147 passed, 0 failed, 0 skipped.
- Full Integration: 287 passed, 0 failed, 0 skipped.
- Independent security: PASS, zero findings.
- Independent code review: APPROVED.
- Independent Engineering Judge: APPROVED.

All prior T09 findings are closed, including last-good snapshot preservation, media/pagination
descriptors, local-only reads, bounded resilience, provider-origin/DNS/redirect/token controls,
persisted URL sanitization, canonical recurring/catch-up/retry/dead-letter/correlation behavior,
restart/crash deduplication, and final-attempt repair/dead-letter requeue.

The unsuppressed locked audited restore remains externally blocked by 11 `NU1900` diagnostics
because NuGet vulnerability metadata is unreachable. No repository suppression was added.

## T19 Operations independent validation — 2026-08-20

**Verdict: CHANGES_REQUIRED**

All executed builds and tests passed, but the frozen T19 evidence does not cover all assigned
acceptance criteria or several production orchestration branches.

Executed commands and results:

- `dotnet build HusayniaSite.sln -c Release --no-restore -warnaserror`
  — build succeeded, 0 warnings, 0 errors.
- `dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release --no-restore --no-build --filter "FullyQualifiedName~Operations" --logger "console;verbosity=normal"`
  — 13 passed, 0 failed, 0 skipped.
- `dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-restore --no-build --filter "FullyQualifiedName~Operations" --logger "console;verbosity=normal"`
  — 10 passed, 0 failed, 0 skipped. One of the ten was
  `Persistence.Core.UnitOfWorkTests.UnsafeOperationsDoNotRetryByDefault`, matched because its name
  contains `Operations`; nine were under the Operations folders.
- `dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release --no-restore --no-build --logger "console;verbosity=minimal"`
  — 26 passed, 0 failed, 0 skipped.
- `dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-restore --no-build --logger "console;verbosity=minimal"`
  — 69 passed, 0 failed, 0 skipped.

The focused tests ran again in the full suites, with no flake observed in two executions. No
`NU1900` warning appeared because all validation commands used `--no-restore`.

Passing evidence includes real SQL LocalDB single-lease concurrency, expired-lease takeover,
abandoned-attempt recording, retry/dead-letter/audited requeue, pending/running cancellation,
restart recovery, concurrent duplicate enqueue, health degradation/staleness, non-diagnostic probe
failure, synthetic in-process correlation, hostile redaction, retention dry-run/apply/idempotency/
holds/batch bounds, persisted hold placement/release, and selected configuration validation.

Blocking acceptance gaps:

- **AC-17:** no inspection of donation logs, database, telemetry export, browser storage, or admin
  screens for prohibited payment data.
- **AC-25:** no test independently fails each external dependency while proving unrelated public
  pages/journeys remain available and the failure is observable.
- **AC-27:** no T19 Operations test proves optional trackers default denied or start/stop on consent
  grant/withdrawal.
- **AC-32:** no staging backup/restore, integrity, RPO/RTO, media/configuration-reference, or
  post-restore smoke evidence.
- **AC-33:** no executed synthetic request, validation failure, dependency timeout, admin edit, or
  sandbox webhook failure correlated across logs, metrics, traces, and a linked runbook.

Additional test gaps:

- `DurableJobProcessor` handler success/failure, unregistered handler, lease renewal/loss, timeout,
  exception retry/dead-letter, and host-stop release paths are untested.
- `/health/live` and `/health/ready` HTTP contracts and status codes are untested.
- Retention invalid request, unknown target, in-progress duplicate, target mismatch, timeout/partial
  failure, and real target mutation are untested.
- Configuration boundary coverage is incomplete, especially lease-duration and recovery-delay
  limits.

## T03 Final Nested-Cancellation Confirmation — 2026-08-20

**Verdict: APPROVED_WITH_EXTERNAL_NU1900_BLOCKER**

- Unsuppressed audited locked restore failed only with external `NU1900` because the NuGet
  vulnerability index was unavailable.
- Locked restore and strict Release solution build with only `NU1900` downgraded succeeded:
  11 `NU1900` warnings, 0 errors.
- Focused cancellation regressions: 4 passed, 0 failed, 0 skipped.
- Full `Persistence.Core` LocalDB suite: 52 passed, 0 failed, 0 skipped.
- Full integration project: 53 passed, 0 failed, 0 skipped.
- Full solution: 90 passed, 0 failed, 0 skipped.
- Post-focused, post-persistence, post-integration, and post-solution inventories each contained
  zero `HusayniaT03_%` databases.

The separate-token nested-cancellation cases before and after the nested write both return
`NestedOperationFailed` after the outer operation catches cancellation, and database verification
proves that outer, nested, and continued partial writes are all rolled back. The existing
outer-token cancellation and pre-cancelled transaction-acquisition tests still propagate
`OperationCanceledException`, persist no cancelled work, and allow subsequent recovery.

## T03 Independent Rework Validation — 2026-08-20

**Verdict: APPROVED_WITH_EXTERNAL_NU1900_BLOCKER**

The design-time endpoint allowlist defect is corrected. Exact approved LocalDB/local-host forms
pass, deceptive prefixes, invalid ports/instances, named pipes, alternate LocalDB instances,
SQL-authentication forms, network-library overrides, and malformed strings are rejected.

Executed results:

- Unsuppressed audited locked restore: failed only with external `NU1900` because
  `https://api.nuget.org/v3/index.json` was unavailable.
- Locked restore and strict Release solution build with only `NU1900` downgraded:
  build succeeded, 11 `NU1900` warnings, 0 errors.
- Full `Persistence.Core` LocalDB suite: 50 passed, 0 failed, 0 skipped.
- Full integration project: 51 passed, 0 failed, 0 skipped.
- Full solution: 88 passed, 0 failed, 0 skipped.
- Positive `dotnet ef dbcontext info` probes: 2/2 passed for deterministic default and explicit
  approved LocalDB configuration.
- Negative `dotnet ef dbcontext info` probes: 6/6 rejected with exit code 1; unit/integration
  theory coverage additionally exercised every frozen deceptive endpoint case.
- Setup-failure cleanup stress: 5 runs, 10/10 tests passed, 0 leaked database observations.
- Final post-persistence, post-integration, and post-solution inventories each contained zero
  `HusayniaT03_%` databases.

Passing coverage includes transaction-acquisition failure recovery, pre-cancel acquisition
recovery, commit/rollback/nested/external transaction behavior, execution-strategy restrictions
and transient retries, cancellation, rowversion concurrency, managed shadow timestamps,
configuration discovery, idempotency/app-lock concurrency, payload conflicts, deterministic
design-time naming, setup-failure cleanup, primary-exception preservation with cleanup diagnostics,
database isolation, and guarded deterministic deletion.

One immediate inventory after the first 50-test run transiently reported count `1`; a direct
follow-up found no database, the repeated full persistence run reported zero, both broader suites
reported zero, and five cleanup stress runs produced zero observations. It was not reproduced.

## T03 Independent Test Result — 2026-08-19

**Verdict: CHANGES_REQUIRED**

Actual LocalDB validation found one production defect:

- `HusayniaDesignTimeDbContextFactory` accepts deceptive remote hosts beginning with
  `localhost` or `127.0.0.1` because the allowlist uses prefix matching
  (`src/Husaynia.Infrastructure/Persistence/Core/HusayniaDesignTimeDbContextFactory.cs:46-47`).
  Independent cases for `localhost.example.test` and `127.0.0.1.example.test` failed
  consistently in three runs. The factory must parse and exactly validate an approved local
  server/instance form rather than accepting arbitrary prefixes.

Executed evidence:

- Unsuppressed audited locked restore: failed only with external `NU1900` because
  `https://api.nuget.org/v3/index.json` was unreachable; no repository exemption was used.
- Diagnostic locked restore with only `NU1900` downgraded: succeeded.
- Strict Release solution build after diagnostic restore: succeeded with 11 `NU1900` warnings,
  0 other warnings, 0 errors.
- Original T03 focused suite: 14 passed, 0 failed, 0 skipped (not the reported 15 focused tests).
- Expanded independent T03 suite: 23 cases, 21 passed, 2 failed, 0 skipped.
- Broader solution suite: 61 total, 59 passed, 2 failed, 0 skipped; both failures are the same
  design-time host-validation defect.
- `dotnet ef dbcontext info`: succeeded; EF CLI 10.0.11 discovered
  `HusayniaDbContext`, SQL Server provider, deterministic database
  `HusayniaSite_DesignTime_BE56E8D7BD57`, and `(localdb)\MSSQLLocalDB`.
- LocalDB was available (17.0.4025.3). Final query found zero remaining
  `HusayniaT03_%` databases after test-owned cleanup.

Passing executed coverage includes configuration discovery without context edits, managed shadow
timestamp insert/update behavior, rowversion concurrency, commit, expected/unexpected rollback,
nested success/failure, ambient/external transaction rejection, cancellation, post-failure
recovery, unsafe no-retry behavior, actual transient retry for explicitly idempotent work,
concurrent duplicate idempotency with transaction app-lock serialization, payload conflict,
deterministic database naming, fixture isolation, identifier guards, and deterministic cleanup.

Independent tests added only under
`tests/Husaynia.IntegrationTests/Persistence/Core/`.

## Final validation — 2026-08-15 15:30 PDT

**Verdict: APPROVED_WITH_ENV_BLOCKER**

No repository findings remain.

- Literal role set, literal policy set, public-registration flag, MFA flag, and allowed/denied
  privileged-attempt audit flag are asserted.
- Every permission-matrix cell is asserted from literal role names; omitted and unknown roles are
  asserted as `CapabilityAccess.None`.
- Previously remediated route semantics, migration schema conditions, endpoint ordering, provider
  signatures, exact SDK pin, and strict build policy remain covered and passing.
- Focused tests: authorization 3/3, contract/schema/semantic/provider 21/21, build/composition 5/5.
- Full suite: 38 passed, 0 failed, 0 skipped.
- Format verification exited 0.
- Diagnostic publish produced 13 files; runtime started and returned the expected empty-foundation
  HTTP 404.
- Two diagnostic Release builds produced 20 production DLL/PDB artifacts each with zero SHA-256
  differences.

The strict audited locked restore and strict warnings-as-errors build remain externally blocked:

```text
error NU1900: Warning As Error: Error occurred while getting package vulnerability data:
Unable to load the service index for source https://api.nuget.org/v3/index.json.
```

The parent independently reproduced the same `api.nuget.org` TLS failure with both curl and
`Invoke-WebRequest`. The repository contains no NU1900 exemption and correctly fails closed.

## Revalidation — 2026-08-15 13:17 PDT

**Verdict: CHANGES_REQUIRED**

Developer rework corrected the previously reported production/contract defects:

- semantic route validation rejects duplicate IDs, duplicate canonical paths, non-NFC paths,
  redirect chains, and redirect cycles;
- migration schema enforces apply-with-plan and dry-run-without-plan;
- the NU1900 repository exemption is removed;
- `global.json` pins SDK `10.0.400` with `rollForward: disable`;
- positive/negative schema tests, endpoint-order tests, build-policy tests, provider-signature tests,
  and matrix-cell tests execute successfully.

Strict audited locked restore was attempted twice, five seconds apart:

```text
dotnet restore HusayniaSite.sln --locked-mode --force-evaluate -p:NuGetAudit=true
```

Both attempts exited 1 because NuGet audit could not reach
`https://api.nuget.org/v3/index.json` and strict policy correctly promoted `NU1900` to an error.
The exact error was:

```text
error NU1900: Warning As Error: Error occurred while getting package vulnerability data:
Unable to load the service index for source https://api.nuget.org/v3/index.json.
```

Consequently the exact strict Release build also exited 1 with 10 NU1900 errors. This is an
environmental NuGet TLS/service blockage, not a repository exemption.

Supplemental diagnostics retained audit but downgraded only NU1900 on the command line:

- build succeeded with 10 NU1900 warnings;
- 38 tests passed, 0 failed, 0 skipped;
- exact format verification exited 0;
- publish produced 13 files;
- two Release builds produced 20 production DLL/PDB files with 0 SHA-256 differences;
- runtime started in Production and returned the expected empty-foundation HTTP 404.

One repository test-coverage gap remains:

- `tests/Husaynia.Application.Tests/FrozenContractTests.cs:7-15` claims exact role/policy coverage
  but asserts only counts. The matrix test also builds expectations from the same role constants,
  so a renamed frozen role could pass. It also does not assert
  `AuthorizationContract.AuditAllowedAndDeniedPrivilegedAttempts`. Add literal expected role and
  policy sets plus the audit-required flag assertion.

## Verdict

**CHANGES_REQUIRED**

## Executed evidence

- Clean isolated copy created at `%TEMP%\HusayniaSite-T02-validation`, excluding T01-owned paths and all `bin`/`obj` directories.
- `dotnet --version` -> `10.0.400`.
- `dotnet restore HusayniaSite.sln --locked-mode --force-evaluate` -> exit 0, with ten `NU1900` warnings.
- `dotnet build HusayniaSite.sln --no-restore --configuration Release -warnaserror` -> exit 0, 10 warnings, 0 errors.
- `dotnet test HusayniaSite.sln --no-build --configuration Release --logger "console;verbosity=minimal"` -> 22 passed, 0 failed, 0 skipped.
- `dotnet format HusayniaSite.sln --no-restore --verify-no-changes --verbosity minimal` -> exit 0; workspace-load warning reported.
- `dotnet publish src/Husaynia.Web/Husaynia.Web.csproj --no-restore --configuration Release --output %TEMP%\HusayniaSite-T02-publish -warnaserror` -> exit 0, 13 files, with `NU1900` warnings.
- Published runtime smoke on `http://127.0.0.1:5189/` -> process started in Production, HTTP 404 from the intentionally empty pipeline, process remained alive.
- Two clean Release builds -> 20 production DLL/PDB artifacts each, 0 SHA-256 differences.
- Empty-package-cache locked restore -> blocked by NuGet.org TLS handshake (`NU1301`); clean-agent restore could not be proven in this environment.
- PowerShell `Test-Json -SchemaFile` fixtures -> 8 expected cases passed and 4 frozen-invariant cases were incorrectly accepted.

## Blocking findings

1. `contracts/routes/route-manifest.schema.json` does not enforce unique route IDs/canonical paths, NFC paths, or one-hop redirects. Invalid fixtures were accepted. The invariants exist only in the `$comment` at line 160.
2. `contracts/migration/import-manifest.schema.json` permits `"mode":"apply"` without `planId`, contrary to C5/ADR-007. The independent negative fixture was accepted; relevant definitions are lines 36-44.
3. `Directory.Build.props:8` exempts `NU1900` from warnings-as-errors. Consequently `dotnet build -warnaserror` succeeded with 10 warnings, so the warnings-as-errors gate is not actually strict.
4. `global.json:4` uses `rollForward: latestFeature`, which does not pin the declared .NET 10 feature band and permits a later installed feature band.

## Test-foundation gaps

- `tests/Husaynia.ContractTests/SchemaContractTests.cs:29-44` parses schema files as JSON and checks selected fields, but never validates positive/negative instances with a JSON Schema evaluator. This allowed the schema defects above.
- Authorization tests assert only counts/flags, not every matrix cell or every absent-role deny-by-default case.
- Provider compile-time tests assert method names and cancellation placement, but not complete parameter and return signatures.
- Endpoint-module ordering is deterministic in code but has no executed test; only service-module ordering is covered.

## Passing criteria evidence

- Project dependency direction matches the frozen architecture.
- Current role/permission matrix values match C3 and omitted roles deny by default.
- Provider ports are located in Application and their current signatures match C4.
- Module service discovery and configuration validation are deterministic for the tested assembly.
- Nullable, recommended analyzers, code style enforcement, central package versions, lock files, deterministic compilation, publish, and startup are present and executable.
