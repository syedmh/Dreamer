[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactRoot,
    [string]$ExpectedAppSha256,
    [string]$PolicyPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

$repositoryRoot = Resolve-HusayniaRepositoryRoot
if ([string]::IsNullOrWhiteSpace($PolicyPath)) {
    $PolicyPath = Join-Path $repositoryRoot 'pipelines\config\promotion-policy.json'
}
$artifact = (Resolve-Path -LiteralPath $ArtifactRoot).Path
$failures = [Collections.Generic.List[string]]::new()

function Add-Failure {
    param([string]$Message)
    $script:failures.Add($Message)
}

function Test-JsonSchemaNode {
    param(
        [System.Text.Json.JsonElement]$Value,
        [System.Text.Json.JsonElement]$Schema,
        [string]$JsonPath
    )

    $supportedKeywords = @(
        '$schema', '$id', '$comment', 'title', 'description',
        'type', 'additionalProperties', 'required', 'properties',
        'const', 'enum', 'minLength', 'pattern', 'format',
        'minItems', 'items', 'uniqueItems', 'minimum'
    )
    foreach ($schemaProperty in $Schema.EnumerateObject()) {
        if ($schemaProperty.Name -notin $supportedKeywords) {
            Add-Failure "Unsupported release-manifest schema keyword (validator drift): $($schemaProperty.Name) at $JsonPath"
        }
    }

    $schemaValue = [Text.Json.JsonElement]::new()
    if ($Schema.TryGetProperty('type', [ref]$schemaValue)) {
        $expectedType = $schemaValue.GetString()
        $typeMatches = switch ($expectedType) {
            'object' { $Value.ValueKind -eq [Text.Json.JsonValueKind]::Object }
            'array' { $Value.ValueKind -eq [Text.Json.JsonValueKind]::Array }
            'string' { $Value.ValueKind -eq [Text.Json.JsonValueKind]::String }
            'integer' {
                $integerValue = [long]0
                $Value.ValueKind -eq [Text.Json.JsonValueKind]::Number -and
                    $Value.TryGetInt64([ref]$integerValue)
            }
            default { $false }
        }
        if (-not $typeMatches) {
            Add-Failure "Release manifest schema type mismatch at $JsonPath; expected $expectedType."
            return
        }
    }

    if ($Value.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $required = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('required', [ref]$required)) {
            foreach ($requiredName in $required.EnumerateArray()) {
                $ignored = [Text.Json.JsonElement]::new()
                if (-not $Value.TryGetProperty($requiredName.GetString(), [ref]$ignored)) {
                    Add-Failure "Release manifest is missing required property at $JsonPath.$($requiredName.GetString())."
                }
            }
        }

        $properties = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('properties', [ref]$properties)) {
            $allowAdditional = $true
            $additional = [Text.Json.JsonElement]::new()
            if ($Schema.TryGetProperty('additionalProperties', [ref]$additional) -and
                $additional.ValueKind -eq [Text.Json.JsonValueKind]::False) {
                $allowAdditional = $false
            }

            foreach ($property in $Value.EnumerateObject()) {
                $propertySchema = [Text.Json.JsonElement]::new()
                if (-not $properties.TryGetProperty($property.Name, [ref]$propertySchema)) {
                    if (-not $allowAdditional) {
                        Add-Failure "Release manifest contains an additional property at $JsonPath.$($property.Name)."
                    }
                    continue
                }
                Test-JsonSchemaNode -Value $property.Value -Schema $propertySchema -JsonPath "$JsonPath.$($property.Name)"
            }
        }
    }

    if ($Value.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $minItems = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('minItems', [ref]$minItems) -and
            $Value.GetArrayLength() -lt $minItems.GetInt32()) {
            Add-Failure "Release manifest array has too few items at $JsonPath."
        }
        $uniqueItems = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('uniqueItems', [ref]$uniqueItems) -and $uniqueItems.GetBoolean()) {
            $seenItems = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($item in $Value.EnumerateArray()) {
                if (-not $seenItems.Add($item.GetRawText())) {
                    Add-Failure "Release manifest array contains duplicate items at $JsonPath."
                }
            }
        }
        $itemSchema = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('items', [ref]$itemSchema)) {
            $index = 0
            foreach ($item in $Value.EnumerateArray()) {
                Test-JsonSchemaNode -Value $item -Schema $itemSchema -JsonPath "$JsonPath[$index]"
                $index++
            }
        }
    }

    if ($Value.ValueKind -eq [Text.Json.JsonValueKind]::String) {
        $text = $Value.GetString()
        $minLength = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('minLength', [ref]$minLength) -and $text.Length -lt $minLength.GetInt32()) {
            Add-Failure "Release manifest string is too short at $JsonPath."
        }
        $pattern = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('pattern', [ref]$pattern) -and $text -notmatch $pattern.GetString()) {
            Add-Failure "Release manifest string does not match its pattern at $JsonPath."
        }
        $format = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('format', [ref]$format) -and $format.GetString() -eq 'date-time') {
            $parsedDate = [DateTimeOffset]::MinValue
            if ($text -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$' -or
                -not [DateTimeOffset]::TryParse($text, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsedDate)) {
                Add-Failure "Release manifest date-time is invalid at $JsonPath."
            }
        }
    }

    if ($Value.ValueKind -eq [Text.Json.JsonValueKind]::Number) {
        $minimum = [Text.Json.JsonElement]::new()
        if ($Schema.TryGetProperty('minimum', [ref]$minimum) -and $Value.GetInt64() -lt $minimum.GetInt64()) {
            Add-Failure "Release manifest number is below minimum at $JsonPath."
        }
    }

    $constant = [Text.Json.JsonElement]::new()
    if ($Schema.TryGetProperty('const', [ref]$constant) -and $Value.GetRawText() -cne $constant.GetRawText()) {
        Add-Failure "Release manifest value does not match const at $JsonPath."
    }
    $enum = [Text.Json.JsonElement]::new()
    if ($Schema.TryGetProperty('enum', [ref]$enum)) {
        $allowed = @($enum.EnumerateArray() | ForEach-Object { $_.GetRawText() })
        if ($Value.GetRawText() -notin $allowed) {
            Add-Failure "Release manifest value is not in the allowed enum at $JsonPath."
        }
    }
}

if ((Split-Path -Leaf $artifact) -notmatch '^husaynia-site-[0-9A-Za-z][0-9A-Za-z._-]{0,127}$') {
    Add-Failure 'Artifact directory name does not follow husaynia-site-{version}.'
}

$requiredExact = @(
    'app/Husaynia.Web.zip',
    'contracts/route-manifest.json',
    'contracts/import-manifest.schema.json',
    'operations/protected-execution-bundle.zip',
    'sbom/sbom.spdx.json',
    'release/release-manifest.json',
    'release/SHA256SUMS'
)
foreach ($relative in $requiredExact) {
    $path = Join-Path $artifact $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        Add-Failure "Missing or empty required artifact file: $relative"
    }
}

$requiredDirectories = @(
    'migrations/sql',
    'migrations/bundle',
    'reports/test-results',
    'reports/security',
    'reports/accessibility'
)
foreach ($relativeDirectory in $requiredDirectories) {
    $directory = Join-Path $artifact $relativeDirectory
    $matches = if (Test-Path -LiteralPath $directory -PathType Container) {
        Get-ChildItem -LiteralPath $directory -Recurse -File
    }
    else {
        @()
    }
    if (@($matches).Count -eq 0) {
        Add-Failure "No files matched required artifact path: $relativeDirectory/**"
    }
}

$allFiles = Get-ChildItem -LiteralPath $artifact -Recurse -File
foreach ($file in $allFiles) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Add-Failure "Artifact contains a reparse point: $(Get-RelativeUnixPath -BasePath $artifact -Path $file.FullName)"
    }
}

$manifestPath = Join-Path $artifact 'release\release-manifest.json'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try {
        $manifestJson = Get-Content -LiteralPath $manifestPath -Raw
        $schemaPath = Join-Path (Resolve-HusayniaRepositoryRoot) 'contracts\pipeline\release-manifest.schema.json'
        if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
            throw 'Authoritative release-manifest schema is missing.'
        }
        $manifestDocument = [Text.Json.JsonDocument]::Parse($manifestJson)
        $schemaDocument = [Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $schemaPath -Raw))
        try {
            Test-JsonSchemaNode -Value $manifestDocument.RootElement -Schema $schemaDocument.RootElement -JsonPath '$'
        }
        finally {
            $schemaDocument.Dispose()
            $manifestDocument.Dispose()
        }

        $manifest = $manifestJson | ConvertFrom-Json
        $expectedFields = @(
            'schemaVersion',
            'version',
            'commitSha',
            'builtAtUtc',
            'dotnetSdk',
            'files',
            'databaseCompatibility',
            'requiredConfigurationKeys',
            'prohibitedLiveConfigurationInNonProduction'
        )
        $actualFields = @($manifest.PSObject.Properties.Name)
        if (@(Compare-Object ($expectedFields | Sort-Object) ($actualFields | Sort-Object)).Count -ne 0) {
            Add-Failure 'Release manifest root fields do not exactly match C6.'
        }
        if ($manifest.schemaVersion -ne '1.0.0') { Add-Failure 'Release manifest schemaVersion must be 1.0.0.' }
        if (-not (Test-SafeVersion -Version ([string]$manifest.version)) -or
            (Split-Path -Leaf $artifact) -cne "husaynia-site-$($manifest.version)") {
            Add-Failure 'Artifact directory suffix must exactly match the validated release manifest version.'
        }
        if ([string]$manifest.commitSha -notmatch '^[a-fA-F0-9]{40}$') { Add-Failure 'Release manifest commitSha is invalid.' }
        if ([string]$manifest.dotnetSdk -notmatch '^10\.') { Add-Failure 'Release manifest dotnetSdk is invalid.' }
        $parsedTimestamp = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$manifest.builtAtUtc, [ref]$parsedTimestamp)) {
            Add-Failure 'Release manifest builtAtUtc is invalid.'
        }

        $promotionPolicy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
        Assert-CanonicalScriptIntegrityPolicy -Policy $promotionPolicy
        $trustedExecutionContract = $promotionPolicy.trustedExecutionContract
        $protectedBundlePath = Join-Path $artifact 'operations\protected-execution-bundle.zip'
        $protectedBundleEntry = @($manifest.files | Where-Object {
            $_.path -ceq 'operations/protected-execution-bundle.zip'
        })
        $applicationEntry = @($manifest.files | Where-Object {
            $_.path -ceq 'app/Husaynia.Web.zip'
        })
        if ([string]$trustedExecutionContract.bundlePath -cne
                'operations/protected-execution-bundle.zip' -or
            $protectedBundleEntry.Count -ne 1 -or
            $applicationEntry.Count -ne 1 -or
            -not (Test-Path -LiteralPath $protectedBundlePath -PathType Leaf) -or
            [string]$protectedBundleEntry[0].sha256 -cne
                (Get-Sha256Lower -Path $protectedBundlePath)) {
            Add-Failure 'Protected-execution bundle is not exactly bound by the C6 release manifest.'
        }
        else {
            try {
                Invoke-CheckedScript -Path (
                    Join-Path $repositoryRoot 'eng\artifact\Test-ProtectedExecutionBundle.ps1'
                ) -Parameters @{
                    BundlePath = $protectedBundlePath
                    ExpectedSha256 = [string]$protectedBundleEntry[0].sha256
                    ExpectedApplicationSha256 = [string]$applicationEntry[0].sha256
                } -Label 'validate C6 protected-execution bundle'
            }
            catch {
                Add-Failure "Protected-execution bundle validation failed: $($_.Exception.Message)"
            }
        }
        $expectedOrchestrationPaths = @(
            'migrations/bundle/Invoke-MigrationBundle.ps1',
            'migrations/bundle/Migration.Common.ps1'
        )
        $orchestrationPolicy = @($promotionPolicy.migration.orchestrationFiles)
        if ($orchestrationPolicy.Count -ne $expectedOrchestrationPaths.Count) {
            Add-Failure 'Migration orchestration policy does not contain the exact reviewed file set.'
        }
        foreach ($relative in $expectedOrchestrationPaths) {
            $policyEntry = @($orchestrationPolicy | Where-Object { $_.relativePath -ceq $relative })
            $manifestEntry = @($manifest.files | Where-Object { $_.path -ceq $relative })
            $path = Join-Path $artifact $relative
            if ($policyEntry.Count -ne 1 -or
                [string]$policyEntry[0].sha256 -notmatch '^[a-f0-9]{64}$' -or
                $manifestEntry.Count -ne 1 -or
                -not (Test-Path -LiteralPath $path -PathType Leaf) -or
                (Get-Sha256Lower -Path $path) -cne [string]$manifestEntry[0].sha256 -or
                (Get-CanonicalTextSha256Lower -Path $path) -cne [string]$policyEntry[0].sha256) {
                Add-Failure "Migration orchestration file is missing or not policy-pinned: $relative"
            }
        }

        $applicationBundlePolicy = $promotionPolicy.migration.applicationBundle
        $applicationBundlePath = [string]$applicationBundlePolicy.relativePath
        $applicationBundleSha256 = [string]$applicationBundlePolicy.sha256
        if ($applicationBundlePath -cne 'migrations/bundle/Husaynia.Database.Migrations.dll' -or
            [string]$applicationBundlePolicy.format -cne 'dotnet-managed-migration-bundle-v1') {
            Add-Failure 'Dedicated application migration bundle policy path or format is invalid.'
        }
        $applicationBundleManifestEntries = @($manifest.files | Where-Object { $_.path -ceq $applicationBundlePath })
        $applicationBundleArtifactPath = Join-Path $artifact $applicationBundlePath
        if ($applicationBundleSha256 -match '^[a-f0-9]{64}$') {
            if ($applicationBundleManifestEntries.Count -ne 1 -or
                [string]$applicationBundleManifestEntries[0].sha256 -cne $applicationBundleSha256 -or
                -not (Test-Path -LiteralPath $applicationBundleArtifactPath -PathType Leaf) -or
                (Get-Sha256Lower -Path $applicationBundleArtifactPath) -cne $applicationBundleSha256) {
                Add-Failure 'Dedicated application migration bundle is not exactly policy and manifest hash-bound.'
            }
        }
        elseif ($applicationBundleManifestEntries.Count -ne 0 -or
            (Test-Path -LiteralPath $applicationBundleArtifactPath)) {
            Add-Failure 'An application migration bundle exists without a reviewed immutable policy checksum.'
        }

        $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $manifest.files) {
            $relative = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($relative) -or
                [IO.Path]::IsPathRooted($relative) -or
                $relative.Contains('..') -or
                -not $manifestPaths.Add($relative)) {
                Add-Failure "Unsafe or duplicate manifest path: $relative"
                continue
            }

            $path = Join-Path $artifact $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Add-Failure "Manifest references a missing file: $relative"
                continue
            }

            $file = Get-Item -LiteralPath $path
            if ([long]$entry.length -ne $file.Length) {
                Add-Failure "Manifest length mismatch: $relative"
            }
            if ([string]$entry.sha256 -ne (Get-Sha256Lower -Path $path)) {
                Add-Failure "Manifest checksum mismatch: $relative"
            }
        }

        $payloadPaths = Get-ChildItem -LiteralPath $artifact -Recurse -File |
            ForEach-Object { Get-RelativeUnixPath -BasePath $artifact -Path $_.FullName } |
            Where-Object { $_ -notlike 'release/*' } |
            Sort-Object
        if (@(Compare-Object $payloadPaths (@($manifest.files.path) | Sort-Object)).Count -ne 0) {
            Add-Failure 'Release manifest file list does not exactly cover the non-release payload.'
        }
    }
    catch {
        Add-Failure "Release manifest could not be validated: $($_.Exception.Message)"
    }
}

$checksumPath = Join-Path $artifact 'release\SHA256SUMS'
if (Test-Path -LiteralPath $checksumPath -PathType Leaf) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ($line -notmatch '^([a-f0-9]{64})  (.+)$') {
            Add-Failure "Malformed SHA256SUMS line: $line"
            continue
        }

        $expected = $Matches[1]
        $relative = $Matches[2]
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..') -or -not $seen.Add($relative)) {
            Add-Failure "Unsafe or duplicate SHA256SUMS path: $relative"
            continue
        }
        $path = Join-Path $artifact $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Add-Failure "SHA256SUMS references a missing file: $relative"
        }
        elseif ((Get-Sha256Lower -Path $path) -ne $expected) {
            Add-Failure "SHA256SUMS mismatch: $relative"
        }
    }

    $expectedChecksumPaths = Get-ChildItem -LiteralPath $artifact -Recurse -File |
        ForEach-Object { Get-RelativeUnixPath -BasePath $artifact -Path $_.FullName } |
        Where-Object { $_ -ne 'release/SHA256SUMS' } |
        Sort-Object
    if (@(Compare-Object $expectedChecksumPaths @($seen) | Sort-Object).Count -ne 0) {
        Add-Failure 'SHA256SUMS does not exactly cover every artifact file except itself.'
    }
}

$appPath = Join-Path $artifact 'app\Husaynia.Web.zip'
if (Test-Path -LiteralPath $appPath -PathType Leaf) {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedAppSha256) -and
        (Get-Sha256Lower -Path $appPath) -ne $ExpectedAppSha256.ToLowerInvariant()) {
        Add-Failure 'Application archive does not match the expected immutable checksum.'
    }

    try {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($appPath)
        try {
            foreach ($entry in $zip.Entries) {
                if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains('../') -or $entry.FullName.Contains('..\')) {
                    Add-Failure "Application archive contains an unsafe path: $($entry.FullName)"
                }
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    catch {
        Add-Failure "Application archive is not a readable ZIP: $($_.Exception.Message)"
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL  $_" }
    Write-Output "ARTIFACT-VERIFY status=FAIL failures=$($failures.Count)"
    throw "Release artifact verification failed with $($failures.Count) failure(s)."
}

Write-Output "ARTIFACT-VERIFY status=PASS files=$($allFiles.Count) appSha256=$(Get-Sha256Lower -Path $appPath)"
