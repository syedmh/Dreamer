# ADR-001: Hash-pin the full repository-owned post-login execution closure
Date: 2026-08-24     Status: Superseded by ADR-004

## Context
The first remediation attempted to protect post-login execution by hashing repository files listed in `promotion-policy.json`.

## Decision
Repository-owned file hashes are not the cloud-authentication trust root.

## Consequences
The lesson that dependencies must be explicit remains, but a candidate checkout cannot approve itself.

## Alternatives considered
- Keep the repo-owned reviewed-file closure - rejected because workflow, validator, and allowlist were candidate-controlled.

# ADR-002: Add a durable SQL-backed apply lease on top of `sp_getapplock`
Date: 2026-08-24     Status: Accepted

## Context
Apply-capable paths need a durable stale-lock and fencing contract in addition to the session-scoped inner lock.

## Decision
Keep `sp_getapplock` as the inner DB-session lock and preserve the durable SQL-backed stage lease/fence around every Apply-capable path.

## Consequences
Serialization and replay protection stay inside the existing database boundary with no new service.

## Alternatives considered
- Azure Storage/blob lease - rejected because it adds storage, RBAC, and target-metadata complexity.
- GitHub concurrency only - rejected because it is not durable and supplies no fencing token.

# ADR-003: Bind Azure OIDC to a pinned reusable workflow subject
Date: 2026-08-24     Status: Superseded by ADR-005

## Context
Binding only `job_workflow_ref` identifies the reusable workflow, not its caller.

## Decision
Superseded by the exact caller-plus-reusable subject in ADR-005.

## Consequences
Existing external marker variables remain fail-closed migration sentinels, not proof that any caller is authorized.

## Alternatives considered
- Keep `repo + context + job_workflow_ref` - rejected because any repository workflow could call the reusable workflow and inherit its identity.

# ADR-004: Execute post-login only from an immutable C6 operations bundle
Date: 2026-08-24     Status: Accepted

## Context
Executing scripts/policy from the checkout after authentication leaves a validation/use race. C6 now contains `operations/protected-execution-bundle.zip`, validated before login and rechecked before use (`pipelines/github/trusted-protected-operations.yml:133-181,239-243`).

## Decision
Execute post-login only from the validated, read-only C6 protected-execution bundle.

## Consequences
C6 is larger and bundle rotation requires external marker coordination, but no post-login repository read or execution path remains.

## Alternatives considered
- Execute checkout scripts after a hash check - rejected because the checker and hash list are mutable candidate content.

# ADR-005: Federate Azure only for an exact caller/reusable workflow pair
Date: 2026-08-24     Status: Accepted (external installation pending)

## Context
The reusable workflow is `workflow_call` only and is called by automatic and manual wrappers, but the current token check validates only the called reusable identity (`pipelines/github/trusted-protected-operations.yml:4-86`; `eng/promotion/Test-ExternalTrustMarker.ps1:31-60`). GitHub documents that `workflow_ref` identifies the caller and `job_workflow_ref` identifies the called reusable workflow in this context.

## Decision
Configure immutable GitHub `sub` claims with `repo`, `context`, `workflow_ref`, and `job_workflow_ref`; configure Entra exact FIC subjects for only the five caller/stage rows in `architecture.md`. Validate those claims locally before Azure login, but rely on Entra exact issuer/audience/subject matching as the authorization boundary.

## Consequences
An arbitrary repository workflow cannot inherit the protected identity. Adding a caller or rotating the reusable SHA requires coordinated GitHub/Entra configuration; caller source remains governed by protected `main`.

## Alternatives considered
- Repo-owned caller allowlist - rejected because a repository writer can edit it.
- `job_workflow_ref` alone - rejected because it does not distinguish callers.
- Include `workflow_sha` in the FIC subject - rejected because every approved caller workflow edit would require a FIC rotation.
- Separate workflow repository/ruleset - rejected as a larger governance change than the requested minimum.

# ADR-006: Verify pre-OIDC artifact provenance through GitHub metadata and attestation
Date: 2026-08-24     Status: Accepted

## Context
`Assert-DeploymentEvidence` and `Assert-TrustedExecutionEvidence` accept SHA-shaped, self-declared JSON (`eng/common/Release.Common.ps1:289-369,629-680`). Manual run IDs are passed directly to artifact download, and a reusable job shares the caller workflow run, so run metadata alone cannot prove a preflight/raw-evidence artifact was emitted by the trusted reusable job.

## Decision
Before requesting an Azure token, resolve every artifact with the GitHub Actions run/artifact APIs, hash the archive against GitHub's immutable artifact digest, and verify a signed producer-content manifest. Require GitHub/Sigstore attestations with exact signer workflow/digest for reusable preflight, raw-evidence, and input producers. Reject deployment evidence until an actual dedicated producer is separately designed and added to the externally pinned role map.

## Consequences
Caller-selected run IDs become safe opaque selectors; a matching name/JSON alone cannot reach StageOperations. The design adds narrowly scoped `actions: read`, `attestations: read/write` permissions and dependence on native GitHub API/Sigstore availability, but no new service or post-login repository read.

## Alternatives considered
- Continue validating names, payload hashes, and self-declared `trustedExecution` - rejected because a forged artifact can satisfy them.
- GitHub artifact metadata only for reusable-job outputs - rejected because another job in the same caller run can upload an identically named artifact.
- A new provenance database/service - rejected because GitHub's artifact API and Sigstore attestations provide the required authority without new infrastructure.

# ADR-007: Separate top-level API-run identity from producer-workflow identity
Date: 2026-08-26     Status: Accepted

## Context
Reusable jobs share the top-level caller run ID. R8 wrote the reusable stage-input workflow into
the manifest's API-run fields, then required those fields to equal the API top-level workflow.

## Decision
Producer manifest v2 records the top-level caller run and the exact producer workflow as separate
identities. Consumers validate the pair against API provenance, attestation signer, and policy.

## Consequences
Stage-input and trusted-preflight manifests become verifiable without identity aliasing. The
unreleased internal v2 shape changes atomically across writers, resolver, consumers, and tests.

## Alternatives considered
- Treat the reusable workflow as the API run - rejected because GitHub reports the top-level caller.
- Validate only the attestation signer - rejected because it would not authorize the top-level caller.

# ADR-008: Resolve CTO and migration authorizations as attested producer roles
Date: 2026-08-26     Status: Accepted

## Context
Production prepared inputs do not consume CTO authorization, and migration authorization is
explicitly excluded from the common resolver.

## Decision
Add attested `cto-authorization` and `migration-authorization` roles to the existing GitHub
API/digest/manifest resolver. Authorization JSON is used only after consumer-created provenance
binds it to the exact release, stage, caller, producer, and approval context.

## Consequences
Every privileged authorization uses one provenance mechanism before OIDC. Two additional workflow
pins must be externally installed before enablement.

## Alternatives considered
- Trust the authorization JSON and artifact name - rejected because both are producer-controlled.
- Add an authorization database/service - rejected because GitHub immutable artifacts and
  attestations satisfy the requirement without new infrastructure.

# ADR-009: Remove embedded legacy run metadata from prepared inputs and preflight artifacts
Date: 2026-08-26     Status: Accepted

## Context
R8 passes `verified-provenance.json` into v1 validators and copies run JSON into produced artifacts.

## Decision
Consumers accept resolver-created v2 provenance paths, derive roots, and write only normalized
cross-bindings into signed manifests/evidence. Legacy `*-run.json` files are not authority and are
not emitted by these producers.

## Consequences
The trust boundary is explicit and caller roots/names disappear. All affected script signatures and
tests must change together.

## Alternatives considered
- Teach v1 metadata validators to accept v2 documents - rejected because it preserves ambiguous
  authority and schema semantics.
- Carry both formats temporarily - rejected because all stages are disabled and dual acceptance
  increases attack surface.

# ADR-010: Preserve zero deployment after complete authorization materialization
Date: 2026-08-26     Status: Accepted

## Context
T21-R9 must prove complete preflight, prepared-input, CTO, and migration authorization wiring while
deployment remains forbidden.

## Decision
Resolve and semantically validate all required authorization artifacts before the existing
deployment-evidence-forbidden boundary. Keep that boundary before OIDC, login, mutation, downstream
evidence, or receipt creation.

## Consequences
The unreachable graph is complete and causally testable without authorizing deployment. GitHub API
and attestation reads may occur before the blocker, but cloud authentication and mutations remain
zero.

## Alternatives considered
- Reject before resolving migration authorization - rejected because it leaves the StageOperations
  contract unimplemented and untestable.
- Add a fake deployment producer - rejected because it would weaken the explicit zero-deployment
  policy.

# ADR-011: Use a nonprivileged coordinator and separate top-level lifecycle runs
Date: 2026-08-27     Status: Accepted

## Context
R9 builds, produces, and consumes artifacts in the same top-level runs. Selected preflight and
prepared-input runs must instead be completed before authorization and StageOperations, while
Development/Staging sequencing must remain automatic around protected manual migration approval.

## Decision
Make release, preflight, prepared inputs, CTO authorization, migration authorization, and
StageOperations separate top-level runs. Add one `workflow_run` coordinator with only GitHub
Actions read/write permissions to dispatch idempotent `workflow_dispatch` children after exact
completed-success predecessors; keep Production outside automatic orchestration.

## Consequences
The lifecycle is observable and completed-run provenance is enforceable. One workflow gains
`actions:write`, but it has no OIDC, environment, secret, cloud, artifact-write, or deployment
authority and every dispatched consumer independently revalidates provenance.

## Alternatives considered
- Keep producers as jobs beside StageOperations - rejected because their top-level run is still in
  progress when selected.
- Pure `workflow_run` chain - rejected because parallel joins, protected manual authorization, and
  GitHub's three-level limit make the full graph unreliable.
- Add a PAT, GitHub App, queue, or orchestration service - rejected as unnecessary new trust,
  operations, and cost; `workflow_dispatch` is supported with `GITHUB_TOKEN`.

# ADR-012: Require completed-success provenance before the consumer run starts
Date: 2026-08-27     Status: Accepted

## Context
The resolver currently permits in-progress reusable producer runs and has no explicit current/future
consumer boundary.

## Decision
Every selectable role must be `completed/success`. The resolver fetches the consumer run, rejects
equal run IDs, and requires the selected run update time to be no later than consumer creation.
Producer commit and attestation source bind the selected run's actual `head_sha`; release commit
remains a separate C6 binding.

## Consequences
Current, future, failed, cancelled, and still-running selectors fail uniformly. Lifecycles may span
protected-main commits without conflating producer-code identity with immutable release identity.

## Alternatives considered
- Compare numeric run IDs only - rejected because monotonic allocation is not the authority for
  completion ordering.
- Trust status alone - rejected because it does not explicitly reject a later/current consumer
  relationship.
- Require every producer run commit to equal the release commit - rejected because separately
  dispatched workflows cannot reliably run an arbitrary historical SHA and the v2 model already
  separates producer and release identities.

# ADR-013: Move Production change-record ownership to CTO authorization
Date: 2026-08-27     Status: Accepted

## Context
The frozen R10 order requires prepared inputs to complete before CTO authorization, while R9
currently requires CTO context to create Production change-record evidence.

## Decision
Production prepared inputs emit an inert authorization request bound to release, target, and
approval reference. The CTO workflow resolves completed preflight and prepared inputs, performs the
protected change-record check, and publishes its result with the CTO record. Migration and
StageOperations bind both artifacts.

## Consequences
The observable order is satisfiable without treating prepared input as authorization. The CTO
artifact grows by one checked evidence file and its internal contract changes atomically.

## Alternatives considered
- Generate change-record evidence before CTO and treat it as authorized - rejected because that
  weakens the R9 authorization boundary.
- Keep CTO before prepared inputs - rejected because it violates the CTO-frozen R10 lifecycle.
- Add a second post-CTO prepared-input workflow - rejected because it adds an unnecessary producer
  phase and selector.

# ADR-014: Standardize checkout roots and binary/action trust primitives
Date: 2026-08-27     Status: Accepted

## Context
The checkout root is `Dreamer`, Husaynia is its child, some PowerShell bodies inherit the wrong
shell/root, `gh api --output` is unsupported, and the current Azure login/attestation pins are not
the peeled action commits.

## Decision
Checkout jobs use one validated `HUSAYNIA_REPOSITORY_ROOT=${{ github.workspace }}/HusayniaSite`
contract and `pwsh`; trusted post-login execution continues to use only
`T21_TRUSTED_BUNDLE_ROOT`. Download the binary carrier through PowerShell 7.4+ native stdout
redirection with strict cleanup/size/digest checks. Pin Azure login to
`7184910d9eb2b1c5e48f7073824a90609bb9b6d6` and attestation to
`e8998f949152b193b063cb0ec769d69d929409be`.

## Consequences
Checkout paths are deterministic, arbitrary binary bytes are preserved, and action identities have
recorded upstream tag-peel provenance. Unsupported runners fail closed instead of falling back to
text conversion or repository-root guessing.

## Alternatives considered
- Keep `$GITHUB_WORKSPACE` as the Husaynia root - rejected because it is the `Dreamer` checkout.
- Use `Invoke-WebRequest` for the outer carrier - rejected because the existing authenticated
  GitHub CLI contract is sufficient and the requested fix is smaller.
- Use tags/branches or the annotated tag object as action pins - rejected because the workflow
  contract requires immutable existing commit SHAs.

## R10 implementation note — 2026-08-27

ADR-011 through ADR-014 were implemented without changing their frozen direction. Read-only
upstream evidence resolved Azure/login `v2.3.1^{}` to
`7184910d9eb2b1c5e48f7073824a90609bb9b6d6`; the lightweight
actions/attest-build-provenance `v2.4.0` tag resolves directly to
`e8998f949152b193b063cb0ec769d69d929409be`. No workflow was installed or dispatched, no
authentication/deployment/database/resource mutation occurred, and no architecture exception was
introduced. Independent gates remain pending.

# ADR-015: Preserve string dispatch inputs and authenticate read-only Preflight before execution
Date: 2026-08-27     Status: Accepted

## Context
GitHub CLI typed `-F` fields convert decimal run IDs to JSON numbers, and enabled Preflight invoked
its SQL-backed immutable producer before the required Azure CLI identity existed.

## Decision
Use raw `gh api -f` fields for every dispatch input. Keep immutable stage enablement and
StageOperations' forbidden boundary before token; for Preflight only, perform OIDC validation and
Azure login before the revalidated bundle producer, with `T21_MIGRATION_IDENTITY_MODE=readonly`
scoped to that producer step.

## Consequences
String workflow input contracts are preserved, `sqlcmd -G` executes only after authenticated
login, and apply identity is not exposed to Preflight. No repository code runs after login.

# ADR-016: Source read-only report credentials only from protected environments
Date: 2026-08-27     Status: Accepted

## Context
Prepared-input and CTO report steps require `T21_STAGE_READONLY_PROBE_TOKEN`, but the workflows did
not bind a protected environment secret.

## Decision
Prepared inputs use the exact stage operations/Production environment; CTO authorization keeps its
dedicated environment. Map `secrets.T21_STAGE_READONLY_PROBE_TOKEN` only on the report-producing
step in each workflow.

## Consequences
Enabled I/C report paths can authenticate their read-only GETs without job-level secret exposure.
The token is absent from report, artifact, manifest, upload, and attestation contracts.

# ADR-017: Vendor the existing SqlClient closure into the immutable protected bundle
Date: 2026-08-27     Status: Accepted

## Context
The pinned `ubuntu-24.04` image has Azure CLI, PowerShell 7.6.5, and .NET 10 but no `sqlcmd`.
Runtime installation/download is forbidden. C6 already contains a locked
`Microsoft.Data.SqlClient` 6.1.1 application payload, while post-login execution must remain inside
the externally hash-pinned, read-only protected closure.

## Decision
At C6 build time, extract the exact managed Unix SqlClient dependency closure from
`app/Husaynia.Web.zip` into `runtime/sqlclient/` in the protected bundle. Bind it to the application
archive SHA in protected manifest v1.1, validate it before and after login, load it through a
manifest-restricted collectible `AssemblyLoadContext`, obtain an Azure SQL access token from the
already-authenticated Azure CLI, and execute Preflight and lease SQL through
`Microsoft.Data.SqlClient`.

This supersedes only ADR-015's `sqlcmd -G` mechanism; its string inputs, pre-token enablement,
OIDC/login ordering, readonly identity, and no-checkout decisions remain accepted.

## Consequences
Enabled Preflight is executable on the hosted image with no install/restore/download, and future
lease execution no longer depends on an absent tool. C6 duplicates a small dependency closure and
bundle rotation is coupled to the locked SqlClient graph, but post-login code remains externally
hash-pinned and read-only. Tokens exist only in process memory and SQL result parsing becomes typed
rather than delimiter-based.

## Alternatives considered
- Install or download `sqlcmd` - rejected by the no-runtime-supply-chain constraint.
- Add a console project or package - rejected because it requires source/package/restore change.
- Pull a SQL tools container or add an action/service - rejected as a new external dependency and
  post-login download.
- Load arbitrary assemblies directly from `app/Husaynia.Web.zip` after login - rejected because it
  broadens the executable closure and weakens the protected-bundle TOCTOU boundary.
- Use Azure.Identity default credential discovery - rejected because Azure CLI already represents
  the explicitly logged-in identity and default discovery broadens credential sources.

# ADR-018: Keep producer commit and release commit as independent provenance bindings
Date: 2026-08-27     Status: Accepted

## Context
ADR-012 permits completed producer and release runs from different protected-main commits, and the
v2 producer manifest stores both identities. `Invoke-MigrationBundle.ps1:225-227` nevertheless
requires equality, making the separately completed lifecycle fail.

## Decision
Remove only the producer-equals-release comparison. Continue requiring the release run commit to
equal `release-manifest.commitSha`, and independently require the producer commit to equal its
GitHub API, attestation, producer manifest, and resolver bindings.

## Consequences
Separate completed runs work as designed without weakening either identity. Evidence and
authorization records continue to carry the immutable release commit and actual producer commit;
tests must use distinct values in the valid baseline.

## Alternatives considered
- Force every producer to run the historical release SHA - rejected as unreliable for protected
  reusable workflows and contrary to ADR-012.
- Drop producer commit validation - rejected because it would weaken exact caller/provenance.
- Rewrite records to contain one generic commit - rejected because it conflates two trust roles.

## ADR-017/018 implementation record — 2026-08-27

Implemented as accepted. No new package, project, action, service, install, restore, download,
container, public schema, stage-policy, or application behavior was introduced. The protected
manifest alone advances to `1.1.0`; the release manifest remains `1.0.0`. Four current internal
hash bindings were regenerated (the two migration orchestration files and the two protected
validators).

## ADR-017 clarification — protected identity versus Apply payload

Preflight's evidence `operation.bundlePath` and `operation.bundleSha256` identify the protected
execution bundle (`operations/protected-execution-bundle.zip`), matching the R10 producer,
authorization, and StageOperations contracts. They do not identify the application migration
DLL. The managed `Husaynia.Database.Migrations.dll` remains an Apply-only payload under the
existing application-bundle contract. Therefore a null application-bundle SHA is valid for
Preflight, while Acquire/Renew/Release/Apply continue to fail closed until that DLL is
materialized and hash-bound.

The Azure CLI timeout implementation must not return immediately after `Kill(true)`; it waits for
confirmed process-tree termination. The protected workflow applies `IsReadOnly` only to files and
removes directory write permissions recursively on Linux.

Independent test and security gates passed, and independent code review returned `APPROVED`.
Final engineering judgment remains pending.

# ADR-019: Read protected OIDC context from the environment claim
Date: 2026-08-27     Status: Accepted

## Context
GitHub's OIDC discovery document at
`https://token.actions.githubusercontent.com/.well-known/openid-configuration` advertises the
`environment` claim but no standalone `context` claim. The caller matrix and subject-template
design use “context” as the protected policy value; requiring a JWT property named `context`
therefore makes every enabled Preflight fail before Azure login.

## Decision
Require the JWT `environment` claim and use its value as `$context`. Compare that value exactly to
the selected caller-matrix `context`, then render and compare the existing immutable custom `sub`
without changing its format. Do not request or accept a standalone `context` claim.

Retain every exact issuer, audience, repository, immutable repository/owner ID, protected ref,
caller workflow ref/SHA, reusable workflow ref/SHA, and external exact-subject-marker check.

## Consequences
A valid GitHub JWT containing `environment` and no `context` can pass, while a missing or wrong
environment, caller, reusable workflow, rendered subject, or external subject marker remains
fail-closed. The discovery URL is documentation evidence only; runtime validation and tests do not
depend on network access.

# ADR-020: Treat the external OIDC marker as the exact rendered caller-matrix set
Date: 2026-08-27     Status: Accepted

## Context
The R4 validator required three unique nonempty external marker strings and membership of only the
current token subject. A marker containing the valid Development subject plus two junk strings
therefore passed while omitting the approved Staging and Production subjects.

## Decision
Render the approved marker set from exactly the three `t21ProvenanceContract.callerMatrix` rows.
Every subject uses the current token's immutable `repository_owner_id` and `repository_id`, that
row's exact `context` and `workflowRef`, and the exact pinned `ExpectedWorkflowRef`.

Compare the supplied marker as a `StringComparer.Ordinal` set. Reject a missing, extra, duplicate,
junk, case-changed, wrong-environment, wrong-caller, or wrong-reusable value. Continue validating
the current token against its one exact stage/environment/caller row and retain the supported
`environment` claim, exact `sub`, issuer, audience, repository, immutable IDs, protected ref,
caller workflow ref/SHA, and reusable workflow ref/SHA checks.

## Consequences
The external marker is now the complete least-privilege three-caller trust contract rather than a
count plus current-subject membership check. No workflow, identity, deployment, database,
resource, secret, or artifact contract changes.

# ADR-021: Make root, lifecycle, binary CLI, and action resolution evidence checked-in
Date: 2026-08-27     Status: Accepted

## Context
R10 had passing one-off probes for upstream action resolution, exact checkout roots, and lifecycle
selection. The repository tests did not themselves execute every exact checkout root body, invoke
the installed GitHub CLI parser, or simulate the completed-run coordinator state machine. Exact
action tag provenance was recorded only in narrative evidence rather than one central machine-
checked policy.

## Decision
Keep the frozen runtime graph and permissions unchanged. Add:

1. a central five-action tag/commit registry in `promotion-policy.json`;
2. a static all-workflow full-SHA contract plus optional read-only `git ls-remote` tag/peel
   verification;
3. exact-body `Dreamer/HusayniaSite` root execution for every checkout job;
4. an installed `gh api --help` parser assertion with no network request;
5. a local exact-coordinator state-machine fixture covering `R -> P/I -> M -> S`, failures,
   checksum continuity, no cycles, no current/future producer selection, the forbidden Staging
   receipt boundary, and separate manual Production;
6. inclusion of those contracts and the existing HTTP suite in both safe T21 harness modes.

Migration authorization remains a separately completed protected manual approval; the coordinator
automates only the safe edges around it. Internal reusable workflow all-zero SHAs remain the sole
allowed installation sentinels.

## Consequences
The required evidence is reproducible without authentication, dispatch, deployment, package
restore, tool installation, database access, or cloud mutation. Optional upstream verification is
public/read-only and fails closed. The policy-content change requires a new protected-bundle SHA
only during a later separately reviewed installation; the checked-in stages remain disabled.

## ADR-021 review disposition

The initial independent review rendered the resolver's bearer source as a redaction mask. Raw
source inspection and the existing successful four-request local fixture proved the runtime header
was already `Bearer $token`; no runtime authorization change was required.

Two real review findings were accepted and remediated:

- remove the name-based provenance assertion override so the final header regression cannot pass
  without the exact captured runtime bearer; and
- paginate/slurp all nonproduction wrapper runs before correlation filtering so an older matching
  run cannot fall beyond the first 100 results and be duplicated.

The pagination response and API failure are fail-closed. The page-two regression is checked in and
the coordinator's permissions and no-Production/no-OIDC boundaries are unchanged.
