Set-StrictMode -Version Latest

function Get-StageTargetProperty {
    param($Object, [string]$Name)

    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Assert-StageTargetExactProperties {
    param(
        $Object,
        [string[]]$Expected,
        [string]$Label
    )

    if ($null -eq $Object) {
        throw "$Label is missing."
    }
    $actual = @($Object.PSObject.Properties.Name)
    if (@(Compare-Object ($Expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) {
        throw "$Label fields do not exactly match the reviewed contract."
    }
}

function ConvertTo-AuthorizedActorIdSet {
    param([Parameter(Mandatory = $true)][string]$Value)

    $ids = @($Value -split '[,\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($ids.Count -eq 0 -or @($ids | Where-Object { $_ -notmatch '^[1-9][0-9]*$' }).Count -gt 0) {
        throw 'The protected immutable actor ID allowlist is empty or malformed.'
    }

    $set = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $ids) {
        if (-not $set.Add([string]$id)) {
            throw 'The protected immutable actor ID allowlist contains a duplicate.'
        }
    }
    return ,$set
}

function Get-StageTargetFingerprint {
    param([Parameter(Mandatory = $true)]$TargetMetadata)

    $normalized = @(
        ([string]$TargetMetadata.stage).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlServerFqdn).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlDatabaseName).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlServerResourceId).ToLowerInvariant(),
        ([string]$TargetMetadata.sqlDatabaseResourceId).ToLowerInvariant()
    ) -join "`n"
    return ([Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))
    )).ToLowerInvariant()
}

function Assert-ReviewedProtectedHookRoot {
    param(
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]$Policy
    )

    Assert-CanonicalScriptIntegrityPolicy -Policy $Policy
    $rootRelativePath = [string](Get-StageTargetProperty $Policy 'protectedHookRootRelativePath')
    if ($rootRelativePath -cne 'eng/promotion/hooks' -or
        [IO.Path]::IsPathRooted($rootRelativePath) -or
        $rootRelativePath.Contains('..')) {
        throw 'Protected hook root is not the reviewed repository-owned path.'
    }
    $repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
    $hookRoot = (Resolve-Path -LiteralPath (Join-Path $repository $rootRelativePath)).Path
    $relativeRoot = [IO.Path]::GetRelativePath($repository, $hookRoot)
    if ([IO.Path]::IsPathRooted($relativeRoot) -or $relativeRoot.StartsWith('..')) {
        throw 'Protected hook root escaped the repository checkout.'
    }

    $reviewedFiles = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($supportFile in @($Policy.protectedHookSupportFiles)) {
        Assert-StageTargetExactProperties -Object $supportFile -Label 'Protected hook support file' -Expected @(
            'relativePath',
            'sha256'
        )
        $relativePath = [string]$supportFile.relativePath
        if ($relativePath -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.ps1$' -or
            [string]$supportFile.sha256 -notmatch '^[a-f0-9]{64}$' -or
            $reviewedFiles.ContainsKey($relativePath)) {
            throw 'Protected hook support-file policy is unsafe, malformed, or duplicated.'
        }
        $reviewedFiles.Add($relativePath, [string]$supportFile.sha256)
    }
    foreach ($stage in @($Policy.stages)) {
        foreach ($hook in @($stage.operationHooks)) {
            Assert-StageTargetExactProperties -Object $hook -Label 'Protected operation hook policy' -Expected @(
                'evidenceType',
                'relativePath',
                'sha256'
            )
            $relativePath = [string]$hook.relativePath
            $sha256 = [string]$hook.sha256
            if ($relativePath -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.ps1$' -or
                $sha256 -notmatch '^[a-f0-9]{64}$') {
                throw 'Protected operation hook policy path or checksum is malformed.'
            }
            if ($reviewedFiles.ContainsKey($relativePath)) {
                if ($reviewedFiles[$relativePath] -ne $sha256) {
                    throw 'The same protected hook path has conflicting policy checksums.'
                }
            }
            else {
                $reviewedFiles.Add($relativePath, $sha256)
            }
        }
    }
    if ($reviewedFiles.Count -eq 0) {
        throw 'Protected hook policy contains no reviewed files.'
    }

    $actualFiles = @(Get-ChildItem -LiteralPath $hookRoot -File -Filter '*.ps1' | Sort-Object Name)
    if (@(Compare-Object @($reviewedFiles.Keys | Sort-Object) @($actualFiles.Name | Sort-Object)).Count -ne 0) {
        throw 'Protected hook root does not exactly match the policy-pinned reviewed file set.'
    }
    foreach ($file in $actualFiles) {
        if ((Get-CanonicalTextSha256Lower -Path $file.FullName) -ne $reviewedFiles[$file.Name]) {
            throw "Protected hook checksum changed: $($file.Name)"
        }
    }
    return $hookRoot
}

function Read-StageTargetMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)]$StagePolicy
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'Protected stage target metadata is missing.'
    }

    $metadata = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-StageTargetExactProperties -Object $metadata -Label 'Protected stage target metadata' -Expected @(
        'schemaVersion',
        'source',
        'stage',
        'stageCode',
        'githubEnvironment',
        'dataIsolationKey',
        'serviceConnectionSecretName',
        'immutableResourceGroupName',
        'sqlServerName',
        'sqlServerFqdn',
        'sqlDatabaseName',
        'sqlServerResourceId',
        'sqlDatabaseResourceId',
        'webAppName',
        'webAppResourceId',
        'keyVaultName',
        'secretReferences',
        'providerModes',
        'operationHooks'
    )

    if ([string]$metadata.schemaVersion -ne '1.0.0' -or
        [string]$metadata.source -ne 'T20_T21_PROTECTED_STAGE_CONFIGURATION' -or
        [string]$metadata.stage -ne $Stage -or
        [string]$metadata.stageCode -ne [string]$StagePolicy.stageCode -or
        [string]$metadata.githubEnvironment -ne [string]$StagePolicy.githubEnvironment -or
        [string]$metadata.dataIsolationKey -ne [string]$StagePolicy.dataIsolationKey -or
        [string]$metadata.serviceConnectionSecretName -ne [string]$StagePolicy.serviceConnectionSecretName -or
        [string]$metadata.immutableResourceGroupName -ne [string]$StagePolicy.immutableResourceGroupName) {
        throw 'Protected stage target metadata does not match the immutable stage policy.'
    }

    $stageCode = [regex]::Escape([string]$StagePolicy.stageCode)
    if ([string]$metadata.sqlServerName -notmatch "^husaynia-sql-$stageCode-([a-z0-9]{13})$") {
        throw 'Protected stage SQL server name does not match the immutable T20 naming contract.'
    }
    $stableToken = $Matches[1]
    if ([string]$metadata.sqlServerFqdn -ne "$($metadata.sqlServerName).database.windows.net" -or
        [string]$metadata.sqlDatabaseName -ne "husaynia-$($StagePolicy.stageCode)" -or
        [string]$metadata.webAppName -ne "husaynia-web-$($StagePolicy.stageCode)-$stableToken" -or
        [string]$metadata.keyVaultName -ne "hsy-kv-$($StagePolicy.stageCode)-$stableToken") {
        throw 'Protected stage target names are cross-stage or do not match the T20 handoff.'
    }

    $serverResourcePattern = '^/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/([^/]+)/providers/Microsoft\.Sql/servers/([^/]+)$'
    if ([string]$metadata.sqlServerResourceId -notmatch $serverResourcePattern) {
        throw 'Protected SQL server resource ID is invalid.'
    }
    $subscriptionId = $Matches[1].ToLowerInvariant()
    $resourceGroup = $Matches[2]
    $resourceServerName = $Matches[3]
    $parsedSubscriptionId = [guid]::Empty
    if (-not [guid]::TryParse($subscriptionId, [ref]$parsedSubscriptionId) -or
        $resourceGroup -ne [string]$StagePolicy.immutableResourceGroupName -or
        $resourceServerName -ne [string]$metadata.sqlServerName) {
        throw 'Protected SQL server resource ID does not bind the immutable stage target.'
    }

    $expectedDatabaseResourceId = "$($metadata.sqlServerResourceId)/databases/$($metadata.sqlDatabaseName)"
    $expectedWebAppResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.Web/sites/$($metadata.webAppName)"
    $expectedKeyVaultResourceId = "/subscriptions/$subscriptionId/resourceGroups/$resourceGroup/providers/Microsoft.KeyVault/vaults/$($metadata.keyVaultName)"
    if ([string]$metadata.sqlDatabaseResourceId -cne $expectedDatabaseResourceId -or
        [string]$metadata.webAppResourceId -cne $expectedWebAppResourceId) {
        throw 'Protected database or web application resource ID does not bind the immutable stage target.'
    }

    $expectedSecrets = [ordered]@{
        'ConnectionStrings__HusayniaDatabase' = 'SqlConnectionString'
        'APPLICATIONINSIGHTS_CONNECTION_STRING' = 'ApplicationInsightsConnectionString'
    }
    $secretReferences = @($metadata.secretReferences)
    if ($secretReferences.Count -ne $expectedSecrets.Count) {
        throw 'Protected stage metadata must contain exactly the T20-approved secret references.'
    }
    foreach ($reference in $secretReferences) {
        Assert-StageTargetExactProperties -Object $reference -Label 'Protected secret reference' -Expected @(
            'settingKey',
            'secretName',
            'provider',
            'referenceIdentifier',
            'resourceId',
            'mode',
            'stage'
        )
        $settingKey = [string]$reference.settingKey
        if (-not $expectedSecrets.Contains($settingKey) -or
            [string]$reference.secretName -ne [string]$expectedSecrets[$settingKey] -or
            [string]$reference.provider -ne 'AzureKeyVault' -or
            [string]$reference.resourceId -cne $expectedKeyVaultResourceId -or
            [string]$reference.mode -ne 'reference' -or
            [string]$reference.stage -ne $Stage) {
            throw 'Protected secret reference does not match the T20 handoff and immutable stage.'
        }
        $vaultName = [regex]::Escape([string]$metadata.keyVaultName)
        $secretName = [regex]::Escape([string]$reference.secretName)
        if ([string]$reference.referenceIdentifier -notmatch "^@Microsoft\.KeyVault\(SecretUri=https://$vaultName\.vault\.azure\.net/secrets/$secretName/[A-Za-z0-9-]{1,128}\)$") {
            throw 'Protected secret reference must be an exact version-pinned reference in the stage Key Vault.'
        }
    }

    Assert-StageTargetExactProperties -Object $metadata.providerModes -Label 'Provider modes' -Expected @(
        'payments',
        'messaging',
        'analytics',
        'contentMutation'
    )
    foreach ($providerName in @('payments', 'messaging', 'analytics', 'contentMutation')) {
        $mode = [string](Get-StageTargetProperty $metadata.providerModes $providerName)
        $allowedModes = @((Get-StageTargetProperty $StagePolicy.allowedProviderModes $providerName))
        if ($mode -notin $allowedModes) {
            throw "Provider mode '$providerName=$mode' is not approved for $Stage."
        }
    }

    if ($Stage -ne 'Production') {
        $nonProductionText = (@($secretReferences | ConvertTo-Json -Depth 10 -Compress) +
            @($metadata.providerModes | ConvertTo-Json -Depth 10 -Compress)) -join "`n"
        if ($nonProductionText -match '(?i)(production|prod[-_/]|prd[-_/]|sk_live|pk_live|live[-_ ]?(payment|messag|analytics)|public[-_ ]?messag|production[-_ ]?content)') {
            throw 'Nonproduction protected configuration contains a live or Production-bound reference.'
        }
    }

    $requiredHookTypes = @($StagePolicy.requiredEvidence |
        ForEach-Object { [IO.Path]::GetFileNameWithoutExtension([string]$_) } |
        Where-Object { $_ -notin @('migration-preflight', 'migration-apply') } |
        Sort-Object)
    $policyHooks = @($StagePolicy.operationHooks)
    if ($policyHooks.Count -ne $requiredHookTypes.Count) {
        throw 'Immutable stage policy does not define exactly the required reviewed operation hooks.'
    }
    $policyHooksByType = @{}
    foreach ($policyHook in $policyHooks) {
        Assert-StageTargetExactProperties -Object $policyHook -Label 'Stage operation hook policy' -Expected @(
            'evidenceType',
            'relativePath',
            'sha256'
        )
        if ([string]$policyHook.evidenceType -notin $requiredHookTypes -or
            $policyHooksByType.ContainsKey([string]$policyHook.evidenceType) -or
            [string]$policyHook.relativePath -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.ps1$' -or
            [string]$policyHook.sha256 -notmatch '^[a-f0-9]{64}$') {
            throw 'Immutable stage policy hook mapping is missing, duplicated, unsafe, or not hash-bound.'
        }
        $policyHooksByType[[string]$policyHook.evidenceType] = $policyHook
    }

    $hookTypes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($hook in @($metadata.operationHooks)) {
        Assert-StageTargetExactProperties -Object $hook -Label 'Protected operation hook' -Expected @(
            'evidenceType',
            'relativePath',
            'sha256'
        )
        $evidenceType = [string]$hook.evidenceType
        $expectedHook = $policyHooksByType[$evidenceType]
        $expectedRelativePath = [string](Get-StageTargetProperty $expectedHook 'relativePath')
        $expectedSha256 = [string](Get-StageTargetProperty $expectedHook 'sha256')
        if ($evidenceType -notin $requiredHookTypes -or
            -not $hookTypes.Add([string]$hook.evidenceType) -or
            [string]$hook.relativePath -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.ps1$' -or
            [string]$hook.sha256 -notmatch '^[a-f0-9]{64}$' -or
            [string]$hook.relativePath -cne $expectedRelativePath -or
            [string]$hook.sha256 -cne $expectedSha256) {
            throw 'Protected operation hook mapping is missing, duplicated, unsafe, or not hash-bound.'
        }
    }
    if (@(Compare-Object $requiredHookTypes @($hookTypes) | Sort-Object).Count -ne 0) {
        throw 'Protected operation hook mappings do not exactly cover the stage evidence contract.'
    }

    return $metadata
}
