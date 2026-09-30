[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BicepPath,

    [string]$InfraRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$infra = (Resolve-Path $InfraRoot).Path
$bicepCommand = if (Test-Path -LiteralPath $BicepPath) {
    (Resolve-Path -LiteralPath $BicepPath).Path
}
else {
    (Get-Command $BicepPath -ErrorAction Stop).Source
}

function Get-ParameterValue {
    param(
        [object]$ParameterDocument,
        [string]$Name
    )

    return $ParameterDocument.parameters.$Name.value
}

function Test-Unapproved {
    param([object]$Value)

    return [string]$Value -match '^UNAPPROVED'
}

function Test-ExactHostCidr {
    param([string]$Value)

    try {
        $network = [System.Net.IPNetwork]::Parse($Value)
        $requiredPrefix = if ($network.BaseAddress.AddressFamily -eq [System.Net.Sockets.AddressFamily]::InterNetworkV6) { 128 } else { 32 }
        return $network.PrefixLength -eq $requiredPrefix
    }
    catch {
        return $false
    }
}

function Write-TemplateResources {
    param(
        [object]$Template,
        [string]$Prefix = ''
    )

    $resources = if ($Template.resources -is [System.Management.Automation.PSCustomObject]) {
        @($Template.resources.PSObject.Properties | ForEach-Object {
            [pscustomobject]@{
                SymbolicName = $_.Name
                Value = $_.Value
            }
        })
    }
    else {
        @($Template.resources | ForEach-Object {
            [pscustomobject]@{
                SymbolicName = [string]$_.name
                Value = $_
            }
        })
    }

    foreach ($entry in $resources) {
        $resource = $entry.Value
        $path = if ([string]::IsNullOrEmpty($Prefix)) {
            [string]$entry.SymbolicName
        }
        else {
            "$Prefix/$($entry.SymbolicName)"
        }

        $condition = if ($null -eq $resource.condition) { '<none>' } else { [string]$resource.condition }
        Write-Output "CAPABILITY type=$($resource.type) name=$path condition=$condition"
        $script:resourceCount++
        $script:resourceTypeCounts[[string]$resource.type] =
            1 + [int]($script:resourceTypeCounts[[string]$resource.type])

        if ($resource.type -eq 'Microsoft.Resources/deployments' -and $null -ne $resource.properties.template) {
            Write-TemplateResources -Template $resource.properties.template -Prefix $path
        }
    }
}

$firstTemplate = $null
$parameterFiles = Get-ChildItem -Path (Join-Path $infra 'parameters') -File -Filter '*.bicepparam' |
    Sort-Object FullName

Write-Output 'STATIC_PLAN planType=compiled-parameter-guard-evaluation azureStateQueried=false liveWhatIf=false deploymentExecuted=false'

foreach ($file in $parameterFiles) {
    $compiled = @(& $bicepCommand build-params $file.FullName --stdout 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Bicep build-params failed for $($file.FullName): $($compiled -join ' | ')"
    }

    $buildResult = ($compiled -join "`n") | ConvertFrom-Json -Depth 100
    $parameters = $buildResult.parametersJson | ConvertFrom-Json -Depth 100
    $template = $buildResult.templateJson | ConvertFrom-Json -Depth 100
    if ($null -eq $firstTemplate) {
        $firstTemplate = $template
    }

    $stage = [string](Get-ParameterValue $parameters 'stage')
    $stageDefinition = $template.variables.stageDefinitions.PSObject.Properties[$stage].Value
    $deploymentEnabled = [bool](Get-ParameterValue $parameters 'deploymentEnabled')
    $cost = Get-ParameterValue $parameters 'costConfiguration'
    $network = Get-ParameterValue $parameters 'networkConfiguration'
    $availability = Get-ParameterValue $parameters 'availabilityConfiguration'
    $alerting = Get-ParameterValue $parameters 'alertConfiguration'
    $retention = Get-ParameterValue $parameters 'retentionConfiguration'
    $capabilities = Get-ParameterValue $parameters 'capabilityRequests'

    $costInputsApproved =
        -not (Test-Unapproved $cost.location) -and
        -not (Test-Unapproved $cost.appServicePlanSkuName) -and
        -not (Test-Unapproved $cost.appServicePlanSkuTier) -and
        -not (Test-Unapproved $cost.sqlSkuName) -and
        -not (Test-Unapproved $cost.sqlSkuTier) -and
        [int]$cost.sqlSkuCapacity -gt 0 -and
        -not (Test-Unapproved $cost.sqlBackupStorageRedundancy) -and
        -not (Test-Unapproved $cost.storageSkuName) -and
        -not (Test-Unapproved $cost.storageAccessTier) -and
        -not (Test-Unapproved $cost.logAnalyticsSkuName) -and
        -not (Test-Unapproved $cost.keyVaultSkuName)

    $availabilityInputsApproved =
        -not (Test-Unapproved $availability.locationId) -and
        [int]$availability.frequencySeconds -gt 0 -and
        [int]$availability.timeoutSeconds -gt 0

    $retentionInputsApproved =
        [int]$retention.mediaVersionDays -gt 0 -and
        [int]$retention.operationalEvidenceDays -gt 0

    $alertingInputsApproved =
        @($alerting.approvedEmailReceivers).Count -gt 0 -and
        ([string]$alerting.runbookUrl).StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -and
        ([string]$alerting.runbookUrl).Length -gt 11 -and
        ([string]$alerting.runbookUrl).Replace('https://', '').Contains('.') -and
        -not ([string]$alerting.runbookUrl).Contains('@') -and
        -not ([string]$alerting.runbookUrl).Contains('?') -and
        -not ([string]$alerting.runbookUrl).Contains('#') -and
        -not [string]::IsNullOrWhiteSpace([string]$alerting.owner) -and
        @($alerting.approvedEmailReceivers | Where-Object {
            [string]::IsNullOrWhiteSpace([string]$_.name) -or
            -not ([string]$_.emailAddress).Contains('@') -or
            ([string]$_.emailAddress).Contains(' ')
        }).Count -eq 0

    $invalidAppRules = @($network.appServiceAccessRestrictions | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.name) -or
        [string]::IsNullOrWhiteSpace([string]$_.description) -or
        [string]$_.action -ne 'Allow' -or
        -not (Test-ExactHostCidr ([string]$_.ipAddress))
    })
    $invalidSqlRules = @($network.sqlFirewallRules | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.name) -or
        ([string]$_.startIpAddress).IndexOf('.') -lt 0 -or
        [string]$_.startIpAddress -ne [string]$_.endIpAddress -or
        [string]$_.startIpAddress -eq '0.0.0.0'
    })
    $invalidStorageRules = @($network.storageIpRules | Where-Object { -not (Test-ExactHostCidr ([string]$_.value)) })
    $invalidKeyVaultRules = @($network.keyVaultIpRules | Where-Object { -not (Test-ExactHostCidr ([string]$_.value)) })
    $networkRulesAreNarrow =
        $invalidAppRules.Count -eq 0 -and
        $invalidSqlRules.Count -eq 0 -and
        $invalidStorageRules.Count -eq 0 -and
        $invalidKeyVaultRules.Count -eq 0

    $connectivityInputsApproved =
        [string]$network.appServicePublicNetworkAccess -eq 'Enabled' -and
        @($network.appServiceAccessRestrictions).Count -gt 0 -and
        [string]$network.sqlPublicNetworkAccess -eq 'Enabled' -and
        @($network.sqlFirewallRules).Count -gt 0 -and
        [string]$network.storagePublicNetworkAccess -eq 'Enabled' -and
        @($network.storageIpRules).Count -gt 0 -and
        [string]$network.keyVaultPublicNetworkAccess -eq 'Enabled' -and
        @($network.keyVaultIpRules).Count -gt 0 -and
        $networkRulesAreNarrow

    $reservedCapabilityRequested =
        [bool]$capabilities.privateEndpoints -or
        [bool]$capabilities.frontDoorOrCdn -or
        [bool]$capabilities.deploymentSlots -or
        [bool]$capabilities.defenderForStorage -or
        [bool]$capabilities.zoneOrEnhancedBackupRedundancy

    $allDeploymentInputsApproved =
        $costInputsApproved -and
        $availabilityInputsApproved -and
        $retentionInputsApproved -and
        $alertingInputsApproved

    $effectiveDeploymentEnabled =
        $deploymentEnabled -and
        [bool]$stageDefinition.t20DeploymentAllowed -and
        $allDeploymentInputsApproved -and
        $connectivityInputsApproved -and
        -not $reservedCapabilityRequested

    if ($effectiveDeploymentEnabled) {
        throw "Static safety failure: supplied stage $stage evaluated as effective."
    }

    Write-Output ([string]::Format(
        'STAGE stage={0} stageCode={1} immutableResourceGroup={2} deploymentRequested={3} t20StageAllowed={4} costInputsApproved={5} connectivityInputsApproved={6} alertingInputsApproved={7} reservedCapabilityRequested={8} productionStorageLockRequired={9} effectiveDeploymentEnabled={10} create=0 update=0 delete=0 replace=0',
        $stage,
        $stageDefinition.code,
        $stageDefinition.resourceGroupName,
        $deploymentEnabled.ToString().ToLowerInvariant(),
        ([bool]$stageDefinition.t20DeploymentAllowed).ToString().ToLowerInvariant(),
        $costInputsApproved.ToString().ToLowerInvariant(),
        $connectivityInputsApproved.ToString().ToLowerInvariant(),
        $alertingInputsApproved.ToString().ToLowerInvariant(),
        $reservedCapabilityRequested.ToString().ToLowerInvariant(),
        ([bool]$stageDefinition.storageLockRequired).ToString().ToLowerInvariant(),
        $effectiveDeploymentEnabled.ToString().ToLowerInvariant()
    ))
}

$script:resourceCount = 0
$script:resourceTypeCounts = @{}
Write-Output 'CAPABILITY_DECLARATION_DELTA_BEGIN'
Write-TemplateResources -Template $firstTemplate
Write-Output 'CAPABILITY_DECLARATION_DELTA_END'
foreach ($resourceType in ($resourceTypeCounts.Keys | Sort-Object)) {
    Write-Output "CAPABILITY_COUNT type=$resourceType declarations=$($resourceTypeCounts[$resourceType])"
}

Write-Output 'DESTRUCTION_OR_REPLACEMENT destroy=0 replace=0 statefulDestroy=0 statefulReplace=0'
Write-Output "SUMMARY suppliedStageCreate=0 suppliedStageUpdate=0 suppliedStageDelete=0 suppliedStageReplace=0 capabilityResourceDeclarations=$resourceCount"
