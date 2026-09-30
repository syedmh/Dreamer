# T21-R10 architecture — completed-run lifecycle hardening
Date: 2026-08-27  
Status: **R10-R2 DESIGN ACCEPTED — implementation and independent gates pending**

This is the smallest change that remediates the four R9 High findings while retaining the R9
immutable-C6, provenance, authorization, fencing, and zero-deployment boundaries. It adds one
nonprivileged orchestration workflow; it adds no service, datastore, package, infrastructure, app
contract, deployment, or C6 rebuild.

## Current state — verified FACTs

- **FACT C-01:** The ten JSON-compatible workflows are authoring files under
  `pipelines/github`, not installed workflows. Their producer/StageOperations jobs are unreachable,
  every stage has `deploymentEnabled=false`, Production is separate/manual, and
  `deployment-evidence` is forbidden (`pipelines/README.md:1-43`;
  `pipelines/config/promotion-policy.json:199-216,286-299,353-366,468-481`).
- **FACT C-02:** The release workflow currently builds C6 and embeds Development/Staging
  preflight, prepared-input, and StageOperations jobs in the same top-level run
  (`pipelines/github/release-build-and-nonproduction.yml:3-108`).
- **FACT C-03:** The manual nonproduction and Production wrappers also embed preflight,
  prepared-input, and StageOperations in one top-level run, and StageOperations selects both
  producer artifacts with its own `github.run_id`
  (`pipelines/github/operation-evidence-producer.yml:15-50`;
  `pipelines/github/production-operation-evidence.yml:16-55`).
- **FACT C-04:** Policy permits `trusted-preflight` and `stage-operation-inputs` to be selected
  while their top-level run is in progress, and the resolver implements that exception
  (`pipelines/config/promotion-policy.json:171-193`;
  `eng/promotion/Resolve-GitHubArtifactProvenance.ps1:339-356`).
- **FACT C-05:** The trusted C6 acquisition body calls unsupported
  `gh api ... --output $carrier` before the existing carrier canonicalization, fixed-path streamed
  bundle hash, and extraction controls (`pipelines/github/trusted-protected-operations.yml:73`;
  `eng/test/Test-ProtectedExecutionBundle.ps1:568-585`).
- **FACT C-06:** The release PowerShell body has no `pwsh` or child working directory and passes
  checkout root as the Husaynia `RepositoryRoot`; stage-input bodies use
  `$GITHUB_WORKSPACE/eng` and `$GITHUB_WORKSPACE/pipelines`
  (`pipelines/github/release-build-and-nonproduction.yml:6-19`;
  `pipelines/github/stage-operation-inputs.yml:16-27,31-78`). The actual Husaynia solution is the
  `HusayniaSite` child, and the common root resolver identifies a root by `HusayniaSite.sln`
  (`HusayniaSite.sln:1-24`; `eng/common/Release.Common.ps1:3-27`).
- **FACT C-07:** Three PowerShell workflow bodies currently inherit the platform shell:
  release build, Production forbidden stub, and stage-evidence forbidden stub
  (`pipelines/github/release-build-and-nonproduction.yml:13-19`;
  `pipelines/github/production-promotion.yml:13-19`;
  `pipelines/github/stage-evidence-intake.yml:7-14`).
- **FACT C-08:** The current pins are
  `azure/login@858f4093d287a904987dfd22abd163280f939550` and
  `actions/attest-build-provenance@96b4a1ef7235a096b17240c259729fdd70c83d45`
  (`pipelines/github/trusted-protected-operations.yml:124,149`;
  `pipelines/github/stage-operation-inputs.yml:92`;
  `pipelines/github/cto-authorization-record.yml:142`;
  `pipelines/github/migration-apply-authorization.yml:204`;
  `pipelines/config/promotion-policy.json:220`).
- **FACT C-09:** The installed `gh` is 2.97.0; `gh api --help` documents response output on
  stdout and has no `--output`. Read-only upstream tag resolution proves
  `Azure/login` v2.3.1 peels to `7184910d9eb2b1c5e48f7073824a90609bb9b6d6`, and
  `actions/attest-build-provenance` v2.4.0 (and the current peeled v2 target) is
  `e8998f949152b193b063cb0ec769d69d929409be`.
- **FACT C-10:** CTO and migration authorizers currently resolve release plus preflight, but not
  prepared inputs (`pipelines/github/cto-authorization-record.yml:68-106`;
  `pipelines/github/migration-apply-authorization.yml:101-133`). Production prepared-input
  generation currently requires CTO context before it can create change-record evidence
  (`pipelines/github/stage-operation-inputs.yml:31-66`;
  `eng/promotion/New-StageOperationReport.ps1:198-240`).
- **FACT C-11:** R9 already enforces exact caller/reusable identity, GitHub
  API/digest/manifest/attestation provenance, the canonical outer carrier and fixed protected
  bundle path, read-only C6 post-login execution, and the single pre-OIDC
  `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary
  (`eng/ci/Test-PipelineDefinitions.ps1:149-221,572-595`;
  `eng/promotion/Test-TrustedProtectedOperationInputs.ps1:598`;
  `pipelines/github/trusted-protected-operations.yml:95,120-133`).
- **FACT C-12:** Durable Apply serialization is already owned by the SQL stage-lease record:
  `ActiveMutationId`/`ActiveMutationStartedAtUtc` are idempotently added, and `BeginMutation` /
  `CompleteMutation` use `UPDLOCK,HOLDLOCK` with the exact holder, fence, authorization, expiry,
  and mutation tuple (`eng/artifact/migrations/bundle/Migration.Common.ps1:1228-1265,1370-1455`).
- **FACT C-13:** The contract harness invokes ten focused suites plus secret scanning and preserves
  nonzero failure/block exits (`eng/test/Invoke-T21Validation.ps1:1-56`). The application remains
  a locked, deterministic .NET 10 solution; R10 changes no application or package contract
  (`global.json:1-7`; `Directory.Build.props:1-15`;
  `Directory.Packages.props:1-18`; `HusayniaSite.sln:1-44`).

## Frozen target graph

`R` is the only release/C6 build. `P`, `I`, `C`, `M`, and `S` are separate top-level runs:

```text
R completed/success
  -> P(stage) completed/success        # trusted preflight
  -> I(stage) completed/success        # prepared inputs; may run parallel with P
  -> C(Production) completed/success   # Production only; consumes R + P + I
  -> M(stage) completed/success        # consumes R + P + I (+ C for Production)
  -> S(stage) separately dispatched    # resolves every prior run before OIDC
```

No downstream run contains a C6 build command. Every edge carries only opaque run IDs plus the
expected application SHA; artifact names and roots remain role-derived.

### Automatic Development and Staging

Add `automatic-nonproduction-orchestration.yml`, triggered only by `workflow_run: completed` for
the release, migration-authorization, and nonproduction operation-wrapper workflows.
Every dispatch branch additionally requires the exact protected-main predecessor identity, its
`conclusion=success`, the stage-specific protected enable variable, and
`promotion-policy.json` `deploymentEnabled=true`. The current absent/false variables and false
policy therefore yield zero dispatches.

1. A successful protected-main release completion causes the coordinator to discover the C6
   identity through the existing release resolver, then dispatch distinct Development `P` and `I`
   workflow-dispatch runs and wait for both to finish successfully.
2. Migration authorization remains manual/protected. Its workflow accepts the completed
   Development `P` and `I` selectors. A successful completed `M(Development)` event causes the
   coordinator to dispatch a separate `S(Development)` run.
3. Staging producer dispatch is gated by completed-success `S(Development)` **and** the existing
   canonical Development receipt provenance. Because deployment evidence and receipts remain
   forbidden in R10, this branch is present but cannot dispatch in the checked-in state.
4. If that future gate is satisfied by a separately approved mission, the coordinator dispatches
   distinct Staging `P` and `I`; completed manual/protected `M(Staging)` then causes separate
   `S(Staging)`.

The coordinator has only `actions:write`, `attestations:read`, and `contents:read`.
It has no environment, secrets, `id-token`, cloud login, artifact write, or deployment permission.
`GITHUB_TOKEN` dispatch is used only for `workflow_dispatch`, the documented recursion-safe
exception. A lifecycle key derived from the triggering predecessor run ID and target phase makes
dispatch idempotent: zero matching child runs causes one dispatch, one is reused, and more than one
fails closed. Exact `run-name`/correlation data is routing metadata only; the consumer resolver is
the authority. The coordinator never triggers on itself, ignores `P`/`I` completions, and therefore
has no event cycle.

### Manual and Production paths

- Manual Development/Staging dispatch `P`, `I`, protected `M`, then `S`, supplying opaque completed
  run IDs.
- Production remains wholly manual and separate: `R -> P/I -> C -> M -> S`.
- Production `I` emits non-authorizing prepared inputs plus a release/target/approval-reference
  request. `C` resolves completed `P` and `I`, performs the protected change-record check, and
  publishes the CTO record plus that checked evidence. This moves, but does not weaken, the R9 CTO
  gate so the frozen completed-run order is possible.
- Every existing enable flag and `deploymentEnabled=false` remains. Production promotion and stage
  evidence intake still stop at `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN`.

## Frozen contracts

### Workflow dispatch contracts

`operation-evidence-producer.yml` and `production-operation-evidence.yml` each execute exactly one
mode per top-level run:

```text
mode: Preflight | PreparedInputs | StageOperations
stage: Development | Staging                    # nonproduction wrapper only
sourceReleaseRunId: positive decimal
expectedAppSha256: nonzero lowercase 64-hex
preflightRunId: positive decimal                # StageOperations only
stageOperationInputsRunId: positive decimal     # StageOperations only
migrationAuthorizationRunId: positive decimal  # StageOperations only
ctoAuthorizationRunId: positive decimal         # Production StageOperations only
ctoApprovalReference: approved opaque reference # Production I/C/M/S as applicable
orchestrationCorrelationId: bounded routing token; never authority
```

Unused mode inputs must be empty; selector/run-shape mismatches fail before reusable invocation.
`release-build-and-nonproduction.yml` becomes release-only.

`cto-authorization-record.yml` adds required `stageOperationInputsRunId`.
`migration-apply-authorization.yml` adds required `stageOperationInputsRunId`. Both resolve
`R`, `P`, and `I` as completed-success before record creation; Production migration also resolves
completed `C`.

Canonical outputs are unchanged except for the CTO-owned change-record file:

| Run | Canonical output |
|---|---|
| `R` | `husaynia-site-{version}`: the sole C6 directory artifact |
| `P` | `raw-{stageLower}-preflight-{applicationSha256}`: preflight evidence + producer manifest |
| `I` | `stage-operation-inputs-{stageLower}-{applicationSha256}`: prepared-input manifest, reports, checksums, producer manifest |
| `C` | `cto-authorization-{applicationSha256}`: CTO record, checked `source-change-record.json`, producer manifest |
| `M` | `migration-apply-authorization-{Stage}-{applicationSha256}-{preflightEvidenceSha256}`: migration record + producer manifest |
| `S` | no artifact, evidence, deployment, or receipt in R10; it terminates at the forbidden boundary |

The coordinator publishes no artifact. Its only outputs are nonsecret job-summary routing facts:
predecessor run ID, correlation ID, dispatched child run IDs, and final conclusions.

### Resolver and identity contract

Keep producer-manifest v2 identity separation. API run fields describe the top-level run;
`trustedExecution.producerWorkflowRef` describes the pinned reusable signer or exact direct
producer commit.

`Resolve-GitHubArtifactProvenance.ps1` adds mandatory `ConsumerRunId` for workflow use and applies
one rule to every selectable role:

1. fetch selected and consumer runs from the GitHub Actions API;
2. require exact repository, protected ref, workflow path/caller pair, positive attempt, and
   parseable timestamps;
3. require selected `status=completed`, `conclusion=success`, selected ID different from consumer
   ID, and selected `updated_at <= consumer.created_at`;
4. require one unexpired canonical artifact, immutable API digest, bounded safe extraction, exact
   content manifest, and role-pinned attestation where required;
5. bind producer `commitSha` and attestation source digest to the selected producer run's actual
   `head_sha`; retain the release commit as a separate immutable C6 binding.

API 404, missing conclusion, queued/in-progress/failed/cancelled run, current run, later-created
run, malformed timestamp, duplicate artifact, digest mismatch, or cross-release/stage/caller/signer
binding fails closed. A read-only release-discovery mode may omit only the expected application SHA
for the coordinator; it must fully validate C6 internally and output the discovered identity.
Strict producer/authorization/StageOperations consumers still require and compare the expected SHA.
`P` and `I` selectors must also be distinct because each wrapper run executes exactly one mode.

Authorization records remain internal/unreleased and change atomically to v2.1:

```text
producerBindings.preflight        = run ID + artifact/content-manifest SHA
producerBindings.preparedInputs   = run ID + artifact/content-manifest SHA
releaseBinding                    = run ID + app/bundle/manifest/commit SHA
productionCtoAuthorization        = CTO run/provenance/change-record binding (Production M only)
```

StageOperations requires those exact bindings to match the independently resolved artifacts.

### Production prepared-input/CTO ownership

Prepared-input v2.1 retains all nonauthorization report evidence and adds
`productionAuthorizationRequest` with stage, release binding, target fingerprint, and approval
reference. It does not contain a success placeholder and cannot authorize Production.
`source-change-record.json` moves to the CTO authorization artifact, where it is created only after
completed `P` and `I` provenance is resolved. CTO, migration, and StageOperations validators all
bind its SHA and approval reference.

### Canonical checkout root and shells

Every checkout-consuming job defines exactly:

```text
HUSAYNIA_REPOSITORY_ROOT=${{ github.workspace }}/HusayniaSite
defaults.run.shell=pwsh
defaults.run.working-directory=HusayniaSite
```

The first PowerShell body validates that the resolved root equals that exact child, is beneath
`GITHUB_WORKSPACE`, and contains `HusayniaSite.sln`, `eng`, and `pipelines`. All checked-out
Husaynia paths and every explicit `-RepositoryRoot` derive from this variable; there is no
workspace-root fallback. Jobs without checkout still use `pwsh` but never define or consume this
variable. Trusted execution continues exclusively through `T21_TRUSTED_BUNDLE_ROOT`, including
after login.

### Binary C6 retrieval and action pins

The trusted workflow uses native stdout redirection, not a pipeline or text conversion:

```powershell
if ($PSVersionTable.PSVersion -lt [version]'7.4.0') { throw 'Binary-safe native redirection is unavailable.' }
& gh api ('repos/{0}/actions/artifacts/{1}/zip' -f $env:GITHUB_REPOSITORY, $artifact.id) > $carrier
$downloadExit = $LASTEXITCODE
```

The carrier path must be absent before the call. Nonzero CLI exit, absent/empty/oversize carrier, or
API digest mismatch deletes the partial file and stops before ZIP open, environment/output-file
writes, marker/validator execution, OIDC, login, mutation, evidence, deployment, or receipt. Only
after that check does the unchanged R9 carrier canonicalization stream the sole
`operations/protected-execution-bundle.zip` to the fixed runner-temp path and compare its protected
hash.

Freeze these action commits everywhere in workflows, policy, and tests:

```text
azure/login@7184910d9eb2b1c5e48f7073824a90609bb9b6d6
actions/attest-build-provenance@e8998f949152b193b063cb0ec769d69d929409be
```

Authoritative provenance is read-only upstream `git ls-remote` tag peeling. Static tests enforce
the exact frozen values, one value per action, four approved attestation producers, the existing
exact checkout pin, and unchanged least-privilege permissions. External reusable-workflow
all-zero SHA sentinels remain installation gates only.

## Security, failure, concurrency, and observability

- All GitHub/API/attestation reads, producer materialization, CTO/migration semantic validation,
  and the stable deployment-evidence-forbidden stop remain before OIDC. No post-login checkout path
  is introduced.
- The exact caller matrix may be reduced to the three wrappers actually used
  (`operation-evidence-producer.yml` for Development/Staging and
  `production-operation-evidence.yml` for Production); it must never broaden.
- Coordinator and stage concurrency groups are per release/stage/phase with
  `cancel-in-progress:false`. Artifact upload remains `overwrite:false`; authorization expiry and
  the SQL durable mutation claim remain unchanged.
- A failed/cancelled/timed-out predecessor causes no successor dispatch. API/attestation outage,
  duplicate correlation, malformed routing metadata, missing artifact, or stale authorization is
  terminal and requires a new reviewed run; there is no automatic success retry.
- Logs/job summaries may contain stage, role, run IDs, artifact IDs, immutable digests, correlation
  ID, and conclusion. They must not contain tokens, endpoint credentials, protected target JSON,
  OIDC payloads, or authorization secrets.

## File-by-file delta

- `pipelines/github/release-build-and-nonproduction.yml` — release-only; canonical root/pwsh; never
  dispatch or build C6 downstream.
- `pipelines/github/automatic-nonproduction-orchestration.yml` — **new** nonprivileged completed-run
  coordinator with exact event filters, idempotent dispatch discovery, bounded waits, and no cycle.
- `pipelines/github/operation-evidence-producer.yml`,
  `production-operation-evidence.yml` — one mode per top-level run; completed selectors only.
- `pipelines/github/stage-operation-inputs.yml` — canonical root; Production v2.1 authorization
  request, no pre-CTO success evidence.
- `pipelines/github/cto-authorization-record.yml`,
  `migration-apply-authorization.yml` — consume both completed producer selectors and bind them.
- `pipelines/github/trusted-protected-operations.yml` — supported binary download, verified pins,
  consumer-run temporal checks, unchanged immutable bundle and forbidden-boundary ordering.
- `pipelines/github/production-promotion.yml`, `stage-evidence-intake.yml` — explicit `pwsh`; retain
  forbidden stubs. `pr-validation.yml` adds the same canonical root environment while retaining its
  existing commands.
- `pipelines/config/promotion-policy.json` — all selectable roles completed-success; v2.1 prepared
  and authorization bindings; exact action pin; no enablement.
- `eng/common/Release.Common.ps1` — validate explicit checkout root without changing
  `T21_TRUSTED_BUNDLE_ROOT` precedence.
- `eng/promotion/Resolve-GitHubArtifactProvenance.ps1`,
  `New-T21ProducerManifest.ps1` — consumer temporal boundary and actual producer commit binding.
- `eng/promotion/New-StageOperationInputBundle.ps1`,
  `Test-StageOperationInputBundle.ps1`, `StageOperationInput.Common.ps1`,
  `New-StageOperationReport.ps1` — v2.1 Production request/change-record ownership.
- `eng/promotion/New-CtoAuthorizationRecord.ps1`,
  `Test-CtoAuthorizationRecord.ps1`,
  `New-MigrationApplyAuthorization.ps1`,
  `Test-TrustedProtectedOperationInputs.ps1` — exact `R/P/I/C/M` cross-bindings.
- `eng/artifact/New-ProtectedExecutionBundle.ps1`,
  `Test-ProtectedExecutionBundle.ps1` — include/hash the changed protected closure.
- `eng/ci/Test-PipelineDefinitions.ps1` and focused `eng/test/Test-*Provenance.ps1`,
  `Test-StageOperationInputProducer.ps1`, `Test-PreflightPolicyBoundary.ps1`,
  `Test-ProtectedExecutionBundle.ps1`, `Test-TrustedStageOperationsSemantics.ps1`,
  `Invoke-T21Validation.ps1` — replace same-run assertions with the R10 matrix.
- `pipelines/README.md` — document the completed-run graph and installation gates.

No `src`, `tests/*.csproj`, package, infrastructure, database schema, resource, or public contract
file changes.

## Production-shaped test matrix

1. **Binary:** exact checked-in run body + native fake `gh` preserves NUL/CR/LF/arbitrary bytes;
   rejects unknown `--output`, CLI failure, missing/empty/oversize carrier, and digest mismatch with
   zero side effects.
2. **Pins/permissions:** exact two action SHAs everywhere; reject tag/branch/zero/nonexistent/
   inconsistent values, fifth attestation producer, checkout drift, or permission broadening.
3. **Root/shell:** enumerate every `run` body as `pwsh`; execute/evaluate a
   `Dreamer/HusayniaSite` fixture; wrong child root fails before artifact, attestation, auth, or
   mutation; trusted bundle-root execution remains unchanged.
4. **Resolver lifecycle:** completed-success prior run passes; queued, in-progress, failed,
   cancelled, missing conclusion, current run ID, later-created/future run, API 404, wrong
   stage/release/caller/signer/digest/manifest/attestation all reject.
5. **Development:** completed `R`, distinct completed `P/I`, completed bound `M`, and distinct `S`
   reach only the forbidden boundary. Any predecessor failure causes zero successor dispatch.
6. **Staging:** no producer dispatch before successful Development `S` plus canonical receipt;
   absence of the forbidden receipt keeps dispatch count zero; no event cycle or duplicate child.
7. **Production:** completed `R`, distinct `P/I`, completed `C`, completed `M`, then manual distinct
   `S`; missing/wrong prepared binding, CTO change record, approval reference, actor, expiry, or
   migration-to-CTO link rejects.
8. **No rebuild/R9 regression:** only release contains the C6 build; outer-carrier limits,
   fixed-path/bundle hash, API/digest/manifest/attestation, exact identities, disabled stages,
   no post-login repo code, durable fence, and the single forbidden boundary remain equal or
   stricter.
9. **Gates:** JSON parse, PowerShell parse, cached actionlint, focused R10 and R9 suites,
   `Test-PipelineDefinitions.ps1`, and `Invoke-T21Validation.ps1 -ContractOnly`; official locked
   NuGet audit remains fail-closed on `NU1900`.

## Tradeoffs, risks, and migration

- **Chosen:** one read/write-Actions-only coordinator plus explicit workflow-dispatch runs. This
  makes every producer observable and completed before selection without a new service.
- Pure `workflow_run` chaining is rejected because joins/manual authorization and GitHub's
  three-level chain limit make the Production graph unreliable. Keeping same-run jobs is rejected
  because a run cannot be both completed and its own consumer. A PAT/App/queue/database is rejected
  as unnecessary additional trust and cost.
- Moving Production change-record materialization into CTO authorization increases that artifact's
  contract, but preserves the R9 control while satisfying the CTO-frozen order. Treating prepared
  input as already authorized is rejected.
- `actions:write` is a coordinator-only risk. Mitigation: exact predecessor allowlist,
  protected-main event checks, deterministic idempotency key, no secrets/OIDC/environment, and
  full downstream provenance revalidation.
- Main may advance between lifecycle runs. Producer API/attestation identity therefore binds its
  actual run commit separately from the immutable release commit; conflating them is rejected.

Forward migration is one atomic authoring change: policy/contracts and validators, producer and
authorization writers, wrappers/coordinator, protected-bundle hashes, then tests/docs. Do not
install any intermediate revision. Rollback restores the complete R9 authoring set; there is no
data rollback because no workflow is installed and no deployment/database/resource mutation
occurs. External installation, nonzero reusable pins, OIDC/FIC/environment/variable setup, and
enablement remain a later reviewed change.

## R10 implementation evidence — 2026-08-27

Tasks T21-R10-1 through T21-R10-7 were implemented in the frozen serial order. The authored graph
now has eleven workflows, release-only `R`, distinct one-mode `P/I/S` wrappers, completed
`C/M` authorizers, and one nonprivileged completed-run coordinator. The resolver requires
`ConsumerRunId`, completed-success predecessors, and the selected-update/consumer-create temporal
boundary. Prepared-input/CTO/migration/StageOperations records use the v2.1 cross-bindings.

Local evidence: pipeline definitions 34/34; protected binary/bundle 15/15; GitHub provenance
34/34; CTO 15/15; preflight boundary 8/8; stage-input producer 16/16; stage-input bundle 13/13;
migration authorization 15/15; StageOperations semantics 14/14; durable fence 12/12; HTTP 7/7;
ContractOnly and safe-full harness 11/11; exit codes 2/2; actionlint 11 workflows; Bicep
19/19 + 31/31 + 12/12; PowerShell parse 66; JSON parse 14; secret scan 83 files/0 findings.
This is implementation/self-validation evidence only. Independent test, security, code-review,
and judgment gates remain pending.

## T21-R10-R1 enabled-path correction — 2026-08-27

The three enabled-path review defects were corrected without changing ADR-011 through ADR-014:

- coordinator dispatch still targets the fixed protected-main workflow, but every
  `workflow_dispatch` input now uses raw `gh api -f` fields so decimal run IDs remain JSON strings;
- trusted Preflight now enforces immutable bundle policy enablement before token, validates OIDC,
  logs in, rechecks the read-only extraction, and only then runs the bundle-owned producer with the
  step-scoped `T21_MIGRATION_IDENTITY_MODE=readonly`; StageOperations still terminates at the
  pre-token `T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN` boundary;
- prepared-input reports run in the exact Development/Staging operations or Production
  environment, while CTO change-record production remains in `Husaynia-CTO-Authorization`.
  `T21_STAGE_READONLY_PROBE_TOKEN` is mapped only to each report-producing step and is not passed
  to artifact, manifest, upload, or attestation steps.

No repository checkout path was added after login, no permission was broadened, and all
completed-run, ConsumerRunId, v2.1, immutable-bundle, no-rebuild, disabled-stage, and durable-fence
contracts remain unchanged.

## T21-R10-R2 no-install SQL execution replan — 2026-08-27

### Current state — verified facts

- **FACT R2-01:** The trusted job runs on `ubuntu-24.04`; enabled Preflight reaches Azure login and
  then the immutable-bundle producer (`HusayniaSite/pipelines/github/trusted-protected-operations.yml:25-37,107-175`).
  The authoritative image `20260816.277.1` lists Azure CLI 2.89.1, PowerShell 7.6.5, and .NET SDK
  10.0.400, but its database section lists only SQLite/PostgreSQL/MySQL and no `sqlcmd`
  (`https://raw.githubusercontent.com/actions/runner-images/main/images/ubuntu/Ubuntu2404-Readme.md`).
- **FACT R2-02:** real Preflight resolves `sqlcmd` and invokes `sqlcmd -G`; durable SQL lease
  operations do the same (`HusayniaSite/eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1:272-303`;
  `HusayniaSite/eng/artifact/migrations/bundle/Migration.Common.ps1:1208-1211,1625-1635`).
- **FACT R2-03:** C6 already creates and manifest-binds `app/Husaynia.Web.zip` before creating the
  protected bundle (`HusayniaSite/eng/artifact/New-ReleaseArtifact.ps1:63-112,161-209`), and the
  release validator requires and hashes both archives
  (`HusayniaSite/eng/artifact/Test-ReleaseArtifact.ps1:166-175,258-274,399-419`).
- **FACT R2-04:** The locked web dependency graph contains `Microsoft.Data.SqlClient` 6.1.1,
  Azure.Identity, and Unix runtime assets
  (`HusayniaSite/src/Husaynia.Web/packages.lock.json:15-51`;
  `HusayniaSite/src/Husaynia.Web/bin/Release/net10.0/Husaynia.Web.deps.json:31-145`).
- **FACT R2-05:** The protected bundle is an exact, checksummed closure and is made/read back
  read-only before post-login execution
  (`HusayniaSite/eng/artifact/New-ProtectedExecutionBundle.ps1:68-174`;
  `HusayniaSite/eng/artifact/Test-ProtectedExecutionBundle.ps1:22-64,126-221,303-352`;
  `HusayniaSite/pipelines/github/trusted-protected-operations.yml:96-105,137-151`).
- **FACT R2-06:** Preflight policy fixes the query contract to SELECT-only and forbids repository
  SQL and mutation permissions (`HusayniaSite/pipelines/config/promotion-policy.json:72-85`;
  `HusayniaSite/eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1:66-89,274-288`).
- **FACT R2-07:** `Invoke-MigrationBundle.ps1` separately validates the release commit and producer
  run, but then incorrectly requires equality at lines 225-227. Producer manifest v2 already stores
  release and producer commits as separate fields
  (`HusayniaSite/eng/promotion/New-T21ProducerManifest.ps1:236-251`;
  `.ai-org/missions/2026-08-20-husaynia-t21/decisions.md:208-232`).

### Desired state and decision

Use the existing locked `Microsoft.Data.SqlClient` 6.1.1 payload; add no package, project, action,
service, install, restore, download, or container. During the existing C6 build,
`New-ProtectedExecutionBundle.ps1` extracts the exact managed SQL-client dependency closure from
the already-created `app/Husaynia.Web.zip` into `runtime/sqlclient/` inside the protected bundle.
After login, migration code loads only that read-only, bundle-manifest-bound closure into a
collectible `AssemblyLoadContext`, obtains an Azure SQL token from the already-authenticated Azure
CLI, and uses `SqlConnection.AccessToken` for both fixed Preflight and lease SQL.

The runtime source is never the checkout and is not extracted from the application archive after
login. StageOperations keeps its existing deployment-evidence-forbidden stop before OIDC, so the
lease adapter is made executable for the future authorized path without making that path reachable.

### Binding contracts

1. **Protected bundle builder**

   ```text
   New-ProtectedExecutionBundle.ps1
     -RepositoryRoot <root>
     -ApplicationArchivePath <C6>/app/Husaynia.Web.zip
     -DestinationPath <C6>/operations/protected-execution-bundle.zip
   ```

   Scan the application ZIP with the existing canonical path/alias rules and bounded counts/sizes.
   Parse its single `Husaynia.Web.deps.json`, require target `.NETCoreApp,Version=v10.0` and package
   `Microsoft.Data.SqlClient/6.1.1`, recursively select its managed runtime dependency closure,
   prefer the `unix` runtime target, reject native Unix assets, project assemblies, duplicate simple
   names, missing entries, and any source outside the ZIP. Copy only that closure; do not copy the
   web application or all published DLLs.

2. **Protected manifest v1.1**

   `bundle-manifest.json` adds exact `sqlRuntime` fields:

   ```text
   schemaVersion: 1.1.0
   sqlRuntime:
     schemaVersion: 1.0.0
     sourceApplicationPath: app/Husaynia.Web.zip
     sourceApplicationSha256: <64 lowercase hex>
     dependencyManifestSourcePath: Husaynia.Web.deps.json
     packageId: Microsoft.Data.SqlClient
     packageVersion: 6.1.1
     target: .NETCoreApp,Version=v10.0
     runtime: unix
     entryAssembly: runtime/sqlclient/Microsoft.Data.SqlClient.dll
     assemblies[]: { sourcePath, bundlePath, simpleName, sha256, length }
   ```

   `assemblies` is ordinally sorted and its bundle paths extend the validator's exact entry set.
   The normal bundle `files` and `SHA256SUMS` still hash every runtime file. The release manifest
   schema remains `1.0.0`; `Test-ReleaseArtifact.ps1` additionally requires
   `sqlRuntime.sourceApplicationSha256` to equal the manifest hash of `app/Husaynia.Web.zip`.

3. **Validator**

   `Test-ProtectedExecutionBundle.ps1` adds mandatory
   `-ExpectedApplicationSha256 <64hex>` to archive and extracted modes. It validates the v1.1
   runtime contract, exact entries, per-file hashes/lengths/simple assembly names, source
   application hash, Linux entry assembly, and existing read-only checks. The trusted workflow
   supplies `inputs.expectedAppSha256` on every pre- and post-login validation.

4. **SQL adapter in `Migration.Common.ps1`**

   ```text
   Assert-MigrationSqlRuntimeCompatibility(BundleRoot, ExpectedApplicationSha256)
   Get-MigrationAzureSqlAccessToken([LocalAccessTokenProvider])
   Invoke-MigrationSqlQuery(ServerName, DatabaseName, Query, ExpectedColumnCount,
                            CommandTimeoutSeconds, [LocalSqlExecutor])
       -> object[][] Rows
   ```

   Real execution is supported only on Linux x64, PowerShell 7.6+, and .NET 10; all other real
   platforms fail before token acquisition. The pre-token enabled-Preflight check calls
   `Assert-MigrationSqlRuntimeCompatibility`; the post-login producer revalidates before loading.
   The collectible loader resolves only manifest-listed assemblies, defers platform assemblies to
   the host, rejects unknown/duplicate resolutions, invokes SqlClient through reflection without
   compiling a project, and unloads in `finally`.

   Token acquisition runs the resolved Azure CLI directly, without a shell, with a 30-second
   timeout and redirected streams:

   ```text
   az account get-access-token
      --resource https://database.windows.net/
      --query accessToken --output tsv --only-show-errors
   ```

   The token must be one nonempty bounded line. It is held only in memory, assigned to
   `SqlConnection.AccessToken`, never placed in an environment variable/file/argument after
   acquisition, and cleared with all token response references when the nonpooled connection is
   disposed.

   The connection is constructed only from the already-pinned server/database:
   `tcp:<server>,1433`, `Encrypt=True`, `TrustServerCertificate=False`, `Pooling=False`,
   15-second connection timeout, and fixed application name. No credential-bearing connection
   string is logged or persisted. Commands have a 60-second timeout, no automatic retry, and must
   yield exactly one result set, one row, and the requested column count.

5. **Callers**

   `Invoke-MigrationBundle.ps1` replaces Preflight delimiter parsing with the typed 11-column
   result but preserves the query text and every identity/permission assertion. Its lease path and
   `Invoke-SqlMigrationStageLease` use the same adapter for the existing seven-column result and
   unchanged transaction/fence SQL. The existing application-bundle connection-string contract is
   unchanged.

6. **Distinct commits**

   Delete only the producer-equals-release comparison at
   `Invoke-MigrationBundle.ps1:225-227`. Keep release provenance equal to
   `release-manifest.commitSha`, and keep producer `sourceRun.commitSha` equal to resolver/attested
   producer provenance. Evidence continues to carry both values; neither may substitute for the
   other.

7. **Local-only seams**

   Optional `-EnableLocalTestSeams`, `-LocalAccessTokenProvider`, and `-LocalSqlExecutor`
   parameters are accepted only when `GITHUB_ACTIONS` is not `true`. Any seam supplied in GitHub
   Actions fails before token, assembly load, SQL, or evidence. The SQL seam receives only a
   sanitized request object and returns rows; production never branches to it.

### Exact implementation delta

- Change:
  `eng/artifact/New-ReleaseArtifact.ps1`,
  `eng/artifact/Test-ReleaseArtifact.ps1`,
  `eng/artifact/New-ProtectedExecutionBundle.ps1`,
  `eng/artifact/Test-ProtectedExecutionBundle.ps1`,
  `eng/artifact/migrations/bundle/Migration.Common.ps1`,
  `eng/artifact/migrations/bundle/Invoke-MigrationBundle.ps1`,
  `pipelines/github/trusted-protected-operations.yml`, and only the two orchestration hashes in
  `pipelines/config/promotion-policy.json`.
- Update causal/static tests:
  `eng/test/Test-ProtectedExecutionBundle.ps1`,
  `eng/test/Test-PreflightPolicyBoundary.ps1`,
  `eng/test/Test-T21R10EnabledPathRegressions.ps1`,
  `eng/test/Test-MigrationStageLeaseFence.ps1`,
  new `eng/test/Test-MigrationSqlClientExecution.ps1`,
  `eng/ci/Test-PipelineDefinitions.ps1`, and `eng/test/Invoke-T21Validation.ps1`.
- Do not change `src/**`, package/lock files, infrastructure, public contracts, stage policy,
  action pins, caller matrix, producer manifests, authorization schemas, or deployment behavior.

### Failure, security, and observability

- Missing/version-drifted/unsafe SQL runtime content fails C6 creation. Manifest/hash/read-only or
  host incompatibility fails enabled Preflight before OIDC.
- Azure CLI absence, timeout, nonzero exit, malformed token, assembly load failure, connection
  failure, timeout, extra result sets, or shape/value mismatch fails before evidence, upload, or
  attestation. Error output uses stable codes (`T21_SQL_RUNTIME_INVALID`,
  `T21_AZURE_SQL_TOKEN_UNAVAILABLE`, `T21_SQL_EXECUTION_FAILED`,
  `T21_SQL_RESULT_INVALID`) and never includes token, raw CLI output, connection string, SQL text,
  or returned row values.
- Preflight remains nonmutating and does not retry. Lease operations also do not retry after an
  ambiguous transport failure; the existing durable authorization hash, holder, fence token,
  mutation claim, expiry, and replay rules remain the recovery authority.
- Allowed logs are package/version, immutable hashes, stage, target fingerprint, operation,
  elapsed time, row/result counts, and stable status. OIDC claims, access tokens, database
  credentials, raw target metadata, SQL, and results are prohibited.

### Causal test matrix

1. Exact runner/workflow inspection proves no `sqlcmd`, install, restore, tool install, download,
   container, or new action path and proves disabled StageOperations still stops before OIDC.
2. Synthetic app ZIPs prove deterministic exact closure extraction and app-hash cross-binding;
   missing deps, wrong package/version/target, unsafe/alias paths, duplicate assemblies, native
   Unix assets, tamper, extra bundle entries, and read-only drift reject.
3. Local compatibility tests load/unload the selected managed entry assembly without DB/auth and
   reject non-Linux/wrong architecture/runtime/unknown dependency through injected platform facts.
4. Fake token-provider tests assert the exact Azure CLI argument vector, timeout/nonzero/malformed
   failures, and zero token occurrence in stdout, stderr, exceptions, environment, or files.
5. Fake SQL-executor tests run real fixed Preflight parsing: valid SELECT-only row passes; wrong
   target/status/SELECT/mutation bit, extra row/result/column, timeout, and thrown error reject with
   no evidence.
6. Every lease mode uses the same adapter and unchanged SQL; seven-column validation, no retry,
   takeover, replay, active-claim, completion, and expiry tests remain causal.
7. Distinct release/producer commits pass. A release commit mismatch, producer-vs-resolver
   mismatch, attestation mismatch, or swapped run binding still rejects.
8. Supplying either local seam with `GITHUB_ACTIONS=true` rejects before adapter invocation.
9. Protected-bundle hash/manifest tests, focused R9/R10 suites, pipeline definitions, both T21
   harness modes, PowerShell/JSON parse, cached actionlint/Bicep, and secret scan all rerun with
   zero installs/restores/auth/database calls.

### Tradeoffs, risks, and migration

This duplicates a small SQL dependency closure inside C6, increasing bundle size and coupling the
protected manifest to the locked SqlClient version. That cost is accepted because it preserves the
stronger post-login protected-closure boundary and avoids runtime supply-chain activity.

Primary risks are incorrect `.deps.json` asset selection, host/driver incompatibility, accidental
token disclosure, and an external bundle-SHA/workflow-pin mismatch. Mitigations are exact
package/runtime manifest validation, a pre-OIDC loadability check on the real runner contract,
redirected/non-echoed token handling with leakage tests, and disabled-stage atomic rotation with
rollback to the prior C6/pin/SHA tuple.

Rejected alternatives: install `sqlcmd` with apt/brew/dotnet tool; download a client after login;
pull a container; add a migration console project/package; execute the web app/EF startup; use
management-plane REST as a T-SQL substitute; load arbitrary application ZIP assemblies after
login; use Azure.Identity default credential discovery; keep delimiter-parsed CLI output; retry
ambiguous lease writes; or require producer commit to equal release commit.

Forward migration is one atomic internal contract update: build/validator/runtime adapter and
workflow, policy hashes, then tests. Build a new C6, rotate the existing external protected-bundle
SHA and pinned reusable-workflow reference together, and keep all stages disabled until matched.
Rollback restores the prior workflow pin, bundle SHA, C6, policy hashes, and scripts together.
There is no data/schema migration, destructive action, new service, cost change, or external
contract break, so no CTO decision is required.

### R10-R2 implementation conformance — 2026-08-27

The repository implementation conforms to ADR-017/018 without widening the frozen design:
protected manifest v1.1 carries the exact 19-assembly managed closure and application hash;
Preflight and lease execution use the restricted collectible in-process adapter; all three trusted
validations carry `ExpectedApplicationSha256`; local seams fail in GitHub Actions; and producer and
release commits remain separate. Local evidence is recorded in `test-results.md`. No actual C6,
authentication, database call, deployment, install, restore, or external rotation occurred.
Final review remediation keeps two bundle identities explicit:

- Preflight evidence binds `operations/protected-execution-bundle.zip` and its externally fixed
  SHA, because that is the executable closure selected by R10 provenance and authorization.
- `migrations/bundle/Husaynia.Database.Migrations.dll` remains the Apply-only application bundle.
  A null policy SHA and absent DLL are valid for Preflight; every non-Preflight migration mode
  still requires the DLL to be present and manifest/hash-bound before any seam, SQL, or mutation.

The trusted workflow makes files read-only without assigning file-only properties to directories,
then removes write permission recursively on Linux. Azure CLI timeout handling kills the process
tree and waits for confirmed termination before returning a stable redacted error.

Independent R2 test, security, and code-review gates subsequently passed. Final judgment remains
pending.

### R10-R7 checked-in verification-contract closure — 2026-08-27

R7 does not change the frozen runtime graph or widen any workflow permission. It makes the
previously ad-hoc R10 evidence reproducible from checked-in code:

- `promotion-policy.json` now contains one central `trustedActions` registry for checkout,
  setup-dotnet, upload-artifact, Azure login, and build-provenance attestation. Each row binds the
  public repository URL, reviewed tag, exact executable commit, and annotated-tag object where one
  exists.
- `Test-GitHubActionPins.ps1` rejects any external action that is not a reviewed nonzero 40-hex
  commit, permits only the explicitly installation-gated internal reusable-workflow zero
  sentinels, and optionally performs read-only upstream tag/peel resolution.
- `Test-PipelineDefinitions.ps1` executes every checkout job's exact first root-validation body in
  a temporary `Dreamer/HusayniaSite` tree and proves the parent `Dreamer` root is rejected. It also
  proves the official PR audit retains locked mode, `NuGetAudit=true`, `NuGetAuditMode=all`, and a
  nonzero failure path with no official suppression flag.
- `Test-ProtectedExecutionBundle.ps1` invokes the real installed `gh api --help` parser without
  network access and proves the unsupported `--output` flag is absent, while retaining the exact
  arbitrary-binary carrier/hash fixture.
- `Test-T21LifecycleStateMachine.ps1` executes the exact coordinator run body with only local
  GitHub API/resolver fixtures. It proves completed `R` dispatches distinct `P/I`, their completion
  cannot bypass separately completed protected `M`, successful nonproduction `M` dispatches
  distinct `S`, failed `P/I` stops progression, Development cannot fabricate a Staging receipt,
  the checksum is unchanged, the graph is cycle-free, and Production is excluded.

The nonproduction lifecycle remains automatic around the separately approved migration
authorization boundary: `R -> P/I -> [manual protected M] -> S`. Production remains wholly
separate/manual: `R -> P/I -> C -> M -> S`. StageOperations remains stopped before OIDC at the
single deployment-evidence-forbidden boundary, and no C6 is rebuilt.

Adding the central pin registry changes protected policy content. Any later installation must build
a new reviewed C6 and atomically rotate its protected-bundle SHA and nonzero reusable-workflow pins
while all stages remain disabled. No such build, rotation, authentication, dispatch, or deployment
is part of R7.

#### R7 independent-review remediation

The coordinator's correlation lookup now uses supported `gh api --paginate --slurp` discovery,
checks the native exit code and nonempty response, combines every page, and only then applies the
exact correlation suffix. A local state-machine case places the matching run after 100 unrelated
runs and proves it is reused rather than duplicated.

The runtime resolver already emitted `Authorization: Bearer <runtime token>`; an independent
review display redacted that source text. The real defect was a name-specific assertion override
that could replace the captured-header predicate with a source-text predicate. That override was
removed. Both authenticated local resolver fixtures now pass only when every captured request
contains the exact nonsecret fixture bearer.
