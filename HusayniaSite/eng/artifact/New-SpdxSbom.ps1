[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-fA-F0-9]{40}$')]
    [string]$CommitSha,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$CreatedAtUtc = ([DateTimeOffset]::UtcNow.ToString('O'))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

$packages = [ordered]@{}
$lockFiles = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter 'packages.lock.json' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' } |
    Sort-Object FullName

if ($lockFiles.Count -eq 0) {
    throw 'No packages.lock.json files were found; refusing to create an empty dependency SBOM.'
}

foreach ($lockFile in $lockFiles) {
    $lock = Get-Content -LiteralPath $lockFile.FullName -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($dependency in $framework.Value.PSObject.Properties) {
            $resolvedProperty = $dependency.Value.PSObject.Properties['resolved']
            $resolved = if ($null -ne $resolvedProperty) { [string]$resolvedProperty.Value } else { '' }
            if ([string]::IsNullOrWhiteSpace($resolved)) {
                continue
            }

            $key = "$($dependency.Name)@$resolved"
            if (-not $packages.Contains($key)) {
                $packages[$key] = [ordered]@{
                    SPDXID = "SPDXRef-Package-$(([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key)))).Substring(0, 16))"
                    name = $dependency.Name
                    versionInfo = $resolved
                    downloadLocation = 'NOASSERTION'
                    filesAnalyzed = $false
                    licenseConcluded = 'NOASSERTION'
                    licenseDeclared = 'NOASSERTION'
                    copyrightText = 'NOASSERTION'
                    externalRefs = @(
                        [ordered]@{
                            referenceCategory = 'PACKAGE-MANAGER'
                            referenceType = 'purl'
                            referenceLocator = "pkg:nuget/$([Uri]::EscapeDataString($dependency.Name))@$([Uri]::EscapeDataString($resolved))"
                        }
                    )
                }
            }
        }
    }
}

if ($packages.Count -eq 0) {
    throw 'Lock files contained no resolved packages; refusing to create an empty dependency SBOM.'
}

$rootPackage = [ordered]@{
    SPDXID = 'SPDXRef-Package-HusayniaSite'
    name = 'HusayniaSite'
    versionInfo = $Version
    downloadLocation = 'NOASSERTION'
    filesAnalyzed = $false
    licenseConcluded = 'NOASSERTION'
    licenseDeclared = 'NOASSERTION'
    copyrightText = 'NOASSERTION'
}

$relationships = foreach ($package in $packages.Values) {
    [ordered]@{
        spdxElementId = $rootPackage.SPDXID
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $package.SPDXID
    }
}

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "husaynia-site-$Version"
    documentNamespace = "https://sbom.husaynia.org/releases/$($CommitSha.ToLowerInvariant())/$([Uri]::EscapeDataString($Version))"
    creationInfo = [ordered]@{
        created = ([DateTimeOffset]::Parse($CreatedAtUtc)).ToUniversalTime().ToString('O')
        creators = @('Tool: Husaynia-T21-LockFile-SBOM/1.0.0')
    }
    packages = @($rootPackage) + @($packages.Values)
    relationships = @($relationships)
}

Write-Utf8Json -Value $document -Path $OutputPath -Depth 30
Write-Output "SBOM packages=$($packages.Count) lockFiles=$($lockFiles.Count) output=$OutputPath"
