# Husaynia isolated Azure baseline (T20)

This folder is a **non-deploying, non-authorizing infrastructure capability baseline**. It does not
authenticate to Azure, select a paid SKU, run a deployment/what-if, create DNS or certificates, or
claim that a newly created web app is application-ready.

## Immutable stage and naming policy

`main.bicep` contains the immutable Development, Staging, and Production map. A caller may select a
stage, but cannot supply its resource group, code, name suffix, classification tags, or Production
policy. Global names contain the stage code plus `uniqueString(subscription().subscriptionId)`;
no subscription or resource-group ID is hard-coded.

Every supplied `.bicepparam` file has:

- `deploymentEnabled = false`;
- every cost/SKU/capacity input set to `UNAPPROVED` or `0`;
- deny-by-default public networking and empty allow lists;
- no alert receiver, runbook, caller-selected SQL administrator, live synthetic target, or deletion retention;
- every unimplemented capability request set to `false`.

All three immutable stage definitions have `t20DeploymentAllowed: false`. T20 therefore cannot
authorize deployment or cost through any parameter combination. Development/Staging enablement
requires a reviewed T21 code and stage-pipeline change after exact cost, identity, network, and
operations inputs are approved. Production requires a separate future CTO-authorized code and
pipeline change. Its immutable metadata already requires a Storage `CanNotDelete` lock.

## Capability topology, not an application deployment

After that future reviewed **non-production** enablement change replaces every placeholder, passes
exact-host network and alerting guards, and requests no unimplemented capability, Bicep defines:

- an isolated resource group, Linux App Service plan/web app on the reviewed built-in
  `DOTNETCORE|10.0` runtime, managed identity, TLS 1.2,
  `/health`, deny-by-default app/SCM ingress, and disabled FTP/SCM basic publishing credentials;
- Azure SQL infrastructure with authentication-event auditing, SQL security diagnostics, and
  point-in-time/backup-redundancy capability. T21 separately assigns the immutable stage-approved
  Entra administrator and enables Entra-only authentication; T20 accepts no principal ID;
- StorageV2 with shared keys/public blobs disabled, media/import-evidence/report containers,
  versioning, soft delete, and lifecycle deletion for approved base blobs, versions, and snapshots;
- RBAC Key Vault with purge/soft-delete protection and default-deny networking;
- Log Analytics, workspace-based Application Insights, a standard web test, a real
  web-test/App-Insights availability alert, a separate HTTP 5xx alert, SQL capacity alert, and an
  action group.

The web identity has Blob Data Contributor only on `media`. `import-evidence` and `reports` use a
resource-group custom data role limited to blob read/write/add with delete explicitly excluded.
No deployment slot is declared.

The future effective non-production guard permits only exact-host `/32` IPv4 or `/128` IPv6 rules
for App Service, Storage, and Key Vault. Every SQL firewall rule must be one non-`0.0.0.0` address
with identical start/end values; ranges and Azure's allow-services rule are rejected. The synthetic
target is derived as the deterministic stage web app's `/health` URL, never caller-selected. The
guard also requires a valid receiver, non-empty owner, and an HTTPS runbook with a host and no
userinfo, query, or fragment before alert resources can be effective.

## Package and secret ownership

T20 deliberately owns **no App Service application settings**. This prevents a later Bicep reapply
from deleting or overwriting T21-owned package and Key Vault-reference settings.

`t21Handoff` provides only non-secret names and keys:

- package key `WEBSITE_RUN_FROM_PACKAGE`;
- approved secret names `SqlConnectionString` and `ApplicationInsightsConnectionString`;
- runtime setting keys and deterministic resource names;
- an explicit two-phase bootstrap sequence.

T21 must use one distinct federated workload identity per stage with no cross-stage role
assignments. After the baseline creates the web identity and vault, T21 must create approved secret
values without logging them, assign the immutable stage-approved SQL administrator, enable
Entra-only SQL authentication, add secret-scoped read assignments only after the secrets exist,
then apply the Key Vault references and immutable package setting together. Health/readiness may
be claimed only after package, secret resolution, smoke tests, synthetic checks, and alert delivery
pass. Live receiver delivery, secret rotation, restore, and rollback exercises remain T21
operations; T20 enforces the structural handoff without accepting a caller-selected principal.

## Cost-reserved options

Private endpoints, Front Door/CDN, deployment slots, Defender for Storage, and zone/enhanced backup
redundancy are `REQUEST_ONLY_NEEDS_DECISION` metadata. No corresponding resource exists. Any true
request forces effective deployment false. Choosing any SKU, retention, test frequency, log
ingestion level, egress design, or reserved option is a separate cost decision.

## Local validation only

Use a standalone Bicep binary in an isolated temporary directory:

```powershell
pwsh -NoProfile -File .\infra\scripts\validate-bicep.ps1 `
  -BicepPath C:\temporary\isolated\bicep.exe

pwsh -NoProfile -File .\infra\scripts\validate-static-policy.ps1 `
  -BicepPath C:\temporary\isolated\bicep.exe

pwsh -NoProfile -File .\infra\scripts\validate-negative-mutations.ps1 `
  -BicepPath C:\temporary\isolated\bicep.exe

pwsh -NoProfile -File .\infra\scripts\summarize-static-plan.ps1 `
  -BicepPath C:\temporary\isolated\bicep.exe
```

The scripts do not log in, contact Azure, provision, or apply. Compiler output and negative
mutation copies are temporary and deleted. The static plan evaluates only compiled supplied
parameters and template guards; it is not a live Azure what-if and reports zero create/update/
delete/replace for all supplied inert stages.

Bicep assertions are not used because Bicep CLI 0.46.1 still marks them experimental and emits a
production-safety warning. Stable sealed types, decorators, allow-listed literals, deterministic
construction, module-local guards, and the parent effective-deployment guard enforce the same
fail-closed behavior without an experimental feature.

No secret, connection string, access key, certificate, state file, DNS change, deployment command,
or production approval belongs in this folder.
