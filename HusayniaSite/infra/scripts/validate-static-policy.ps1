[CmdletBinding()]
param(
    [string]$InfraRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$BicepPath
)

$ErrorActionPreference = 'Stop'
$infra = (Resolve-Path $InfraRoot).Path
$parametersPath = Join-Path $infra 'parameters'
$mainPath = Join-Path $infra 'main.bicep'
$main = Get-Content -Raw $mainPath
$appService = Get-Content -Raw (Join-Path $infra 'modules\app-service.bicep')
$alerts = Get-Content -Raw (Join-Path $infra 'modules\alerts.bicep')
$storage = Get-Content -Raw (Join-Path $infra 'modules\storage.bicep')
$sql = Get-Content -Raw (Join-Path $infra 'modules\sql.bicep')
$optionalCapabilities = Get-Content -Raw (Join-Path $infra 'modules\optional-capabilities.bicep')
$bicepFiles = Get-ChildItem -Path $infra -Recurse -File -Include '*.bicep', '*.bicepparam'
$source = ($bicepFiles | ForEach-Object { Get-Content -Raw $_.FullName }) -join "`n"
$passed = 0
$failed = 0

function Assert-Policy {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Failure
    )

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

function Resolve-BicepCommand {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    if (Test-Path -LiteralPath $Path) {
        return (Resolve-Path -LiteralPath $Path).Path
    }

    return (Get-Command $Path -ErrorAction Stop).Source
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

function Test-ApprovedRunbookUrl {
    param([string]$Value)

    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    return $uri.Scheme -eq 'https' -and
        -not [string]::IsNullOrWhiteSpace($uri.Host) -and
        [string]::IsNullOrEmpty($uri.UserInfo) -and
        [string]::IsNullOrEmpty($uri.Query) -and
        [string]::IsNullOrEmpty($uri.Fragment)
}

Assert-Policy 'module-layout' (
    (Test-Path $mainPath) -and
    ((Get-ChildItem (Join-Path $infra 'modules') -Filter '*.bicep').Count -ge 6) -and
    ((Get-ChildItem $parametersPath -Filter '*.bicepparam').Count -eq 3)
) 'Expected main entry point, at least six modules, and exactly three supplied stage files.'

$stageFiles = @(
    @{ Name = 'Development'; File = 'development.bicepparam' },
    @{ Name = 'Staging'; File = 'staging.bicepparam' },
    @{ Name = 'Production'; File = 'production.bicepparam' }
)

$costStringFields = @(
    'location',
    'appServicePlanSkuName',
    'appServicePlanSkuTier',
    'sqlSkuName',
    'sqlSkuTier',
    'sqlBackupStorageRedundancy',
    'storageSkuName',
    'storageAccessTier',
    'logAnalyticsSkuName',
    'keyVaultSkuName'
)
$capabilityFields = @(
    'privateEndpoints',
    'frontDoorOrCdn',
    'deploymentSlots',
    'defenderForStorage',
    'zoneOrEnhancedBackupRedundancy'
)

$suppliedStagesAreInert = $true
$suppliedStagesHaveNoCostSelection = $true
$suppliedCapabilitiesAreRequestsOnly = $true
$stageSelectionOnly = $true
$suppliedStageNetworkRulesSafe = $true

foreach ($stageFile in $stageFiles) {
    $content = Get-Content -Raw (Join-Path $parametersPath $stageFile.File)
    $stageSelectionOnly = $stageSelectionOnly -and
        ($content -match "param stage = '$($stageFile.Name)'") -and
        ($content -notmatch '(?m)^param\s+(resourceGroupName|resourcePrefix|nameSuffix|productionDeploymentAuthorization|additionalTags|sqlIdentityConfiguration|linuxFxVersion)\b')
    $suppliedStagesAreInert = $suppliedStagesAreInert -and
        ($content -match 'param deploymentEnabled = false')

    foreach ($field in $costStringFields) {
        $suppliedStagesHaveNoCostSelection = $suppliedStagesHaveNoCostSelection -and
            ($content -match "(?m)^\s*${field}:\s*'UNAPPROVED(?:-LOCATION)?'\s*$")
    }

    $suppliedStagesHaveNoCostSelection = $suppliedStagesHaveNoCostSelection -and
        ($content -match '(?m)^\s*sqlSkuCapacity:\s*0\s*$')

    foreach ($field in $capabilityFields) {
        $suppliedCapabilitiesAreRequestsOnly = $suppliedCapabilitiesAreRequestsOnly -and
            ($content -match "(?m)^\s*${field}:\s*false\s*$")
    }

    $suppliedStageNetworkRulesSafe = $suppliedStageNetworkRulesSafe -and
        ($content -notmatch "(?is)startIpAddress:\s*'0\.0\.0\.0'.*?endIpAddress:\s*'(0\.0\.0\.0|255\.255\.255\.255)'") -and
        ($content -notmatch "(?im)(ipAddress|value):\s*'(0\.0\.0\.0/0|::/0|\*|any)'")
}

Assert-Policy 'supplied-stages-inert' $suppliedStagesAreInert 'Every supplied stage file must keep deploymentEnabled=false.'
Assert-Policy 'per-stage-no-paid-sku' $suppliedStagesHaveNoCostSelection 'Each supplied stage file must retain every cost/SKU field as UNAPPROVED and SQL capacity as zero.'
Assert-Policy 'per-stage-cost-requests-disabled' $suppliedCapabilitiesAreRequestsOnly 'Each supplied stage file must keep every unimplemented cost capability request false.'
Assert-Policy 'stage-selection-only' $stageSelectionOnly 'Stage files may select only the immutable stage; resource group, prefix, suffix, tags, and production authorization are not caller inputs.'

$immutableStageMapping = (
    ($main -match 'var stageDefinitions = \{') -and
    ($main -match "(?s)Development:\s*\{.*?code:\s*'dev'.*?resourceGroupName:\s*'rg-husaynia-development'.*?t20DeploymentAllowed:\s*false") -and
    ($main -match "(?s)Staging:\s*\{.*?code:\s*'stg'.*?resourceGroupName:\s*'rg-husaynia-staging'.*?t20DeploymentAllowed:\s*false") -and
    ($main -match "(?s)Production:\s*\{.*?code:\s*'prd'.*?resourceGroupName:\s*'rg-husaynia-production'.*?t20DeploymentAllowed:\s*false.*?storageLockRequired:\s*true") -and
    ($main -match 'var selectedStage = stageDefinitions\[stage\]') -and
    ($main -match 'var resourceGroupName = selectedStage\.resourceGroupName') -and
    ($main -match 'uniqueString\(subscription\(\)\.subscriptionId\)') -and
    ($main -notmatch '(?m)^param\s+(resourceGroupName|resourcePrefix|nameSuffix|productionDeploymentAuthorization|additionalTags)\b')
)
Assert-Policy 'immutable-stage-targeting' $immutableStageMapping 'Resource group, stage code, production lock metadata, names, and tags must derive from the internal immutable stage map and subscription-derived uniqueness.'

$productionCannotDeploy = (
    ($main -match "(?s)Production:\s*\{.*?t20DeploymentAllowed:\s*false") -and
    ($main -match 'var stageDeploymentEnabled = deploymentRequested && selectedStage\.t20DeploymentAllowed') -and
    ($main -notmatch 'AUTHORIZED_BY_CTO|productionDeploymentAuthorization')
)
Assert-Policy 'production-unconditionally-blocked' $productionCannotDeploy 'Production must be blocked by code with no parameter or magic marker capable of enabling it.'

Assert-Policy 'all-t20-stages-code-blocked' (
    ($main -match "(?s)Development:\s*\{.*?t20DeploymentAllowed:\s*false") -and
    ($main -match "(?s)Staging:\s*\{.*?t20DeploymentAllowed:\s*false") -and
    ($main -match "(?s)Production:\s*\{.*?t20DeploymentAllowed:\s*false")
) 'T20 is a zero-deployment/zero-cost-authorization baseline; every stage requires a later reviewed code/pipeline enablement change.'

Assert-Policy 'typed-inputs-and-guards' (
    ($main -match '@sealed\(\)\s*\r?\ntype CostConfiguration') -and
    ($main -match '@sealed\(\)\s*\r?\ntype NetworkConfiguration') -and
    ($main -match '@sealed\(\)\s*\r?\ntype AlertConfiguration') -and
    ($main -match 'var storageAccountNameValid =') -and
    ($main -match 'var webAppNameValid =') -and
    ($main -match 'var resourceNamesValid =') -and
    ($main -match 'var stageDeploymentEnabled = .*resourceNamesValid')
) 'Typed parameter shapes plus deterministic fail-closed name/network guard expressions are required; stable Bicep assertions are not available.'

Assert-Policy 'package-setting-pipeline-owned' (
    ($appService -notmatch "name:\s*'WEBSITE_RUN_FROM_PACKAGE'") -and
    ($appService -notmatch 'REQUIRED_FROM_IMMUTABLE_PIPELINE_ARTIFACT') -and
    ($main -match "packageSettingKey:\s*'WEBSITE_RUN_FROM_PACKAGE'") -and
    ($main -match "packageConfigurationOwner:\s*'T21_PIPELINE'")
) 'T20 must not own a package URL/value; it may expose only the non-secret T21 setting-key contract.'

Assert-Policy 'fixed-built-in-runtime' (
    ($main -match "var linuxFxVersion = 'DOTNETCORE\|10\.0'") -and
    ($main -notmatch '(?m)^param\s+linuxFxVersion\b') -and
    ($source -notmatch "DOCKER\|")
) 'The built-in runtime must be fixed in reviewed code; caller-selected custom containers are prohibited.'

Assert-Policy 'two-phase-secret-bootstrap' (
    ($appService -notmatch '@Microsoft\.KeyVault\(SecretUri=') -and
    ($appService -notmatch 'vaults/secrets') -and
    ($main -match "secretBootstrapOwner:\s*'T21_PIPELINE'") -and
    ($main -match "sqlConnectionStringSecretName:\s*'SqlConnectionString'") -and
    ($main -match "applicationInsightsSecretName:\s*'ApplicationInsightsConnectionString'") -and
    ($main -match 'cleanApplicationHealthy:\s*false')
) 'Secret creation, secret-scoped RBAC, Key Vault-reference settings, and package application must be an explicit T21 phase after baseline identity creation.'

Assert-Policy 'sql-administrator-pipeline-owned' (
    ($main -notmatch '(?m)^param\s+sqlIdentityConfiguration\b') -and
    ($sql -notmatch 'Microsoft\.Sql/servers/administrators') -and
    ($sql -notmatch 'azureADOnlyAuthentications') -and
    ($main -match "sqlAdministratorBootstrapOwner:\s*'T21_STAGE_SCOPED_PIPELINE'") -and
    ($main -match 'no caller-supplied principal')
) 'T20 must not accept or assign a caller-selected SQL administrator; T21 owns immutable stage-approved Entra bootstrap.'

$availabilityLinkage = (
    ($alerts -match "'odata.type':\s*'Microsoft.Azure.Monitor.WebtestLocationAvailabilityCriteria'") -and
    ($alerts -match 'webTestId:\s*availabilityTest\.id') -and
    ($alerts -match 'componentId:\s*applicationInsights\.id') -and
    ($alerts -match '(?s)scopes:\s*\[\s*availabilityTest\.id\s*applicationInsights\.id\s*\]') -and
    ($alerts -match '(?s)actions:\s*\[\s*\{\s*actionGroupId:\s*actionGroup\.id') -and
    ($alerts -match "name:\s*'\$\{webAppName\}-http5xx'") -and
    ($alerts -match 'runbookUrl') -and
    ($alerts -match 'alertOwner')
)
Assert-Policy 'real-availability-and-actions' $availabilityLinkage 'A real web-test availability criterion must scope the web test and App Insights component and link every alert to the action group; HTTP 5xx remains separate.'

Assert-Policy 'stage-health-target-derived' (
    ($main -match "var availabilityTestUrl = 'https://\$\{webAppName\}\.azurewebsites\.net/health'") -and
    ($main -notmatch '(?m)^\s*testUrl:\s*') -and
    ($main -match 'availabilityTestUrl:\s*availabilityTestUrl')
) 'Synthetic availability must target the deterministic stage-owned web app /health endpoint, not a caller-selected host.'

$alertingGuard = (
    ($main -match 'var invalidAlertReceivers = filter') -and
    ($main -match 'var runbookUrlApproved =') -and
    ($main -match "startsWith\(normalizedRunbookUrl, 'https://'\)") -and
    ($main -match "!contains\(normalizedRunbookUrl, '\?'\)") -and
    ($main -match "!contains\(normalizedRunbookUrl, '#'\)") -and
    ($main -match '!empty\(trim\(alertConfiguration\.owner\)\)') -and
    ($main -match 'allDeploymentInputsApproved = .*alertingInputsApproved') -and
    ($alerts -match 'var alertingInputsValid =') -and
    ($alerts -match '= if \(deployResources && alertingInputsValid\)')
)
Assert-Policy 'receiver-runbook-required' $alertingGuard 'Deployable non-production stages require at least one receiver plus non-empty owner and HTTPS runbook.'

Assert-Policy 'publishing-credentials-disabled' (
    ($appService -match "Microsoft.Web/sites/basicPublishingCredentialsPolicies@") -and
    ($appService -match "name:\s*'ftp'") -and
    ($appService -match "name:\s*'scm'") -and
    ([regex]::Matches($appService, 'allow:\s*false').Count -ge 2) -and
    ($appService -notmatch 'Microsoft.Web/sites/slots@')
) 'FTP and SCM basic publishing credentials must be explicitly disabled and no deployment slot may exist.'

$broadNetworkRejection = (
    $suppliedStageNetworkRulesSafe -and
    ($main -match 'var invalidAppServiceRules =') -and
    ($main -match 'var invalidSqlFirewallRules =') -and
    ($main -match 'var invalidStorageIpRules =') -and
    ($main -match 'var invalidKeyVaultIpRules =') -and
    ([regex]::Matches($main, 'parseCidr\(').Count -ge 3) -and
    ($main -match [regex]::Escape(".cidr != (contains(rule.ipAddress, ':') ? 128 : 32)")) -and
    ($main -match [regex]::Escape(".cidr != (contains(rule.value, ':') ? 128 : 32)")) -and
    ($main -match 'trim\(rule\.startIpAddress\) != trim\(rule\.endIpAddress\)') -and
    ($main -match "trim\(rule\.startIpAddress\) == '0\.0\.0\.0'") -and
    ($main -match 'var networkRulesAreNarrow =') -and
    ($main -match 'allConnectivityInputsApproved = .*networkRulesAreNarrow')
)
Assert-Policy 'broad-network-rules-rejected' $broadNetworkRejection 'Empty, malformed, deny, wildcard, Azure-services, IPv4-all, and IPv6-all rules must not satisfy the deployment guard.'

$leastPrivilegeStorage = (
    ($appService -match 'Microsoft.Authorization/roleDefinitions@') -and
    ($appService -match 'OperationalBlobWriterNoDelete') -and
    ($appService -match 'containers/blobs/read') -and
    ($appService -match 'containers/blobs/write') -and
    ($appService -match 'containers/blobs/add/action') -and
    ($appService -match 'containers/blobs/delete') -and
    ($appService -match 'notDataActions') -and
    ($appService -match 'roleDefinitionId:\s*operationalBlobWriterNoDeleteRole!?\.id') -and
    ([regex]::Matches($appService, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe').Count -eq 1)
)
Assert-Policy 'least-privilege-operational-storage' $leastPrivilegeStorage 'Import-evidence and reports require a scoped custom read/write/add role with delete excluded; only media may retain Blob Data Contributor.'

Assert-Policy 'evidence-version-and-snapshot-retention' (
    ($storage -match "(?s)name:\s*'operationalEvidenceLifecycle'.*?baseBlob:.*?version:.*?snapshot:") -and
    ([regex]::Matches($storage, 'operationalEvidenceRetentionDays').Count -ge 4) -and
    ($main -match 'storageLockRequired:\s*true')
) 'Operational evidence/report lifecycle must delete base blobs, versions, and snapshots at the approved retention, and Production metadata must require a lock.'

Assert-Policy 'sql-security-audit-capability' (
    ($sql -match 'Microsoft.Sql/servers/auditingSettings@') -and
    ($sql -match 'isAzureMonitorTargetEnabled:\s*true') -and
    ($sql -match "category:\s*'SQLSecurityAuditEvents'") -and
    ($sql -notmatch 'BATCH_COMPLETED_GROUP')
) 'Azure SQL auditing and its security-audit diagnostic category must be declared without literal secrets.'

$requestOnlyCapabilities = (
    ($main -match 'var reservedCapabilityRequested =') -and
    ($main -match 'var stageDeploymentEnabled = .*&&\s*!reservedCapabilityRequested') -and
    ($optionalCapabilities -match "'REQUEST_ONLY_NEEDS_DECISION'") -and
    ($optionalCapabilities -notmatch 'Microsoft\.Web/sites/slots') -and
    ($optionalCapabilities -notmatch '(?m)\bEnabled:\s*')
)
Assert-Policy 'optional-capabilities-request-only' $requestOnlyCapabilities 'Unimplemented paid capabilities must be request-only metadata, must never report Enabled, and any request must force effective deployment false.'

$requiredResources = @(
    'Microsoft.Resources/resourceGroups',
    'Microsoft.Web/serverfarms',
    'Microsoft.Web/sites',
    'Microsoft.Sql/servers',
    'Microsoft.Sql/servers/databases',
    'Microsoft.Storage/storageAccounts',
    'Microsoft.KeyVault/vaults',
    'Microsoft.OperationalInsights/workspaces',
    'Microsoft.Insights/components',
    'Microsoft.Insights/webtests',
    'Microsoft.Insights/diagnosticSettings',
    'Microsoft.Insights/metricAlerts',
    'Microsoft.Insights/actionGroups',
    'Microsoft.Authorization/roleAssignments'
)
Assert-Policy 'required-topology' (
    @($requiredResources | Where-Object { $source -notmatch [regex]::Escape($_) }).Count -eq 0
) 'A required isolated PaaS capability resource type is missing.'

$resourceDeclarationLines = @([regex]::Matches($source, '(?m)^resource\s+.*$') | ForEach-Object { $_.Value })
$unguardedResourceLines = @($resourceDeclarationLines | Where-Object {
    $_ -notmatch '\sexisting\s*=\s*\{' -and
    $_ -notmatch '= if \(' -and
    $_ -notmatch '= \[for .*: if \('
})
$moduleDeclarationLines = @([regex]::Matches($main, '(?m)^module\s+.*$') | ForEach-Object { $_.Value })
$unguardedModuleLines = @($moduleDeclarationLines | Where-Object { $_ -notmatch '= if \(stageDeploymentEnabled\)' })
Assert-Policy 'all-resources-guarded' (
    ($main -match 'resource stageResourceGroup .* = if \(stageDeploymentEnabled\)') -and
    ($unguardedResourceLines.Count -eq 0) -and
    ($unguardedModuleLines.Count -eq 0)
) 'Every creatable resource and nested module must be protected by the effective deployment guard.'

$literalSecretPatterns = @(
    '(?i)(accountkey|sharedaccesssignature|clientsecret|client_secret|password)\s*(=|:)\s*[\x22\x27][^\x22\x27]{8,}',
    '(?i)\b(api[_-]?key|token|connectionstring|private[_-]?key)\b\s*(=|:)\s*[\x22\x27][^\x22\x27]{8,}',
    '(?i)(DefaultEndpointsProtocol|AccountKey=|SharedAccessSignature=)',
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'https?://[^/\s\x22\x27]+:[^@\s\x22\x27]+@'
)
$containsLiteralSecret = @($literalSecretPatterns | Where-Object { $source -match $_ }).Count -gt 0
Assert-Policy 'no-literal-secrets' (-not $containsLiteralSecret) 'Detected a credential-like literal in Bicep source or supplied parameter files.'

Assert-Policy 'no-hard-coded-subscription' (
    ($main -match 'subscription\(\)\.subscriptionId') -and
    ($source -notmatch '(?i)subscriptionId\s*[:=]\s*[\x22\x27]?[0-9a-f]{8}-[0-9a-f-]{27,}')
) 'Subscription-derived uniqueness is required, but no literal subscription ID is permitted.'

if (-not [string]::IsNullOrWhiteSpace($BicepPath)) {
    $bicepCommand = Resolve-BicepCommand $BicepPath
    $compiledTemplates = @()
    $compiledParameterDocuments = @()
    $compiledParametersValid = $true

    foreach ($stageFile in $stageFiles) {
        $file = Join-Path $parametersPath $stageFile.File
        $diagnostics = @(& $bicepCommand build-params $file --stdout 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $compiledParametersValid = $false
            Write-Output "COMPILE-DIAGNOSTIC $($stageFile.File) :: $($diagnostics -join ' | ')"
            continue
        }

        try {
            $document = ($diagnostics -join "`n") | ConvertFrom-Json -Depth 100
            $compiledParameterDocuments += ($document.parametersJson | ConvertFrom-Json -Depth 100)
            $compiledTemplates += ($document.templateJson | ConvertFrom-Json -Depth 100)
        }
        catch {
            $compiledParametersValid = $false
            Write-Output "COMPILE-DIAGNOSTIC $($stageFile.File) :: invalid JSON: $($_.Exception.Message)"
        }
    }

    Assert-Policy 'compiled-stage-parameters' $compiledParametersValid 'All supplied bicepparam files must compile to parameter/template JSON.'

    if ($compiledTemplates.Count -gt 0) {
        $compiledJson = $compiledTemplates[0] | ConvertTo-Json -Depth 100 -Compress
        $compiledStageDefinitions = $compiledTemplates[0].variables.stageDefinitions
        Assert-Policy 'compiled-stage-policy' (
            -not [bool]$compiledStageDefinitions.Development.t20DeploymentAllowed -and
            -not [bool]$compiledStageDefinitions.Staging.t20DeploymentAllowed -and
            -not [bool]$compiledStageDefinitions.Production.t20DeploymentAllowed -and
            [bool]$compiledStageDefinitions.Production.storageLockRequired
        ) 'Compiled semantics must show all T20 stages blocked and the Production storage lock required; comments or duplicate decoys cannot satisfy this policy.'

        $compiledStageNetworksSafe = $true
        foreach ($parameterDocument in $compiledParameterDocuments) {
            $network = $parameterDocument.parameters.networkConfiguration.value
            foreach ($rule in @($network.appServiceAccessRestrictions)) {
                $compiledStageNetworksSafe = $compiledStageNetworksSafe -and (Test-ExactHostCidr ([string]$rule.ipAddress))
            }
            foreach ($rule in @($network.storageIpRules)) {
                $compiledStageNetworksSafe = $compiledStageNetworksSafe -and (Test-ExactHostCidr ([string]$rule.value))
            }
            foreach ($rule in @($network.keyVaultIpRules)) {
                $compiledStageNetworksSafe = $compiledStageNetworksSafe -and (Test-ExactHostCidr ([string]$rule.value))
            }
            foreach ($rule in @($network.sqlFirewallRules)) {
                $compiledStageNetworksSafe = $compiledStageNetworksSafe -and
                    -not [string]::IsNullOrWhiteSpace([string]$rule.name) -and
                    [string]$rule.startIpAddress -eq [string]$rule.endIpAddress -and
                    [string]$rule.startIpAddress -ne '0.0.0.0'
            }
        }
        Assert-Policy 'compiled-stage-network-policy' $compiledStageNetworksSafe 'Compiled supplied parameters may contain only exact-host CIDRs and single-IP non-Azure-services SQL rules.'

        $compiledStageAlertingSafe = $true
        foreach ($parameterDocument in $compiledParameterDocuments) {
            $alerting = $parameterDocument.parameters.alertConfiguration.value
            $receivers = @($alerting.approvedEmailReceivers)
            $isEmptyPlaceholder =
                $receivers.Count -eq 0 -and
                [string]::IsNullOrEmpty([string]$alerting.runbookUrl) -and
                [string]::IsNullOrEmpty([string]$alerting.owner)
            $receiversValid = @($receivers | Where-Object {
                [string]::IsNullOrWhiteSpace([string]$_.name) -or
                -not ([string]$_.emailAddress).Contains('@') -or
                ([string]$_.emailAddress).Contains(' ')
            }).Count -eq 0
            $configuredValueValid =
                $receivers.Count -gt 0 -and
                $receiversValid -and
                (Test-ApprovedRunbookUrl ([string]$alerting.runbookUrl)) -and
                -not [string]::IsNullOrWhiteSpace([string]$alerting.owner)
            $compiledStageAlertingSafe = $compiledStageAlertingSafe -and ($isEmptyPlaceholder -or $configuredValueValid)
        }
        Assert-Policy 'compiled-stage-alerting-policy' $compiledStageAlertingSafe 'Supplied alert configuration must be wholly empty/inert or contain valid receivers, owner, and an absolute HTTPS runbook without userinfo, query, or fragment.'

        Assert-Policy 'compiled-availability-linkage' (
            ($compiledJson -match 'Microsoft\.Azure\.Monitor\.WebtestLocationAvailabilityCriteria') -and
            ($compiledJson -match 'webTestId') -and
            ($compiledJson -match 'componentId') -and
            ($compiledJson -match 'actionGroupId') -and
            ($compiledJson -match 'azurewebsites\.net/health')
        ) 'The compiled nested templates must retain web-test/App Insights criteria and action-group linkage.'
    }
}

Write-Output "SUMMARY total=$($passed + $failed) passed=$passed failed=$failed"
if ($failed -gt 0) {
    exit 1
}
