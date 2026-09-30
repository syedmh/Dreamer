# T21-R11 pipeline authoring

`pipelines/github/*.yml` contains exactly eleven JSON-compatible GitHub Actions authoring
definitions. They are not installed under `.github/workflows`.

## Completed-run lifecycle

`release-build-and-nonproduction.yml` is release-only and is the sole C6 builder (`R`).
Every later phase is a separate top-level run:

```text
R completed/success
  -> P(stage) completed/success          trusted preflight
  -> I(stage) completed/success          inert prepared inputs
  -> C(Production) completed/success     CTO authorization only
  -> M(stage) completed/success          migration authorization
  -> S(stage) separately dispatched      stops at the forbidden boundary
```

Development and Staging use `R -> P/I -> M -> S`, with the protected migration authorization
`M` remaining a separate manual approval point inside the otherwise automatic nonproduction
chain. Production remains separate, manual, disabled, and uses `R -> P/I -> C -> M -> S`.
`P` and `I` are distinct. Every selector must identify a completed-success run that finished
before the consumer started; current, future, in-progress, failed, cancelled, missing,
cross-stage, and cross-release selectors fail closed. Every phase binds the same release run,
application SHA, protected-bundle SHA, release-manifest SHA, and release commit. No downstream
phase rebuilds C6.

`automatic-nonproduction-orchestration.yml` is the only automatic coordinator. Its permissions are
exactly `actions:write`, `attestations:read`, and `contents:read`. It has no environment, secret,
OIDC, cloud, artifact-write, or deployment authority; it never dispatches Production. It accepts
only exact completed-success protected-main predecessors, uses bounded duplicate-fail-closed
canonical input identity, and is disabled by the checked-in `deploymentEnabled=false` policy and
protected enable variables. A producer title or display title is never deduplication or completion
authority. Each successful nonproduction wrapper run emits one immutable completion manifest whose
normalized dispatch-input hash, exact workflow/event/ref/head/run/repository identity, run attempt,
and selected producer graph are bound by the artifact digest and protected-workflow attestation.
The coordinator reuses only one API-verified exact `mode/stage/R/P/I/M/C/correlation` match; missing,
malformed, stale, replayed, or mismatched records are ignored, while multiple exact matches fail
closed. Before any nonproduction `StageOperations` dispatch, it discovers the unique
canonical migration-authorization artifact without using the predecessor title, verifies its
API run, immutable digest, producer manifest, and role-pinned attestation, resolves the bound
release/preflight/prepared-input artifacts, and runs the existing semantic authorization
validator. Only canonical `AUTHORIZE` + `Apply` with exact stage, caller, reusable, release,
preflight, and prepared-input bindings can dispatch; DENY or any resolution/validation failure
dispatches nothing. The Staging branch additionally stops at
`T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` because no canonical Development receipt producer
exists. Correlation discovery uses fail-closed `gh api --paginate --slurp`; a checked-in
fixture proves title-only history cannot suppress dispatch and a canonical older page match is
reused rather than duplicated.

## Producer and authorization contracts

The nonproduction and Production wrappers execute exactly one mode per run:
`Preflight`, `PreparedInputs`, or `StageOperations`. Unused selectors must be empty.
StageOperations receives opaque completed `P`, `I`, `M`, and Production `C` run IDs; it never uses
its own run ID.

Prepared-input, CTO, and migration records use the internal v2.1 contract:

- prepared inputs contain nonauthorizing reports plus a Production authorization request;
- `source-change-record.json` is absent from prepared inputs;
- CTO authorization resolves completed `R/P/I`, checks and owns `source-change-record.json`, then
  emits the CTO record;
- migration authorization resolves completed `R/P/I` and Production `C`;
- StageOperations independently resolves and cross-binds exact `R/P/I/C/M` provenance.

All GitHub artifacts are resolved through API run identity, immutable artifact digest, canonical
content manifest, and role-pinned attestation. Producer run commit identity is separate from the
immutable release commit.

## Protected read-only report token

Enabled prepared-input report production runs in the exact protected stage environment:
`Husaynia-Development-Operations`, `Husaynia-Staging-Operations`, or `Husaynia-Production`.
Production CTO change-record production remains in the dedicated
`Husaynia-CTO-Authorization` environment. Each environment must define the protected
`T21_STAGE_READONLY_PROBE_TOKEN` secret before its corresponding authored path can be enabled.

The secret is mapped only to the step that calls `New-StageOperationReport.ps1`. It is used only as
the bearer credential for the pinned read-only service GET, is never passed to artifact writers,
and must not be logged or included in report, bundle, manifest, attestation, or receipt content.
No job-level secret mapping or permission expansion is permitted.

## Checkout and binary trust

Every checkout job uses:

```text
HUSAYNIA_REPOSITORY_ROOT=${{ github.workspace }}/HusayniaSite
defaults.run.shell=pwsh
defaults.run.working-directory=HusayniaSite
```

Its first PowerShell body verifies the exact `Dreamer/HusayniaSite` child and requires
`HusayniaSite.sln`, `eng`, and `pipelines`. Jobs without checkout use `pwsh` only.
Protected execution continues exclusively from the validated read-only
`T21_TRUSTED_BUNDLE_ROOT`; no repository content executes after login.

Enabled Preflight preserves this exact order: validate caller and completed provenance, validate
and make the immutable bundle read-only, enforce `deploymentEnabled` from the bundle policy before
requesting a token, validate the exact OIDC claims, run `azure/login`, recheck the immutable
extraction, then execute the bundle-owned producer with the step-scoped
`T21_MIGRATION_IDENTITY_MODE=readonly`. StageOperations still terminates at
`T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` before OIDC/login. No checked-out repository script
runs after login.

The protected bundle manifest v1.1 also binds the exact managed
`Microsoft.Data.SqlClient` 6.1.1 Unix dependency closure to `app/Husaynia.Web.zip`. Enabled
Preflight validates and loads that read-only closure in-process, obtains the Azure SQL token
directly from the already-authenticated Azure CLI, and executes the fixed typed query without
installing or downloading a database client. Durable lease SQL uses the same non-retrying adapter.

The outer C6 carrier uses PowerShell 7.4+ binary-safe native stdout redirection:

```powershell
& gh api ('repos/{0}/actions/artifacts/{1}/zip' -f $env:GITHUB_REPOSITORY, $artifact.id) > $carrier
$downloadExit = $LASTEXITCODE
```

The carrier path must be absent first. CLI failure, absent/empty/oversize output, or API digest
mismatch is deleted/rejected before ZIP processing or trusted side effects. R9 safe-name,
alias/type/count/size/decompression controls, fixed protected-bundle path, and streamed bundle hash
remain.

Frozen action commits:

- `actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683`
- `actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9`
- `actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02`
- `azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6`
- `actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be`

`pipelines/config/promotion-policy.json` records each repository, reviewed tag, exact commit, and
annotated-tag object where applicable. `eng/ci/Test-GitHubActionPins.ps1 -VerifyUpstream` performs
read-only `git ls-remote` tag/peel resolution. There are exactly four attestation producers.

## Disabled installation boundary

All stages remain `deploymentEnabled=false`; Production remains manual. The trusted StageOperations
path resolves and validates required provenance, then stops before OIDC/login/mutation at the
single stable `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary. No deployment evidence,
receipt, or deployment is produced.

Installation still requires a separate reviewed change for the authored workflows, nonzero
internal reusable-workflow pins, exact GitHub OIDC subjects, matching Entra federated credentials,
protected variables/environments/endpoints, and enablement. The all-zero reusable SHA values are
intentional installation sentinels.

## Local validation

No-install/no-restore checks:

```powershell
./eng/ci/Test-PipelineDefinitions.ps1
./eng/ci/Test-GitHubActionPins.ps1
./eng/ci/Test-GitHubActionPins.ps1 -VerifyUpstream
./eng/test/Test-T21LifecycleStateMachine.ps1
./eng/test/Invoke-T21Validation.ps1 -ContractOnly
./eng/test/Invoke-T21Validation.ps1
./eng/test/Test-T21ValidationExitCodes.ps1
```

Cached actionlint and Bicep paths must be passed explicitly to their validation wrappers. Missing
cached tooling is a blocker; the wrappers must not install it for R10 evidence. The official
locked NuGet audit remains fail-closed on the recorded `NU1900` vulnerability-service outage and
must not be waived, disabled, or success-normalized.
