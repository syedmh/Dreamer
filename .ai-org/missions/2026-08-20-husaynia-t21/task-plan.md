# T21-R4 implementation plan — caller authorization and artifact provenance

Status: **VALIDATION — INDEPENDENT RERUNS PENDING**  
Scope: repository-only remediation of ADR-005 and ADR-006.  No GitHub/Azure
configuration, authentication, deployment, database work, network installation, or
workflow dispatch is part of this plan.

## Frozen interfaces

All production consumers use only the following v2 contracts.  They are internal
breaking changes: v1 self-declared run metadata, `trustedExecution`, same-run flags,
and deployment-evidence selectors are rejected/removed; there is no compatibility
period because every stage is disabled.

1. **OIDC caller authorization.** `Test-ExternalTrustMarker.ps1` receives decoded
   claims, `T21_TRUSTED_WORKFLOW_REF`, `T21_TRUSTED_OIDC_SUBJECTS_JSON`, and the
   externally pinned bundle SHA.  It requires exactly these claim values before
   Azure login: issuer `https://token.actions.githubusercontent.com`, audience
   `api://AzureADTokenExchange`, `repository=syedmh/Dreamer`, immutable positive
   decimal `repository_id` and `repository_owner_id`, `ref=refs/heads/main`,
   matching `environment`/`context`, a 40-lowercase-hex `workflow_sha`, the exact
   caller `workflow_ref`, and the exact reusable `job_workflow_ref` and
   `job_workflow_sha`.  It constructs `sub` from the immutable IDs and accepts it
   only when it is both the actual claim and one of the five exact subjects in the
   external JSON marker.  The protected policy contains the only five rows:

   | Stage / context | permitted `workflow_ref` |
   | --- | --- |
   | Development / Husaynia-Development-Operations | `syedmh/Dreamer/.github/workflows/release-build-and-nonproduction.yml@refs/heads/main` |
   | Development / Husaynia-Development-Operations | `syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main` |
   | Staging / Husaynia-Staging-Operations | `syedmh/Dreamer/.github/workflows/release-build-and-nonproduction.yml@refs/heads/main` |
   | Staging / Husaynia-Staging-Operations | `syedmh/Dreamer/.github/workflows/operation-evidence-producer.yml@refs/heads/main` |
   | Production / Husaynia-Production | `syedmh/Dreamer/.github/workflows/production-operation-evidence.yml@refs/heads/main` |

   `T21_TRUSTED_OIDC_SUBJECTS_JSON` is an installation sentinel/mirror, never an
   authorization source; Entra's exact FIC issuer/audience/subject match remains
   authoritative.  The all-zero reusable SHA or a missing/malformed marker fails
   before an ID-token request.

2. **Producer content manifest.** Every attested reusable producer archive contains
   exactly one UTF-8/LF `t21-producer-manifest.json`, is its attestation subject,
   and has the ADR-006 exact v2 shape: `schemaVersion`, `kind`, `producerRole`,
   `stage`, `applicationSha256`, `bundleSha256`, `release`, `producer`,
   `trustedExecution`, lexically sorted `files`, and `createdAtUtc`.  No extra
   properties, unsafe paths, links, duplicate/alias paths, non-lowercase digests,
   or uncovered payload files are allowed.  `trustedExecution` v2 is exactly
   `{ callerWorkflowRef, reusableWorkflowRef, bundlePath, bundleSha256 }`.

3. **Consumer-created provenance.**
   `Resolve-GitHubArtifactProvenance.ps1` is the only privileged artifact resolver.
   It takes an expected role, opaque positive-decimal run selector, canonical
   expected stage/application/release/bundle bindings, and an output directory.
   It writes consumer-created `verified-provenance.json` with the ADR-006 v2 exact
   shape (`authority=github-actions-api-and-sigstore-v1`, expected role, API run,
   immutable artifact identity/digest, content-manifest digest, and required
   attestation record when applicable), and returns the safely extracted root.
   It queries the Actions run/artifact APIs, selects one immutable artifact ID,
   downloads by ID, hashes the archive against GitHub's `sha256:<64hex>` digest,
   safely extracts it, validates the content manifest, and runs:

   `gh attestation verify t21-producer-manifest.json --repo syedmh/Dreamer
   --signer-workflow <role-pinned-workflow> --signer-digest <role-pinned-sha>
   --source-ref refs/heads/main --source-digest <API-head-sha>
   --predicate-type https://slsa.dev/provenance/v1 --deny-self-hosted-runners`.

   The CLI must already be available; no installation fallback is permitted.
   Consumers accept verified-provenance paths, never producer-created
   `*-run.json` files.  Resolver failures (API/CLI unavailable, nonunique/expired
   artifact, digest/manifest/role/run/attestation mismatch, or a forbidden role)
   are fail-closed before OIDC.

4. **Externally pinned role map.** The protected bundle policy owns the role map:
   `release-c6` (API only, completed-success release build); `trusted-preflight`
   and `trusted-stage-evidence` (the caller matrix plus attestation signed by
   `trusted-protected-operations.yml@T`); `stage-operation-inputs` (attestation
   signed by the pinned `stage-operation-inputs.yml`); and
   `migration-authorization` (canonical name and API-bound completed-success
   authorized path).  The only attestation action is
   `actions/attest-build-provenance@96b4a1ef7235a096b17240c259729fdd70c83d45`.
   Its `attestations: write` and `id-token: write` permissions are granted only
   to the two producer jobs that use this full-SHA action (the stage-input job
   uses its ID token only for GitHub attestation, never Azure login).  Consumers
   use `actions: read` and `attestations: read`; the trusted reusable does not
   check out the repository.

5. **Intentional zero-deployment contract.** `deployment-evidence` is an explicit
   `forbidden` role in the v2 map.  No workflow accepts, downloads, resolves, or
   trusts deployment-evidence run/name inputs.  Each StageOperations/gate path
   calls the deployment-evidence assertion before OIDC/receipt creation; it throws
   the stable `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` failure before reading a
   candidate evidence file.  Thus disabled stages produce no token request,
   mutation, evidence, or success receipt; if a stage is incorrectly enabled,
   it still fails before OIDC and cannot claim a deployment.  This is a complete
   and intentional zero-deployment state, not a missing producer.  A future,
   separately approved deployment mission must create a real completed-success
   producer and change this externally pinned role-map revision; it is not
   authorized by T21-R4.

6. **Workflow inputs and order.** Run IDs are opaque selectors only.  Canonical
   non-C6 artifact names derive from `{stageLower, applicationSha256}` and are not
   caller inputs.  `sourceReleaseSameRun` and `operationEvidenceSameRun` are
   removed.  For every privileged path: validate selectors and external markers;
   resolve API/artifact/attestation provenance; perform existing semantic,
   freshness, policy, and deployment-forbidden checks; request OIDC; validate the
   exact caller/reusable subject; Azure login; recheck the read-only C6 bundle;
   execute bundled code.  No `New-Run`, synthetic metadata, checkout, or
   repository path is permitted after C6 validation or before this sequence.

## Tasks

T21-R4-1  Freeze v2 bundle policy and manifest writer
    owner:        DevOps/SRE specialist
    objective:    Encode the caller matrix, pinned producer-role map, explicit
                  forbidden deployment role, and canonical producer-manifest
                  writer in the immutable C6 bundle.
    files:        `pipelines/config/promotion-policy.json`;
                  `eng/artifact/New-ProtectedExecutionBundle.ps1`;
                  `eng/artifact/Test-ProtectedExecutionBundle.ps1`;
                  `eng/test/Test-ProtectedExecutionBundle.ps1`;
                  `eng/promotion/New-T21ProducerManifest.ps1` (new)
    depends_on:   -
    parallel_ok:  no — this freezes the interfaces consumed by every later task.
    exit_criteria: The generated bundle contains the new writer and all v2
                  policy fields; its deterministic manifest/checksums cover them;
                  exact five-row caller/role-map, safe canonical manifest, v1
                  rejection, all-zero pin, and forbidden deployment-role tests
                  pass locally.  No external marker is populated.
    status:       DONE

T21-R4-2  Implement pre-OIDC provenance and caller validators
    owner:        DevOps/SRE specialist
    objective:    Replace self-declared run identity with API/digest/attestation
                  verified provenance and validate the exact caller-plus-reusable
                  OIDC subject before token acquisition.
    files:        `eng/promotion/Resolve-GitHubArtifactProvenance.ps1` (new);
                  `eng/promotion/Test-ExternalTrustMarker.ps1`;
                  `eng/promotion/Test-TrustedProtectedOperationInputs.ps1`;
                  `eng/promotion/Test-StageEvidenceBundle.ps1`;
                  `eng/promotion/Test-StageOperationInputBundle.ps1`;
                  `eng/common/Release.Common.ps1`;
                  `eng/artifact/Resolve-ReleaseArtifactRoot.ps1`;
                  `eng/test/Test-GitHubArtifactProvenance.ps1` (new)
    depends_on:   T21-R4-1
    parallel_ok:  no — it consumes the frozen bundle contracts and changes shared
                  assertion signatures.
    exit_criteria: Fixture-backed tests prove all five subject rows pass and a
                  swapped caller/stage, unapproved caller, missing `workflow_ref`,
                  old subject, wrong reusable SHA, wrong IDs, or bad audience
                  fails before the token boundary.  Mocked API/archive/CLI cases
                  reject wrong repository/path/ref/head SHA/attempt/status,
                  duplicate/expired/no-digest artifact, changed archive/content,
                  mismatched role/stage/app/bundle, synthetic metadata, and
                  missing/foreign/replayed/wrong-signer attestation.  Only the
                  exact signed manifest succeeds.  A valid-shaped deployment JSON
                  and provenance cannot bypass the forbidden-role failure.
    status:       DONE

T21-R4-3  Convert producers, gates, and receipts to v2 provenance
    owner:        DevOps/SRE specialist
    objective:    Make raw preflight/evidence and stage-input producers emit and
                  attest v2 manifests; make all local consumers receive verified
                  provenance paths; preserve existing semantic/freshness/lease
                  checks while making any operation/gate receipt fail at the
                  explicit zero-deployment boundary.
    files:        `eng/promotion/Invoke-OperationEvidenceProducer.ps1`;
                  `eng/promotion/Invoke-StageGate.ps1`;
                  `eng/promotion/Invoke-AutomaticStagingPromotion.ps1`;
                  `eng/promotion/Get-GitHubRunMetadata.ps1`
    depends_on:   T21-R4-2
    parallel_ok:  no — its parameter changes are consumed by the workflow wave.
    exit_criteria: No producer-created `*-run.json` is accepted by the changed
                  privileged/gate path; all carried trusted-execution structures
                  are v2 and bound to matching verified provenance.  Automatic,
                  manual, and Production gate calls have no deployment-evidence
                  argument or success-receipt path.  Existing C6, freshness,
                  read-only-preflight, SQL lease/fence, and Production-disabled
                  unit cases remain green, and the new zero-deployment negative
                  tests prove zero OIDC/mutation/evidence/receipt.
                  `Get-GitHubRunMetadata.ps1` may remain only as the resolver's
                  API adapter; all direct privileged/gate uses and all synthetic
                  same-run metadata construction are removed.
    status:       DONE

T21-R4-4  Wire immutable workflow producers and consumers
    owner:        DevOps/SRE specialist
    objective:    Apply the frozen resolver sequence and minimal permissions to
                  every relevant caller/producer/consumer, attest the two allowed
                  producer artifacts, and remove every deployment-evidence and
                  same-run workflow selector.
    files:        `pipelines/github/trusted-protected-operations.yml`;
                  `pipelines/github/release-build-and-nonproduction.yml`;
                  `pipelines/github/operation-evidence-producer.yml`;
                  `pipelines/github/production-operation-evidence.yml`;
                  `pipelines/github/stage-operation-inputs.yml`;
                  `pipelines/github/stage-evidence-intake.yml`;
                  `pipelines/github/production-promotion.yml`
    depends_on:   T21-R4-3
    parallel_ok:  no — all seven workflows exchange the changed inputs/artifacts.
    exit_criteria: The trusted reusable workflow resolves every pre-OIDC artifact
                  by API ID/digest and required attestation before accessing
                  `ACTIONS_ID_TOKEN_REQUEST_URL`; it has no checkout or
                  `contents: read`.  Raw preflight/evidence and stage-input
                  archives create `t21-producer-manifest.json` then use only the
                  frozen provenance-action SHA.  Consumers resolve by role, not
                  input artifact name.  The three `deploymentEvidence*` inputs,
                  downloads, and variable selectors are absent from all wrappers
                  and promotion workflows.  All stage conditions remain false by
                  default and each path fails closed without a receipt.
    status:       DONE

T21-R4-5  Update contract gates and run repository-only validation
    owner:        DevOps/SRE specialist
    objective:    Encode the R4 static/causal regressions and execute all
                  repository-only checks without dispatching workflows or touching
                  cloud resources.
    files:        `eng/ci/Test-PipelineDefinitions.ps1`;
                  `eng/test/Invoke-T21Validation.ps1`;
                  `eng/test/Test-T21ValidationExitCodes.ps1`
    depends_on:   T21-R4-4
    parallel_ok:  no — assertions must match the final shared workflow contract.
    exit_criteria: Static tests require resolver-before-token-before-login order,
                  exact permissions/action SHA, no post-login checkout/path,
                  no same-run/synthetic metadata/deployment selectors, all stages
                  disabled, and no possible receipt after the forbidden-role
                  failure.  Run `pwsh -NoProfile -File
                  .\eng\ci\Test-PipelineDefinitions.ps1`, `pwsh -NoProfile -File
                  .\eng\test\Test-ProtectedExecutionBundle.ps1`, `pwsh
                  -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1`,
                  and `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1
                  -ContractOnly`; report counts and any existing environmental
                  NU1900/format blocks separately.  Evidence must show zero
                  authentication, deployment, database, and resource mutation.
    status:       DONE

## Execution waves

`wave 1: T21-R4-1 -> wave 2: T21-R4-2 -> wave 3: T21-R4-3 -> wave 4:
T21-R4-4 -> wave 5: T21-R4-5`

All waves are deliberately serial: each changes the C6 policy, shared PowerShell
contracts, or the same workflow call graph.  One DevOps/SRE specialist owns the
implementation; independent validation begins only after wave 5.

## Post-implementation independent gates

`parallel(test-engineer, security-engineer, code-reviewer) -> engineering-judge`.
The external GitHub OIDC-template readback, exact Entra FIC installation, final
trusted-workflow SHA, and final external C6 bundle checksum remain accepted
fail-closed prerequisites, not tasks in this repository plan.

## R4 implementation evidence

- T21-R4-1 through T21-R4-5: **DONE** (implementation/self-validation only).
- Local evidence is recorded in `test-results.md`: definitions 11/11, protected bundle
  28/28, provenance 15/15, contract harness 4/4, exit-code 1/1, static Bicep 26/26,
  secret scan 77 files/0 findings; all mutation counts are zero.
- Independent test, security, code review, and judgment are **PENDING**. No implementation
  evidence is a substitute for those gates.

## T21-R4 remediation follow-up — 2026-08-24

Status: **REMEDIATION IMPLEMENTED — INDEPENDENT GATES MUST RERUN**

The reported runtime bearer-header, pre-token marker ordering/bundle-root persistence, and
allowed-producer wiring defects were remediated in the approved `pipelines/**` and `eng/**`
scope. The stage-input producer contract test was reconciled from the removed
`workflow_dispatch`/free-artifact-name assumptions to the frozen `workflow_call` role-derived
interface; all stage conditions remain unreachable from their declared triggers.

Local regression evidence is implementation evidence only: pipeline definitions 14/14,
protected bundle 32/32, GitHub provenance 22/22, stage-input producer 17/17, T21 contract
harness 4/4, exit-code regression 1/1, cached actionlint schema validation, static Bicep
26/26, and owned-file secret scan 77 files/0 findings. The independent test, security,
code-review, and judgment gates remain **not passed** and must be rerun; this mission is not
complete. See `test-results.md` for commands, the initial red regressions, and the validation
constraint finding.

## T21-R4 independent-review rework — 2026-08-24

Status: **IMPLEMENTATION/REWORK — LOCAL CAUSAL TESTS PASS; INDEPENDENT GATES PENDING**

This bounded rework repairs the second independent code review's executable workflow graph and
the security review's pre-token external bundle-marker equality gap. The resolver now accepts
only an authorized top-level caller run from the frozen five-row matrix, requires the signed
manifest caller to match that API run path, and verifies the separate pinned stage-input signer;
the manual consumer now invokes the producer before selecting that shared caller run.

The stage-input job checks out only `${{ github.sha }}` with `contents: read` before the writer,
upload, and attestation. Every caller of the attesting trusted reusable delegates exactly
`actions: read`, `attestations: write`, and `id-token: write`; after C6 validation, the
immutable bundle-root marker validator compares the external bundle SHA before token minting,
while retaining the final claims/bundle recheck.

Initial causal red summaries: pipeline definitions 14/18; provenance 25/26; stage-input
producer 17/18; protected bundle 32/33. Final local summaries: pipeline definitions 18/18,
protected bundle 35/35, provenance 26/26, stage-input producer 18/18, contract harness 4/4,
exit-code 1/1, and secret scan 77 files/0 findings.

No authentication/API, deployment, database, resource, secret, install/download, network,
commit, push, or history operation was performed. Independent test, security, code-review, and
judgment gates remain pending; external OIDC/FIC/final-SHA prerequisites and official
NuGet-audit/format blockers are unchanged.

## T21-R4-R6 final independent code-review remediation — 2026-08-24

Status: **IMPLEMENTATION DONE — INDEPENDENT GATES MUST RERUN**

The trusted reusable workflow now passes only resolver-created `verified-provenance.json` paths
for raw preflight and stage-operation inputs to the privileged semantic consumer. That consumer
derives each `artifact` root itself, validates the exact v2 provenance/run/artifact/attestation/
content-manifest bindings against the frozen role/caller map and pinned signer references, then
reaches the unchanged `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary before candidate
deployment evidence, receipt, OIDC, or mutation.

`PreflightRunMetadataPath` and `StageOperationInputRunMetadataPath` are removed from that
consumer and trusted workflow call. `Invoke-AutomaticStagingPromotion.ps1` is reduced to the
same deliberate zero-deployment boundary, removing its synthetic run-metadata and producer-path
compatibility path. R4-1 through R4-5 remain DONE; this is bounded remediation/revalidation
evidence, not an independent gate result.

The local causal sequence first failed with pipeline definitions `18/20` (2 failures) and
protected bundle `35/39` (4 failures), then passed after the production fix. The tests use the
real fixture-backed resolver to create provenance accepted by the semantic consumer; producer
`*-run.json`, missing provenance, and role-mismatched provenance reject before the token
boundary, while valid provenance reaches only the preserved forbidden deployment boundary.

Final evidence in `test-results.md`: pipeline definitions 20/20, protected bundle 39/39, GitHub
provenance 26/26, stage-input producer 18/18, T21 contract 4/4, exit codes 1/1, HTTP fixture
7/7, static Bicep policy 26/26, and secret scan 77 files/0 findings. No authentication,
deployment, database, resource, network, or install action was performed. The earlier direct
workflow-schema validation installer incident remains a compliance finding; no actionlint/Bicep
installer wrapper or binary was invoked for this remediation.

Independent test, security, code-review, and judge gates are **PENDING RERUN**. The mission is
not COMPLETE; external immutable-subject/FIC/final-SHA prerequisites and pre-existing
NuGet-audit/format blockers remain unchanged.

## T21-R4-R7 final independent-finding remediation — 2026-08-24

Status: **LOCAL IMPLEMENTATION PASS — INDEPENDENT GATES MUST RERUN**

This bounded follow-up removes `releaseArtifactName`, `preflightArtifactName`, and
`artifactRoot` from `migration-apply-authorization.yml`.  Its only artifact selectors are opaque
run IDs; it now invokes the v2 resolver for `release-c6` and `trusted-preflight`, passing only
the resolver-created `verified-provenance.json` paths to the authorization consumer.  The
authorization consumer verifies the API/digest/manifest/attestation bindings and derives the
extracted roots itself; its policy-pinned hash was refreshed.  The durable SQL lease/fence and
fixed SELECT-only migration-preflight contract are unchanged, and all deployment flags remain
false.

The trusted semantic consumer now invokes `Assert-StageDeploymentEnabled` for **both**
`Preflight` and `StageOperations` before its Preflight return.  The reusable workflow's mandatory
consumer step therefore fails while disabled before the later OIDC request and `azure/login`
steps.  The existing `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` StageOperations boundary is
unchanged and remains before candidate deployment evidence, receipt, token, and mutation paths.

Files: `pipelines/github/migration-apply-authorization.yml`,
`pipelines/config/promotion-policy.json`, `eng/promotion/{Resolve-GitHubArtifactProvenance,
New-MigrationApplyAuthorization,Test-TrustedProtectedOperationInputs}.ps1`,
`eng/ci/Test-PipelineDefinitions.ps1`, and the T21 test harness plus its new Preflight/migration
causal suites.

Exit criteria: local causal regressions prove disabled Preflight fails at the mandatory
pre-token/no-Azure-login boundary; legacy migration selectors and producer run metadata have no
privileged path; missing v2 migration provenance fails before candidate evidence/receipt; and the
existing fenced/read-only migration checks remain green.  No independent gate is self-certified.

## T21-R8 nine-finding remediation — 2026-08-25

Status: **REWORK / IMPLEMENTATION IN PROGRESS**

Single serial implementation task because the workflow contracts, bundle trust boundary,
migration fence, and static/causal harnesses are shared:

| Task | Owner | Dependencies | Exit criteria | Status |
| --- | --- | --- | --- | --- |
| T21-R8 | DevOps/SRE specialist | R4-R7 | Remediate all nine approved findings; add causal regressions; run only local/cached validation; record actual counts and zero-mutation evidence. | DONE |

Frozen R8 acceptance criteria:

1. Every production `actions/checkout` reference uses exactly
   `11bd71901bbe5b1630ceea73d27597364c9af683`; the test contract rejects every other
   full SHA and generic 40-hex matching.
2. Trusted reusable/manual/wrapper inputs do not accept artifact root/name or v1
   release/operation metadata selectors. The reusable flow resolves canonical C6 through
   `Resolve-GitHubArtifactProvenance.ps1` before OIDC and only consumes its v2
   verified-provenance and derived roots.
3. Disabled Development/Staging/prepared-input flows are concrete role-derived producer/
   consumer graphs, receive all required evidence/preflight/prepared-input/authorization/
   receipt bindings, and remain zero-execution with no success receipt.
4. Migration authorization grants precisely the minimum `gh attestation verify`
   permission and static regression proves the exact permission map.
5. At the mutation boundary, migration Apply atomically validates the current durable SQL
   lease holder, fence token, authorization hash, and expiry while binding bundle execution
   to the fence; the local fixture proves stale holder -> takeover -> old holder rejection
   before mutation.
6. The trusted reusable directly hashes the downloaded C6 archive and compares it to
   protected `vars.T21_TRUSTED_BUNDLE_SHA256` before extraction/validator/execution;
   caller inputs cannot choose the expected hash. A malicious caller-matching hash fixture
   proves marker/validator are not reached, and static ordering is asserted.
7. `Invoke-T21Validation.ps1` classifies only real failed prerequisites/evidence as
   environmental blocks. The checked-in PR-shaped fixture completes with exit 0; NU1900
   remains fail-closed and unsuppressed.
8. Pipeline and contract tests prove the production wiring, selector prohibitions, exact
   checkout pin, and exact attestation permission contract.
9. Each forbidden-deployment assertion has a distinct causal behavior and stable test name.

Constraints: no network/install/authentication/workflow dispatch/deployment/database/resource
mutation; no external configuration; no history operation; only allowed `HusayniaSite/pipelines/**`,
`HusayniaSite/eng/**`, `.config/dotnet-tools.json`, and mission artifacts. Post-implementation
test, security, code-review, and judge gates remain pending independent rerun.

### T21-R8 implementation result

Status: **DONE (local implementation evidence only)**

The bounded implementation added exact checkout/attestation permission contracts, canonical
C6/provenance and prepared-input wiring, pre-extraction protected archive equality, a durable
mutation-boundary fence assertion with a local takeover fixture, a non-blocking full T21 harness
fixture, and distinct deployment-forbidden causal coverage. Final local evidence is appended to
`test-results.md`: pipeline 30/30, protected bundle 38/38, provenance 26/26, stage producer
19/19, preflight 2/2, migration provenance 7/7, durable fence 3/3, T21 harness 7/7,
exit-code 2/2, HTTP 7/7, cached actionlint 10 workflows, cached Bicep 62 checks, static Bicep
26/26, PowerShell parse 64 files, and secret scan 80 files/0 findings.

Independent test, security, code-review, and judgment remain pending and are not
self-certified. The external OIDC/FIC/final-SHA and historical fail-closed NU1900/format
conditions remain unchanged.

### T21-R8 remaining self-validation gaps — 2026-08-25

Status: **DONE — LOCAL SELF-VALIDATION ONLY**

The R8 task is reopened without incrementing `reworkCount`. Scope is limited to three evidence
gaps: execute the checked-in pre-extraction archive equality logic against a malicious
caller-matching hash fixture and prove zero marker/validator side effects; exercise the production
fence path through a safe fake mutation executor so the stale holder is rejected before mutation
and the current holder executes exactly once; and assert the literal automatic/manual
Development/Staging plus prepared-input Production StageOperations contracts through the frozen
deployment-evidence-forbidden boundary, including all required canonical arguments and zero
execution/mutation/receipt behavior. Independent gates remain pending.

Implementation result:

- The protected-bundle test extracts and executes the exact checked-in pre-extraction workflow
  body with fake `gh`, a valid carrier digest, and a caller-matching malicious inner bundle hash
  that differs from the protected hash. Rejection occurs before trusted-archive export,
  stage-target write, marker side effect, or validator side effect. Static ordering remains a
  separate assertion.
- `Invoke-MigrationFencedMutation` is the single production Apply seam: it performs the existing
  durable `AssertFence` operation before invoking an executor. The local no-database fixture proves
  a stale holder invokes the fake executor zero times and the current holder exactly once.
- Automatic Development/Staging, manual nonproduction, and manual Production StageOperations
  callers supply the preflight, prepared-input, and migration-authorization run selectors. The
  trusted consumer receives canonical preflight, prepared-input, authorization, deployment,
  raw-evidence, validated-evidence, and receipt paths. Static contracts distinguish this
  complete-but-unreachable wiring from missing wiring while every stage remains disabled and the
  deployment-evidence producer remains forbidden.

Final local evidence is appended to `test-results.md`. State is `VALIDATION`; independent test,
security, code-review, and judgment reruns remain pending.

## T21-R9 current independent R8-finding remediation — 2026-08-26

Status: **DONE — LOCAL IMPLEMENTATION/SELF-VALIDATION ONLY**

| Task | Owner | Exit criteria | Status |
| --- | --- | --- | --- |
| T21-R9-A | DevOps/SRE specialist | Validate every outer carrier ZIP entry in trusted inline code, stream the sole canonical protected bundle to one fixed path, and reject control/path/link/alias/duplicate/size abuse before side effects. | DONE |
| T21-R9-B | DevOps/SRE specialist | Replace preflight producer v1 run metadata/root inputs with resolver-created v2 release provenance and explicit top-level/reusable identities for all three stages. | DONE |
| T21-R9-C | DevOps/SRE specialist | Produce and validate v2 prepared-input bundles for Development/Staging/Production with every stage-specific report dependency and protected Production CTO context. | DONE |
| T21-R9-D | DevOps/SRE specialist | Make producer manifests and consumers distinguish the API top-level run from the pinned reusable producer; accept the exact stage-input workflow manifest and reject swapped identities. | DONE |
| T21-R9-E | DevOps/SRE specialist | Restore opaque migration-authorization selectors for all four StageOperations callers; resolve API/digest/attestation provenance and validate canonical C6/preflight/caller/reusable/Production CTO bindings before OIDC. | DONE |
| T21-R9-V | DevOps/SRE specialist | Run local/cached production-shaped causal, schema, Bicep/static, parse, secret, harness, fence, and fail-closed audit validation with zero prohibited operations. | DONE |

Implementation preserves the exact checkout pin, minimal attestation permissions, protected hash
ordering, immutable/no-rebuild C6, no post-login repository code, durable mutation claim/fence,
strict exit semantics, disabled stages, forbidden deployment evidence, and no success receipt.
The authorization graph is complete but unreachable: release -> preflight -> prepared inputs ->
Production CTO when required -> migration authorization -> semantic validation ->
`T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` -> token/login (never reached).

Independent test, security, code-review, and engineering-judge gates remain **PENDING**. External
OIDC/FIC installation, final nonzero workflow pins, protected selectors/endpoints, and the
fail-closed NuGet vulnerability-service blocker remain outside this repository-only task.

## T21-R10 dependency-ordered implementation and validation plan — 2026-08-27

Status: **IMPLEMENTATION COMPLETE — LOCAL SELF-VALIDATION PASS; INDEPENDENT GATES PENDING**

This section preserves the complete R4-R9 history above and converts the frozen R10 architecture
into one serial, repository-only implementation stream followed by independent parallel gates.
The repository root is `C:\Users\syedhu\source\repos\Dreamer`; the implementation root is its
`HusayniaSite` child. Requirements, Definition of Done, Architecture, and ADRs are binding inputs.
No task in this plan authorizes workflow installation or dispatch, authentication, deployment,
database/resource/secret mutation, tool installation, package restore, an actual C6 build, or a
commit/push/history operation.

### R10 frozen interfaces

1. **Lifecycle and caller boundary.** `R`, `P`, `I`, `C`, `M`, and `S` are separate top-level
   runs. `R` is the only C6 build. Development and Staging use
   `R -> completed P/I -> completed M -> distinct S`; Production uses
   `R -> completed P/I -> completed C -> completed M -> distinct manual S`. `P` and `I` may run
   concurrently after `R`, but no implementation task is concurrent. The caller matrix is reduced
   to the least-privilege three rows actually used after release becomes release-only:
   Development and Staging use
   `operation-evidence-producer.yml@refs/heads/main`; Production uses
   `production-operation-evidence.yml@refs/heads/main`. No release-workflow caller row remains.

2. **Producer-mode dispatch contract.**
   `operation-evidence-producer.yml` and `production-operation-evidence.yml` execute exactly one
   `mode` per top-level run:

   ```text
   mode: Preflight | PreparedInputs | StageOperations
   stage: Development | Staging                  # nonproduction wrapper only
   sourceReleaseRunId: positive decimal
   expectedAppSha256: nonzero lowercase 64-hex
   preflightRunId: positive decimal              # StageOperations only
   stageOperationInputsRunId: positive decimal   # StageOperations only
   migrationAuthorizationRunId: positive decimal # StageOperations only
   ctoAuthorizationRunId: positive decimal       # Production StageOperations only
   ctoApprovalReference: [A-Za-z0-9][A-Za-z0-9._:/#-]{0,127}
                                                # Production I/C/M/S as applicable
   orchestrationCorrelationId: [A-Za-z0-9][A-Za-z0-9._:-]{0,127}
                                                # routing/idempotency only
   ```

   Unused inputs are the empty string and are rejected if populated. `PreparedInputs` never accepts
   preflight or CTO run IDs. `StageOperations` requires distinct completed `P` and `I` selectors
   and may not use its own `github.run_id`. `cto-authorization-record.yml` takes completed
   `R/P/I`, `ctoApprovalReference`, expiry, and decision. `migration-apply-authorization.yml`
   takes completed `R/P/I`, plus completed `C` and the same approval reference only for Production.
   There are no compatibility aliases for `changeReference` or same-run inputs.

3. **Automatic coordinator contract.**
   `automatic-nonproduction-orchestration.yml` is triggered only by completed workflow-run events
   for the existing top-level names corresponding to
   `release-build-and-nonproduction.yml`, `migration-apply-authorization.yml`, and
   `operation-evidence-producer.yml`. It requires exact repository, protected-main workflow path,
   `status=completed`, `conclusion=success`, the existing stage enable variable, and
   `promotion-policy.json` `deploymentEnabled=true` before dispatch. It:
   - dispatches distinct Development `Preflight` and `PreparedInputs` children after `R`;
   - dispatches Development or Staging `StageOperations` after the matching completed `M`;
   - dispatches distinct Staging `P/I` only after completed Development `S` and canonical
     Development receipt provenance; the checked-in forbidden receipt therefore keeps this at
     zero dispatches;
   - never dispatches Production and ignores coordinator, `P`, and `I` completions.

   Its lifecycle key is `r10-<predecessorRunId>-<stageLower>-<modeLower>` and is carried in the
   child input, exact run-name correlation, and summary. Zero matching children causes one
   dispatch, one matching child is reused, and more than one fails closed. Polling is bounded;
   failed, cancelled, timed-out, missing, or duplicate children dispatch no successor.
   Permissions are exactly `actions: write`, `attestations: read`, and `contents: read`; there is
   no environment, secret, `id-token`, artifact-write, cloud, or deployment authority. It writes
   no artifact.

4. **Resolver and producer identity contract.**
   `Resolve-GitHubArtifactProvenance.ps1` adds mandatory positive-decimal `ConsumerRunId` to every
   workflow invocation. A release-only `-DiscoverReleaseIdentity` switch may omit only
   `ApplicationSha256`; it still requires the expected bundle SHA, fully validates C6, writes the
   normal `verified-provenance.json`, and exposes the discovered application/release identity.
   Every selectable role requires selected `status=completed`, `conclusion=success`,
   `selected.id != ConsumerRunId`, and `selected.updated_at <= consumer.created_at` after strict UTC
   parsing. API 404, missing conclusion, queued/in-progress/failed/cancelled runs, current run,
   later/future run, malformed time, or duplicate artifact rejects. Producer manifest
   `producer.commitSha`, verified provenance `run.commitSha`, and attestation source digest bind
   the selected run's actual `head_sha`; the immutable release commit remains a separate C6
   binding. `P` and `I` run IDs must differ.

5. **Prepared-input and authorization v2.1 contract.**
   Prepared inputs retain nonauthorizing reports and add
   `productionAuthorizationRequest = { stage, releaseBinding, targetFingerprint,
   ctoApprovalReference }` for Production. They do not contain `source-change-record.json`, a
   success placeholder, or CTO authorization context. CTO authorization resolves completed
   `R/P/I`, performs the protected change-record check, and publishes
   `cto-authorization.json`, `source-change-record.json`, and its producer manifest. CTO,
   migration, and StageOperations bind:

   ```text
   producerBindings.preflight      = run ID + artifact/content-manifest SHA
   producerBindings.preparedInputs = run ID + artifact/content-manifest SHA
   releaseBinding                  = run ID + app/bundle/manifest/commit SHA
   productionCtoAuthorization      = CTO run/provenance/change-record binding # Production M/S
   ```

   The canonical `R/P/I/C/M` artifact names in Architecture remain unchanged. The change is atomic;
   v2.0 authorization/prepared-input compatibility is not accepted.

6. **Checkout root and shell contract.** Every job that checks out the repository defines exactly
   `HUSAYNIA_REPOSITORY_ROOT=${{ github.workspace }}/HusayniaSite`,
   `defaults.run.shell=pwsh`, and `defaults.run.working-directory=HusayniaSite`. Its first
   PowerShell body requires that exact resolved child beneath `GITHUB_WORKSPACE` and verifies
   `HusayniaSite.sln`, `eng`, and `pipelines`. All checkout paths and explicit `-RepositoryRoot`
   values derive from that variable; there is no workspace-root fallback. Jobs without checkout
   still use `pwsh` and do not define the root variable. `T21_TRUSTED_BUNDLE_ROOT` keeps precedence
   for protected pre/post-login execution and is never redirected to checkout content.

7. **Binary and action trust primitives.** The checked-in C6 download body requires PowerShell
   7.4+, an absent carrier path, and binary-safe native stdout redirection:

   ```powershell
   & gh api ('repos/{0}/actions/artifacts/{1}/zip' -f $env:GITHUB_REPOSITORY, $artifact.id) > $carrier
   $downloadExit = $LASTEXITCODE
   ```

   `gh api --output`, pipelines, and text conversion are forbidden. CLI failure, absent/empty/
   oversize output, or digest mismatch deletes the partial file and stops before ZIP open,
   environment/output writes, marker/validator execution, OIDC, login, mutation, evidence, or
   receipt. The exact pins everywhere are:
   `azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6` and
   `actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be`.
   Exactly four approved producer jobs attest; permissions and the checkout pin do not broaden.

8. **Zero-deployment and evidence contract.** All stage policy values remain
   `deploymentEnabled=false`. The one stable
   `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary remains before OIDC/login/mutation/
   downstream evidence/receipt. R10 implementation and gate evidence must report:
   `authentication=0`, `workflow dispatches=0`, `deployments=0`, `success receipts=0`,
   `database calls/mutations=0`, `cloud resource/secret mutations=0`, `installs=0`,
   `package restores=0`, `commit/push/history operations=0`, and `actual C6 builds=0`.
   Fixture-only fake dispatch/executor observations are reported separately and are not actual
   operations.

### R10 tasks

T21-R10-1  Freeze policy, root, resolver, and producer identity primitives
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Land the shared v2.1 policy and temporal identity contracts before any producer
                  or workflow consumes them.
    inputs:       `requirements.md`; `definition-of-done.md`; `architecture.md` frozen contracts,
                  file delta, and tests; ADR-011 through ADR-014; current R9 implementation.
    files:        `HusayniaSite/pipelines/config/promotion-policy.json`;
                  `HusayniaSite/eng/common/Release.Common.ps1`;
                  `HusayniaSite/eng/promotion/Resolve-GitHubArtifactProvenance.ps1`;
                  `HusayniaSite/eng/promotion/New-T21ProducerManifest.ps1`
    depends_on:   -
    parallel_ok:  no — these are the shared contracts for every later task.
    test_commands: JSON parse the policy; PowerShell-parse the three scripts; run local resolver
                  fixtures only after T21-R10-6 updates the frozen expectations.
    artifact_updates: none
    exit_criteria: Policy has three least-privilege caller rows, all selectable roles require
                  completed-success, v2.1 bindings and the frozen attestation pin are exact, all
                  stages remain disabled, explicit checkout-root validation preserves trusted
                  bundle precedence, and resolver/manifest signatures implement
                  `ConsumerRunId`, discovery mode, temporal ordering, and actual producer
                  `head_sha` binding. No compatibility or enablement path is added.
    status:       DONE — policy/root/resolver/producer identity primitives implemented; policy JSON and three PowerShell files parse successfully.

T21-R10-2  Implement prepared-input and authorization v2.1 semantics
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Move Production change-record ownership to completed CTO authorization and bind
                  exact completed `R/P/I/C/M` provenance through every semantic validator.
    inputs:       T21-R10-1 frozen policy/resolver contract; Architecture v2.1 field and artifact
                  contracts; R9 semantic/freshness/actor/expiry controls.
    files:        `HusayniaSite/eng/promotion/New-StageOperationInputBundle.ps1`;
                  `HusayniaSite/eng/promotion/Test-StageOperationInputBundle.ps1`;
                  `HusayniaSite/eng/promotion/StageOperationInput.Common.ps1`;
                  `HusayniaSite/eng/promotion/New-StageOperationReport.ps1`;
                  `HusayniaSite/eng/promotion/New-CtoAuthorizationRecord.ps1`;
                  `HusayniaSite/eng/promotion/Test-CtoAuthorizationRecord.ps1`;
                  `HusayniaSite/eng/promotion/New-MigrationApplyAuthorization.ps1`;
                  `HusayniaSite/eng/promotion/Test-TrustedProtectedOperationInputs.ps1`
    depends_on:   T21-R10-1
    parallel_ok:  no — the writers and validators exchange one breaking internal schema.
    test_commands: PowerShell parser over exactly the eight owned files; focused causal suites are
                  updated and executed in T21-R10-6.
    artifact_updates: none
    exit_criteria: Production prepared inputs are inert and contain the exact authorization
                  request but no change-record success evidence; CTO produces and binds the checked
                  change record only after completed `P/I`; migration and StageOperations require
                  exact prepared-input bindings and Production CTO linkage. Missing, swapped,
                  cross-release, cross-stage, wrong approval, actor, expiry, or digest bindings
                  reject before the preserved forbidden boundary.
    status:       DONE — prepared-input, CTO, migration, and StageOperations v2.1 scripts implemented; all eight owned PowerShell files parse successfully.

T21-R10-3  Wire producer, authorization, and trusted reusable workflows
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Apply the frozen run selectors, temporal resolver calls, v2.1 ownership,
                  binary-safe download, pins, roots, and pre-OIDC ordering to producer and
                  authorization workflows.
    inputs:       T21-R10-1 and T21-R10-2 interfaces; Architecture binary/pin/root contracts.
    files:        `HusayniaSite/pipelines/github/stage-operation-inputs.yml`;
                  `HusayniaSite/pipelines/github/cto-authorization-record.yml`;
                  `HusayniaSite/pipelines/github/migration-apply-authorization.yml`;
                  `HusayniaSite/pipelines/github/trusted-protected-operations.yml`
    depends_on:   T21-R10-2
    parallel_ok:  no — all four workflows exchange the same selectors and producer identities.
    test_commands: JSON parse the four definitions; structurally enumerate inputs, permissions,
                  `run` shells, resolver calls, action pins, and pre-OIDC ordering.
    artifact_updates: none
    exit_criteria: Prepared inputs consume only completed `R`; CTO and migration consume completed
                  `R/P/I` and Production `C` as applicable; every resolver call passes the current
                  consumer run ID; the trusted workflow uses native binary redirect with strict
                  cleanup; all checkout jobs use the canonical child root; pins are exact; four
                  and only four producer attestations remain; immutable bundle execution and the
                  forbidden boundary remain equal or stricter.
    status:       DONE — four producer/authorization/trusted workflows rewired; JSON parse, ConsumerRunId enumeration, exact binary body, one login pin, and four attestation pins verified.

T21-R10-4  Split top-level lifecycle runs and add the automatic coordinator
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Make release, each producer mode, and StageOperations separate top-level runs,
                  with one idempotent nonprivileged automatic coordinator and no Production
                  automation.
    inputs:       T21-R10-3 workflow-call contracts; frozen lifecycle and coordinator interface.
    files:        `HusayniaSite/pipelines/github/release-build-and-nonproduction.yml`;
                  `HusayniaSite/pipelines/github/automatic-nonproduction-orchestration.yml` (new);
                  `HusayniaSite/pipelines/github/operation-evidence-producer.yml`;
                  `HusayniaSite/pipelines/github/production-operation-evidence.yml`;
                  `HusayniaSite/pipelines/github/production-promotion.yml`;
                  `HusayniaSite/pipelines/github/stage-evidence-intake.yml`;
                  `HusayniaSite/pipelines/github/pr-validation.yml`
    depends_on:   T21-R10-3
    parallel_ok:  no — the coordinator and wrappers share run-name, input, predecessor, and
                  selector contracts, and partial installation is forbidden.
    test_commands: JSON parse all eleven workflow definitions after the new file is added; inspect
                  exact coordinator event allowlist/permissions/correlation and wrapper
                  mode/input exclusivity.
    artifact_updates: none
    exit_criteria: Release has only the C6 build; each wrapper run executes exactly one mode;
                  automatic Development/Staging and manual Production match the frozen completed
                  graph; same/current run selectors are absent; coordinator dispatch is
                  deterministic, bounded, duplicate-fail-closed, cycle-free, and protected by
                  false policy/variables; Production remains separate/manual/disabled; every
                  PowerShell body uses `pwsh`.
    status:       DONE — release-only R, one-mode nonproduction/Production wrappers, 11th nonprivileged coordinator, explicit pwsh/root contracts, and disabled forbidden stubs implemented; all 11 JSON definitions parse and lifecycle static checks pass.

T21-R10-5  Refresh the immutable protected execution closure
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Include and hash the final changed protected policy/scripts without adding
                  workflows or post-login checkout content to the trusted bundle.
    inputs:       Final outputs of T21-R10-1 through T21-R10-4; unchanged R9 canonicalization,
                  read-only extraction, and entrypoint rules.
    files:        `HusayniaSite/eng/artifact/New-ProtectedExecutionBundle.ps1`;
                  `HusayniaSite/eng/artifact/Test-ProtectedExecutionBundle.ps1`
    depends_on:   T21-R10-4
    parallel_ok:  no — bundle hashes must be computed from the final protected file contents.
    test_commands: Build only a temporary protected-execution-bundle fixture and validate it with
                  `Test-ProtectedExecutionBundle.ps1`; this is not an actual C6 build.
    artifact_updates: none
    exit_criteria: The protected closure and entrypoints contain every changed privileged script
                  exactly once, canonical hashes validate, policy strips repository-only hash
                  metadata as before, and neither a workflow file nor checkout-root execution is
                  introduced into the post-login bundle.
    status:       DONE — protected closure refreshed with New-CtoAuthorizationRecord and v2.1/three-caller policy checks; temporary bundle validated twice with 43 entries.

T21-R10-6  Implement the R10 static and causal regression matrix
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Replace R9 same-run expectations with production-shaped completed-run,
                  binary, pin, root, coordinator, v2.1, and preservation assertions.
    inputs:       Final implementation from T21-R10-1 through T21-R10-5; Architecture test matrix;
                  all R9 negative tests, which may be strengthened but not removed.
    files:        `HusayniaSite/eng/ci/Test-PipelineDefinitions.ps1`;
                  `HusayniaSite/eng/test/Test-CtoAuthorizationProvenance.ps1`;
                  `HusayniaSite/eng/test/Test-GitHubArtifactProvenance.ps1`;
                  `HusayniaSite/eng/test/Test-MigrationAuthorizationProvenance.ps1`;
                  `HusayniaSite/eng/test/Test-StageOperationInputProducer.ps1`;
                  `HusayniaSite/eng/test/Test-StageOperationInputBundle.ps1`;
                  `HusayniaSite/eng/test/Test-PreflightPolicyBoundary.ps1`;
                  `HusayniaSite/eng/test/Test-ProtectedExecutionBundle.ps1`;
                  `HusayniaSite/eng/test/Test-TrustedStageOperationsSemantics.ps1`;
                  `HusayniaSite/eng/test/Invoke-T21Validation.ps1`
    depends_on:   T21-R10-5
    parallel_ok:  no — these tests assert the final shared graph and exact file/action counts.
    test_commands: Run each owned focused suite directly, then
                  `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1` and
                  `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot
                  (Get-Location) -ContractOnly`.
    artifact_updates: none
    exit_criteria: Fixtures execute the exact checked-in binary/coordinator/root bodies and cover
                  valid completed prior runs plus CLI/missing/empty/oversize/digest failure,
                  tag/branch/zero/nonexistent/inconsistent pins, permission/producer broadening,
                  wrong root, in-progress/failed/cancelled/missing/current/future runs, duplicate
                  dispatch, cross-stage/release/caller/signer bindings, and no-rebuild/R9
                  regressions. Every rejection proves zero prohibited side effects; no test is
                  weakened, skipped, or success-normalized.
    status:       DONE — R10/R9 static and causal matrix implemented; ContractOnly harness passed 11/11 with focused assertion totals 34+15+34+15+8+16+13+15+14+12 and secret scan 83 files/0 findings.

T21-R10-7  Run local cached validation and publish implementation evidence
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Execute the complete no-install/no-restore validation set, document the R10 graph,
                  and record reproducible implementation evidence without self-approving a gate.
    inputs:       T21-R10-6 final source/tests; existing cached tools only; prior R9 evidence for
                  the fail-closed official NuGet audit outage.
    files:        `HusayniaSite/pipelines/README.md`;
                  `.ai-org/missions/2026-08-20-husaynia-t21/test-results.md`
    depends_on:   T21-R10-6
    parallel_ok:  no — documentation and evidence must describe one validated final revision.
    test_commands:
                  `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-StageOperationInputProducer.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-StageOperationInputBundle.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1`;
                  `pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1`;
                  unchanged R9 fence and HTTP suites;
                  `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot
                  (Get-Location) -ContractOnly`;
                  `pwsh -NoProfile -File .\eng\test\Test-T21ValidationExitCodes.ps1`;
                  JSON parse over `pipelines/**/*.json` and `pipelines/github/*.yml`;
                  PowerShell parse over `eng/**/*.ps1`;
                  `Invoke-WorkflowSchemaValidation.ps1 -ActionlintPath <pre-existing cached
                  actionlint.exe>` only when that binary already exists;
                  owned-file secret scan including untracked files.
    artifact_updates: Append an R10 implementation/self-validation section to `test-results.md`
                  and update only the R10 graph/install-gate section of `pipelines/README.md`;
                  preserve prior evidence/history.
    exit_criteria: All executable cached/local checks report command, exit code, assertion/file
                  counts, and zero failures. A missing cached actionlint is BLOCKED, not installed.
                  No restore is run; the prior official `NU1900` audit outage remains an explicit
                  fail-closed blocker and is never waived. Required zero-operation counters and
                  `actual C6 builds=0` are recorded. Independent gates remain pending.
    status:       DONE — local/cached evidence published: focused R9/R10 suites green, harness 11/11 in both modes, actionlint 11, Bicep 19+31+12, PowerShell 66, JSON 14, secret scan 83/0, exact pins/root/binary fixtures pass; no restore/install/live operation.

T21-R10-GT  Independently validate R10 tests and acceptance criteria
    owner:        cto-engineering-org:test-engineer
    objective:    Independently execute the frozen cached/local matrix and map real results to all
                  20 R10 acceptance criteria and R9 preservation requirements.
    inputs:       Frozen requirements/DoD/architecture/ADRs/task plan; T21-R10-7 evidence only as a
                  command index, never as proof.
    files:        `.ai-org/missions/2026-08-20-husaynia-t21/test-results.md`
    depends_on:   T21-R10-7
    parallel_ok:  yes — parallel only with T21-R10-GS and T21-R10-GR; source is read-only.
    test_commands: Independently rerun the T21-R10-7 commands with existing tools and no restore,
                  install, dispatch, authentication, deployment, or mutation.
    artifact_updates: Append a clearly labeled independent R10 test-gate section; preserve all
                  implementation and historical evidence.
    exit_criteria: Report commands, exit codes, counts, acceptance mapping, and operation counters.
                  Any failed assertion is FAIL. Missing cached tooling or official audit
                  connectivity is BLOCKED, never normalized to PASS.
    status:       PENDING

T21-R10-GS  Independently security-review the R10 trust and orchestration changes
    owner:        cto-engineering-org:security-engineer
    objective:    Threat-model and audit coordinator authority, temporal provenance, binary
                  retrieval, roots, action pins, authorization cross-bindings, and preservation
                  of the pre-OIDC/zero-deployment boundary.
    inputs:       Frozen artifacts and complete T21-R10 implementation/evidence.
    files:        `.ai-org/missions/2026-08-20-husaynia-t21/security-review.md`
    depends_on:   T21-R10-7
    parallel_ok:  yes — read-only source review with a disjoint evidence artifact.
    test_commands: Read-only static/fixture inspection as needed; no live GitHub/Azure operation.
    artifact_updates: Append/replace only the R10 security-review section; preserve R4-R9 history.
    exit_criteria: APPROVED only with zero unresolved Critical/High findings and evidence that
                  `actions:write` is coordinator-only, no cycle/duplicate escalation exists,
                  pins/permissions are exact, and all failure paths stop before prohibited effects.
    status:       PENDING

T21-R10-GR  Independently code-review the complete R10 change set
    owner:        cto-engineering-org:code-reviewer
    objective:    Review correctness, maintainability, interface adherence, failure handling,
                  atomic R9 preservation, and test adequacy across the complete final source.
    inputs:       Frozen artifacts; T21-R10-7 final implementation and evidence.
    files:        `.ai-org/missions/2026-08-20-husaynia-t21/code-review.md`
    depends_on:   T21-R10-7
    parallel_ok:  yes — read-only source review with a disjoint evidence artifact.
    test_commands: Read-only inspection and reproduction commands only; no source edit.
    artifact_updates: Append/replace only the R10 code-review section; preserve R4-R9 history.
    exit_criteria: Return APPROVED or CHANGES_REQUIRED with exact file/line evidence. Any current,
                  future, same-run, root, pin, permission, rebuild, or forbidden-boundary defect is
                  blocking.
    status:       PENDING

T21-R10-J  Judge the complete R10 Definition of Done
    owner:        cto-engineering-org:engineering-judge
    objective:    Decide whether every frozen R10 requirement and DoD item is proven by independent
                  evidence rather than implementation claims.
    inputs:       T21-R10-GT, T21-R10-GS, and T21-R10-GR artifacts; frozen mission artifacts; final
                  source and operation counters.
    files:        `.ai-org/missions/2026-08-20-husaynia-t21/final-verdict.md`
    depends_on:   T21-R10-GT, T21-R10-GS, T21-R10-GR
    parallel_ok:  no — judgment starts only after all three independent gates finish.
    test_commands: Evidence verification and selective read-only reproduction only.
    artifact_updates: Append/replace only the R10 verdict section; preserve prior verdict history.
    exit_criteria: APPROVED only if all 18 requirements, 20 acceptance criteria, action-pin
                  existence evidence, R9 preservation, independent gates, audit disposition, and
                  zero-operation counters are proven. Otherwise REJECTED/BLOCKED with exact failed
                  criterion and required rework.
    status:       PENDING

### Execution waves and collision decision

`wave 1: T21-R10-1 -> wave 2: T21-R10-2 -> wave 3: T21-R10-3 -> wave 4:
T21-R10-4 -> wave 5: T21-R10-5 -> wave 6: T21-R10-6 -> wave 7: T21-R10-7 ->
wave 8: T21-R10-GT, T21-R10-GS, T21-R10-GR (parallel) -> wave 9: T21-R10-J`

**No implementation fan-out is permitted.** Some file groups are disjoint, but the policy schema,
resolver signature, producer manifests, authorization records, wrapper inputs, coordinator
correlation, protected-bundle hashes, and exact-count tests form one atomic internal contract.
Parallel developers could independently choose incompatible names/shapes or hash intermediate
content, and all target files are part of the existing untracked working tree. One
`cto-engineering-org:devops-sre-specialist` therefore owns waves 1-7 serially. The only safe
parallelism is wave 8: all three agents read the same frozen source and write disjoint mission
artifacts.

### Rework routing

- A T21-R10-GT, T21-R10-GS, or T21-R10-GR failure creates a new serial
  `T21-R10-RW<n>` task owned by `cto-engineering-org:devops-sre-specialist`, limited to the exact
  failing source/test files and carrying the gate's raw evidence and root cause.
- Any source or test rework invalidates T21-R10-7 and all three independent gates; rerun the local
  validation/evidence wave, then the complete parallel gate fan-out, then judgment.
- Reviewers do not repair source during their gate. Missing coverage routes to the same rework
  stream rather than permitting an independent test agent to change source while other reviews
  are running.
- An implementation naming/ownership disagreement is resolved by this Tech Lead against the frozen
  contracts above. A design contradiction returns to the Architect; a product/requirement
  ambiguity returns to the VP/CTO. Critical/High security exceptions, deployment, external
  installation, authentication, commit/push/history, or a scope increase require CTO action.
- Three repetitions of the same gate failure without new evidence make the mission BLOCKED rather
  than authorizing another identical retry.

## T21-R10-R1 enabled-path review remediation — 2026-08-27

T21-R10-R1  Remediate three independent enabled-path correctness findings
    owner:        cto-engineering-org:devops-sre-specialist
    status:       DONE — local implementation/self-validation only
    work:         Raw string coordinator dispatch; immutable pre-token enablement plus
                  post-OIDC/login readonly Preflight producer; protected stage/CTO environment
                  secret mapping limited to report-producing steps; causal regressions and docs.
    evidence:     Exact pre-fix `-F` mutation exited 1 with 11/12 passing and decimal IDs captured
                  as JSON numbers. Pre-fix ordering/token-contract mutation exited 1 with 5/12
                  passing. Final direct named suites passed 200/200; both T21 harness modes
                  12/12; exit-code contract 2/2; cached actionlint 11 workflows; cached Bicep
                  19/19, 31/31, 12/12; PowerShell parse 67/67; JSON-compatible parse 15/15;
                  secret scan 84 files/0 findings.
    counters:     authentication=0; workflow dispatches=0; deployments=0; success receipts=0;
                  database calls/mutations=0; cloud resource/secret mutations=0; installs=0;
                  package restores=0; commit/push/history operations=0; actual C6 builds=0.

T21-R10-GT / T21-R10-GS / T21-R10-GR / T21-R10-J remain PENDING. This rework does not claim
independent test, security, review, or judgment approval. The retained official audit disposition
is exit 1 with eleven fail-closed NU1900 warning-as-error failures; it was not rerun because the
authorized local implementation pass forbids package restore.

## T21-R10-R2 no-install SQL execution replan — 2026-08-27

`reworkCount` remains **10**. R10-R2 is the architecture correction for the current review, not an
increment or permission to widen scope.

T21-R10-R2-1  Bind the existing SqlClient runtime into the protected C6 closure
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Pass `app/Husaynia.Web.zip` to the protected-bundle builder; extract only the
                  exact managed Microsoft.Data.SqlClient 6.1.1 Unix dependency closure; add
                  protected manifest v1.1 application-hash/runtime bindings; validate exact
                  entries, hashes, assembly identities, and read-only state.
    files:        `eng/artifact/New-ReleaseArtifact.ps1`,
                  `eng/artifact/Test-ReleaseArtifact.ps1`,
                  `eng/artifact/New-ProtectedExecutionBundle.ps1`,
                  `eng/artifact/Test-ProtectedExecutionBundle.ps1`.
    depends_on:   none
    exit_criteria: No package/project/action/service/install/download is added; release-manifest
                  schema remains 1.0.0; unsafe, extra, missing, version-drifted, native-Unix, or
                  app-hash-mismatched runtime content fails C6 validation.
    status:       DONE — protected manifest v1.1 contains the exact 19-assembly managed Unix
                  closure, application hash binding, exact hashes/identities, and read-only
                  validation. Synthetic closure/tamper/drift tests passed 30/30.

T21-R10-R2-2  Replace sqlcmd and preserve distinct run commits
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Add the manifest-restricted collectible SqlClient loader, bounded Azure CLI
                  access-token provider, typed SQL executor, Preflight/lease integration, stable
                  redaction/failure semantics, and local-only seams; remove only the
                  producer-equals-release comparison.
    files:        `eng/artifact/migrations/bundle/Migration.Common.ps1`,
                  `eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1`,
                  `pipelines/github/trusted-protected-operations.yml`,
                  `pipelines/config/promotion-policy.json`.
    depends_on:   T21-R10-R2-1
    exit_criteria: Enabled Preflight validates runtime before OIDC, uses readonly Azure login then
                  in-process SqlClient with no token persistence/logging, retains the fixed
                  SELECT-only query, and emits evidence only after exact typed validation.
                  Lease SQL uses the same adapter with no retry; StageOperations still stops at
                  `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` before OIDC. Distinct protected-main
                  producer/release commits pass while each remains independently verified.
    status:       DONE — manifest-restricted collectible SqlClient adapter, bounded direct Azure
                  CLI token acquisition, typed Preflight/lease execution, local-only seams, and
                  distinct producer/release commit handling implemented.

T21-R10-R2-3  Add causal no-auth/no-database regressions
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Add deterministic closure, loader, token redaction, typed Preflight/lease,
                  distinct-commit, seam-blocking, workflow-ordering, and no-install regressions.
    files:        `eng/test/Test-ProtectedExecutionBundle.ps1`,
                  `eng/test/Test-PreflightPolicyBoundary.ps1`,
                  `eng/test/Test-T21R10EnabledPathRegressions.ps1`,
                  `eng/test/Test-MigrationStageLeaseFence.ps1`,
                  new `eng/test/Test-MigrationSqlClientExecution.ps1`,
                  `eng/ci/Test-PipelineDefinitions.ps1`,
                  `eng/test/Invoke-T21Validation.ps1`.
    depends_on:   T21-R10-R2-2
    exit_criteria: Tests use injected local-only providers/executors with no live auth or DB;
                  `GITHUB_ACTIONS=true` blocks every seam before invocation; negative cases cover
                  closure tamper/drift, token failures/leakage, result shape/value failures,
                  ambiguous lease failure/no retry, and independent commit mismatches.
    status:       DONE — `Test-MigrationSqlClientExecution.ps1` and retained R9/R10 causal suites
                  cover closure, load restriction, platform compatibility, token redaction,
                  typed result failures, no retry, Actions seam rejection, workflow ordering, and
                  distinct commits.

T21-R10-R2-4  Rebuild internal hashes and run local validation
    owner:        cto-engineering-org:devops-sre-specialist
    objective:    Regenerate only the two migration orchestration hashes and all protected-closure
                  expectations, then run the complete cached/local R9/R10/R10-R2 matrix.
    files:        Same implementation/test files above plus implementation evidence appended to
                  `.ai-org/missions/2026-08-20-husaynia-t21/test-results.md`.
    depends_on:   T21-R10-R2-3
    exit_criteria: Zero failed assertions; no install/restore/auth/dispatch/database/deployment/C6
                  build; PowerShell/JSON parse, cached actionlint/Bicep, secret scan,
                  `Test-PipelineDefinitions.ps1`, and both `Invoke-T21Validation.ps1` modes report
                  real counts. The locked NU1900 audit remains fail-closed.
    status:       DONE — final local/cached evidence: 246 focused assertions, both harness modes
                  13/13, exit tests 2/2, actionlint 11 workflows, Bicep 19+31+12, PowerShell 68,
                  JSON-compatible 15, secret scan 85/0, policy hashes 4/4, runtime supply scan
                  3 files/0 hits; all live-operation/install/restore/C6-build counters zero.

T21-R10-R2-GT  Independently test the R10-R2 implementation
    owner:        cto-engineering-org:test-engineer
    depends_on:   T21-R10-R2-4
    parallel_ok:  yes — with R2-GS and R2-GR
    exit_criteria: Independently reproduce the causal matrix and operation counters with zero live
                  auth/database/install/restore activity.
    status:       PENDING

T21-R10-R2-GS  Independently security-review the runtime and token boundary
    owner:        cto-engineering-org:security-engineer
    depends_on:   T21-R10-R2-4
    parallel_ok:  yes — with R2-GT and R2-GR
    exit_criteria: APPROVED only if the executable closure remains exact/read-only/app-hash-bound,
                  tokens cannot be logged/persisted, local seams cannot activate in Actions, and
                  every R9/R10 pre-OIDC/provenance/fence boundary is equal or stricter.
    status:       PENDING

T21-R10-R2-GR  Independently code-review R10-R2
    owner:        cto-engineering-org:code-reviewer
    depends_on:   T21-R10-R2-4
    parallel_ok:  yes — with R2-GT and R2-GS
    exit_criteria: APPROVED only if enabled ubuntu Preflight is executable without sqlcmd/install,
                  runtime/hash/cleanup/error contracts are correct, and producer/release commits
                  remain distinct and independently bound.
    status:       PENDING

T21-R10-R2-J  Judge the complete R10-R2 Definition of Done
    owner:        cto-engineering-org:engineering-judge
    depends_on:   T21-R10-R2-GT, T21-R10-R2-GS, T21-R10-R2-GR
    parallel_ok:  no
    exit_criteria: APPROVED only after all R10-R2 exit criteria and every retained R9/R10 boundary
                  have independent evidence; otherwise route exact rework without changing
                  `reworkCount` unless the VP explicitly changes mission state.
    status:       PENDING

Execution order:
`R2-1 -> R2-2 -> R2-3 -> R2-4 -> (R2-GT || R2-GS || R2-GR) -> R2-J`.
Implementation remains serial because app/runtime hashes, protected manifest shape, loader
contract, workflow ordering, and exact-count tests are one atomic internal contract.

Implementation completed locally on 2026-08-27 without incrementing `reworkCount`. Independent
R2-GT/R2-GS/R2-GR and R2-J remain **PENDING**: two dispatch attempts were rejected by the
orchestration environment with `Maximum sub-agent depth of 4 reached`, so no independent approval
is claimed.

### T21-R10-R2 independent-gate remediation — 2026-08-27

`reworkCount` remains **10**.

T21-R10-R2-RW1  Remediate all supplied R2 independent findings
    owner:        developer
    depends_on:   T21-R10-R2-4 and supplied R2-GT/R2-GS/R2-GR failing evidence
    files:        only `HusayniaSite/pipelines/**`, `HusayniaSite/eng/**`, and root T21 artifacts
    result:       DONE — provenance requires policy v2.1; the Apply DLL resolves from validated
                  C6; exact Unix SqlClient path/RID/package/version/full identity is manifest and
                  ALC-bound; local lease/token/SQL seams require explicit opt-in and reject in
                  Actions; timeout/nonzero and real protected producer/wrapper Preflight
                  regressions pass.
    evidence:     263/263 focused; both T21 harness modes 13/13; exit 2/2; actionlint 11;
                  Bicep 19+31+12; static policy 26; PowerShell parse 68; JSON parse 15;
                  secret scan 85/0; policy hashes and runtime install/download scan green.
    counters:     authentication=0; dispatches=0; deployments=0; receipts=0; database=0;
                  cloud/resource/secret mutations=0; installs=0; restores=0; actual C6 builds=0;
                  commit/push/history mutations=0.

T21-R10-R2-GT, T21-R10-R2-GS, and T21-R10-R2-GR are reset to **PENDING RERUN** against this
remediated source. T21-R10-R2-J remains **PENDING** and starts only after all three independent
gates finish. No gate is approved by this task.

### T21-R10-R2 final gate remediation — 2026-08-27

`reworkCount` remains **10**.

T21-R10-R2-RW2  Separate protected-bundle provenance from the Apply payload
    owner:        devops-sre-specialist
    depends_on:   supplied final R2-GR findings
    result:       DONE — current-policy Preflight no longer requires the unmaterialized Apply DLL;
                  all non-Preflight modes still require it before seams or SQL. Preflight evidence,
                  producer validation, and v2.1 authorization bind the fixed protected-execution
                  bundle path/SHA. The workflow sets read-only state on files and recursively
                  removes Linux write permission. Azure CLI timeout kills and waits.
    evidence:     266/266 focused; both harness modes 13/13; exit 2/2; actionlint 11;
                  Bicep 19+31+12; secret scan 85/0; no live-operation counters.

T21-R10-R2-GT  Independently test the R10-R2 implementation
    status:       PASS — repository suites and supplemental cached/static scans passed; retained
                  NU1900 remains fail-closed.

T21-R10-R2-GS  Independently security-review the runtime and token boundary
    status:       PASS — 0 Critical, 0 High.

T21-R10-R2-GR  Independently code-review R10-R2
    status:       APPROVED.

T21-R10-R2-J  Judge the complete R10-R2 Definition of Done
    status:       APPROVED — after the specialized judge dispatcher returned an OS path error, a
                  separate general-purpose agent acting only as an independent judge verified the
                  implementation, representative
                  source, all three independent gate results, exact `reworkCount=10`, and the
                  permitted local-only evidence. Live C6/Azure/SQL/OIDC remains out of scope.

### T21-R10-R3 terminating lease-guard remediation — 2026-08-27

`reworkCount` remains **10**. The prior R2 judgment is superseded by the fresh independent
code-review finding that transactional `RAISERROR` guards can return control and allow later
mutation or `COMMIT`.

T21-R10-R3-1  Make every transactional lease guard terminating
    owner:        devops-sre-specialist
    result:       DONE — replaced all 13 `RAISERROR` branches in AcquireLease, BeginMutation,
                  CompleteMutation, RenewLease, and ReleaseLease with semicolon-safe
                  `;THROW 51021` through `;THROW 51033`, preserving messages and success paths.

T21-R10-R3-2  Add causal/static regression coverage
    owner:        devops-sre-specialist
    result:       DONE — the lease suite rejects any returned `RAISERROR`, missing/duplicate/
                  non-semicolon-safe numbered guard, or guard positioned after subsequent work.
                  The local model also proves a stale release is rejected while the current
                  holder, fence, authorization, and unreleased state remain unchanged.

T21-R10-R3-3  Refresh protected policy integrity
    owner:        devops-sre-specialist
    result:       DONE — `Migration.Common.ps1` canonical policy SHA-256 is
                  `96f012509751c87235a612a044968152feec539a2c001dedcb9b3222e7284617`;
                  direct protected-bundle validation remains 38/38.

T21-R10-R3-4  Run local/cached validation
    owner:        devops-sre-specialist
    result:       DONE — affected checks 133/133; full focused matrix 269/269; both T21 harness
                  modes 13/13; exit tests 2/2; actionlint 11 workflows; Bicep 19/19 + 31/31 +
                  12/12 with zero static-plan operations; PowerShell parse 68/0; JSON-compatible
                  parse 15/0; secret scan 85 files/0 findings. All prohibited-operation counters
                  remained zero.

T21-R10-R3-GT  Independently test R3
    status:       PENDING

T21-R10-R3-GS  Independently security-review R3
    status:       PENDING

T21-R10-R3-GR  Independently code-review R3
    status:       PENDING

T21-R10-R3-J  Independently judge R3
    status:       PENDING — starts only after R3-GT, R3-GS, and R3-GR finish.

No independent gate is approved by this remediation task.

### T21-R10-R4 supported OIDC context claim remediation — 2026-08-27

`reworkCount` remains **10**. A fresh independent review proved GitHub's OIDC discovery contract
exposes `environment` but no standalone `context` claim, so the prior validator rejected every
enabled Preflight before Azure login.

T21-R10-R4-1  Correct the protected-context claim source
    owner:        developer
    result:       DONE — `Test-ExternalTrustMarker.ps1` now requires `environment`, assigns its
                  exact value to `$context`, compares it to caller-matrix `context`, and renders
                  the unchanged immutable custom `sub`. No issuer/audience/repository/ID/ref/
                  caller/reusable/external-marker check was removed.

T21-R10-R4-2  Add authoritative and causal regressions
    owner:        developer
    result:       DONE — protected-bundle tests statically require the exact supported claim set
                  and environment-derived `$context`; a valid JWT payload with environment and
                  no context passes, while missing/wrong environment, wrong rendered subject,
                  wrong caller, wrong reusable ref/SHA, and malformed caller SHA fail.

T21-R10-R4-3  Refresh protected closure and ADR
    owner:        developer
    result:       DONE — protected marker canonical SHA-256 is
                  `1c5d25af39bb837c8e170c98d487436804d677c94202be487f40440320d2a93e`;
                  the 38-file reviewed closure passes. ADR-019 records the discovery contract and
                  states that the discovery URL is documentation evidence only.

T21-R10-R4-4  Run local/cached validation
    owner:        developer
    result:       DONE — focused matrix 279/279; both harness modes 13/13; exit tests 2/2;
                  actionlint 11 workflows; Bicep 19/19 + 31/31 + 12/12 with zero plan mutations;
                  PowerShell parse 68/0; JSON-compatible parse 15/0; secret scan 85 files/0
                  findings. All prohibited-operation counters remained zero.

T21-R10-R4-GT  Independently test R4
    status:       PENDING

T21-R10-R4-GS  Independently security-review R4
    status:       PENDING

T21-R10-R4-GR  Independently code-review R4
    status:       PENDING

T21-R10-R4-J  Independently judge R4
    status:       PENDING — starts only after R4-GT, R4-GS, and R4-GR finish.

No independent gate is approved by this R4 implementation task.

### T21-R10-R5 exact external subject-set remediation — 2026-08-27

`reworkCount` remains **10**. Independent review proved
`[validDevelopmentSubject,'junk-1','junk-2']` passed the R4 count/uniqueness/current-membership
check while omitting the approved Staging and Production subjects.

T21-R10-R5-1  Enforce the rendered caller-matrix subject set
    owner:        developer
    result:       DONE — `Test-ExternalTrustMarker.ps1` renders one subject from each of the exact
                  three caller-matrix rows using the current immutable owner/repository IDs, each
                  row's exact context/workflow ref, and `ExpectedWorkflowRef`. Ordinal set equality
                  rejects missing, extra, duplicate, junk, and case-drifted values.

T21-R10-R5-2  Add causal exact-set regressions
    owner:        developer
    result:       DONE — the exact three-subject set passes. Current+junk, missing approved, extra,
                  duplicate, case-changed, wrong reusable, wrong caller, and wrong environment
                  marker sets fail. Existing current-token environment/caller/sub/reusable/SHA
                  regressions remain green.

T21-R10-R5-3  Refresh protected integrity policy
    owner:        developer
    result:       DONE — marker canonical SHA-256 is
                  `c7da97819c2a671d5350147423cec195ce54e1591078c76cd7db91243ff55963`;
                  the exact 38-file reviewed protected closure passes.

T21-R10-R5-4  Run local and cached validation
    owner:        developer
    result:       DONE — focused matrix 287/287; both T21 harness modes 13/13; exit tests 2/2;
                  actionlint 11 workflows; Bicep 19/19 + 31/31 + 12/12 with every supplied-stage
                  create/update/delete/replace count zero; PowerShell parse 68/0;
                  JSON-compatible parse 15/0; secret scan 85 files/0 findings. All prohibited
                  operation counters remained zero.

T21-R10-R5-GT  Independently test R5
    status:       PENDING

T21-R10-R5-GS  Independently security-review R5
    status:       PENDING

T21-R10-R5-GR  Independently code-review R5
    status:       PENDING

T21-R10-R5-J  Independently judge R5
    status:       PENDING — starts only after R5-GT, R5-GS, and R5-GR finish.

No independent gate is approved by this R5 implementation task.

### T21-R10-R6 step-scoped provenance token remediation — 2026-08-27

`reworkCount` remains **10**. Independent review proved the protected resolver runs in a separate
step from the C6 binary download, so the earlier step's `GITHUB_TOKEN` mapping was unavailable and
the enabled pre-OIDC provenance path failed before its first GitHub Actions API request.

T21-R10-R6-1  Scope the GitHub token to the provenance resolver
    owner:        developer
    result:       DONE — added exactly `${{ github.token }}` as `GITHUB_TOKEN` on the protected
                  pre-OIDC provenance-resolution step. The existing C6 API/download step retains
                  its required mapping; job scope and all later OIDC, login, producer, upload, and
                  attestation steps remain token-free.

T21-R10-R6-2  Add exact structural and causal regressions
    owner:        developer
    result:       DONE — workflow structure requires the exact mapping on only the two GitHub API
                  consumer steps, rejects job/run-body or unrelated/later-step exposure, and
                  retains every R10/R1-R5 ordering/control assertion. Direct resolver execution
                  with no token fails before its request seam; a mapped nonsecret token plus local
                  request/artifact/attestation fixture completes verified provenance.

T21-R10-R6-3  Preserve protected integrity
    owner:        developer
    result:       DONE — no protected-bundle source changed, so no protected hash was refreshed.
                  Canonical policy verification passed 38/38 with `changedIncluded=0`; the external
                  marker hash remains
                  `c7da97819c2a671d5350147423cec195ce54e1591078c76cd7db91243ff55963`.

T21-R10-R6-4  Run local and cached validation
    owner:        developer
    result:       DONE — focused matrix 290/290; both T21 harness modes 13/13; exit tests 2/2;
                  actionlint 11 workflows; Bicep 19/19 + 31/31 + 12/12 plus standalone static
                  26/26; PowerShell parse 68/0; JSON-compatible parse 15/0; secret scan 85 files/0
                  findings. All prohibited-operation counters remained zero.

T21-R10-R6-GT  Independently test R6
    status:       PENDING

T21-R10-R6-GS  Independently security-review R6
    status:       PENDING

T21-R10-R6-GR  Independently code-review R6
    status:       PENDING

T21-R10-R6-J  Independently judge R6
    status:       PENDING — starts only after R6-GT, R6-GS, and R6-GR finish.

No independent gate is approved by this R6 implementation task.

## T21-R10-R7 — checked-in evidence contract closure

T21-R10-R7-1  Centralize and resolve every external action pin
    owner:        devops-sre-specialist
    result:       DONE — added the exact five-action repository/tag/commit registry and a static
                  all-workflow pin validator with optional public read-only tag/peel verification.
                  Internal reusable-workflow zero-SHA sentinels remain installation-gated.

T21-R10-R7-2  Execute the exact binary CLI and checkout-root contracts
    owner:        devops-sre-specialist
    result:       DONE — the protected-bundle suite now invokes the installed `gh api --help`
                  parser without network and keeps its byte-integrity fixture. Pipeline contracts
                  execute all six exact checkout root-validation bodies against temporary valid
                  and wrong-root `Dreamer/HusayniaSite` layouts.

T21-R10-R7-3  Add the completed-run lifecycle state machine
    owner:        devops-sre-specialist
    result:       DONE — local exact-body fixtures prove `R -> P/I -> [protected M] -> S`,
                  completed-success temporal selection, checksum continuity, bounded failure stop,
                  no cycles, no fabricated Development receipt, and separate manual Production.

T21-R10-R7-4  Restore the full safe validation harness
    owner:        devops-sre-specialist
    result:       DONE — both harness modes now include action-pin and lifecycle contracts plus the
                  existing stage HTTP suite. The exit-code contract requires the complete 16/16
                  harness. Official NuGet audit strictness is checked statically and not rerun.

T21-R10-R7-GT  Independently test R7
    status:       DONE — independent safe execution passed 357/357 reported checks, including
                  pipeline 45/45, enabled path 12/12, lifecycle 12/12, provenance 36/36, both
                  harnesses 16/16, exit 2/2, and actionlint 11. No repository write or prohibited
                  operation was reported.

T21-R10-R7-GS  Independently security-review R7
    status:       DONE — PASS, Critical=0 and High=0 after the pagination/assertion remediation.

T21-R10-R7-GR  Independently code-review R7
    status:       DONE — APPROVED after removing the name-based bearer assertion override and
                  changing coordinator correlation discovery from the newest 100 runs to all
                  paginated runs with a checked-in page-two reuse regression.

T21-R10-R7-J  Independently judge R7
    status:       BLOCKED — repository source and independent gates pass, but mission completion
                  remains blocked by eleven fail-closed NU1900 errors, absent external
                  installation/provisioning, and the earlier restore/generated-write compliance
                  violation in an untracked workspace.

T21-R10-R7-RW1  Remediate independent review findings
    owner:        devops-sre-specialist
    result:       DONE — exact runtime bearer behavior remained unchanged and was proven from raw
                  source; the success-shaped assertion override was removed. Coordinator run
                  discovery is now paginated/slurped and fails closed. A 101-run fixture proves
                  idempotent reuse beyond page one.

The earlier delegated restore/generated-write compliance blocker remains historical and cannot be
rewritten as zero. R7 itself performed no authentication, workflow dispatch, deployment, success
receipt, database/cloud mutation, install, package restore, C6 build, commit, or push.

## T21-R11 bounded migration authorization dispatch fix — 2026-08-28

`reworkCount`: **11**

T21-R11  Validate completed migration authorization before coordinator dispatch
    owner:        devops-sre-specialist
    status:       DONE — local implementation/self-validation only
    scope:        `HusayniaSite/pipelines/**`, `HusayniaSite/eng/**`, `.config`, and this T21
                  mission/state only; no application or infrastructure change.
    implementation:
                  The nonprivileged completed-run coordinator no longer derives migration
                  dispatch bindings from the workflow title. It uses the existing resolver's
                  migration discovery mode to require one canonical Development/Staging artifact,
                  completed-success API run identity, immutable API digest, exact producer
                  manifest, and role-pinned attestation. It then resolves the exact release,
                  preflight, and prepared-input runs and invokes the existing StageOperations
                  semantic validator. Only its final immutable
                  `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` fence proves canonical
                  `AUTHORIZE` + `Apply` and exact stage/release/preflight/prepared/caller/reusable
                  bindings before dispatch is permitted.
    lifecycle:
                  Canonical APPLY produces exactly one simulated StageOperations dispatch.
                  DENY, malformed, missing, duplicate, replay, wrong context, producer failure,
                  resolver/API failure, and Production scope produce zero. An event trace proves
                  authorization validation strictly precedes dispatch.
    preservation:
                  R10 `R -> P/I -> M -> S` graph, child-run idempotency, immutable checksum flow,
                  no rebuild, disabled stages, manual-disabled Production, C6 protected hash
                  closure, deployment-forbidden fence, and all trust controls remain intact.
    counters:     authentication=0; actual workflow dispatches=0; deployments=0; receipts=0;
                  database/cloud/resource/secret mutations=0; installs=0; package restores=0;
                  actual C6 builds=0; commits/push/history operations=0.

T21-R11-GT  Independent R11 test gate
    owner:        test-engineer
    status:       PENDING

T21-R11-GS  Independent R11 security gate
    owner:        security-engineer
    status:       PENDING

T21-R11-GR  Independent R11 code-review gate
    owner:        code-reviewer
    status:       PENDING

T21-R11-J  Independent R11 judgment
    owner:        engineering-judge
    depends_on:   T21-R11-GT, T21-R11-GS, T21-R11-GR
    status:       PENDING

Local evidence is recorded in `test-results.md`. No independent approval or mission completion is
claimed.
