# T21-R3 independent security review

STATUS:          FAIL

SUMMARY:
- The immutable-C6 bundle path materially remediates the former mutable post-login repository execution and its local tamper tests pass.
- One repository-side High remains: the pinned reusable workflow does not bind a protected Azure identity to an approved *calling* workflow.  Any newly added caller can invoke the approved reusable workflow and receive the same externally trusted `job_workflow_ref` identity.
- The external GitHub customized-sub/Azure exact-subject FIC installation and replacement of the all-zero SHA sentinel are still prerequisites.  They are not counted as repository vulnerabilities because the present sentinel/marker design fails closed; they would not, however, fix the unapproved-caller High.

WORK_COMPLETED:
- Threat-modeled candidate repository workflow changes, caller-supplied artifact run IDs, C6/bundle inputs, OIDC claim boundaries, post-login bundle execution, evidence/replay controls, disabled stages, and migration apply fencing.
- Reviewed the current allowed implementation in `pipelines/**`, `eng/**`, and `.config/dotnet-tools.json`, plus the prior review, architecture, decisions, definition of done, and final verdict.
- Locally reproduced protected-bundle determinism, validation, extraction, and negative tamper/trust-marker cases.  No authentication, deployment, database, or resource mutation was performed.

EVIDENCE:
- `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1` — `SUMMARY total=368 passed=368 failed=0`.
- `pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1` — process exit `0`; `total=20 passed=20 failed=0`.  This exercised outer-hash, manifest, `SHA256SUMS`, extraction TOCTOU, traversal, archive-alias, and CRLF rejection; exact/absent/mismatched external-marker behavior; and missing preflight/deployment/prepared-input fail-before-OIDC behavior.
- Static source evidence: `pipelines/github/trusted-protected-operations.yml:4-86,90-100,141-181,217-250`; `eng/promotion/Test-ExternalTrustMarker.ps1:31-60`; `eng/promotion/Test-TrustedProtectedOperationInputs.ps1:54-112`; `eng/common/Release.Common.ps1:629-648`; `eng/ci/Test-PipelineDefinitions.ps1:540-565`.
- Mutation counts: authentication `0`; deployments `0`; database mutations `0`; resource mutations `0`.

ARTIFACTS:
- `.ai-org/missions/2026-08-20-husaynia-t21/security-review.md`

FINDINGS:

[HIGH] Trusted reusable workflow accepts an unapproved caller
Location:     `pipelines/github/trusted-protected-operations.yml:4-86,90-100,141-181,217-250`; `eng/promotion/Test-ExternalTrustMarker.ps1:31-60`; `eng/ci/Test-PipelineDefinitions.ps1:540-565`
Issue:        `trusted-protected-operations.yml` is callable through `workflow_call` and grants `id-token: write` to its protected job, but neither the workflow nor the external-trust-marker validation binds the OIDC identity to an approved immutable caller-workflow identity.  The required subject/claims validate repository, protected context/environment, reusable `job_workflow_ref`, and reusable SHA only.  Those values remain the same for every caller of this reusable workflow.  Caller-provided artifact run IDs are then downloaded, while the local checks validate the downloaded content and self-declared metadata rather than an authoritative GitHub provenance relationship to an approved caller.
Attack path:  After the external exact-subject FIC is provisioned and a stage is enabled, an actor able to land a repository workflow on the protected branch adds a thin workflow that calls the approved reusable-workflow SHA, targets the protected environment, and supplies selected or attacker-produced C6/evidence artifact run IDs.  The reusable job obtains an OIDC token because its `job_workflow_ref` and SHA match the FIC exactly; the unapproved initiating caller is absent from the trust decision.  The job then logs in and runs the accepted immutable bundle, including apply-capable protected operations when its inputs permit them.
Impact:       Repository workflow control becomes cloud-protected-stage execution.  The actor can cause unauthorized migration/deployment orchestration or protected API/database actions under the Azure identity, defeating the repository-side trust-path remediation.
Fix:          Bind authorization outside candidate-controlled repository code to both (1) an immutable approved caller-workflow identity and (2) the pinned reusable-workflow identity, and independently authenticate artifact provenance against the authoritative GitHub run/caller identity before login.  A repo-owned allowlist or validator is not an adequate substitute.
Confidence:   High

RISKS:
- Bundle canonicalization and post-login repository-read protections were not found to create a separate Critical/High issue: the local negative cases reject tampering, alias/traversal, CRLF, and extraction TOCTOU; the bundle recheck occurs before use.  This defense does not authorize the caller.
- The stage-disabled, preflight/apply, and durable lease/fencing static controls remain fail-closed in the reviewed path, but an unapproved caller obtaining the protected identity bypasses the trust assumption beneath those controls.

BLOCKERS:
- **External prerequisite, not a repository vulnerability:** GitHub immutable/customized OIDC subject configuration, Azure exact-subject FIC provisioning, and a real final pinned reusable-workflow SHA are not present.  The all-zero SHA sentinel and missing/mismatched external trust marker fail closed, so no repository fallback was identified.  These items must be completed before protected execution can operate.
- The High caller-identity authorization defect above must be remediated before any external trust prerequisite is enabled for an operational stage.

NEXT_ACTION:
- Redesign the external trust anchor and pre-login validation to approve the immutable caller workflow as well as the reusable workflow; bind artifact provenance to that authorized caller.  Keep the sentinel and all stages disabled until this is independently re-reviewed.  Then complete the external OIDC/FIC provisioning and final-SHA rotation as a separate controlled prerequisite.

SECURITY RESULT

Scope:           T21-R3 repository-side trust remediation: mission evidence, `pipelines/**`, `eng/**`, and `.config/dotnet-tools.json`
Critical: 0   High: 1   Medium: 0   Low: 0   Informational: 0

Blocking findings:
- [HIGH] Trusted reusable workflow accepts an unapproved caller.

All findings:
- See the detailed finding above.

Conclusion: FAIL

---

# T21-R10 independent security review — 2026-08-27

SECURITY RESULT

Scope:           T21-R10 GitHub Actions/PowerShell trust-boundary changes in `HusayniaSite/pipelines/**`, `HusayniaSite/eng/**`, `.config/dotnet-tools.json`, and the frozen R10 mission artifacts
Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings:
None

All findings:
None

Conclusion: PASS

STATUS:          PASS

SUMMARY:
- Independently threat-modeled and audited the R10 binary C6 download, action pins, canonical checkout root, completed-run coordinator, GitHub API/Sigstore resolver, v2.1 prepared-input/CTO/migration bindings, immutable protected bundle, stable pre-OIDC forbidden boundary, and durable migration fence.
- No exploitable repository weakness was found. Every reviewed R9 control is retained or strengthened, and there are zero unresolved Critical or High findings.
- The authored workflows remain uninstalled and fail closed: six internal reusable-workflow references retain all-zero installation sentinels, all three stages have `deploymentEnabled=false`, Production remains manual, and `deployment-evidence` remains forbidden.

WORK_COMPLETED:
- Threat model:
  - **Entry points:** `workflow_run` event fields; manual wrapper/authorization inputs and run names; protected repository/environment variables; GitHub run/artifact API responses; downloaded C6 and producer artifacts; CTO/change-record responses; actor ID allowlists.
  - **Trust boundaries:** GitHub event -> coordinator `actions:write`; GitHub API/Sigstore -> resolver-created provenance; binary artifact bytes -> runner filesystem/extraction; checkout source -> attested producer artifacts; immutable protected bundle -> OIDC/Azure login; CTO/migration records -> Apply authorization and SQL fence.
  - **Assets:** coordinator `GITHUB_TOKEN`; OIDC/Azure stage identities; immutable C6/application and protected-bundle hashes; attestation identities; Production CTO/change-record approval; migration authorization; database mutation exclusivity; evidence/receipt integrity.
  - **Dangerous sinks:** workflow-dispatch POSTs; native binary redirection, ZIP parsing/extraction, and environment-file writes; OIDC token request and `azure/login`; artifact publication/attestation; protected service reads; migration execution and SQL lease mutation.
  - **Actors considered:** a user able to submit workflow-dispatch inputs; a contributor controlling an unprotected ref; a malicious/compromised artifact producer; a replaying authorized user; and malformed API/artifact content. GitHub Actions API and Sigstore are the authoritative provenance services.
- Verified the native `gh api` endpoint is passed as one PowerShell argument, not re-evaluated as code; PowerShell 7.4+ binary redirection preserves bytes; CLI/missing/empty/oversize/digest failures stop before ZIP open, trusted markers, environment writes, OIDC, login, mutation, evidence, deployment, or receipt (`pipelines/github/trusted-protected-operations.yml:75-104`).
- Verified exact existing action commits and no broadened producer/permission set: one Azure login, four attestation producers, and six checkout uses. Public read-only tag resolution peels Azure/login v2.3.1 to `7184910d9eb2b1c5e48f7073824a90609bb9b6d6`; the lightweight attestation v2.4.0 tag is `e8998f949152b193b063cb0ec769d69d929409be`.
- Verified every checkout job uses `pwsh`, `HUSAYNIA_REPOSITORY_ROOT=${{ github.workspace }}/HusayniaSite`, and the exact child-root validation. `T21_TRUSTED_BUNDLE_ROOT` retains precedence and the trusted workflow has no checkout or post-login repository path (`eng/common/Release.Common.ps1:6-38`; `pipelines/github/trusted-protected-operations.yml:86-138`).
- Verified the coordinator's only permissions are `actions:write`, `attestations:read`, and `contents:read`; predecessor name/path/repository/main/success are exact; dispatch target and ref are fixed; migration run-name fields are allowlisted; correlation matching is escaped; duplicates, failed children, and timeouts fail closed; Production and self-trigger cycles are absent (`pipelines/github/automatic-nonproduction-orchestration.yml:13-62`).
- Verified coordinator event/run-name/correlation data is routing metadata only. Every dispatched producer and StageOperations consumer independently validates completed prior-run API provenance and exact release/stage/caller/signer bindings.
- Verified resolver authority: selected and consumer runs are fetched from the GitHub API; selected run must be distinct, completed/success, and have `updated_at <= consumer.created_at`; exact repository/path/ref/attempt, artifact digest, content manifest, and pinned attestation signer/source commit are enforced (`eng/promotion/Resolve-GitHubArtifactProvenance.ps1:19,58-68,324-397,528-630`).
- Verified Production prepared inputs are inert v2.1 requests with exact release/target/approval binding and no change-record authorization (`eng/promotion/New-StageOperationInputBundle.ps1:102-134`; `eng/promotion/Test-StageOperationInputBundle.ps1:249-298`).
- Verified CTO and migration records retain exact `R/P/I/C/M` run/artifact/content-manifest bindings, `AUTHORIZE`, actor ID, approval reference, checked change-record hash, target/release/bundle identity, and expiry; StageOperations revalidates them before the stable forbidden boundary (`eng/promotion/Test-CtoAuthorizationRecord.ps1:404-496`; `eng/promotion/Test-TrustedProtectedOperationInputs.ps1:425-563,640-641`).
- Verified the protected execution bundle is an exact closed file set, includes the final resolver/authorization validators, is hash-checked, extracted to a new path, made read-only, and revalidated (`eng/artifact/Test-ProtectedExecutionBundle.ps1:23-40,128-211,333-352`).
- Verified durable Apply serialization/replay protection remains tied to the exact holder, authorization hash, fence token, expiry, and mutation ID under `UPDLOCK, HOLDLOCK` (`eng/artifact/migrations/bundle/Migration.Common.ps1:1235-1262,1291-1353,1371-1471`).
- Reviewed secrets/dependencies/configuration: `.config/dotnet-tools.json` contains no tools; no package was added; owned secret scan returned zero findings; TLS verification was not disabled; no permissive CORS/debug/default credential change is in scope.

EVIDENCE:
- `pwsh -NoProfile -File .\eng\ci\Test-PipelineDefinitions.ps1` — exit `0`; `34/34`, workflows `11`, run bodies `32`, auth/dispatch/deployment/receipt/database/resource counters `0`.
- `pwsh -NoProfile -File .\eng\test\Test-ProtectedExecutionBundle.ps1` — exit `0`; `15/15`, binary cases `4`; arbitrary NUL/CR/LF/binary bytes preserved and CLI/empty/digest failures rejected before effects.
- `pwsh -NoProfile -File .\eng\test\Test-GitHubArtifactProvenance.ps1` — exit `0`; `34/34`.
- Independent additional resolver mutations — `7/7`: future, failed, cancelled, missing conclusion, malformed time, consumer-ID mismatch, and same-run selectors rejected.
- `pwsh -NoProfile -File .\eng\test\Test-CtoAuthorizationProvenance.ps1` — exit `0`; `15/15`.
- `pwsh -NoProfile -File .\eng\test\Test-MigrationAuthorizationProvenance.ps1` — exit `0`; `15/15`.
- `pwsh -NoProfile -File .\eng\test\Test-TrustedStageOperationsSemantics.ps1` — exit `0`; `14/14`.
- `pwsh -NoProfile -File .\eng\test\Test-MigrationStageLeaseFence.ps1` — exit `0`; `12/12`; old-holder fake mutation `0`, current-holder fake mutation `1`.
- `pwsh -NoProfile -File .\eng\test\Test-PreflightPolicyBoundary.ps1` — exit `0`; `8/8`; auth/login/database/mutation/evidence/receipt effects `0`.
- Independent coordinator static attack-surface assertions — `10/10`: exact event, permissions, name/path pairing, fixed dispatch, input allowlist, duplicate/replay handling, bounded waits, no cycle, Staging forbidden fence, and no dynamic evaluation.
- `pwsh -NoProfile -File .\eng\test\Invoke-T21Validation.ps1 -RepositoryRoot (Get-Location) -ContractOnly` — exit `0`; `11/11`, blocked `0`; secret scan `83` files / `1,030,071` bytes / `0` findings.
- PowerShell parser — `66` files, `0` errors. JSON parser — `14` files, `0` errors. Cached actionlint `1.7.12` — `11` workflows, PASS.
- Pin enumeration — login `1` exact; attestation `4` exact; checkout `6` exact. Policy state — enabled stages `0`; rebuildable stages `0`; forbidden deployment role `1`; caller rows `3`.
- `git ls-remote https://github.com/Azure/login.git 'refs/tags/v2.3.1' 'refs/tags/v2.3.1^{}'` — annotated tag peeled to the checked-in login commit.
- `git ls-remote https://github.com/actions/attest-build-provenance.git refs/tags/v2.4.0` — lightweight tag equals the checked-in attestation commit.
- Operation counters: authentication=`0`; workflow dispatches=`0`; deployments=`0`; success receipts=`0`; database calls/mutations=`0`; cloud resource/secret mutations=`0`; installs=`0`; package restores=`0`; commit/push/history operations=`0`; actual C6 builds=`0`.

ARTIFACTS:
- `.ai-org/missions/2026-08-20-husaynia-t21/security-review.md`

FINDINGS:
None.

RISKS:
- **External installation prerequisite, not a repository vulnerability:** the six all-zero reusable-workflow SHA sentinels, exact GitHub customized OIDC subjects, Azure FICs, protected variables/actor lists/endpoints, and workflow installation must be completed and independently verified before any stage can be enabled. The checked-in state fails closed without them.
- The official locked NuGet audit was not rerun because this gate prohibited package restore. Its prior `NU1900` connectivity result remains nonzero and unsuppressed; resolving that external validation prerequisite belongs to the overall release gate, not this R10 repository-security verdict.
- R10 intentionally performs no live authentication, dispatch, deployment, endpoint, database, or cloud-resource validation; this PASS does not attest external configuration.

BLOCKERS:
None for the R10 repository security gate.

NEXT_ACTION:
Keep workflows uninstalled, all reusable sentinels unresolved, and every stage disabled until the remaining independent gates pass. Then install the exact pinned workflow/OIDC/FIC configuration through a separately reviewed change and rerun security validation before enablement.

---

# T21-R10-R1 independent security rerun — 2026-08-27

SECURITY RESULT

Scope:           The three R10-R1 enabled-path fixes and the R9 controls they touch: coordinator event/run-name/dispatch handling; protected environment and read-only token scoping; immutable Preflight OIDC/login ordering; StageOperations' pre-token forbidden boundary; action pins/permissions; completed-run temporal provenance; v2.1 prepared-input/CTO/migration bindings; report HTTP client; protected bundle; and durable migration fencing.
Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings:
None

All findings:
None

Conclusion: PASS

STATUS:          PASS

SUMMARY:
- Independently reran the T21-R10 security gate after R10-R1. No exploitable repository flaw or R9 control regression was found; there are zero unresolved Critical or High findings.
- Coordinator dispatch values are raw JSON strings passed as individual `gh` arguments. Event/run-name values are strictly allowlisted, never evaluated as code, and remain non-authoritative routing metadata that every consumer revalidates.
- Enabled Preflight reaches OIDC/login only after immutable C6, bundle, caller, temporal provenance, and stage-enablement checks. It then rechecks the read-only bundle and runs only bundle-owned code with the sole step-scoped `T21_MIGRATION_IDENTITY_MODE=readonly`; the fixed SQL query verifies SELECT and rejects the listed mutation permissions.
- StageOperations still terminates in the immutable semantic gate at `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` before any OIDC request or Azure login.
- Prepared inputs use the exact stage operations/Production environment and CTO retains `Husaynia-CTO-Authorization`. The read-only probe secret has exactly two workflow references, each on its report-producing step only; it is sent only as an in-memory Bearer header, redirects are disabled, SSRF destinations are constrained, responses are bounded, and generated artifacts do not persist it.
- All stages remain disabled, all six internal reusable-workflow references remain all-zero installation sentinels, no downstream C6 build exists, and the v2.1 authorization/provenance and durable SQL fence contracts remain intact.

WORK_COMPLETED:
- Threat model:
  - **Entry points:** `workflow_run` name/path/title/ID fields; manual workflow inputs and run names; protected variables/environment secrets; GitHub run/artifact responses; immutable C6 and producer artifacts; protected report-service responses.
  - **Trust boundaries:** event metadata -> coordinator `actions:write`; dispatch inputs -> wrapper validation; protected environment -> step-only token; immutable bundle -> OIDC/Azure identity; GitHub API/attestation -> resolver provenance; v2.1 CTO/migration records -> StageOperations; authorization -> SQL lease/fence.
  - **Assets:** coordinator token, stage OIDC/Azure identities, read-only report token, C6/bundle hashes, CTO/change authorization, migration authorization, evidence integrity, and database mutation exclusivity.
  - **Dangerous sinks:** workflow-dispatch POST, OIDC request, Azure login, authenticated report GET, artifact upload/attestation, migration SQL and fenced mutation execution.
  - **Actors:** a workflow-dispatch user, an untrusted-ref contributor, a malicious artifact/API response, a replaying authorized user, and malformed event/run-name input.
- Reviewed current mission requirements, architecture, ADRs, task plan, test evidence, prior security verdict, all eleven workflow definitions, promotion policy, protected bundle construction/validation, provenance resolver contracts, report HTTP client, migration bundle, and lease fence.
- Checked command/argument injection: eight coordinator input fields use raw `-f`, typed `-F/--field` is absent, endpoints/ref/mode/stage are fixed, IDs and hashes are allowlisted, correlation regexes are escaped, and no dynamic evaluation is present. Five malicious run-title probes were rejected.
- Checked secret lifecycle: exact protected environment selection; two step-only secret mappings; no job/output/environment-file propagation; no token in generated evidence; missing-token paths stop before publishable manifest/upload/attestation inputs.
- Checked identity separation: Preflight-only token/login conditions, actual read-only database permission verification, no checkout/post-login repository execution, and StageOperations' earlier immutable forbidden stop.
- Checked supply chain/configuration: one exact Azure login pin, four exact attestation pins, zero nonzero internal reusable pins, zero enabled/rebuildable stages, no package or tool installation, and no TLS verification disablement.

EVIDENCE:
- Direct causal/static suites: **200 passed, 0 failed**:
  - `Test-T21R10EnabledPathRegressions.ps1` — `12/12`.
  - `Test-PreflightPolicyBoundary.ps1` — `8/8`.
  - `Test-StageOperationInputProducer.ps1` — `17/17`.
  - `Test-CtoAuthorizationProvenance.ps1` — `16/16`.
  - `Test-PipelineDefinitions.ps1` — `37/37`, workflows `11`, run bodies `33`.
  - `Test-ProtectedExecutionBundle.ps1` — `15/15`, binary cases `4`.
  - `Test-GitHubArtifactProvenance.ps1` — `34/34`.
  - `Test-StageOperationInputBundle.ps1` — `13/13`.
  - `Test-MigrationAuthorizationProvenance.ps1` — `15/15`.
  - `Test-TrustedStageOperationsSemantics.ps1` — `14/14`.
  - `Test-MigrationStageLeaseFence.ps1` — `12/12`, old mutation markers `0`, current marker `1`.
  - `Test-StageOperationHttpClient.ps1` — `7/7`.
- `Invoke-T21Validation.ps1 -ContractOnly` — exit `0`; `12/12`, failed `0`, blocked `0`.
- Cached actionlint `1.7.12` — PASS, workflows `11`; no installation.
- PowerShell parser — files `67`, errors `0`. JSON-compatible parser — files `121`, errors `0`.
- Owned secret scan — files `84`, bytes `1,083,489`, findings `0`.
- Independent invariant enumeration: raw coordinator fields `8`, typed fields `0`, dynamic evaluation `false`; login pins `1`; attestation pins `4`; probe-secret references `2`; internal reusable pins `6`, nonzero `0`; enabled stages `0`; rebuildable stages `0`; trusted checkout steps `0`; identity-mode steps `1`.
- Files reviewed include:
  - `pipelines/github/automatic-nonproduction-orchestration.yml`
  - `pipelines/github/trusted-protected-operations.yml`
  - `pipelines/github/stage-operation-inputs.yml`
  - `pipelines/github/cto-authorization-record.yml`
  - `pipelines/github/migration-apply-authorization.yml`
  - `pipelines/github/operation-evidence-producer.yml`
  - `pipelines/github/production-operation-evidence.yml`
  - `pipelines/config/promotion-policy.json`
  - `eng/promotion/StageOperationInput.Common.ps1`
  - `eng/promotion/New-StageOperationReport.ps1`
  - `eng/promotion/Invoke-OperationEvidenceProducer.ps1`
  - `eng/promotion/Test-ExternalTrustMarker.ps1`
  - `eng/promotion/Resolve-GitHubArtifactProvenance.ps1`
  - `eng/promotion/Test-TrustedProtectedOperationInputs.ps1`
  - `eng/artifact/New-ProtectedExecutionBundle.ps1`
  - `eng/artifact/Test-ProtectedExecutionBundle.ps1`
  - `eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1`
  - `eng/artifact/migrations/bundle/Migration.Common.ps1`
- Operation counters: authentication=`0`; workflow dispatches=`0`; deployments=`0`; success receipts=`0`; database calls/mutations=`0`; cloud resource/secret mutations=`0`; installs=`0`; package restores=`0`; actual C6 builds=`0`. Commit/push/history mutations=`0`. One read-only `git log -1` metadata probe was executed during initial scoping; it changed no source or history.
- The official NuGet audit was not run because package restore is prohibited. Its retained fail-closed `NU1900` disposition was not suppressed or normalized.

ARTIFACTS:
- `.ai-org/missions/2026-08-20-husaynia-t21/security-review.md`

FINDINGS:
None.

RISKS:
- External installation remains outside this gate. Before enablement, independently verify the exact nonzero reusable-workflow pins, customized OIDC subjects/FICs, protected-main environment restrictions, environment reviewers, actor allowlists, stage-specific read-only token scope/lifetime, and endpoint configuration.
- This was local/static validation. It did not attest live OIDC, Azure RBAC, report-service authorization, workflow installation, deployment, or database behavior.
- A single read-only git-history metadata probe occurred despite the requested zero-history operating constraint; no repository or history mutation occurred.

BLOCKERS:
None for the repository security gate.

NEXT_ACTION:
Keep workflows uninstalled, reusable pins unresolved, and every stage disabled until the remaining independent gates pass. Treat environment/OIDC/FIC/token installation as a separate reviewed change and rerun security validation before any stage is enabled.

---

## T21-R10-R2 security-finding remediation response — 2026-08-27

Status: **REMEDIATED LOCALLY — INDEPENDENT SECURITY RERUN PENDING**

- Exact Unix SqlClient asset selection no longer collapses to a basename. The protected manifest
  binds package key, dependency path, archive source path, RID, hash, length, assembly/file
  versions, and full managed identity; validator and restricted ALC reject root/Windows/path/
  alias/version/identity substitutions.
- The local JSON lease path is now an explicit `EnableLocalTestSeams` capability and
  `GITHUB_ACTIONS=true` rejects before any provider, executor, or state-file invocation.
- The Azure CLI test seam exercises timeout and nonzero process outcomes once, returns only stable
  sanitized errors, and proves no token/stdout/stderr persistence.
- Existing immutable C6, protected read-only closure, completed-run provenance, pre-OIDC forbidden
  boundary, disabled stages, scoped probe secret, and durable SQL fence remain asserted.

No Critical/High/Medium gate disposition is self-issued here. R2-GS remains pending independent
rerun.

---

## T21-R10-R2 final independent security rerun — 2026-08-27

Status: **PASS**

Critical: **0**  
High: **0**  
Medium: **0**  
Low: **0**

The rerun found no security vulnerabilities in the final changes. It verified the exact
Unix/runtime/full-identity closure, manifest-restricted ALC, protected/application bundle identity
separation, Preflight-only null Apply payload behavior, fail-closed non-Preflight materialization,
explicit Actions-blocked seams, direct bounded Azure CLI execution with kill-and-wait, token
redaction, fixed typed SQL, durable fencing, provenance, probe-secret scope, and no post-login
checkout.

---

## T21-R10-R3 security disposition — 2026-08-27

The R3 terminating-guard remediation has local implementation and regression evidence only.
Independent R3 security review remains **PENDING**; the prior R2 PASS is not reused as R3 approval.

---

## T21-R10-R6 final independent security gate — 2026-08-27

STATUS: **PASS**

Critical: **0**  
High: **0**

The independent final review found no exploitable vulnerability after R3-R6. It covered the
terminating SQL lease guards, supported `environment` OIDC claim, exact three-subject external
marker set, step-scoped GitHub API token, binary carrier, action pins, completed-run provenance,
protected probe secret, no-install SqlClient closure/token handling, durable fence, no post-login
checkout, and the pre-OIDC StageOperations forbidden boundary.

---

## T21-R10-R7 independent security gate — 2026-08-27

STATUS: **PASS**

Critical: **0**  
High: **0**

The post-remediation security rerun verified binary fixed-path/API/stream hashes, exact reviewed
action commits, canonical child roots and PowerShell shell, completed-success temporal provenance,
paginated duplicate prevention, checksum continuity, exact migration authorization and durable
fence, step-scoped tokens/secrets, no post-login checkout execution, disabled pre-token/no-receipt
boundaries, minimal permissions, no automatic Production, and zero deployment.

The retained NU1900 outage, absent external installation, and prior generated-write compliance
uncertainty are non-security completion blockers.
