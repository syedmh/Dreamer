[CmdletBinding()]
param(
    [string]$Stage, [string]$ArtifactRoot, [string]$StageTargetMetadataPath,
    [string]$SourceRunMetadataPath, [string]$OutputPath, [string]$PreflightEvidencePath,
    [string]$BackupEvidencePath, [string]$ProductionAuthorizationContextPath,
    [string]$PolicyPath, [string]$LocalDryRunFixtureRoot
)
. (Join-Path $PSScriptRoot 'OperationHook.Common.ps1')
Invoke-ReviewedOperationHook -EvidenceType 'accessibility' -Parameters $PSBoundParameters
