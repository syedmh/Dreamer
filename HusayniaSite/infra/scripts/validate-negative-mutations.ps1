[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BicepPath,

    [string]$InfraRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$infra = (Resolve-Path $InfraRoot).Path
$validator = Join-Path $infra 'scripts\validate-static-policy.ps1'
$bicepCommand = if (Test-Path -LiteralPath $BicepPath) {
    (Resolve-Path -LiteralPath $BicepPath).Path
}
else {
    (Get-Command $BicepPath -ErrorAction Stop).Source
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "husaynia-t20-negative-$([guid]::NewGuid().ToString('N'))"
$passed = 0
$failed = 0

function Replace-RequiredText {
    param(
        [string]$Path,
        [string]$OldText,
        [string]$NewText
    )

    $content = [IO.File]::ReadAllText($Path)
    $count = ([regex]::Matches($content, [regex]::Escape($OldText))).Count
    if ($count -ne 1) {
        throw "Expected exactly one mutation target in $Path but found $count."
    }

    [IO.File]::WriteAllText($Path, $content.Replace($OldText, $NewText))
}

function Invoke-NegativeCase {
    param(
        [string]$Name,
        [string]$ExpectedPolicy,
        [string]$CompileParameterFile,
        [scriptblock]$Mutate
    )

    $caseRoot = Join-Path $tempRoot $Name
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
    Copy-Item -Path (Join-Path $infra '*') -Destination $caseRoot -Recurse -Force

    try {
        & $Mutate $caseRoot

        $compilePath = Join-Path $caseRoot "parameters\$CompileParameterFile"
        $compileOutput = @(& $bicepCommand build-params $compilePath --stdout 2>&1)
        $compileExit = $LASTEXITCODE
        if ($compileExit -ne 0) {
            $script:failed++
            Write-Output "FAIL  $Name :: mutation did not compile: $($compileOutput -join ' | ')"
            return
        }

        $policyOutput = @(& pwsh -NoProfile -File $validator -InfraRoot $caseRoot -BicepPath $bicepCommand 2>&1)
        $policyExit = $LASTEXITCODE
        $detected = @($policyOutput | Where-Object { $_ -match "^FAIL  $([regex]::Escape($ExpectedPolicy))\b" }).Count -gt 0

        if ($policyExit -ne 0 -and $detected) {
            $script:passed++
            Write-Output "PASS  $Name compiled=true detectedPolicy=$ExpectedPolicy"
        }
        else {
            $script:failed++
            Write-Output "FAIL  $Name :: expected policy '$ExpectedPolicy' was not the blocking failure (exit=$policyExit)."
            $policyOutput | ForEach-Object { Write-Output "      $_" }
        }
    }
    finally {
        if (Test-Path -LiteralPath $caseRoot) {
            Remove-Item -LiteralPath $caseRoot -Recurse -Force
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

    $baselineOutput = @(& pwsh -NoProfile -File $validator -InfraRoot $infra -BicepPath $bicepCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Baseline policy must pass before negative mutation testing: $($baselineOutput -join ' | ')"
    }

    Invoke-NegativeCase 'production-code-bypass-with-decoy' 'compiled-stage-policy' 'production.bicepparam' {
        param($caseRoot)
        $old = "dataClassification: 'production'`r`n    t20DeploymentAllowed: false"
        $new = "dataClassification: 'production'`r`n    t20DeploymentAllowed: true`r`n    // t20DeploymentAllowed: false"
        Replace-RequiredText (Join-Path $caseRoot 'main.bicep') $old $new
    }

    Invoke-NegativeCase 'paid-sku-stage-file' 'per-stage-no-paid-sku' 'development.bicepparam' {
        param($caseRoot)
        Replace-RequiredText (Join-Path $caseRoot 'parameters\development.bicepparam') "appServicePlanSkuName: 'UNAPPROVED'" "appServicePlanSkuName: 'P1v3'"
    }

    Invoke-NegativeCase 'reserved-cost-request' 'per-stage-cost-requests-disabled' 'development.bicepparam' {
        param($caseRoot)
        Replace-RequiredText (Join-Path $caseRoot 'parameters\development.bicepparam') 'privateEndpoints: false' 'privateEndpoints: true'
    }

    Invoke-NegativeCase 'caller-selected-custom-container' 'fixed-built-in-runtime' 'development.bicepparam' {
        param($caseRoot)
        Replace-RequiredText (Join-Path $caseRoot 'main.bicep') "var linuxFxVersion = 'DOTNETCORE|10.0'" "var linuxFxVersion = 'DOCKER|attacker.invalid/image:latest'"
    }

    Invoke-NegativeCase 'caller-selected-sql-admin' 'sql-administrator-pipeline-owned' 'development.bicepparam' {
        param($caseRoot)
        $path = Join-Path $caseRoot 'modules\sql.bicep'
        $content = [IO.File]::ReadAllText($path)
        $injectedParameters = @"
param sqlAdministratorName string = 'UNAPPROVED'
param sqlAdministratorObjectId string = 'UNAPPROVED'
param logAnalyticsWorkspaceResourceId string
"@.TrimEnd()
        $content = $content.Replace('param logAnalyticsWorkspaceResourceId string', $injectedParameters)
        $adminResource = @"

resource sqlAdministrator 'Microsoft.Sql/servers/administrators@2022-05-01-preview' = if (deployResources) {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: sqlAdministratorName
    sid: sqlAdministratorObjectId
    tenantId: tenant().tenantId
  }
}
"@
        $content = $content.Replace("resource sqlServerAuditing 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview'", "$adminResource`nresource sqlServerAuditing 'Microsoft.Sql/servers/auditingSettings@2023-08-01-preview'")
        [IO.File]::WriteAllText($path, $content)
    }

    Invoke-NegativeCase 'api-key-like-literal' 'no-literal-secrets' 'development.bicepparam' {
        param($caseRoot)
        $newLine = 'var api' + "Key = 'negative-test-only-value'"
        $path = Join-Path $caseRoot 'modules\app-service.bicep'
        Replace-RequiredText $path 'param logAnalyticsWorkspaceResourceId string' "param logAnalyticsWorkspaceResourceId string`r`n$newLine"
    }

    Invoke-NegativeCase 'broad-sql-firewall' 'broad-network-rules-rejected' 'development.bicepparam' {
        param($caseRoot)
        $replacement = @"
  sqlFirewallRules: [
    {
      name: 'allow-all'
      startIpAddress: '0.0.0.0'
      endIpAddress: '255.255.255.255'
    }
  ]
"@.TrimEnd()
        Replace-RequiredText (Join-Path $caseRoot 'parameters\development.bicepparam') '  sqlFirewallRules: []' $replacement
    }

    Invoke-NegativeCase 'broad-app-cidr' 'compiled-stage-network-policy' 'development.bicepparam' {
        param($caseRoot)
        $replacement = @"
  appServiceAccessRestrictions: [
    {
      name: 'broad-cidr'
      ipAddress: '8.0.0.0/8'
      action: 'Allow'
      priority: 100
      description: 'negative mutation'
    }
  ]
"@.TrimEnd()
        Replace-RequiredText (Join-Path $caseRoot 'parameters\development.bicepparam') '  appServiceAccessRestrictions: []' $replacement
    }

    Invoke-NegativeCase 'invalid-runbook-url' 'compiled-stage-alerting-policy' 'development.bicepparam' {
        param($caseRoot)
        $replacement = @"
param alertConfiguration = {
  approvedEmailReceivers: [
    {
      name: 'operator'
      emailAddress: 'operator@example.invalid'
    }
  ]
  runbookUrl: 'https://'
  owner: 'Operations'
}
"@.TrimEnd()
        $old = @"
param alertConfiguration = {
  approvedEmailReceivers: []
  runbookUrl: ''
  owner: ''
}
"@.TrimEnd()
        Replace-RequiredText (Join-Path $caseRoot 'parameters\development.bicepparam') $old $replacement
    }

    Invoke-NegativeCase 'missing-alert-guard' 'receiver-runbook-required' 'development.bicepparam' {
        param($caseRoot)
        $path = Join-Path $caseRoot 'main.bicep'
        $old = 'var alertingInputsApproved = length(alertConfiguration.approvedEmailReceivers) > 0 && length(invalidAlertReceivers) == 0 && runbookUrlApproved && !empty(trim(alertConfiguration.owner))'
        Replace-RequiredText $path $old 'var alertingInputsApproved = true'
    }

    Invoke-NegativeCase 'unguarded-resource' 'all-resources-guarded' 'development.bicepparam' {
        param($caseRoot)
        $path = Join-Path $caseRoot 'modules\storage.bicep'
        $old = "resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = if (deployResources) {"
        $new = "resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {"
        Replace-RequiredText $path $old $new
    }

    Invoke-NegativeCase 'false-availability-linkage' 'real-availability-and-actions' 'development.bicepparam' {
        param($caseRoot)
        $path = Join-Path $caseRoot 'modules\alerts.bicep'
        Replace-RequiredText $path 'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria' 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Output "SUMMARY total=$($passed + $failed) passed=$passed failed=$failed"
if ($failed -gt 0) {
    exit 1
}
