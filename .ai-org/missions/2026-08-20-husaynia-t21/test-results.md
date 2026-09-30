STATUS: BLOCKED
SUMMARY:
Independent repository-side T21-R3 validation passed: 368/368 pipeline-definition checks, 20/20 protected-execution-bundle causal checks, and 130/130 T21 harness checks passed. The harness recorded zero deployments. Official mission completion remains blocked by a reproduced fail-closed NuGet audit error (11 NU1900 errors caused by unavailable vulnerability metadata) and 30 out-of-scope formatting diagnostics. External OIDC/FIC configuration was deliberately not authenticated or mutated, so local marker tests do not prove that prerequisite is installed.

WORK_COMPLETED:
- Discovered and used locally cached actionlint 1.7.12 and Bicep 0.46.1; no tool installation or network download was performed.
- Independently executed the repository-defined pipeline-definition, protected-bundle, T21 contract, workflow-schema, Bicep, and owned-file secret-scan suites.
- Reproduced the strict locked NuGet-audit and formatting gates without changing repository implementation or tests.
- Performed no GitHub/Azure authentication, workflow dispatch, deployment, database call, resource mutation, secret change, commit, push, or history operation.

EVIDENCE:
TEST RESULT

Command:
1. `& $pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1`
2. `& $pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1`
3. `& $pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ActionlintPath 'C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe' -BicepPath 'C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe'`
4. `& $pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath 'C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe'`
5. `& $pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -BicepPath 'C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe'`
6. `& .\eng\security\Invoke-SecretScan.ps1 -ScanPaths 'pipelines','eng','.config' -ConfigurationPath .\pipelines\config\secret-scan.json -ReportPath <unique Temp report>`
7. `dotnet restore .\HusayniaSite.sln --locked-mode --nologo -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror`
8. `dotnet format .\HusayniaSite.sln --verify-no-changes --no-restore --severity warn --verbosity minimal`

Result:
```text
SUMMARY total=368 passed=368 failed=0
SUMMARY protected-bundle total=20 passed=20 failed=0
SUMMARY total=130 passed=130 failed=0 blocked=2 appSha256=317c628423046c27c9e154f32d707ab5c9f92c04ba54e1a337d6df8baebdd718 deployments=0
VALIDATION-STATUS status=BLOCKED_ENVIRONMENT passed=130 failed=0 blocked=2 contractOnly=false
ACTIONLINT status=PASS version=1.7.12 workflows=10
SUMMARY total=19 passed=19 failed=0
SUMMARY total=31 passed=31 failed=0
SUMMARY total=12 passed=12 failed=0
STATIC_PLAN planType=compiled-parameter-guard-evaluation azureStateQueried=false liveWhatIf=false deploymentExecuted=false
STAGE stage=Development ... create=0 update=0 delete=0 replace=0
STAGE stage=Production ... create=0 update=0 delete=0 replace=0
STAGE stage=Staging ... create=0 update=0 delete=0 replace=0
SECRET-SCAN status=PASS scanned=74 bytes=1111877 findings=0
```

Protected-bundle causal result lines were:
```text
PASS  protected-bundle-deterministic
PASS  protected-bundle-valid-extraction
PASS  protected-bundle-outer-hash-tamper-rejected
PASS  protected-bundle-manifest-tamper-rejected
PASS  protected-bundle-sha256sums-tamper-rejected
PASS  protected-bundle-toctou-recheck-rejected
PASS  protected-bundle-path-traversal-rejected
PASS  protected-bundle-archive-alias-rejected
PASS  protected-bundle-crlf-rejected
PASS  external-trust-marker-exact-match
PASS  external-trust-marker-absent-fails-closed
PASS  external-trust-marker-mismatch-fails-closed
PASS  stage-operations-missing-preflight-fails-before-oidc
PASS  stage-operations-missing-deployment-fails-before-oidc
PASS  stage-operations-missing-prepared-inputs-fail-before-oidc
SUMMARY protected-bundle total=20 passed=20 failed=0
```

The strict audit result was:
```text
error NU1900: Warning As Error: Error occurred while getting package vulnerability data:
Unable to load the service index for source https://api.nuget.org/v3/index.json.
```
It occurred for 11 projects; the restore command exited 1. The T21 harness independently produced `BLOCKED-ENVIRONMENT locked restore reached fail-closed NU1900 vulnerability-service TLS failure`.

The formatting result contained 30 `error WHITESPACE` diagnostics and exited 2. Affected paths are outside the T21 pipeline/engineering/configuration scope:
`src/Husaynia.Infrastructure/Identity/IdentityPersistence.cs` (195-198),
`tests/Husaynia.Application.Tests/Operations/Retention/RetentionWorkflowTests.cs` (391-392, 415-416, 452-453),
`tests/Husaynia.IntegrationTests/Identity/IdentityAnonymousRateLimitingTests.cs` (162-164),
`tests/Husaynia.IntegrationTests/Identity/IdentityCompatibilityRegressionTests.cs` (31-37), and
`tests/Husaynia.IntegrationTests/Identity/IdentityPrivilegedAuditOutcomeMatrixTests.cs` (59-65, 76-78).

Passed:
- Pipeline definitions: 368
- Protected bundle: 20
- T21 harness: 130
- Workflow schema: 1 run / 10 workflows
- Bicep static checks: 62 (19 + 31 + 12)
- Secret scan: 74 files, 0 findings

Failed:
- T21 assertion failures: 0
- Pipeline-definition failures: 0
- Protected-bundle failures: 0
- Official gate command failures: 2 (NuGet audit and formatting; detailed below)

Skipped: 0

Blocked:
- 2 in the T21 harness: fail-closed NuGet audit and out-of-scope formatting drift.

Failures:
- `dotnet restore` locked audit — expected a clean locked audit; actual 11 NU1900 warning-as-error diagnostics for the NuGet vulnerability service at `https://api.nuget.org/v3/index.json`; root cause: environmental vulnerability-metadata connectivity/TLS failure. The strict audit correctly failed closed. This is not a repository-side T21 logic failure.
- `dotnet format --verify-no-changes` — expected zero diagnostics; actual 30 whitespace diagnostics in the non-T21 files listed above; root cause: formatting drift outside `pipelines/**`, `eng/**`, and `.config/dotnet-tools.json`. The official quality gate remains non-green.

Coverage of acceptance criteria:
- DoD-1 exact SDK/tools, locked dependency restore, fail-closed audit -> BLOCKED. The harness proves the fail-closed branch and direct command reproduces 11 NU1900 errors; a clean audit cannot be proved while vulnerability metadata is unavailable.
- DoD-2 warnings-as-errors build and discovered compiled tests -> PASS. `Invoke-T21Validation.ps1`: `PASS discovered-tests projects=7 trx=7`.
- DoD-3 formatting/analyzers, secret scan, SBOM/dependency, Bicep, and required-file gates -> BLOCKED. Actionlint (10 workflows), Bicep (62 checks), secret scan (74 files/0 findings), required-file and accessibility fail-closed harness checks passed; formatting has 30 unrelated diagnostics.
- DoD-4 versioned C6 includes required content -> PASS. T21 artifact verification passed (`files=27` manifest view; C6 content view `files=26`) and verified `operations/protected-execution-bundle.zip` (40 entries).
- DoD-5 deterministic application archive -> PASS. `PASS deterministic-real-app-archive`; both artifacts used app SHA-256 `317c628423046c27c9e154f32d707ab5c9f92c04ba54e1a337d6df8baebdd718`.
- DoD-6 Development/Staging immutable checksum wiring and manual-disabled Production -> PASS. `PASS same-checksum-through-stage-definitions`, `PASS automatic-development-build-release-chain`, `PASS automatic-staging-development-chain-default-disabled`, and `PASS production-remains-disabled-and-separate`.
- DoD-7 fail-closed stage isolation and operation gates -> PASS for local/static repository behavior. The 368 definition checks and 130 harness checks include required artifact, isolation, preflight, hook, receipt, replay, and failure-stop paths. Actual cloud execution remains out of scope.
- DoD-8 tamper and induced-gate rejection -> PASS. Bundle outer hash, manifest, SHA256SUMS, extraction TOCTOU, CRLF, case alias, and traversal attacks were rejected; harness reported `PASS tamper-rejected`.
- DoD-9 separate/manual-disabled Production and zero deployment -> PASS for repository-side/static behavior. Production policy/wiring tests passed; harness recorded `deployments=0`; Bicep static plan recorded `deploymentExecuted=false` and zero create/update/delete/replace for every stage.
- DoD-10 YAML/static, dry run, secret-scan, independent security/review, and final judgment -> GAP. YAML/schema, Bicep static validation, T21 dry-run fixtures, and secret scan ran. Independent security review, code review, and final judgment are separate gates and were not rerun by this testing task.

CTO-required causal coverage:
- Missing deployment, preflight, and prepared-input artifacts: covered by `stage-operations-missing-*-fails-before-oidc` (all PASS).
- Bundle tamper, manifest/hash mismatch, TOCTOU, CRLF, aliases, and traversal: covered by the eight named protected-bundle causal tests above (all PASS).
- Stale/replay/fence behavior: `migration-stage-lease-authorization-replay-rejected`, `migration-stage-lease-stale-takeover-accepted`, and `migration-stage-lease-lost-fence-rejected` (all PASS).
- Disabled-stage no mutation/no success receipt: pipeline definitions include `stage-input-producer-protected-disabled-nonproduction` and `stage-input-producer-no-release-rebuild-or-mutation` (PASS); static plan reports zero resource changes.
- Absent/mismatched external trust marker: named marker tests above (PASS). These are local claim fixtures, not evidence of external OIDC/FIC installation.
- Pinned workflow identity: definition checks `trusted-workflow-call-only`, `trusted-workflow-full-sha-installation-sentinel`, and `trusted-wrapper-contracts` (PASS).
- Production disabled and zero mutation: production definition/harness checks (PASS); T21 reports `deployments=0`, and Bicep reports no resource operations.

Conclusion: PASS for repository-side T21-R3 tests; BLOCKED for official/mission completion.

ARTIFACTS:
- Updated only `C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\2026-08-20-husaynia-t21\test-results.md`.
- Temporary test-only artifacts/reports were created below `%TEMP%`; no repository implementation or test file was edited.

FINDINGS:
- Repository-side remediation test evidence is green: 368/368 definitions, 20/20 protected bundle tests, and 130/130 T21 checks.
- The initial nested PowerShell invocation of the standalone secret scanner passed its string-array parameters incorrectly and executed no scan; it was immediately rerun directly with the repository-supported array binding and passed (74 scanned, 0 findings). This was an invocation error, not an implementation test failure.
- No flaky test was observed in the executed T21 suites.

RISKS:
- Local trust-marker fixtures cannot establish that the required immutable GitHub OIDC subject and Azure federated credentials are installed. External authentication/configuration remains explicitly untested.
- The mission DoD cannot be declared complete until the separate security, code-review, and final-judgment gates pass.

BLOCKERS:
- NuGet vulnerability audit metadata is unavailable, producing fail-closed NU1900 errors for 11 projects.
- Thirty whitespace diagnostics leave the official formatting gate non-green.
- External OIDC/FIC provisioning and live workflow execution are out of scope and intentionally unperformed.

NEXT_ACTION:
Restore NuGet vulnerability-service access and correct the 30 formatting diagnostics through their owning work; then rerun the official PR gate. Independently run the security-review, code-review, and final-judgment gates. Do not treat this repository-side PASS as mission completion until the external OIDC/FIC prerequisite is installed and all blockers/gates are resolved.

---

# T21-R4 implementation validation — 2026-08-24

STATUS: PASS (repository-only implementation evidence; independent gates pending)

SUMMARY:
Implemented the approved caller-bound subject, bundle-owned role map, consumer-created API/digest/attestation provenance, and explicit zero-deployment boundary. All local R4 contract checks passed without external calls.

EVIDENCE:

| Command | Result |
| --- | --- |
| `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1` | `total=11 passed=11 failed=0` |
| `pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1` | `total=28 passed=28 failed=0` |
| `pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1` | `total=15 passed=15 failed=0` |
| `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly` | `total=4 passed=4 failed=0 blocked=0`; secret scan `scanned=77 findings=0` |
| `pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1` | `total=1 passed=1 failed=0` |
| `pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra` | `total=26 passed=26 failed=0` |

R4 causal checks include all five approved caller rows; missing/malformed external markers; all-zero reusable pins; v1 manifest rejection; archive/manifest traversal, alias, CRLF, checksum, and TOCTOU rejection; wrong GitHub run repository/path/ref/head SHA/attempt/status; duplicate, expired, digestless, altered, and foreign artifacts; wrong/replayed signer fixtures; mismatched application binding; and the exact `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary before candidate StageOperations input/receipt work.

MUTATION COUNTS:
- GitHub authentication/API calls: 0
- Azure logins: 0
- Workflow dispatches/deployments: 0
- Database calls/mutations: 0
- Resource/secret changes: 0
- Git commits/push/history operations: 0

FINDINGS:
- Bicep semantic compilation was not run because no Bicep executable is installed and network installation is forbidden. The installed-tools static Bicep policy suite passed 26/26.
- The existing external GitHub immutable subject/OIDC-template, Entra FIC, final trusted workflow SHA, and external bundle checksum remain deliberately unprovisioned; source retains all-zero/missing-marker fail-closed behavior.
- The required independent test, security, code-review, and judge gates have not been self-certified and remain pending.

---

# T21-R4 remediation validation — 2026-08-24

STATUS: LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES MUST RERUN

The bounded T21-R4 remediation repaired the runtime GitHub bearer-header path, pre-token
external subject marker/caller-stage validation, persistent canonical read-only bundle-root
handoff, allowed C6/raw-preflight/stage-input producer wiring, and the R4 stage-input producer
test contract. This is not an independent test, security, code-review, or judgment result; the
mission remains non-complete.

Initial causal red evidence before the implementation changes:

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=14 passed=11 failed=3

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=16 passed=15 failed=1

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
The property 'workflow_dispatch' cannot be found on this object.
```

The provenance regression was then made deterministic with an injected local request seam: it
captures three API/download request headers in memory, never prints the non-secret fixture
bearer value, and fails if any captured header is not `Bearer <fixture-value>`. A literal mask
cannot satisfy that assertion.

Final local evidence:

```text
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
SUMMARY total=4 passed=4 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=17 failed=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=1 passed=1 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath <cached actionlint.exe>
ACTIONLINT status=PASS version=1.7.12 workflows=10

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

.\eng\security\Invoke-SecretScan.ps1 -ScanPaths @('pipelines','eng','.config')
SECRET-SCAN status=PASS scanned=77 bytes=781292 findings=0
```

The T21 contract harness includes final pipeline definitions 14/14, protected-bundle causal
checks 32/32, and GitHub provenance causal checks 22/22. These cover missing/malformed marker,
all-zero reusable pin, unapproved caller, caller/stage mismatch, exact API repository/workflow/
ref/head/attempt/status/artifact-ID/digest rejection, archive/manifest role-stage-bundle
mismatch, missing/foreign/replayed attestation, deployment-role rejection, C6 tamper/
canonicalization/TOCTOU defenses, and the zero-deployment boundary before candidate receipt
work.

MUTATION AND CONSTRAINT RECORD:

- GitHub/Azure authentication, workflow dispatch, deployment, database, resource, secret,
  commit, push, and history mutations: **0**.
- No token was printed; the runtime bearer regression uses a non-secret local fixture value
  only.
- An initial direct workflow-schema invocation entered that script's `install pinned actionlint`
  branch before it failed on existing workflow diagnostics. That violated the no-install
  validation constraint; no egress outcome can be established from its output. The final schema
  validation used a pre-existing cached `actionlint.exe` and performed no installation.
- Independent test, security, code-review, and judgment gates: **PENDING RERUN; not
  self-certified**.

---

# T21-R4 independent-review rework validation — 2026-08-24

STATUS: LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES PENDING

Initial causal red regressions:

```text
Test-PipelineDefinitions.ps1: total=18 passed=14 failed=4
Test-GitHubArtifactProvenance.ps1: total=26 passed=25 failed=1
Test-StageOperationInputProducer.ps1: passed=17 failed=1
Test-ProtectedExecutionBundle.ps1: total=33 passed=32 failed=1
```

Final deterministic local evidence:

```text
Test-PipelineDefinitions.ps1: SUMMARY pipeline-definitions total=18 passed=18 failed=0 auth=0 deployments=0 database=0 resources=0
Test-ProtectedExecutionBundle.ps1: SUMMARY protected-bundle total=35 passed=35 failed=0
Test-GitHubArtifactProvenance.ps1: SUMMARY github-provenance total=26 passed=26 failed=0 auth=0 deployments=0 database=0 resources=0
Test-StageOperationInputProducer.ps1: SUMMARY stage-operation-input-producer passed=18 failed=0
Invoke-T21Validation.ps1 -ContractOnly: SUMMARY total=4 passed=4 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0
Test-T21ValidationExitCodes.ps1: SUMMARY t21-validation-exit-codes total=1 passed=1 failed=0
Invoke-SecretScan.ps1: SECRET-SCAN status=PASS scanned=77 bytes=800595 findings=0
```

Coverage: an authorized top-level stage-input caller with the pinned separate signer accepts;
reusable-path, manifest-caller, and run-selector confusion reject. The writer checkout precedes
manifest/upload/attestation; every trusted-reusable permission chain is exact; and the immutable
bundle-root validator rejects the absent/malformed/mismatched external bundle marker before the
token-request step while preserving the final post-token recheck.

MUTATION/OPERATION COUNTS: network/API 0; authentication/Azure logins 0; install/download 0;
workflow dispatches/deployments 0; database calls/mutations 0; resource/secret changes 0;
commits/pushes/history operations 0.

PENDING GATES: independent test-engineer, security-engineer, code-reviewer, and engineering-judge
gates; external GitHub immutable OIDC subject readback, Entra exact FIC installation, final
pinned workflow SHA, external C6 checksum; and pre-existing official NuGet-audit/format blockers.

---

# T21-R4-R6 final independent code-review remediation validation — 2026-08-24

STATUS: LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES PENDING RERUN

## Causal red evidence before production fix

The new deterministic regressions were added before the production change and run locally:

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=20 passed=18 failed=2 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=39 passed=35 failed=4
```

The failures were the missing resolver-output workflow wiring, legacy producer/synthetic
metadata acceptance, and absent consumer validation for valid, forged, mismatched, and missing
v2 provenance. No production file had been changed when these red results were recorded.

## Final repository-only validation

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=20 passed=20 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=39 passed=39 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=26 passed=26 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=18 failed=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
SUMMARY total=4 passed=4 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=1 passed=1 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

.\eng\security\Invoke-SecretScan.ps1 -ScanPaths @('pipelines','eng','.config')
SECRET-SCAN status=PASS scanned=77 bytes=782559 findings=0
```

The protected-bundle causal suite creates v2 `verified-provenance.json` through the real
fixture-backed `Resolve-GitHubArtifactProvenance.ps1` and proves it is accepted only up to the
intentional deployment-forbidden boundary. A producer `*-run.json`, role-mismatched provenance,
and missing provenance each fail before OIDC; the workflow-definition check proves the two
resolver output paths are passed before token minting. Existing C6 canonical/CRLF/alias/path/
TOCTOU, exact five-row caller matrix, no post-OIDC repository read/checkout, SQL/read-only
static controls, and zero-deployment assertions remain green.

MUTATION/CONSTRAINT COUNTS: GitHub/Azure authentication 0; GitHub API/network 0; workflow
dispatches/deployments 0; database calls/mutations 0; resource/secret changes 0; installs/
downloads 0; commits/pushes/history operations 0. The migration-fence live path was not run
because database activity is forbidden for this remediation.

COMPLIANCE FINDING: An earlier historical direct workflow-schema validation entered its
actionlint installer branch. This remediation did not invoke actionlint, Bicep, an installer
wrapper, or a download; the static Bicep policy script above is not the Bicep executable.

Independent test-engineer, security-engineer, code-reviewer, and engineering-judge outcomes are
not self-certified and remain PENDING RERUN. External OIDC/FIC/final-SHA prerequisites and the
previous NuGet-audit/format blockers remain unchanged.

---

# T21-R4-R7 final independent-finding remediation validation — 2026-08-24

STATUS: LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES PENDING RERUN

Initial causal regressions were added and executed before the production remediation:

```text
pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=2 passed=0 failed=2 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=5 passed=2 failed=3 auth=0 deployments=0 database=0 resources=0
```

The original failures prove that disabled Preflight returned before the immutable policy gate and
that migration authorization accepted caller-selected artifact names/root and legacy run metadata
instead of resolver-created v2 provenance.

Final repository-only commands and actual summaries:

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=23 passed=23 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=39 passed=39 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=26 passed=26 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=18 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1
SUMMARY total=12 passed=12 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=2 passed=2 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=7 passed=7 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
SUMMARY total=6 passed=6 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=1 passed=1 failed=0

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

.\eng\security\Invoke-SecretScan.ps1 -RepositoryRoot . -ScanPaths @('pipelines','eng','.config') -ReportPath <unique Temp report>
SECRET-SCAN status=PASS scanned=79 bytes=814856 findings=0
```

The Preflight causal test invokes the real immutable-bundle semantic consumer under the disabled
Development policy; it rejects with the stable `deploymentEnabled=false` error.  It then verifies
that this required non-`continue-on-error` workflow step precedes the token and Azure-login
steps.  The migration suite proves the removed selectors/legacy paths statically, exercises the
real disabled migration-preflight before its database/input boundary, and uses an enabled,
test-only policy fixture to prove missing consumer-created v2 release provenance fails before a
candidate artifact, evidence, or authorization receipt.  It retains the durable fence/read-only
assertions.  A fixture with a valid GitHub-style API digest but malformed C6 payload also rejects
without writing `verified-provenance.json`.

MUTATION/COMPLIANCE COUNTS: GitHub/Azure authentication 0; GitHub API/network 0; workflow
dispatches/deployments 0; database calls/mutations 0; resource/secret changes 0; installers/
downloads 0; commits/pushes/history operations 0.  The direct static Bicep policy script was run
without a Bicep executable; no actionlint/Bicep installer, downloader, schema wrapper, or
deployment wrapper was invoked.

FINDING: the historical direct workflow-schema invocation that entered an actionlint installer
branch remains recorded and was not repeated.  Independent test, security, code-review, and
engineering-judge gates remain PENDING RERUN; this local evidence does not certify them.  External
OIDC/FIC/final-SHA prerequisites and the prior NuGet-audit/format blockers remain unchanged.

---

# T21-R8 local remediation evidence — 2026-08-25

STATUS: LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES PENDING RERUN

The nine approved repository-only remediation items were implemented without enabling any
stage or performing a cloud, database, or resource operation. The final local checks were:

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=30 passed=30 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=38 passed=38 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=26 passed=26 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=19 failed=0

pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=2 passed=2 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=7 passed=7 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1
SUMMARY migration-stage-lease-fence total=3 passed=3 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
SUMMARY total=7 passed=7 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath <cached actionlint.exe>
ACTIONLINT status=PASS version=1.7.12 workflows=10

pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -BicepPath <cached bicep.exe>
SUMMARY total=19 passed=19 failed=0
SUMMARY total=31 passed=31 failed=0
SUMMARY total=12 passed=12 failed=0

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

PowerShell parser over eng\**\*.ps1
PS-PARSE PASS files=64

Workflow JSON parse plus exact pin enumeration
WORKFLOW-PARSE PASS files=10
PIN-VERIFY workflows=10 checkouts=5 exact=5

.\eng\security\Invoke-SecretScan.ps1 -ScanPaths @('pipelines','eng','.config')
SECRET-SCAN status=PASS scanned=80 bytes=838618 findings=0
```

R8 causal coverage:

1. The static contract fixes and enumerates all five production checkout references against
   the sole approved SHA `11bd71901bbe5b1630ceea73d27597364c9af683`.
2. Trusted/manual/Production wrapper input contracts reject caller artifact root/name and
   v1 metadata selectors; C6 resolution and resolver-created provenance precede OIDC.
3. Development/Staging contain disabled preflight, prepared-input, and StageOperations graphs;
   prepared inputs invoke canonical C6 resolution plus real report/bundle scripts, while the
   forbidden deployment boundary remains before token/receipt/mutation.
4. Migration authorization now declares exactly `actions: read`, `attestations: read`, and
   checkout-required `contents: read`; the resolver contains `gh attestation verify`.
5. The new local durable-fence fixture proves stale holder -> takeover/fence increment -> old
   holder rejection before mutation, then accepts only the current holder.
6. The trusted workflow directly compares the protected archive SHA with
   `vars.T21_TRUSTED_BUNDLE_SHA256` before bundle extraction, marker, validator, token, or
   login; the malicious caller-hash static causal test passes.
7. The non-ContractOnly PR command shape now exits 0 for the checked-in fully passing fixture;
   environmental blocking is no longer unconditional.
8. Pipeline contracts cover exact checkout, exact permissions, ordering, wiring, and prohibited
   selectors.
9. Duplicate forbidden-deployment tests were collapsed into one distinct
   `stage-operations-forbidden-deployment-boundary-prevents-token-and-receipt` causal test.

Initial actionlint validation caught an omitted `build_release` dependency on both newly wired
StageOperations jobs; actionlint named both invalid `needs.build_release.outputs.appSha256`
references. The jobs were corrected to include `build_release`, then the final cached actionlint
run passed. No test was weakened or skipped.

MUTATION COUNTERS: authentication=0; deployments=0; database=0; resources=0; installs=0;
network=0; workflow dispatches=0; commits/pushes/history operations=0.

The official locked restore/audit was deliberately not run because this task forbids network
access. Its existing fail-closed `NU1900` vulnerability-metadata blocker remains unsuppressed
and unwaived. Independent test, security, code-review, and judge gates remain pending; this
entry does not mark any independent artifact PASS.

---

# T21-R8 remaining-gap local evidence — 2026-08-25

STATUS: LOCAL SELF-VALIDATION PASS — INDEPENDENT GATES PENDING RERUN

The three remaining self-validation gaps were closed without network, authentication, workflow
dispatch, deployment, database access, resource mutation, installation, or git history changes.

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=33 passed=33 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=39 passed=39 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=26 passed=26 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=19 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1
SUMMARY total=12 passed=12 failed=0

pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=2 passed=2 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=7 passed=7 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1
SUMMARY migration-stage-lease-fence total=3 passed=3 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
SUMMARY total=7 passed=7 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe
ACTIONLINT status=PASS version=1.7.12 workflows=10

pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -BicepPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe
SUMMARY total=19 passed=19 failed=0
SUMMARY total=31 passed=31 failed=0
SUMMARY total=12 passed=12 failed=0

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

PowerShell parser over eng\**\*.ps1
PS-PARSE PASS files=64

pwsh -NoProfile -File .\eng\security\Invoke-SecretScan.ps1 -ScanPaths @("pipelines","eng",".config") -ConfigurationPath .\pipelines\config\secret-scan.json -ReportPath <temporary>
SECRET-SCAN status=PASS scanned=80 bytes=859711 findings=0
```

Finding coverage:

1. Finding 6: `Test-ProtectedExecutionBundle.ps1` retains the separately named static order
   assertion and adds a causal fixture that executes the exact checked-in workflow body with fake
   `gh`. The API carrier digest and caller-controlled inner hash match their fixtures, the protected
   SHA differs, and marker/validator/stage-target/trusted-archive side effects remain zero.
2. Finding 5: production Apply calls `Invoke-MigrationFencedMutation`, which invokes the existing
   local-or-SQL `AssertFence` path before the executor. The fixture proves stale takeover rejection
   with no mutation marker and proves the current holder invokes the fake executor exactly once.
3. Finding 3: all four StageOperations callers supply canonical C6, preflight, prepared-input, and
   migration-authorization selectors. The trusted consumer receives canonical
   `DeploymentEvidencePath`, preflight evidence, prepared-input root, authorization path,
   raw/validated evidence roots, and receipt path. Production-path assertions distinguish
   complete-but-unreachable wiring from missing wiring; every stage remains disabled and the
   deployment-evidence producer remains forbidden before token, mutation, or receipt.

During the rework loop, the first new static run exposed StrictMode access to an absent optional
`needs` property on the Production wrapper, and the first protected-bundle run exposed an indirect
fixture-path assumption. Both test defects were corrected before final runs. An initial secret-scan
invocation passed the three paths as one CLI value and failed closed; the correct array-shaped
invocation above passed. No test or control was weakened or skipped.

MUTATION COUNTERS: authentication=0; deployments=0; database=0; resources=0; network=0;
installs=0; workflow dispatches=0; commits/pushes/history operations=0; stale-holder fake mutation
executions=0; current-holder fake mutation executions=1; receipts produced=0.

Independent test, security, code-review, and engineering-judge gates remain pending. Mission state
is `VALIDATION`; this local evidence is not an independent gate approval.

---

# T21-R9 local remediation evidence — 2026-08-26

STATUS: **LOCAL IMPLEMENTATION PASS / OFFICIAL AUDIT BLOCKED — INDEPENDENT GATES PENDING**

All current independent R8 findings were remediated in the authorized repository paths. No
workflow was dispatched, no GitHub/Azure authentication occurred, no deployment or success
receipt was produced, and no database or cloud resource was accessed or mutated.

## Finding-to-fix/test mapping

| Finding | Repository remediation | Production-shaped causal evidence |
| --- | --- | --- |
| A — outer C6 carrier entry injection | Trusted inline `ZipArchive` validation rejects control characters (including CR/LF/NUL), rooted/traversal/backslash/colon paths, Unicode/case aliases, duplicates, link-like types, invalid directories, excessive count/size/ratio, and a noncanonical bundle entry. It requires exactly `operations/protected-execution-bundle.zip`, stream-hashes/copies it to fixed `runner.temp/t21-trusted-protected-execution-bundle.zip`, compares protected `T21_TRUSTED_BUNDLE_SHA256`, and exports only that fixed path. | Before the fix the added suite was red: `total=50 passed=42 failed=8`. Final protected-bundle suite: `51/51`. Exact checked-in run-body fixtures reject LF and CR environment-file injection, traversal, absolute path, case alias, duplicate canonical name, oversized entry, and wrong protected hash with zero marker/validator/stage-target/fixed-path effects; the valid carrier proves the fixed exported path. |
| B — broken preflight producer invocation | `Invoke-OperationEvidenceProducer.ps1` accepts all three stages, consumes resolver-created `ReleaseVerifiedProvenancePath`, derives the C6 root, uses explicit top-level caller/pinned producer/run identity, and no longer accepts legacy artifact roots or release/operation run-metadata paths. The resolver now consumes the same rootless GitHub artifact ZIP shape as the outer carrier and materializes it under the API-selected canonical artifact name. | `Test-PreflightPolicyBoundary.ps1`: `10/10`; it executes the checked-in trusted-workflow producer body for all five stage/caller rows and proves each disabled policy stops before auth/database/output. Full release resolver fixtures use the rootless uploaded-directory shape. |
| C — prepared-input v1/omitted dependencies/Production auth | Prepared-input contract is v2, carries normalized verified release binding, emits no run JSON, wires every stage-specific report dependency, and materializes a protected Production CTO context from resolver-created release/preflight/CTO provenance and exact approval/caller/reusable bindings. | Stage producer `59/59`; bundle `13/13`; CTO provenance `11/11`. Development/Staging/Production exact bundles pass with fixtures; every missing endpoint/project/prior report/auth input fails before output. Missing, DENY, expired, wrong actor/reference/release/preflight/C6/caller/reusable CTO context rejects. |
| D — producer identity mismatch | Producer manifests explicitly separate `topLevelCallerWorkflowRef` from pinned `producerWorkflowRef`; API-run fields describe the top-level run, while attestation signer identity is role-pinned by bundle policy. | GitHub provenance `34/34`, including execution of the exact manifest run body from `stage-operation-inputs.yml`; swapped identities, wrong API path, wrong signer, legacy identity aliases, and wrong Production caller reject. |
| E — migration authorization absent/inverted | All four StageOperations callers carry only an opaque migration authorization run selector. The trusted reusable resolves canonical `migration-authorization` API/digest/attestation provenance before OIDC and semantically binds the record to C6, app, release, stage, preflight SHA, exact caller/reusable pair, actor, expiry, and Production CTO context/reference. | Pipeline definitions `46/46`; migration authorization `13/13`; StageOperations semantics `12/12`. Missing, wrong stage/app/release/preflight/caller/reusable/actor/expiry, replayed authorization, and wrong Production CTO linkage reject before the deployment-forbidden boundary. |
| F — preserve R8 controls | Exact checkout pins, minimal attestation permissions, protected hash ordering, immutable/no-rebuild release, no post-login repository code, strict exit behavior, durable mutation claim/fence, disabled stages, and no success receipt remain enforced. | Durable fence `12/12` with old/stale fake executor writes `0`, current holder `1`; HTTP `7/7`; pin enumeration reports five exact checkout pins and zero mismatches; actionlint/Bicep/static/parse/secret checks pass. |

## Executed validation

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=46 passed=46 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=51 passed=51 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=34 passed=34 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=10 passed=10 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer passed=59 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1
SUMMARY total=13 passed=13 failed=0

pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1
SUMMARY cto-authorization-provenance total=11 passed=11 failed=0 auth=0 deployments=0 database=0 resources=0 installs=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=13 passed=13 failed=0 auth=0 deployments=0 database=0 resources=0 installs=0

pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1
SUMMARY trusted-stage-operations-semantics total=12 passed=12 failed=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1
SUMMARY migration-stage-lease-fence total=12 passed=12 failed=0 mutationMarkers=2 oldMutationMarkers=0 currentMutationMarkers=1 failureMutationMarkers=1 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location) -ContractOnly
SUMMARY total=11 passed=11 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location)
SUMMARY total=11 passed=11 failed=0 blocked=0 auth=0 deployments=0 database=0 resources=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath <cached actionlint.exe>
ACTIONLINT status=PASS version=1.7.12 workflows=10

pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -BicepPath <cached bicep.exe>
SUMMARY total=19 passed=19 failed=0
SUMMARY total=31 passed=31 failed=0
SUMMARY total=12 passed=12 failed=0

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 -InfraRoot .\infra
SUMMARY total=26 passed=26 failed=0

PowerShell parser over eng\**\*.ps1
PS-PARSE PASS files=66

JSON parser over pipelines\**\*.json and *.yml
JSON-PARSE PASS files=13

Workflow action enumeration
PIN-VERIFY workflows=10 checkouts=5 checkoutMismatches=0 attestations=4

pwsh -NoProfile -File .\eng\security\Invoke-SecretScan.ps1 ...
SECRET-SCAN status=PASS scanned=82 bytes=1250111 findings=0
```

The 12 focused causal suites above contain **270 passing assertions** and zero failures.

## Official locked audit

The strict command was run in a disposable `%TEMP%` repository copy so validation did not write
generated `obj` files under out-of-scope application/test paths:

```text
dotnet restore .\HusayniaSite.sln --locked-mode --nologo
  -p:NuGetAudit=true -p:NuGetAuditMode=all -warnaserror
```

It exited `1` with **11 `NU1900` warning-as-error project failures** because
`https://api.nuget.org/v3/index.json` vulnerability metadata was unavailable. No suppression,
ignore-failed-source option, audit disablement, or waiver was used. An initial disposable-copy
invocation omitted the solution's `tools/Husaynia.Migration` project and failed with `MSB3202`;
the corrected copy included `src`, `tests`, and `tools` and produced the authoritative NU1900
result above.

## Constraint and operation counters

- GitHub/Azure authentication: `0`
- Workflow dispatches: `0`
- Deployments/success receipts: `0`
- Database calls or mutations: `0`
- Cloud resource/secret mutations: `0`
- Tool/package install commands: `0`
- Git commit/push/history operations: `0`
- Stale/old-holder fake mutation executions: `0`
- Current-holder fake mutation executions: `1`

The official audit attempted only the required NuGet vulnerability-service read and failed closed.
No external OIDC/FIC configuration, protected variables, endpoints, or workflow pins were changed.

Independent test-engineer, security-engineer, code-reviewer, and engineering-judge gates remain
pending. This evidence does not self-approve or mark the mission complete.

## T21-R10 implementation and local self-validation — 2026-08-27

STATUS: **PASS — implementation tasks 1 through 7 complete; independent gates pending**

### Implemented

- Release is the sole C6 builder. `P`, `I`, `C`, `M`, and `S` are separate top-level runs.
- Added the nonprivileged completed-run coordinator with exact
  `actions:write`, `attestations:read`, and `contents:read` permissions; Production is never
  dispatched and checked-in false policy/variables yield zero dispatches.
- Resolver now requires `ConsumerRunId`, completed-success predecessors, selected run distinct from
  consumer, and `selected.updated_at <= consumer.created_at`; producer run commit remains separate
  from release commit.
- Prepared-input, CTO, migration, and StageOperations semantics use v2.1 exact `R/P/I/C/M`
  bindings. Production prepared inputs are inert; CTO owns checked `source-change-record.json`.
- Every checkout job uses the exact `Dreamer/HusayniaSite` root and `pwsh`; no-checkout jobs use
  `pwsh` without a checkout root. Protected post-login execution remains bundle-only.
- Binary C6 retrieval uses native stdout redirection with CLI/absent/empty/size/digest cleanup
  before ZIP processing. Exact action pins are consistent and exactly four producers attest.
- Protected closure, tests, README, architecture, decisions, task plan, and active mission state
  were refreshed.

### Executed evidence

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
SUMMARY pipeline-definitions total=34 passed=34 failed=0 workflows=11 runBodies=32

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
SUMMARY protected-bundle total=15 passed=15 failed=0 binaryCases=4

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
SUMMARY github-provenance total=34 passed=34 failed=0

pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1
SUMMARY cto-authorization-provenance total=15 passed=15 failed=0

pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
SUMMARY preflight-policy total=8 passed=8 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
SUMMARY stage-operation-input-producer total=16 passed=16 failed=0

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1
SUMMARY total=13 passed=13 failed=0

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
SUMMARY migration-authorization-provenance total=15 passed=15 failed=0

pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1
SUMMARY trusted-stage-operations total=14 passed=14 failed=0

pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1
SUMMARY migration-stage-lease-fence total=12 passed=12 failed=0
stale/old fake mutations=0; current holder=1

pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
SUMMARY stage-operation-http-client passed=7 failed=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location) -ContractOnly
SUMMARY total=11 passed=11 failed=0 blocked=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location)
SUMMARY total=11 passed=11 failed=0 blocked=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1
  -ActionlintPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe
ACTIONLINT status=PASS version=1.7.12 workflows=11

pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1
  -BicepPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe
Bicep build/lint 19/19; static policy 31/31; negative mutations 12/12;
static plan create/update/delete/replace=0.

PowerShell parser: PASS files=66
JSON parser: PASS files=14
Owned secret scan: PASS files=83 bytes=1030071 findings=0 (final harness rerun)
Dreamer/HusayniaSite root fixture: PASS; Dreamer root negative: rejected
Pin enumeration: login=1 exact; attest=4 exact; checkout=6 exact
```

Read-only upstream pin evidence:

```text
Azure/login refs/tags/v2.3.1^{}
7184910d9eb2b1c5e48f7073824a90609bb9b6d6

actions/attest-build-provenance refs/tags/v2.4.0 (lightweight)
e8998f949152b193b063cb0ec769d69d929409be
```

The official locked audit was not rerun because its official command performs package restore,
which R10 explicitly forbids. The prior authoritative result remains fail-closed: exit `1`, eleven
`NU1900` warning-as-error failures caused by unavailable NuGet vulnerability metadata. It was not
suppressed, waived, ignored, or normalized.

### Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

Independent Test Engineer, Security Engineer, Code Reviewer, and Engineering Judge gates remain
**PENDING**. This section is implementation/self-verification evidence and does not approve them.

---

## T21-R10 independent Test Engineer gate — 2026-08-27

TEST RESULT

Command:

```powershell
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path -ContractOnly
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -RepositoryRoot (Get-Location).Path -ActionlintPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe
pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -RepositoryRoot (Get-Location).Path -BicepPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe
gh api --help
gh api --output ignored.bin repos/octocat/hello-world
git ls-remote https://github.com/Azure/login.git 'refs/tags/v2.3.1' 'refs/tags/v2.3.1^{}'
git ls-remote https://github.com/actions/attest-build-provenance.git 'refs/tags/v2.4.0' 'refs/tags/v2.4.0^{}'
```

Additional inline PowerShell fixture probes parsed and executed the exact checked-in binary/root
bodies, invoked the production provenance resolver against a ten-case completed-run matrix, ran
seven mutated copies through the real pipeline contract validator, enumerated the lifecycle graph,
parsed all owned PowerShell/JSON, and ran the owned secret scanner. All fixture writes were under
`%TEMP%`; repository production and test files were read-only.

Result:

```text
SUMMARY pipeline-definitions total=34 passed=34 failed=0 workflows=11 runBodies=32
SUMMARY protected-bundle total=15 passed=15 failed=0 binaryCases=4
SUMMARY github-provenance total=34 passed=34 failed=0
SUMMARY cto-authorization-provenance total=15 passed=15 failed=0
SUMMARY preflight-policy total=8 passed=8 failed=0
SUMMARY stage-operation-input-producer total=16 passed=16 failed=0
SUMMARY total=13 passed=13 failed=0
SUMMARY migration-authorization-provenance total=15 passed=15 failed=0
SUMMARY trusted-stage-operations total=14 passed=14 failed=0
SUMMARY migration-stage-lease-fence total=12 passed=12 failed=0
SUMMARY stage-operation-http-client passed=7 failed=0
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

ContractOnly: SUMMARY total=11 passed=11 failed=0 blocked=0
Safe full fixture: SUMMARY total=11 passed=11 failed=0 blocked=0

ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep: 19/19 build/lint; 31/31 static policy; 12/12 negative mutations
STATIC_PLAN suppliedStageCreate=0 suppliedStageUpdate=0 suppliedStageDelete=0 suppliedStageReplace=0
PS-PARSE files=66 errors=0
JSON-PARSE files=14 errors=0
SECRET-SCAN status=PASS scanned=83 bytes=1030071 findings=0 silentlySkipped=0

GH-HELP exit=0 outputFlagOccurrences=0
GH-UNSUPPORTED exit=1: unknown flag: --output

BINARY-INDEPENDENT carrierByteIdentical=True arbitraryBytes=000d0aff80410042
BINARY-NEGATIVE cli-failure/empty/digest-mismatch environmentFile=False fixedBundle=False
BINARY-OVERSIZE-INDEPENDENT exit=1 carrierExists=False environmentFile=False fixedBundle=False

ROOT-INDEPENDENT checkoutJobs=6 validPassed=6 wrongRootRejected=6
LIFECYCLE-INDEPENDENT total=10 passed=10 failed=0
  valid completed-success accepted
  same-run, future, in-progress, failed, cancelled, missing conclusion,
  unavailable selector, cross-stage, and cross-release rejected
PIN-NEGATIVE-INDEPENDENT total=7 passed=7 failed=0
  tag, branch, zero, unapproved SHA, inconsistent attestation SHA,
  extra attestation producer, and broadened coordinator permission rejected
PIN-ENUM loginCount=1 loginUnique=1 attestCount=4 attestUnique=1 checkoutCount=6
PIN-UPSTREAM Azure/login v2.3.1^{} =
  7184910d9eb2b1c5e48f7073824a90609bb9b6d6
PIN-UPSTREAM actions/attest-build-provenance v2.4.0 =
  e8998f949152b193b063cb0ec769d69d929409be
GRAPH-INDEPENDENT workflows=11 releaseJobs=1 soleC6BuilderFiles=1
  selfSelectorExpressions=0 coordinatorTriggers=3 cycleFree=True
  stages=Development,Staging productionManualDisabled=True noRebuild=True
REQUIREMENTS-FREEZE requirements=18 acceptanceCriteria=20 assumptions=2 blockingOpenQuestions=0
```

Passed:   185 direct focused assertions; 22 harness checks; all listed cached/static and fixture probes
Failed:   0 implementation assertions
Skipped:  0
Blocked:  1 official locked NuGet audit

Failures:

None in repository-side R10 behavior. Five initial one-off probe commands had tester setup errors
(PowerShell array binding, a null guard, a non-deterministic sparse-file approach, an overly broad
selector regex, and multi-file text concatenation); each was corrected and rerun successfully.
They did not expose or conceal an implementation failure.

The official audit was not rerun because the frozen gate forbids package restore. Retained
authoritative evidence remains exit `1` with eleven `NU1900` warning-as-error project failures
caused by unavailable vulnerability metadata, with no waiver or success normalization
(`test-results.md:726-740,855-858`). The installed PR command remains strict and does not pass the
authoring-only exception (`pipelines/github/pr-validation.yml:60-64`;
`eng/ci/Invoke-DotNetValidation.ps1:37-80`).

Coverage of acceptance criteria:

- AC-BIN-01 -> exact-body binary fixture plus independent ZIP byte comparison: PASS
  (`eng/test/Test-ProtectedExecutionBundle.ps1:101-186`;
  `pipelines/github/trusted-protected-operations.yml:83`).
- AC-BIN-02 -> exact body contains no `--output`; installed `gh` 2.97.0 rejects it: PASS.
- AC-BIN-03 -> CLI/empty/digest causal cases, deterministic oversize probe, missing/static guard,
  and zero environment/fixed-bundle effects: PASS
  (`eng/test/Test-ProtectedExecutionBundle.ps1:188-217`;
  `pipelines/github/trusted-protected-operations.yml:83`).
- AC-PIN-01 -> exact nonzero pins plus fresh read-only upstream tag/peel evidence: PASS.
- AC-PIN-02 -> one login SHA, four identical attestation SHAs, exact policy and test assertions,
  and old production/policy pins absent: PASS (`eng/ci/Test-PipelineDefinitions.ps1:272-300`;
  `pipelines/config/promotion-policy.json:205-207`).
- AC-PIN-03 -> seven temp-copy mutation cases all caused validator exit `1`: PASS.
- AC-ROOT-01 -> 32 workflow run bodies parse/use `pwsh`; six exact child-root bodies execute:
  PASS (`eng/ci/Test-PipelineDefinitions.ps1:52-151`).
- AC-ROOT-02 -> release has one job, is the sole C6 builder, and passes the child root explicitly:
  PASS (`eng/ci/Test-PipelineDefinitions.ps1:153-165`).
- AC-ROOT-03 -> all six wrong-root executions reject without fixture effects; trusted execution is
  still bundle-only: PASS (`eng/test/Test-StageOperationInputProducer.ps1:122-146`;
  `eng/test/Test-ProtectedExecutionBundle.ps1:218-230`).
- AC-LIFE-01 -> completed-success baseline accepted and immutable release bindings retained: PASS
  (`eng/promotion/Resolve-GitHubArtifactProvenance.ps1:356-377`).
- AC-LIFE-02 -> in-progress, failed, cancelled, and missing conclusion independently rejected:
  PASS.
- AC-LIFE-03 -> distinct Development/Staging wrapper modes and `R -> P/I -> M -> S` graph: PASS
  (`eng/test/Test-StageOperationInputProducer.ps1:72-121`).
- AC-LIFE-04 -> completed `R/P/I/C/M` Production contracts plus separate manual-disabled
  Production: PASS (`eng/test/Test-CtoAuthorizationProvenance.ps1:24-68`;
  `eng/test/Test-MigrationAuthorizationProvenance.ps1:22-65`;
  `pipelines/config/promotion-policy.json:466-471`).
- AC-LIFE-05 -> same, future, unavailable, cross-stage, cross-release, wrong caller/signer, and
  failed producer rejection: PASS (`eng/test/Test-GitHubArtifactProvenance.ps1:548-779`).
- AC-LIFE-06 -> coordinator graph is cycle-free, nonproduction-only, and StageOperations is not
  embedded in release/producer runs: PASS
  (`eng/ci/Test-PipelineDefinitions.ps1:166-210`;
  `pipelines/github/automatic-nonproduction-orchestration.yml:2-24,51-62`).
- AC-LIFE-07 -> one release builder, zero downstream builders, all stages `rebuildAllowed=false`:
  PASS (`eng/ci/Test-PipelineDefinitions.ps1:91-102,153-165`).
- AC-GATE-01 -> all named R9/R10 regression suites passed, including fencing and HTTP: PASS.
- AC-GATE-02 -> both harness modes, cached actionlint/Bicep, 66 PowerShell parses, 14 JSON parses,
  and 83-file secret scan passed with no install/restore: PASS.
- AC-GATE-03 -> official audit remains a nonzero fail-closed environmental blocker: PASS as a
  policy behavior, but it blocks the overall gate.
- AC-GATE-04 -> this section is independent Test Engineer evidence only; no security, review, or
  judgment approval is claimed: PASS.

Conclusion: PASS for all repository-side R10 acceptance criteria; overall gate is BLOCKED by the
mandated official `NU1900` audit connectivity blocker.

STATUS:          BLOCKED
SUMMARY:
All 20 frozen R10 acceptance criteria are proven by independently executed repository-side tests
and probes with zero related failures or coverage gaps. The independent test gate cannot be
reported as overall PASS while the official locked audit remains fail-closed on eleven `NU1900`
errors.

WORK_COMPLETED:
- Executed every required focused suite, both harness modes, exit-code contract, cached
  actionlint/Bicep, PowerShell/JSON parsing, secret scan, binary/root/lifecycle/pin/graph probes,
  and fresh read-only upstream pin resolution.
- Did not modify production or test code.

EVIDENCE:
The TEST RESULT above.

ARTIFACTS:
- `.ai-org/missions/2026-08-20-husaynia-t21/test-results.md` — this appended independent R10 gate
  section only.

FINDINGS:
- Repository-side implementation defects: none.
- Acceptance-criterion coverage gaps: none.
- Flakes: none observed across direct and repeated harness execution.
- Environmental finding: official locked audit remains blocked and fail-closed.

RISKS:
- Live GitHub Actions/Azure installation, authentication, dispatch, and deployment were explicitly
  out of scope and are not proven by this local gate.

BLOCKERS:
- NuGet vulnerability metadata connectivity: retained official command exits `1` with eleven
  `NU1900` warning-as-error project failures.

Required operation counters:
- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

NEXT_ACTION:
Restore official NuGet vulnerability-service connectivity and rerun the unchanged locked audit
without waiver, source-ignore, audit disablement, or success normalization. No R10 source rework
is indicated by this independent test gate.

---

# T21-R10-R1 local implementation/self-validation evidence — 2026-08-27

Status: **PASS for local implementation; independent gates remain pending**

## Causal red evidence

The new `eng/test/Test-T21R10EnabledPathRegressions.ps1` executes the checked-in workflow run
bodies with local fake GitHub API, SQL, and protected report-service clients.

- Exact pre-fix coordinator mutation (`-f "inputs[...]"` changed back to
  `-F "inputs[...]"` in a disposable minimal fixture): exit `1`;
  `SUMMARY ... total=12 passed=11 failed=1`. The captured request body contained numeric
  `sourceReleaseRunId`, `preflightRunId`, `stageOperationInputsRunId`, and
  `migrationAuthorizationRunId` instead of JSON strings.
- Pre-fix Preflight/report-contract mutation (producer moved before OIDC/login, readonly mode and
  immutable pre-token enablement removed, stage environment and both report token mappings
  removed): exit `1`; `SUMMARY ... total=12 passed=5 failed=7`. Enabled Preflight failed its
  ordering/auth/readonly/artifact checks, and enabled I/C failed for missing
  `T21_STAGE_READONLY_PROBE_TOKEN`.

Both mutation fixtures were created and removed under `%TEMP%`; no workflow was dispatched and no
authentication, database, endpoint, or cloud operation occurred.

## Focused and regression results

```text
Test-T21R10EnabledPathRegressions.ps1
  total=12 passed=12 failed=0
Test-PreflightPolicyBoundary.ps1
  total=8 passed=8 failed=0
Test-StageOperationInputProducer.ps1
  total=17 passed=17 failed=0
Test-CtoAuthorizationProvenance.ps1
  total=16 passed=16 failed=0
Test-PipelineDefinitions.ps1
  total=37 passed=37 failed=0 workflows=11 runBodies=33
Test-ProtectedExecutionBundle.ps1
  total=15 passed=15 failed=0 binaryCases=4
Test-GitHubArtifactProvenance.ps1
  total=34 passed=34 failed=0
Test-StageOperationInputBundle.ps1
  total=13 passed=13 failed=0
Test-MigrationAuthorizationProvenance.ps1
  total=15 passed=15 failed=0
Test-TrustedStageOperationsSemantics.ps1
  total=14 passed=14 failed=0
Test-MigrationStageLeaseFence.ps1
  total=12 passed=12 failed=0 oldMutationMarkers=0 currentMutationMarkers=1
Test-StageOperationHttpClient.ps1
  passed=7 failed=0
```

Final named direct assertions: **200 passed, 0 failed**.

## Harness/static results

```text
Invoke-T21Validation.ps1 -ContractOnly
  total=12 passed=12 failed=0 blocked=0
Invoke-T21Validation.ps1
  total=12 passed=12 failed=0 blocked=0
Test-T21ValidationExitCodes.ps1
  total=2 passed=2 failed=0
Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath <cached 1.7.12>
  ACTIONLINT status=PASS version=1.7.12 workflows=11
Invoke-BicepValidation.ps1 -BicepPath <cached 0.46.1>
  build/lint=19/19 static-policy=31/31 negative-mutations=12/12
  suppliedStageCreate=0 suppliedStageUpdate=0 suppliedStageDelete=0 suppliedStageReplace=0
PowerShell parser
  files=67 errors=0
JSON-compatible parser (.json plus authored workflow .yml)
  files=15 errors=0
Invoke-SecretScan.ps1
  scanned=84 bytes=1082091 findings=0
```

The official locked audit was not rerun because it performs package restore, which this local
implementation pass explicitly forbids. Its authoritative retained result remains exit `1` with
**11 fail-closed `NU1900` warning-as-error failures** caused by unavailable
`https://api.nuget.org/v3/index.json` vulnerability metadata. No suppression, source ignore,
audit disablement, waiver, or success normalization was added.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

Independent test, security, code-review, and judgment gates are **PENDING RERUN**. This section is
implementation/self-validation evidence only.

---

# T21-R10-R1 independent Test Engineer rerun — 2026-08-27

TEST RESULT

Command:

```powershell
Set-Location C:\Users\syedhu\source\repos\Dreamer\HusayniaSite

pwsh -NoProfile -File .\eng\test\Test-T21R10EnabledPathRegressions.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1 -RepositoryRoot (Get-Location).Path
pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1 -RepositoryRoot (Get-Location).Path

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path -ContractOnly
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1 -RepositoryRoot (Get-Location).Path -ActionlintPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\actionlint\actionlint.exe
pwsh -NoProfile -File .\eng\ci\Invoke-BicepValidation.ps1 -RepositoryRoot (Get-Location).Path -BicepPath C:\Users\syedhu\AppData\Local\Temp\husaynia-t21-independent-20260821\bicep\bicep.exe

git ls-remote https://github.com/Azure/login.git refs/tags/v2.3.1 refs/tags/v2.3.1^{}
git ls-remote https://github.com/actions/attest-build-provenance.git refs/tags/v2.4.0 refs/tags/v2.4.0^{}
gh api --help
gh api --output ignored.bin repos/octocat/hello-world
```

Read-only inline PowerShell probes additionally:

- executed every checkout job's exact first root-validation body against valid
  `Dreamer/HusayniaSite` and wrong-root fixtures;
- invoked the production provenance resolver for completed-success, same-run, future,
  in-progress, failed, cancelled, missing-conclusion, unavailable-selector, cross-stage, and
  cross-release cases;
- ran seven disposable `eng`/`pipelines` mutations through the real pipeline validator for tag,
  branch, zero, nonexistent, inconsistent action pins, an extra attestation producer, and
  broadened coordinator permission;
- executed the exact checked-in binary retrieval body with an independently simulated oversize
  carrier length;
- parsed all 67 owned PowerShell files and 15 JSON-compatible pipeline/config files, verified the
  frozen requirement counters, and invoked the secret scanner with
  `-ScanPaths @('pipelines','eng','.config')`.

All probe writes were under `%TEMP%` and removed. Repository production and test files were
read-only.

Result:

```text
SUMMARY t21-r10-enabled-path total=12 passed=12 failed=0 auth=0 dispatches=0 deployments=0 receipts=0 database=0 resources=0 secrets=0
SUMMARY pipeline-definitions total=37 passed=37 failed=0 workflows=11 runBodies=33
SUMMARY protected-bundle total=15 passed=15 failed=0 binaryCases=4
SUMMARY github-provenance total=34 passed=34 failed=0
SUMMARY cto-authorization-provenance total=16 passed=16 failed=0
SUMMARY preflight-policy total=8 passed=8 failed=0
SUMMARY stage-operation-input-producer total=17 passed=17 failed=0
SUMMARY total=13 passed=13 failed=0
SUMMARY migration-authorization-provenance total=15 passed=15 failed=0
SUMMARY trusted-stage-operations total=14 passed=14 failed=0
SUMMARY migration-stage-lease-fence total=12 passed=12 failed=0 mutationMarkers=2 oldMutationMarkers=0 currentMutationMarkers=1 failureMutationMarkers=1
SUMMARY stage-operation-http-client passed=7 failed=0
SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

ContractOnly: SUMMARY total=12 passed=12 failed=0 blocked=0
Safe full:   SUMMARY total=12 passed=12 failed=0 blocked=0

ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN suppliedStageCreate=0 suppliedStageUpdate=0 suppliedStageDelete=0 suppliedStageReplace=0
PS-PARSE files=67 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=84 bytes=1083489 findings=0

GH-HELP-OUTPUT-FLAG-COUNT=0
GH-UNSUPPORTED-EXIT=1 unknown flag: --output
PIN-UPSTREAM Azure/login v2.3.1^{}=7184910d9eb2b1c5e48f7073824a90609bb9b6d6
PIN-UPSTREAM actions/attest-build-provenance v2.4.0=e8998f949152b193b063cb0ec769d69d929409be

ROOT-INDEPENDENT checkoutJobs=6 total=6 passed=6 failed=0 validPassed=6 wrongRootRejected=6
LIFECYCLE-INDEPENDENT total=10 passed=10 failed=0
PIN-NEGATIVE-INDEPENDENT total=7 passed=7 failed=0
BINARY-OVERSIZE-INDEPENDENT total=1 passed=1 failed=0 carrierExists=False environmentFile=False fixedBundle=False
REQUIREMENTS-FREEZE requirements=18 acceptanceCriteria=20 assumptions=2 blockingOpenQuestions=0
```

The enabled-path suite independently proved all three R10-R1 corrections:

1. the exact coordinator fake request body matched the expected JSON byte-for-byte, every dispatch
   input was a JSON string, and the observed arguments contained no typed `-F`/`--field`;
2. immutable enablement precedes token minting; disabled policy stops pre-token; enabled Preflight
   orders OIDC -> login -> read-only recheck -> bundle producer; fake `sqlcmd -G` observed login
   and `T21_MIGRATION_IDENTITY_MODE=readonly`; missing/wrong mode stopped before SQL or artifact;
3. prepared-input and CTO jobs use the exact protected environments, map
   `T21_STAGE_READONLY_PROBE_TOKEN` only on the report step, complete valid fake HTTP paths without
   persisting the token, and reject a missing token before publishable manifest/artifact inputs.

Passed:   250 behavioral assertion/check executions (200 direct focused assertions, 24 harness
checks, 2 exit-code assertions, and 24 independent lifecycle/pin/root/binary probes), plus all
listed static/file gates
Failed:   0 implementation assertions/checks
Skipped:  0
Blocked:  1 retained official locked NuGet audit

Failures:

None in repository-side R10/R10-R1 behavior.

Three first-form ad-hoc probe commands had tester setup errors: the secret scan paths were initially
passed as one comma-delimited native-process argument, the lifecycle helper initially omitted the
existing common-function import, and the oversize fake initially used a timestamp shape rejected
before the intended size branch. Each command was corrected and rerun to the passing result above.
They were not implementation failures or flakes. The enabled-path suite passed on its direct run
and in both harness modes (3/3).

Coverage of acceptance criteria:

- AC-BIN-01 -> `exact-binary-body-preserves-carrier-and-streams-fixed-bundle`: PASS.
- AC-BIN-02 -> exact retrieval body has no `--output`; installed `gh` reports no such flag and
  rejects it with exit 1: PASS.
- AC-BIN-03 -> exact CLI/empty/digest cases plus independent exact-body oversize execution and
  missing/cleanup guards stop before environment/fixed-bundle effects: PASS.
- AC-PIN-01 -> exact nonzero 40-hex pins and fresh public upstream tag/peel results: PASS.
- AC-PIN-02 -> one login pin, four identical attestation pins, policy consistency, and both old
  invalid pins absent: PASS.
- AC-PIN-03 -> all seven disposable tag/branch/zero/nonexistent/inconsistent/extra-producer/
  permission-broadening mutations caused the real validator to exit 1 at the expected assertion:
  PASS.
- AC-ROOT-01 -> 33 workflow run bodies use/parse as `pwsh`; all six checkout root bodies accepted
  only the exact child fixture: PASS.
- AC-ROOT-02 -> release remains the sole C6 builder and explicitly uses the child repository root:
  PASS.
- AC-ROOT-03 -> all six wrong-root executions rejected; post-login execution remains protected
  bundle-only: PASS.
- AC-LIFE-01 -> completed-success resolver baseline accepted with unchanged release binding:
  PASS.
- AC-LIFE-02 -> in-progress, failed, cancelled, and missing-conclusion resolver cases rejected:
  PASS.
- AC-LIFE-03 -> Development/Staging use distinct completed `R -> P/I -> M -> S` runs and no current
  run selector: PASS.
- AC-LIFE-04 -> Production preserves completed `R/P/I/C/M`, separate manual-disabled `S`, CTO
  environment, change-record ownership, and migration linkage: PASS.
- AC-LIFE-05 -> same-run, future, unavailable, cross-stage, cross-release, wrong caller/signer,
  digest, and failed producer paths reject before the forbidden/OIDC boundary: PASS.
- AC-LIFE-06 -> coordinator is nonproduction-only and cycle-free; StageOperations is not embedded
  in release or producer runs: PASS.
- AC-LIFE-07 -> release is the only C6 build; all downstream stages remain no-rebuild and reuse
  immutable bindings: PASS.
- AC-GATE-01 -> all named R10/R9 suites passed, including HTTP safety and durable fencing: PASS.
- AC-GATE-02 -> both 12/12 harness modes, exit-code contract, cached actionlint/Bicep, 67
  PowerShell parses, 15 JSON-compatible parses, and 84-file secret scan passed with no
  install/restore: PASS.
- AC-GATE-03 -> retained official locked audit remains nonzero and fail-closed; it was not rerun
  because this gate forbids package restore: PASS for policy behavior; official audit remains
  BLOCKED.
- AC-GATE-04 -> this is independent Test Engineer evidence only; security, code-review, and
  judgment approval are not claimed: PASS.

Conclusion: **PASS** for the repository-side T21-R10/R10-R1 test gate with all 20 frozen acceptance
criteria covered and zero gaps. The known official locked audit remains separately **BLOCKED**.

STATUS:          PASS

SUMMARY:

Repository-side R10-R1 remediation and all frozen R10/R9 controls passed independent execution.
The repository gate is not converted to BLOCKED merely because the separate official audit
environment is unavailable, and the official audit is not normalized to success.

WORK_COMPLETED:

- Executed the enabled-path regression, every focused R10/R9 suite, both harness modes, exit-code
  contract, cached actionlint/Bicep/static checks, PowerShell/JSON parsing, secret scan, and
  independent root/lifecycle/pin/binary probes.
- Mapped all 20 frozen acceptance criteria.
- Did not modify production code or tests and performed no live cloud operation.

EVIDENCE:

The TEST RESULT above.

ARTIFACTS:

- `.ai-org/missions/2026-08-20-husaynia-t21/test-results.md` — appended this independent R10-R1
  gate section only.
- Test files added/updated: none.

FINDINGS:

- Repository-side defects: none.
- Acceptance-criterion gaps: none.
- Flakes: none observed.
- The implementation self-evidence reported 15 JSON-compatible files; independent enumeration
  confirms this is 11 workflows + 3 pipeline JSON files + `.config/dotnet-tools.json`.

RISKS:

- Live workflow installation, protected-environment configuration, authentication, dispatch,
  deployment, cloud/database access, and external enablement remain out of scope and unproven.

BLOCKERS:

- Official locked NuGet audit: retained authoritative exit `1` with eleven fail-closed `NU1900`
  warning-as-error project failures because vulnerability metadata at
  `https://api.nuget.org/v3/index.json` is unavailable. No waiver, audit disablement,
  source-ignore, skipped test, or success normalization was used.

Required operation counters:

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

Fixture-only fake GitHub API, `sqlcmd`, and HTTP observations are not actual operations.

NEXT_ACTION:

No repository-side R10-R1 remediation is indicated. Restore official NuGet vulnerability-metadata
connectivity and rerun the unchanged locked audit without waiver or normalization; proceed with
the still-separate security, code-review, and judgment gates.

---

# T21-R10-R2 local implementation/self-validation evidence — 2026-08-27

Status: **PASS for implementation and local validation; independent gates remain pending**

## Implementation evidence

- `New-ProtectedExecutionBundle.ps1` now accepts the already-created application archive, parses
  the exact `.NETCoreApp,Version=v10.0` dependency target, selects the recursive managed
  `Microsoft.Data.SqlClient/6.1.1` Unix closure, and emits protected manifest `1.1.0`.
- The resulting synthetic fixture contains exactly **19** managed runtime assemblies beneath
  `runtime/sqlclient`; project, missing, wrong-package/version/target, native Unix, duplicate,
  unsafe/aliased, extra, tampered, writable, and app-hash-mismatched cases reject.
- Release construction passes `app/Husaynia.Web.zip`; release validation supplies the manifest
  application SHA to the protected validator. Release manifest schema remains `1.0.0`.
- `Migration.Common.ps1` contains a manifest-restricted collectible
  `RestrictedAssemblyLoadContext`, Linux x64 / PowerShell 7.6+ / .NET 10 compatibility gate,
  exact 30-second direct Azure CLI token request, nonpooled encrypted SqlClient connection, typed
  one-result/one-row validation, sanitized stable failures, and no retry.
- Preflight preserves its exact 11-column fixed SELECT and permission assertions. Durable lease
  SQL preserves the existing transaction/fence text and validates the typed seven-column result.
  The application migration bundle connection contract is unchanged.
- Local providers/executors require `EnableLocalTestSeams`; `GITHUB_ACTIONS=true` rejected both
  before either seam ran (`seamActionsCalls=0`).
- The producer-equals-release comparison was removed. Release provenance remains bound to
  `release-manifest.commitSha`; producer API/manifest/attestation bindings remain unchanged.

Key locations:

- `eng/artifact/New-ProtectedExecutionBundle.ps1:5,155-319,396-419`
- `eng/artifact/Test-ProtectedExecutionBundle.ps1:17,72-94,215-294`
- `eng/artifact/migrations/bundle/Migration.Common.ps1:174-257,261-314,318-410,562-711,2299-2317`
- `eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1:31-41,168-170,294-314`
- `pipelines/github/trusted-protected-operations.yml:104,113,138`
- `eng/test/Test-MigrationSqlClientExecution.ps1`

## Final focused and harness results

Commands were run from `C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`.

```text
Test-PipelineDefinitions.ps1                 41/41
Test-ProtectedExecutionBundle.ps1            30/30
Test-GitHubArtifactProvenance.ps1            34/34
Test-CtoAuthorizationProvenance.ps1          16/16
Test-PreflightPolicyBoundary.ps1              8/8
Test-T21R10EnabledPathRegressions.ps1        12/12
Test-StageOperationInputProducer.ps1         17/17
Test-StageOperationInputBundle.ps1           13/13
Test-MigrationAuthorizationProvenance.ps1    15/15
Test-TrustedStageOperationsSemantics.ps1     14/14
Test-MigrationStageLeaseFence.ps1            13/13
Test-MigrationSqlClientExecution.ps1         26/26
Test-StageOperationHttpClient.ps1             7/7
Focused assertions/checks total             246/246

Invoke-T21Validation.ps1 -ContractOnly       13/13, failed=0, blocked=0
Invoke-T21Validation.ps1                     13/13, failed=0, blocked=0
Test-T21ValidationExitCodes.ps1               2/2
```

The SQL-client suite reported
`tokenOccurrences=0 seamActionsCalls=0 sqlCalls=2`; the lease suite reported
`oldMutationMarkers=0 currentMutationMarkers=1 failureMutationMarkers=1`.

## Final static/cached evidence

```text
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN suppliedStageCreate=0 suppliedStageUpdate=0
            suppliedStageDelete=0 suppliedStageReplace=0
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1176072 findings=0
POLICY-HASHES checked=4 mismatches=0
RUNTIME-SUPPLY-CHAIN files=3 hits=0
```

No tool was installed or restored. Synthetic application/protected ZIPs were created only under
`%TEMP%`; no actual C6 release artifact was built. The local Windows host has a `sqlcmd`
application, so hosted-image absence was not represented as a local red test; the accepted R2
architecture retains the authoritative `ubuntu-24.04` image evidence. The checked workflow and
migration runtime contain zero `sqlcmd`, apt/brew/tool-install/restore, container, or runtime
download paths.

The official locked NuGet audit was not rerun because this implementation pass prohibits package
restore. Its retained authoritative result remains exit `1` with eleven fail-closed `NU1900`
warning-as-error failures. No waiver, audit disablement, source ignore, or success normalization
was added.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

## Independent-gate disposition

Two independent-agent launch attempts (implementation specialist, then Test Engineer) were
rejected by the orchestration environment with:

```text
Maximum sub-agent depth of 4 reached. Complete this task without spawning further sub-agents.
```

Therefore R2-GT, R2-GS, R2-GR, and R2-J remain **PENDING**. This section is implementation and
self-validation evidence only; it does not approve any independent gate.

---

# T21-R10-R2 independent-gate remediation self-validation — 2026-08-27

Status: **PASS for local remediation; independent R2 gates remain pending rerun**

All five supplied independent findings were remediated at root cause without changing the frozen
R2 direction or any R1/R9/R10 authorization, disabled-stage, provenance, carrier, token-scoping,
durable-fence, or application-bundle Apply controls.

## Finding-to-remediation evidence

1. Migration provenance now requires policy schema `2.1.0`, while resolver provenance and producer
   manifests retain their separate `2.0.0` schemas. A current v2.1 release with distinct release
   and producer commits passes the real protected producer -> migration wrapper -> typed Preflight
   path; a stale v2.0 policy rejects.
2. The manifest-bound `Husaynia.Database.Migrations.dll` resolves only from the validated C6
   release-artifact root. It is not added to the protected execution closure. The real enabled
   Preflight fixture proves the DLL exists only in C6 and the protected producer/wrapper completes
   through injected token/SQL seams.
3. SqlClient selection copies the exact
   `runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll` ZIP entry. Every runtime assembly now
   binds package key, dependency asset path, actual ZIP source path, RID, hash, length, assembly
   version, file version, and full managed identity. Validator and collectible ALC reject root,
   Windows, alias, duplicate, path, version, and full-identity substitutions.
4. `T21_MIGRATION_STAGE_LEASE_STATE_PATH` is a local seam only when
   `EnableLocalTestSeams` is explicit. Missing opt-in and `GITHUB_ACTIONS=true` reject before
   provider, executor, or state-file invocation; normal mode retains the durable SQL adapter.
5. The local Azure CLI seam returns a bounded process result. Timeout and nonzero exit are each
   exercised exactly once with stable `T21_AZURE_SQL_TOKEN_UNAVAILABLE`, no retry, and zero stdout,
   stderr, token, environment, or file leakage.

## Final focused commands

All commands ran from `C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`.

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
  42/42
pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
  38/38
pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
  34/34
pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1
  16/16
pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1
  8/8
pwsh -NoProfile -File .\eng\test\Test-T21R10EnabledPathRegressions.ps1
  12/12
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
  17/17
pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1
  13/13
pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
  15/15
pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1
  14/14
pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1
  15/15; old mutation=0; current mutation=1; failed-executor marker=1
pwsh -NoProfile -File .\eng\test\Test-MigrationSqlClientExecution.ps1
  32/32; tokenOccurrences=0; seamActionsCalls=0
pwsh -NoProfile -File .\eng\test\Test-StageOperationHttpClient.ps1
  7/7
```

Focused total: **263 passed, 0 failed**.

```text
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path -ContractOnly
  13/13, failed=0, blocked=0
pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location).Path
  13/13, failed=0, blocked=0
pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1 -RepositoryRoot (Get-Location).Path
  2/2
```

## Final cached/static evidence

```text
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN create=0 update=0 delete=0 replace=0
Standalone static infrastructure policy=26/26
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1228579 findings=0
POLICY-HASHES PASS
RUNTIME-INSTALL-DOWNLOAD-SCAN files=3 commandHits=0
```

No install, package restore, authentication, workflow dispatch, live endpoint, database, cloud
resource, secret mutation, deployment, success receipt, actual C6 build, commit, push, or history
mutation occurred. Synthetic archives and fake process/SQL results were confined to `%TEMP%`.
The official locked NuGet audit was not rerun because it performs prohibited package restore; its
retained eleven fail-closed `NU1900` failures remain unsuppressed and unnormalized.

Required operation counters:

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

R2-GT, R2-GS, R2-GR, and R2-J remain **PENDING RERUN**. This is developer
self-verification only and does not approve any independent gate.

---

# T21-R10-R2 final remediation and independent gates — 2026-08-27

The final review findings were corrected without changing `reworkCount=10`:

- the trusted extraction step now applies `IsReadOnly` only to files and uses `chmod -R a-w` on
  Linux, avoiding a directory-property failure before OIDC;
- Preflight evidence binds the protected execution bundle path/SHA, while the application
  migration DLL remains an independently named Apply-only payload;
- current policy `applicationBundle.sha256=null` with no DLL passes the real protected
  producer/wrapper Preflight path; every non-Preflight mode rejects before seams;
- the bounded Azure CLI process helper kills the process tree and waits for termination; actual
  local `pwsh` timeout and nonzero processes are exercised with stable redacted errors.

Final local evidence:

```text
Focused suites: 13 files, 266/266 assertions, 0 failed
Pipeline definitions: 42/42
Protected bundle: 38/38
Migration SqlClient: 35/35
Both T21 harness modes: 13/13 each
Exit-code tests: 2/2
ACTIONLINT: PASS, 11 workflows
Bicep: 19/19 + 31/31 + 12/12
STATIC_PLAN: create=0 update=0 delete=0 replace=0
SECRET-SCAN: 85 files, 0 findings
Runtime prohibited-command scan: 5 files, 0 executable hits
```

Independent gate evidence:

- Test gate: **PASS** after correcting the test agent's own supplemental command construction;
  focused aggregate confirmed as `266`, cached actionlint/Bicep passed, and executable runtime
  command hits were `0`.
- Security gate: **PASS**, no Critical or High findings.
- Code-review gate: **APPROVED** after protected/application bundle identities and workflow
  read-only handling were separated.

Operation counters remained zero for authentication, dispatch, deployment, receipts, database or
cloud mutation, installs, restores, actual C6 builds, commits, pushes, and history mutation. The
official locked audit retains eleven unsuppressed fail-closed `NU1900` connectivity failures.

Independent final judgment: **APPROVED**. The specialized judge dispatcher returned an OS path
error, so a separate general-purpose agent was used solely as the independent judge; no
implementation agent self-approved. It reproduced the `266/266` focused result and `13/13`
harness, checked representative implementation paths, confirmed security PASS and code review
APPROVED, and verified `reworkCount=10`. Residual risks are the intentionally unperformed actual
C6 and live hosted Azure/SQL/OIDC execution plus the retained fail-closed NU1900 condition.

---

# T21-R10-R3 terminating lease-guard remediation self-validation — 2026-08-27

Status: **PASS for local remediation; all independent R3 gates remain pending**

## Implementation

- Replaced all 13 transactional lease-operation `RAISERROR` guards with semicolon-safe terminating
  `THROW` statements using unique stable user error numbers `51021` through `51033`.
- Preserved existing error text and all success-path SQL.
- Added static coverage that requires every exact numbered guard, forbids `RAISERROR`, requires
  unique numbers, and proves each `THROW` precedes its subsequent mutation/`COMMIT` boundary.
- Added a local-model stale release regression proving the current holder, attempt, fence token,
  authorization hash, and unreleased state are preserved.
- Refreshed the canonical `Migration.Common.ps1` policy hash to
  `96f012509751c87235a612a044968152feec539a2c001dedcb9b3222e7284617`.

## Affected checks

```text
Test-MigrationStageLeaseFence.ps1       18/18
  oldMutationMarkers=0 currentMutationMarkers=1 failureMutationMarkers=1
  staleReleasePreserved=1
Test-MigrationSqlClientExecution.ps1    35/35
  tokenOccurrences=0 seamActionsCalls=0 sqlCalls=2
Test-ProtectedExecutionBundle.ps1       38/38
Test-PipelineDefinitions.ps1            42/42
Affected total                         133/133
```

`sqlCalls=2` is the typed local fake-executor seam count; live database calls/mutations remained
zero.

## Full focused R10 matrix

```text
Test-PipelineDefinitions.ps1                 42/42
Test-ProtectedExecutionBundle.ps1            38/38
Test-GitHubArtifactProvenance.ps1            34/34
Test-CtoAuthorizationProvenance.ps1          16/16
Test-PreflightPolicyBoundary.ps1              8/8
Test-T21R10EnabledPathRegressions.ps1        12/12
Test-StageOperationInputProducer.ps1         17/17
Test-StageOperationInputBundle.ps1           13/13
Test-MigrationAuthorizationProvenance.ps1    15/15
Test-TrustedStageOperationsSemantics.ps1     14/14
Test-MigrationStageLeaseFence.ps1            18/18
Test-MigrationSqlClientExecution.ps1         35/35
Test-StageOperationHttpClient.ps1             7/7
Focused total                               269/269
```

## Harness, cached, parse, and scan evidence

```text
Invoke-T21Validation.ps1 -ContractOnly       13/13, failed=0, blocked=0
Invoke-T21Validation.ps1                     13/13, failed=0, blocked=0
Test-T21ValidationExitCodes.ps1               2/2
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN create=0 update=0 delete=0 replace=0 for every supplied stage
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1245813 findings=0
POLICY-HASH migrationCommon match=True
```

An initial local aggregate wrapper exited `1` only because it expected `total=` in the HTTP
suite's summary; all underlying suites had passed, and the corrected wrapper exited `0` with
269/269. An initial standalone secret-scan invocation also passed its array as one command-line
argument and exited `1` before scanning; the corrected direct invocation exited `0` with the
results above. Neither was a product/test assertion failure.

The locked NuGet audit was not rerun because package restore is prohibited in this remediation.
Its retained fail-closed `NU1900` condition was not suppressed, waived, or normalized.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- commit/push/history operations=`0`
- actual C6 builds=`0`

`reworkCount` remains `10`. R3-GT, R3-GS, R3-GR, and R3-J remain **PENDING**. This is local
self-validation evidence only and does not approve any independent gate.

---

# T21-R10-R4 supported OIDC environment-claim remediation — 2026-08-27

Status: **PASS for local remediation; all independent R4 gates remain pending**

## Implementation and causal contract

- `Test-ExternalTrustMarker.ps1` no longer requests the nonexistent standalone `context` claim.
  It requires `environment`, assigns that exact value to `$context`, compares it to the selected
  caller-matrix `context`, and constructs/compares the same immutable custom `sub` as before.
- Exact issuer, audience, repository, immutable repository/owner IDs, protected ref, caller
  workflow ref/SHA, reusable workflow ref/SHA, and external subject-marker checks remain.
- Ten marker checks pass: the static exact supported-claim/environment-derivation contract; a
  valid JWT payload containing environment and no context; missing environment; wrong environment;
  wrong rendered subject; wrong external subject marker; wrong caller; wrong reusable ref; wrong
  reusable SHA; and malformed caller SHA.
- ADR-019 records the read-only discovery source
  `https://token.actions.githubusercontent.com/.well-known/openid-configuration`; no runtime test
  or validation command used network discovery.
- Protected policy closure passes with 38 reviewed files. The marker's canonical SHA-256 is
  `1c5d25af39bb837c8e170c98d487436804d677c94202be487f40440320d2a93e`.

## Full focused R10 matrix

```text
Test-PipelineDefinitions.ps1                 42/42
Test-ProtectedExecutionBundle.ps1            48/48
Test-GitHubArtifactProvenance.ps1            34/34
Test-CtoAuthorizationProvenance.ps1          16/16
Test-PreflightPolicyBoundary.ps1              8/8
Test-T21R10EnabledPathRegressions.ps1        12/12
Test-StageOperationInputProducer.ps1         17/17
Test-StageOperationInputBundle.ps1           13/13
Test-MigrationAuthorizationProvenance.ps1    15/15
Test-TrustedStageOperationsSemantics.ps1     14/14
Test-MigrationStageLeaseFence.ps1            18/18
Test-MigrationSqlClientExecution.ps1         35/35
Test-StageOperationHttpClient.ps1             7/7
Focused total                               279/279
```

The fence suite reported `oldMutationMarkers=0`, `currentMutationMarkers=1`,
`failureMutationMarkers=1`, and `staleReleasePreserved=1`. The SQL client suite reported
`tokenOccurrences=0`, `seamActionsCalls=0`, and `sqlCalls=2`; those two calls are the typed local
fake-executor seam, not live database calls.

## Harness, cached, parse, and scan evidence

```text
Invoke-T21Validation.ps1 -ContractOnly       13/13, failed=0, blocked=0
Invoke-T21Validation.ps1                     13/13, failed=0, blocked=0
Test-T21ValidationExitCodes.ps1               2/2
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN create=0 update=0 delete=0 replace=0 for every supplied stage
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1255585 findings=0
POLICY-HASHES status=PASS reviewed=38 markerMatch=True
```

One first-form standalone secret-scan command passed `pipelines,eng,.config` as one command-line
path and exited `1` before scanning. The corrected direct array-bound invocation produced the
passing 85-file result above. This was an invocation error, not a product/test assertion failure.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- live database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- actual C6 builds=`0`
- commits/pushes/history operations=`0`

The retained fail-closed `NU1900` audit condition was not rerun because package restore is
prohibited. `reworkCount` remains `10`. R4-GT, R4-GS, R4-GR, and R4-J are **PENDING**; this is
developer self-validation only.

---

# T21-R10-R5 exact external subject-set remediation — 2026-08-27

Status: **PASS for local remediation; all independent R5 gates remain pending**

## Implementation and causal contract

- `Test-ExternalTrustMarker.ps1` renders exactly three approved subjects from the three
  caller-matrix rows using the current immutable owner/repository IDs, each row's exact
  environment context and caller workflow ref, and the exact `ExpectedWorkflowRef`.
- The external marker is compared as an exact ordinal set. The exact set passes; current+junk,
  missing approved, extra, duplicate, case-changed, wrong reusable, wrong caller, and wrong
  environment sets fail.
- Current-token caller-row selection and the supported `environment` claim, exact custom `sub`,
  issuer, audience, repository, immutable IDs, protected ref, caller workflow ref/SHA, and reusable
  workflow ref/SHA checks remain.
- Protected closure passes with 38 reviewed files. Marker canonical SHA-256:
  `c7da97819c2a671d5350147423cec195ce54e1591078c76cd7db91243ff55963`.

## Full focused R10 matrix

```text
Test-PipelineDefinitions.ps1                 42/42
Test-ProtectedExecutionBundle.ps1            56/56
Test-GitHubArtifactProvenance.ps1            34/34
Test-CtoAuthorizationProvenance.ps1          16/16
Test-PreflightPolicyBoundary.ps1              8/8
Test-T21R10EnabledPathRegressions.ps1        12/12
Test-StageOperationInputProducer.ps1         17/17
Test-StageOperationInputBundle.ps1           13/13
Test-MigrationAuthorizationProvenance.ps1    15/15
Test-TrustedStageOperationsSemantics.ps1     14/14
Test-MigrationStageLeaseFence.ps1            18/18
Test-MigrationSqlClientExecution.ps1         35/35
Test-StageOperationHttpClient.ps1             7/7
Focused total                               287/287
```

The fence suite reported `oldMutationMarkers=0`, `currentMutationMarkers=1`,
`failureMutationMarkers=1`, and `staleReleasePreserved=1`. The SQL client suite reported
`tokenOccurrences=0`, `seamActionsCalls=0`, and `sqlCalls=2`; these are typed local fake-executor
calls, not live database calls.

## Harness, cached, parse, scan, and hash evidence

```text
Invoke-T21Validation.ps1 -ContractOnly       13/13, failed=0, blocked=0
Invoke-T21Validation.ps1                     13/13, failed=0, blocked=0
Test-T21ValidationExitCodes.ps1               2/2
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
STATIC_PLAN create=0 update=0 delete=0 replace=0 for every supplied stage
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1262566 findings=0
POLICY-HASHES status=PASS reviewed=38 markerMatch=True
```

Two first-form secret-scan invocations failed argument binding before scanning; the corrected
direct array-bound invocation produced the passing result above. An initial parser-wrapper command
also had a shell interpolation error before parsing; the corrected full parser runs passed. No
product or test assertion failed.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- live database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- actual C6 builds=`0`
- commits/pushes/history operations=`0`

The fail-closed `NU1900` audit condition was not rerun because package restore is prohibited.
`reworkCount` remains `10`. R5-GT, R5-GS, R5-GR, and R5-J are **PENDING**; this is developer
self-validation only.

---

# T21-R10-R6 step-scoped provenance token remediation — 2026-08-27

Status: **PASS for local remediation; all independent R6 gates remain pending**

## Implementation and causal contract

- The protected pre-OIDC provenance-resolution step now has the exact step-scoped
  `GITHUB_TOKEN: ${{ github.token }}` mapping. The preceding C6 API/download step retains its own
  required mapping; job scope and later OIDC, Azure login, producer, upload, and attestation steps
  have none, and no protected run body references/logs `GITHUB_TOKEN`.
- The structural regression requires exactly those two GitHub API consumer steps to carry the
  exact mapping and rejects any unrelated/later protected-step or job/run-body exposure.
- Direct resolver execution with its token environment variable absent fails with
  `GitHub Actions API token is absent before OIDC.` before the request seam is called. A mapped
  nonsecret token with local API/download/attestation fixtures completes verified provenance.
- No protected-bundle source changed. No protected hash was refreshed; canonical verification
  passed 38/38 with `changedIncluded=0`, and the external marker hash remains
  `c7da97819c2a671d5350147423cec195ce54e1591078c76cd7db91243ff55963`.

## Full focused R10 matrix

```text
Test-PipelineDefinitions.ps1                 43/43
Test-ProtectedExecutionBundle.ps1            56/56
Test-GitHubArtifactProvenance.ps1            36/36
Test-CtoAuthorizationProvenance.ps1          16/16
Test-PreflightPolicyBoundary.ps1              8/8
Test-T21R10EnabledPathRegressions.ps1        12/12
Test-StageOperationInputProducer.ps1         17/17
Test-StageOperationInputBundle.ps1           13/13
Test-MigrationAuthorizationProvenance.ps1    15/15
Test-TrustedStageOperationsSemantics.ps1     14/14
Test-MigrationStageLeaseFence.ps1            18/18
Test-MigrationSqlClientExecution.ps1         35/35
Test-StageOperationHttpClient.ps1             7/7
Focused total                               290/290
```

The fence suite reported `oldMutationMarkers=0`, `currentMutationMarkers=1`,
`failureMutationMarkers=1`, and `staleReleasePreserved=1`. The SQL client suite reported
`tokenOccurrences=0`, `seamActionsCalls=0`, and `sqlCalls=2`; these are typed local fake-executor
calls, not live database calls.

## Harness, cached, parse, scan, and hash evidence

```text
Invoke-T21Validation.ps1 -ContractOnly       13/13, failed=0, blocked=0
Invoke-T21Validation.ps1                     13/13, failed=0, blocked=0
Test-T21ValidationExitCodes.ps1               2/2
ACTIONLINT status=PASS version=1.7.12 workflows=11
Bicep build/lint=19/19 static-policy=31/31 negative-mutations=12/12
Standalone static Bicep policy=26/26
STATIC_PLAN create=0 update=0 delete=0 replace=0 for every supplied stage
PS-PARSE files=68 errors=0
JSON-COMPATIBLE-PARSE files=15 errors=0
SECRET-SCAN status=PASS scanned=85 bytes=1270642 findings=0
POLICY-HASHES status=PASS reviewed=38 mismatches=0 changedIncluded=0 markerMatch=True
```

An initial supplemental raw-byte hash command reported mismatches because protected policy hashes
are over canonical LF text. The corrected canonical 38-file check passed. During regression
development, a first mapped-request run reused an intentionally mutated fixture archive; the final
isolated fixture passed. Neither affected a final product/test assertion.

## Required operation counters

- authentication=`0`
- workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- live database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- actual C6 builds=`0`
- commits/pushes/history operations=`0`

The fail-closed `NU1900` audit condition was not rerun because package restore is prohibited.
`reworkCount` remains `10`. R6-GT, R6-GS, R6-GR, and R6-J are **PENDING**; this is developer
self-validation only.

---

## T21-R10 post-judgment validation-compliance finding — 2026-08-27

STATUS: **BLOCKED**

A final workspace integrity inspection found evidence that a delegated validation exceeded the
explicit no-restore/no-out-of-scope-write constraint. The validator reported a tool/locked restore
and 468 compiled tests. Files with write times after the CTO-supplied mission start include
generated `infra/*.json` plus `src/**/bin|obj`, `tests/**/bin|obj`, and
`tools/Husaynia.Migration/**/bin|obj` artifacts.

The Git top-level is `Dreamer`, but the entire `HusayniaSite` target tree was already untracked at
mission start. Therefore normal `git diff` cannot prove or safely reverse those generated writes,
and deleting them could destroy pre-existing user artifacts. No automatic cleanup was performed.

This does not change the independently approved R10 source review or zero authentication,
workflow-dispatch, deployment, database, or cloud-resource mutation evidence. It does invalidate a
claim of `package restores=0` and prevents the required proof of zero out-of-scope modifications.
Mission completion remains blocked in addition to the existing eleven fail-closed `NU1900` errors
and absent external workflow/OIDC/FIC/environment installation.

### Final independent repository test gate

Before the compliance finding above, an independent R6 test agent executed the current repository
matrix without editing source and reported: focused `290/290`; ContractOnly `13/13`; safe full
`13/13`; exit `2/2`; actionlint `11`; Bicep `62/62`; PowerShell `68/0`; JSON `15/0`; secret scan
`85/0`. Direct VP re-execution of the final candidate reported pipeline definitions `43/43`,
ContractOnly `13/13`, and exit behavior `2/2`.

Repository behavior is **PASS**. The overall test/compliance gate remains **BLOCKED** because the
official audit is nonzero and the separate delegated validation violated the no-restore and
allowed-write constraints.

---

## T21-R10-R7 checked-in regression closure — 2026-08-27

STATUS: **IMPLEMENTATION SELF-VALIDATION PASS; INDEPENDENT GATES PENDING**

R7 added the missing checked-in production-shaped contracts without changing workflow runtime
permissions or enabling any stage:

- exact real installed `gh api --help` parser contract plus the existing arbitrary-binary carrier
  integrity/failure fixtures;
- exact execution of all six checkout root-validation bodies in temporary
  `Dreamer/HusayniaSite` layouts, including wrong-parent rejection;
- one central five-action repository/tag/commit registry and static/full-SHA workflow scan;
- optional authoritative public read-only tag/peel resolution for all five external actions;
- an exact coordinator lifecycle state machine covering successful and failed `R -> P/I`,
  separately completed protected `M`, `M -> S` for Development/Staging, checksum continuity,
  temporal completed-success selection, no cycle, no automatic Production, and the forbidden
  Development-receipt/Staging boundary;
- the existing HTTP client suite restored to both T21 harness modes;
- a static assertion that the official PR audit retains locked mode, `NuGetAudit=true`,
  `NuGetAuditMode=all`, nonzero failure, and no official suppression flag.

### Commands and results

```text
Test-PipelineDefinitions.ps1
  SUMMARY pipeline-definitions total=45 passed=45 failed=0 workflows=11 runBodies=33

Test-GitHubActionPins.ps1
  SUMMARY github-action-pins total=3 passed=3 failed=0
  workflowUses=25 externalUses=19 internalReusableUses=6 upstream=False

Test-GitHubActionPins.ps1 -VerifyUpstream
  SUMMARY github-action-pins total=9 passed=9 failed=0
  workflowUses=25 externalUses=19 internalReusableUses=6 upstream=True

Test-ProtectedExecutionBundle.ps1
  SUMMARY protected-bundle total=57 passed=57 failed=0 binaryCases=4 ghParserCases=1

Test-T21LifecycleStateMachine.ps1
  SUMMARY t21-lifecycle-state-machine total=12 passed=12 failed=0
  simulatedDispatches=7 actualDispatches=0

Invoke-T21Validation.ps1 -ContractOnly
  SUMMARY total=16 passed=16 failed=0 blocked=0

Invoke-T21Validation.ps1
  SUMMARY total=16 passed=16 failed=0 blocked=0

Test-T21ValidationExitCodes.ps1
  SUMMARY t21-validation-exit-codes total=2 passed=2 failed=0

Invoke-WorkflowSchemaValidation.ps1 (cached actionlint 1.7.12)
  ACTIONLINT status=PASS version=1.7.12 workflows=11

Invoke-BicepValidation.ps1 (cached Bicep 0.46.1)
  build/lint=19/19
  static policy=31/31
  negative mutations=12/12
  suppliedStageCreate=0 suppliedStageUpdate=0 suppliedStageDelete=0 suppliedStageReplace=0

Owned parsers
  PowerShell files=70 errors=0
  JSON-compatible pipeline/config files=14 errors=0

Invoke-SecretScan.ps1
  SECRET-SCAN status=PASS scanned=87 bytes=1312167 findings=0
```

The complete focused repository matrix is **308/308** assertions. Both harness modes are
**16/16**; exit behavior is **2/2**; cached actionlint covers **11** workflows; cached Bicep/static
coverage is **62/62**; parsers are **70/0** PowerShell and **14/0** JSON; the owned secret scan is
**87 files / 0 findings**.

### Authoritative action resolution

Public read-only `git ls-remote <repository> refs/tags/<tag>
refs/tags/<tag>^{}` returned:

```text
actions/checkout v4.2.2
  11bd71901bbe5b1630ceea73d27597364c9af683
actions/setup-dotnet v4.3.1
  67a3573c9a986a3f9c594539f4ab511d57bb3ce9
actions/upload-artifact v4.6.2
  ea165f8d65b6e75b540449e92b4886f43607fa02
Azure/login v2.3.1 annotated tag object
  2035af27c2cea8ff426397d10e3edb28906b51df
Azure/login v2.3.1 peeled executable commit
  7184910d9eb2b1c5e48f7073824a90609bb9b6d6
actions/attest-build-provenance v2.4.0
  e8998f949152b193b063cb0ec769d69d929409be
```

### Counters and blockers

R7 execution counters:

- authentication=`0`
- actual workflow dispatches=`0` (six local simulated dispatch records only)
- deployments=`0`
- success receipts=`0`
- live database calls/mutations=`0`
- cloud resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- actual C6 builds=`0`
- commits/pushes/history operations=`0`

The official locked NuGet audit was not rerun because doing so requires the prohibited restore.
Its retained authoritative result remains eleven unsuppressed fail-closed `NU1900` errors. The
earlier delegated restore and 131 generated out-of-scope writes remain an irreversible mission
compliance blocker in this untracked workspace; R7 neither deleted nor rewrote that evidence.
Independent R7 test, security, code-review, and judgment gates are still required.

### R7 independent review and remediation

The first independent review raised three items. Raw source inspection disproved the apparent
runtime bearer defect: output rendering had redacted the real `Bearer $token` source and the
existing mapped-token fixture already completed four authenticated requests. Two actual defects
were remediated:

1. `Assert-ProvenanceTest` no longer contains a name-specific override; the final runtime-header
   test now succeeds only from its captured request predicate.
2. Coordinator correlation discovery now uses `gh api --paginate --slurp`, validates the native
   result, combines all pages, and fails closed. The lifecycle fixture seeds 100 unrelated runs
   before an existing correlated Preflight run and proves only PreparedInputs is newly dispatched.

Post-remediation self-validation:

```text
pipeline definitions: 45/45
enabled path: 12/12
lifecycle state machine: 12/12, simulatedDispatches=7, actualDispatches=0
GitHub provenance: 36/36
ContractOnly: 16/16
safe full: 16/16
exit behavior: 2/2
actionlint: 11 workflows
PowerShell parse: 70/0
JSON parse: 14/0
secret scan: 87 files / 0 findings
```

Independent rerun:

```text
STATUS PASS
reported checks=357 passed=357 failed=0
pipeline=45/45
enabled path=12/12
lifecycle=12/12
provenance=36/36
ContractOnly=16/16
safe full=16/16
exit=2/2
actual dispatches/auth/deploy/database/resources/installs/restores=0
repository writes=0
```

Independent security rerun: **PASS**, Critical=`0`, High=`0`.

Independent code rereview: **APPROVED**. It verified the actual runtime bearer, causal header
tests, paginated fail-closed discovery, page-two idempotent reuse, and preservation of all four R9
remediations.

Independent final judgment: **BLOCKED**, not rejected for source correctness. Remaining blockers
are the eleven unsuppressed fail-closed `NU1900` errors, absent separately reviewed external
installation/provisioning, and the earlier delegated restore plus generated-write compliance
violation. No self-approval is claimed.

---

# T21-R11 bounded coordinator authorization fix — local evidence — 2026-08-28

Status: **DONE for bounded implementation and local validation; independent gates pending**

The completed-run coordinator now discovers and verifies the unique canonical nonproduction
migration-authorization artifact through the existing Actions API/digest/manifest/attestation
resolver. It derives no authorization binding from the predecessor title. Before dispatch, it
resolves exact release, preflight, and prepared-input provenance and invokes the existing
StageOperations semantic validator. The validator must reach its final immutable deployment
fence, which proves canonical `AUTHORIZE` + `Apply`, unexpired authorization, and exact
release/preflight/prepared-input/stage/caller/reusable bindings. DENY audit publication remains
unchanged.

## Executed validation

All commands ran from
`C:\Users\syedhu\source\repos\Dreamer\HusayniaSite`; every listed command exited `0`.

```text
pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1
  pipeline-definitions total=46 passed=46 failed=0 workflows=11 runBodies=33

pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1
  migration-authorization-provenance total=17 passed=17 failed=0

pwsh -NoProfile -File .\eng\test\Test-T21LifecycleStateMachine.ps1
  t21-lifecycle-state-machine total=22 passed=22 failed=0
  simulatedDispatches=7 actualDispatches=0

pwsh -NoProfile -File .\eng\test\Test-T21R10EnabledPathRegressions.ps1
  t21-r10-enabled-path total=12 passed=12 failed=0

pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1
  github-provenance total=37 passed=37 failed=0

pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1
  protected-bundle total=57 passed=57 failed=0 binaryCases=4 ghParserCases=1

pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1
  stage-operation-input-producer total=17 passed=17 failed=0

pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -ContractOnly
  total=16 passed=16 failed=0 blocked=0
  owned secret scan scanned=88 bytes=1343972 findings=0

pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1
  t21-validation-exit-codes total=2 passed=2 failed=0

pwsh -NoProfile -File .\eng\ci\Invoke-WorkflowSchemaValidation.ps1
  -ActionlintPath <pre-existing cached actionlint 1.7.12>
  ACTIONLINT status=PASS version=1.7.12 workflows=11

PowerShell AST parse over eng/**/*.ps1
  files=71 errors=0

JSON-compatible parse over pipelines/**/*.json, pipelines/github/*.yml, .config/*.json
  files=15 errors=0

eng/security/Invoke-SecretScan.ps1 -ScanPaths pipelines,eng,.config
  scanned=88 bytes=1343972 findings=0
```

The lifecycle suite executed the checked-in coordinator with local resolver/validator/API
fixtures and proved:

- canonical APPLY: exactly `1` simulated StageOperations dispatch;
- DENY: `0`;
- malformed: `0`;
- missing: `0`;
- duplicate: `0`;
- replay: `0` new dispatches;
- wrong context: `0`;
- producer failure: `0`;
- resolver/API failure: `0`;
- Production scope: `0`;
- `authorization-validated` occurs strictly before the dispatch event.

The aggregate `16/16` confirms the unchanged R10 graph, immutable/no-rebuild C6 contract,
disabled stages, manual-disabled Production, durable fence, forbidden deployment boundary, and
existing trust controls. The protected closure was refreshed only for the changed resolver's
canonical text hash; no actual C6 artifact was built.

## Required counters

- authentication=`0`
- actual workflow dispatches=`0`
- deployments=`0`
- success receipts=`0`
- database calls/mutations=`0`
- cloud/resource/secret mutations=`0`
- installs=`0`
- package restores=`0`
- actual C6 builds=`0`
- commits/push/history operations=`0`

The official NuGet audit was not run because this bounded task forbids restore. Its retained
eleven fail-closed `NU1900` failures remain unchanged. External installation/provisioning and the
historical generated-write compliance blocker also remain unchanged.

Independent R11 test, security, code-review, and judgment gates are **PENDING**. This section is
implementation/self-validation evidence only and does not self-approve the mission.
