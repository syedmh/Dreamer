[CmdletBinding()]
param(
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path (Resolve-HusayniaRepositoryRoot) 'pipelines\config\promotion-policy.json'
}
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json -DateKind String

# T21-R4 intentionally has no deployment-evidence producer.  Automatic Staging has
# no compatibility path for producer-created run metadata, synthetic run identity,
# candidate deployment evidence, receipt creation, OIDC, or mutation.
$null = Assert-DeploymentEvidence -Policy $policy -Stage Staging
throw 'T21_DEPLOYMENT_EVIDENCE_PRODUCER_FORBIDDEN'
