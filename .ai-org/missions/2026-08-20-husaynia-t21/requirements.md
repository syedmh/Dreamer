# T21-R10 frozen requirements

Status: **PASS — requirements frozen; implementation and gates not claimed**

## Verified current-state facts

- **FACT F-01:** The actual checkout root is `Dreamer`, while the solution, pipeline scripts, and
  policy are under the `HusayniaSite` child
  (`HusayniaSite/pipelines/github/pr-validation.yml:5-8,25-27,39-44`;
  `HusayniaSite/eng/common/Release.Common.ps1:3-27`).
- **FACT F-02:** The release build step contains PowerShell syntax but declares neither `pwsh` nor
  the `HusayniaSite` working directory and passes the checkout root as `RepositoryRoot`
  (`HusayniaSite/pipelines/github/release-build-and-nonproduction.yml:6-19`).
- **FACT F-03:** The prepared-input job declares `pwsh` but not the child working directory and
  addresses checked-out scripts/policy as `$GITHUB_WORKSPACE/eng` and
  `$GITHUB_WORKSPACE/pipelines`
  (`HusayniaSite/pipelines/github/stage-operation-inputs.yml:16-27,31-52`).
- **FACT F-04:** The trusted C6 acquisition body uses unsupported
  `gh api ... --output <path>` for the binary artifact ZIP
  (`HusayniaSite/pipelines/github/trusted-protected-operations.yml:73`); its fixture currently
  implements that same unsupported option
  (`HusayniaSite/eng/test/Test-ProtectedExecutionBundle.ps1:568-585`).
- **FACT F-05:** The authored workflows and policy repeat
  `azure/login@858f4093d287a904987dfd22abd163280f939550` and
  `actions/attest-build-provenance@96b4a1ef7235a096b17240c259729fdd70c83d45`
  (`HusayniaSite/pipelines/github/trusted-protected-operations.yml:124,149`;
  `HusayniaSite/pipelines/github/stage-operation-inputs.yml:92`;
  `HusayniaSite/pipelines/github/cto-authorization-record.yml:142`;
  `HusayniaSite/pipelines/github/migration-apply-authorization.yml:204`;
  `HusayniaSite/pipelines/config/promotion-policy.json:220`).
- **FACT F-06:** Preflight and prepared-input provenance presently permits an in-progress
  top-level run (`HusayniaSite/pipelines/config/promotion-policy.json:171-193`;
  `HusayniaSite/eng/test/Test-GitHubArtifactProvenance.ps1:356-366`).
- **FACT F-07:** All four StageOperations paths currently use their own `github.run_id` as the
  preflight and prepared-input selectors, and tests require this same-run graph
  (`HusayniaSite/pipelines/github/release-build-and-nonproduction.yml:68-108`;
  `HusayniaSite/pipelines/github/operation-evidence-producer.yml:37-51`;
  `HusayniaSite/pipelines/github/production-operation-evidence.yml:41-55`;
  `HusayniaSite/eng/test/Test-StageOperationInputProducer.ps1:202-238`;
  `HusayniaSite/eng/ci/Test-PipelineDefinitions.ps1:480-525,591-615`).
- **FACT F-08:** The authoring state is deliberately disabled: the workflow triggers cannot
  satisfy the schedule guards, every stage has `deploymentEnabled=false`, Production is separate,
  and `deployment-evidence` is forbidden (`HusayniaSite/pipelines/README.md:15-43`;
  `HusayniaSite/pipelines/config/promotion-policy.json:199-216`;
  `HusayniaSite/pipelines/github/production-promotion.yml:1-19`).
- **FACT F-09:** R9 locally preserved outer-carrier hardening, immutable/no-rebuild C6,
  authoritative provenance, exact caller/producer identity, no post-login repository execution,
  durable migration fencing, and zero deployment, but did not constitute independent approval
  (`.ai-org/missions/2026-08-20-husaynia-t21/test-results.md:644-661,747-759`).
- **FACT F-10:** The official NuGet audit remains fail-closed on `NU1900`; no suppression or
  waiver is authorized (`.ai-org/missions/2026-08-20-husaynia-t21/test-results.md:724-742`).

## Assumptions and questions

- **ASSUMPTION A-01:** Exact replacement action commit SHAs are not selected by this requirements
  freeze. R10 implementation may use only pins accompanied by authoritative upstream existence
  evidence; inability to obtain that evidence is a gate failure, not permission to use a tag,
  branch, zero SHA, or unverified SHA.
- **ASSUMPTION A-02:** “Separately dispatched StageOperations” constrains observable run
  boundaries and ordering, not the GitHub event or orchestration mechanism chosen by Architecture.
- **OPEN QUESTION:** None. There is no blocking business ambiguity in the frozen CTO lifecycle.

## Requirements

- **REQUIREMENT R10-01:** R10 shall remediate exactly these four High findings: unsupported binary
  `gh api --output`; invalid `azure/login` and attestation action pins; missing `pwsh`/child-root
  awareness; and same-run/future-run provenance lifecycle. No other behavior change is authorized.
- **REQUIREMENT R10-02:** Binary artifact retrieval shall use a supported GitHub CLI contract and
  preserve response bytes exactly; `gh api --output` shall be absent.
- **REQUIREMENT R10-03:** Binary retrieval failure, missing/empty output, size-limit breach, or
  GitHub digest mismatch shall fail before extraction, environment-file writes, trusted marker or
  validator execution, OIDC, login, mutation, evidence, or receipt creation.
- **REQUIREMENT R10-04:** Every `azure/login` and `actions/attest-build-provenance` reference shall
  be a nonzero lowercase 40-hex commit proven to exist in the named upstream repository.
- **REQUIREMENT R10-05:** The verified pins shall be identical wherever the same action contract is
  asserted in workflows, immutable policy, and tests. Existing least-privilege permissions and the
  restriction to the approved producer jobs shall not be broadened.
- **REQUIREMENT R10-06:** Every workflow `run` body containing PowerShell shall execute with
  `shell: pwsh`, explicitly or through applicable job defaults.
- **REQUIREMENT R10-07:** Every checked-out Husaynia script, solution, manifest, and policy path
  shall resolve correctly when `GITHUB_WORKSPACE` is the `Dreamer` root and `HusayniaSite` is its
  child. Passing `Dreamer` as the Husaynia `RepositoryRoot` is forbidden.
- **REQUIREMENT R10-08:** Root remediation shall not redirect trusted pre/post-login execution from
  the validated read-only C6 protected-bundle root back to repository checkout content.
- **REQUIREMENT R10-09:** A release run shall complete successfully before any preflight,
  prepared-input, CTO-authorization, migration-authorization, or StageOperations run selects it.
  That release run is the sole C6 build.
- **REQUIREMENT R10-10:** Preflight and prepared-input producer artifacts shall be selected only
  from completed-success top-level runs. In-progress or failed producer runs shall be rejected.
- **REQUIREMENT R10-11:** Authorization producers shall start only after the selected release,
  preflight, and prepared inputs are completed-success. Production CTO authorization shall
  complete before Production migration authorization.
- **REQUIREMENT R10-12:** StageOperations shall run in a separate top-level run dispatched only
  after all required producer/authorization runs have completed successfully. Its current run ID
  shall not be used as any provenance selector, and future/unavailable run selectors shall fail.
- **REQUIREMENT R10-13:** Automatic Development and automatic Staging shall remain distinct
  nonproduction lifecycles composed of completed prior runs. Production shall remain a separate,
  manual, disabled lifecycle.
- **REQUIREMENT R10-14:** Every lifecycle run shall bind the same release run ID, application SHA,
  protected-bundle SHA, release-manifest SHA, and release commit. No producer, authorization, or
  StageOperations run may rebuild or substitute C6.
- **REQUIREMENT R10-15:** All R9 controls remain equal or stricter: exact caller/producer binding,
  API/digest/manifest/attestation provenance, carrier canonicalization and limits, immutable
  read-only bundle use, no post-login checkout content, disabled stages, forbidden
  `deployment-evidence`, the stable pre-OIDC forbidden boundary, Production CTO binding, and
  durable migration claim/fencing.
- **REQUIREMENT R10-16:** Official validation remains fail-closed. R10 shall not suppress `NU1900`,
  disable audit, ignore failed package sources, relax a test, accept success-shaped fixtures, or
  convert an environmental block into success.
- **REQUIREMENT R10-17:** Focused static and causal tests shall cover each finding plus lifecycle
  rejection of in-progress, failed, same-run, future-run, cross-stage, cross-release, wrong-pin,
  wrong-root, and binary-corruption cases.
- **REQUIREMENT R10-18:** R10 evidence shall report commands, exit codes, assertion counts, and the
  operation counters below. Local implementation evidence shall not be reported as an independent
  test, security, review, or judgment approval.

## Acceptance criteria

### Binary GitHub API retrieval

- **REQUIREMENT AC-BIN-01:** **Given** the exact checked-in C6 retrieval run body and a fixture ZIP
  containing NUL/CR/LF and arbitrary binary bytes, **when** the supported fake `gh api` returns
  those bytes, **then** the saved carrier is byte-identical, its SHA-256 matches the API digest,
  and the existing fixed-path carrier/bundle validation continues.
- **REQUIREMENT AC-BIN-02:** **Given** a fake `gh` that rejects unknown options, **when** the exact
  run body executes, **then** no `--output` argument is observed and the valid case succeeds.
- **REQUIREMENT AC-BIN-03:** **Given** CLI failure, no carrier, an empty carrier, oversize content,
  or a digest mismatch, **when** retrieval runs, **then** it fails with zero extraction, marker,
  validator, environment-file, OIDC, login, mutation, evidence, deployment, and receipt effects.

### Action pins

- **REQUIREMENT AC-PIN-01:** **Given** every `azure/login` and attestation `uses` value, **when**
  pin validation runs, **then** each is nonzero lowercase 40-hex and has recorded authoritative
  upstream commit-existence evidence.
- **REQUIREMENT AC-PIN-02:** **Given** workflow, policy, and test references, **when** compared,
  **then** each action uses one consistent verified SHA and neither current invalid SHA remains.
- **REQUIREMENT AC-PIN-03:** **Given** a tag, branch, zero SHA, nonexistent SHA, inconsistent SHA,
  extra attestation producer, or broadened permission, **when** contract validation runs, **then**
  validation fails closed.

### PowerShell and repository-layout awareness

- **REQUIREMENT AC-ROOT-01:** **Given** a fixture checkout shaped
  `<workspace>/Dreamer/HusayniaSite`, **when** every checked-out workflow run body is executed or
  structurally evaluated, **then** PowerShell bodies use `pwsh` and all Husaynia paths resolve
  beneath `<workspace>/Dreamer/HusayniaSite`.
- **REQUIREMENT AC-ROOT-02:** **Given** the release build body, **when** it invokes PR validation,
  **then** the explicit Husaynia repository root contains `HusayniaSite.sln` and one C6 artifact is
  selected without path fallback to the `Dreamer` root.
- **REQUIREMENT AC-ROOT-03:** **Given** a wrong/missing child root, **when** a producer starts,
  **then** it fails before artifact creation, upload, attestation, OIDC, login, or mutation; trusted
  bundle-root execution remains unchanged.

### Completed-run provenance lifecycle

- **REQUIREMENT AC-LIFE-01:** **Given** a release selector, **when** any downstream resolver uses
  it, **then** GitHub reports that run as `completed/success` and the selected C6 identity is reused
  unchanged through every later run.
- **REQUIREMENT AC-LIFE-02:** **Given** preflight or prepared-input provenance, **when** resolved,
  **then** its top-level run is `completed/success`; `in_progress`, failed, cancelled, or missing
  conclusion is rejected.
- **REQUIREMENT AC-LIFE-03:** **Given** Development or Staging, **when** StageOperations is
  dispatched, **then** the completed-success order is release -> preflight/prepared inputs ->
  migration authorization -> StageOperations, and StageOperations has a different current run ID.
- **REQUIREMENT AC-LIFE-04:** **Given** Production, **when** StageOperations is considered, **then**
  the completed-success order is release -> Production preflight/prepared inputs -> CTO
  authorization -> migration authorization -> StageOperations, while Production remains manually
  dispatched and disabled.
- **REQUIREMENT AC-LIFE-05:** **Given** any same-run selector, future/unavailable run ID,
  cross-stage artifact, cross-release digest, wrong caller/producer pair, or failed producer,
  **when** provenance is validated, **then** validation fails before the deployment-forbidden
  boundary can be passed and before OIDC/login/mutation.
- **REQUIREMENT AC-LIFE-06:** **Given** automatic Development and Staging, **when** their graph is
  inspected, **then** neither StageOperations job is embedded in the release or producer run and
  the two stage lifecycles remain distinct.
- **REQUIREMENT AC-LIFE-07:** **Given** any producer, authorization, or StageOperations run,
  **when** its steps are inspected, **then** it contains no C6 build command and consumes the same
  immutable C6 bindings selected from the completed release.

### Preservation and validation

- **REQUIREMENT AC-GATE-01:** **Given** the R10 change set, **when** R9 regression suites and static
  contracts run, **then** all named R9 controls in R10-15 remain asserted with no deleted or
  weakened negative case.
- **REQUIREMENT AC-GATE-02:** **Given** local/cached validation, **when** it completes, **then**
  workflow JSON parsing, PowerShell parsing, actionlint, focused R10 tests, R9 focused suites,
  `Test-PipelineDefinitions.ps1`, and `Invoke-T21Validation.ps1 -ContractOnly` report real counts
  and zero failures. Tools shall not be installed or restored for this validation.
- **REQUIREMENT AC-GATE-03:** **Given** official audit connectivity failure, **when** the locked
  audit is evaluated, **then** it remains a nonzero fail-closed blocker and is not waived or
  success-normalized.
- **REQUIREMENT AC-GATE-04:** **Given** implementation evidence, **when** mission gates are updated,
  **then** test, security, code-review, and judgment remain pending until independently executed.

## Required operation counters

- **REQUIREMENT COUNTERS:** R10 requirements/implementation evidence shall report:
  `authentication=0`, `workflow dispatches=0`, `deployments=0`, `success receipts=0`,
  `database calls/mutations=0`, `cloud resource/secret mutations=0`, `installs=0`,
  `package restores=0`, `commit/push/history operations=0`, and `actual C6 builds=0`.

## Scope boundaries

- **OUT OF SCOPE:** Architecture selection or task decomposition beyond the frozen observable
  lifecycle.
- **OUT OF SCOPE:** Implementation or edits outside T21 mission evidence during this requirements
  freeze.
- **OUT OF SCOPE:** External action-pin resolution in this analyst pass, workflow installation,
  OIDC/FIC configuration, protected variables/endpoints, cloud configuration, authentication,
  deployment, database/resource mutation, or Production enablement.
- **OUT OF SCOPE:** Rebuilding C6, creating a deployment-evidence producer, adding a success
  placeholder, weakening the forbidden boundary, or changing business/promotion policy.
- **OUT OF SCOPE:** Resolving the external NuGet vulnerability-service outage or unrelated
  formatting changes.

## Risks

- **FACT:** The exact replacement action SHAs are a mandatory technical prerequisite; unverified
  values block R10 completion under AC-PIN-01.
- **FACT:** Converting same-run producers to completed prior runs changes workflow contracts and
  tests atomically; partial migration would leave selectors or role policy inconsistent.
- **FACT:** Root correction can accidentally weaken the immutable-bundle boundary if checkout-root
  paths are introduced into trusted execution; R10-08 and AC-ROOT-03 prohibit that regression.

## Counters

- **FACT:** Requirements: **18**.
- **FACT:** Acceptance criteria: **20**.
- **FACT:** Assumptions: **2**.
- **FACT:** Blocking open questions: **0**.
