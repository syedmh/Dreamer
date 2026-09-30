[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$VerifyUpstream
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\common\Release.Common.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Resolve-HusayniaRepositoryRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$passed = 0
$failed = 0

function Assert-ActionPin {
    param([string]$Name, [bool]$Condition, [string]$Failure)

    if ($Condition) {
        $script:passed++
        Write-Output "PASS  $Name"
    }
    else {
        $script:failed++
        Write-Output "FAIL  $Name :: $Failure"
    }
}

$expected = @(
    [pscustomobject]@{
        name = 'actions/checkout'
        repositoryUrl = 'https://github.com/actions/checkout.git'
        tag = 'v4.2.2'
        commitSha = '11bd71901bbe5b1630ceea73d27597364c9af683'
        tagObjectSha = ''
    },
    [pscustomobject]@{
        name = 'actions/setup-dotnet'
        repositoryUrl = 'https://github.com/actions/setup-dotnet.git'
        tag = 'v4.3.1'
        commitSha = '67a3573c9a986a3f9c594539f4ab511d57bb3ce9'
        tagObjectSha = ''
    },
    [pscustomobject]@{
        name = 'actions/upload-artifact'
        repositoryUrl = 'https://github.com/actions/upload-artifact.git'
        tag = 'v4.6.2'
        commitSha = 'ea165f8d65b6e75b540449e92b4886f43607fa02'
        tagObjectSha = ''
    },
    [pscustomobject]@{
        name = 'azure/login'
        repositoryUrl = 'https://github.com/Azure/login.git'
        tag = 'v2.3.1'
        commitSha = '7184910d9eb2b1c5e48f7073824a90609bb9b6d6'
        tagObjectSha = '2035af27c2cea8ff426397d10e3edb28906b51df'
    },
    [pscustomobject]@{
        name = 'actions/attest-build-provenance'
        repositoryUrl = 'https://github.com/actions/attest-build-provenance.git'
        tag = 'v2.4.0'
        commitSha = 'e8998f949152b193b063cb0ec769d69d929409be'
        tagObjectSha = ''
    }
)

$policyPath = Join-Path $RepositoryRoot 'pipelines\config\promotion-policy.json'
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -DateKind String
$configured = @($policy.trustedActions.actions)
Assert-ActionPin 'central-action-pin-policy-is-exact' (
    [string]$policy.trustedActions.resolutionMethod -ceq
        'git-ls-remote-tag-or-peeled-commit-v1' -and
    $configured.Count -eq $expected.Count -and
    @($expected | Where-Object {
            $record = $_
            @($configured | Where-Object {
                    [string]$_.name -ceq $record.name -and
                    [string]$_.repositoryUrl -ceq $record.repositoryUrl -and
                    [string]$_.tag -ceq $record.tag -and
                    [string]$_.commitSha -ceq $record.commitSha -and
                    [string]$_.tagObjectSha -ceq $record.tagObjectSha
                }).Count -ne 1
        }).Count -eq 0
) 'The central action/tag/commit resolution policy is incomplete or inconsistent.'

$workflowRoot = Join-Path $RepositoryRoot 'pipelines\github'
$workflowText = @(Get-ChildItem -LiteralPath $workflowRoot -Filter *.yml -File |
        Sort-Object Name |
        ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }) -join "`n"
$uses = @([regex]::Matches($workflowText, '"uses"\s*:\s*"([^"]+)"') |
        ForEach-Object { $_.Groups[1].Value })
$useErrors = [Collections.Generic.List[string]]::new()
$externalUses = [Collections.Generic.List[object]]::new()
$internalReusableUses = 0
foreach ($use in $uses) {
    if ($use -match '^syedmh/Dreamer/\.github/workflows/[a-z0-9-]+\.yml@([a-f0-9]{40})$') {
        $internalReusableUses++
        continue
    }
    if ($use -notmatch '^([^/@]+/[^@]+)@([a-f0-9]{40})$') {
        $useErrors.Add("not a full-SHA action or approved reusable workflow: $use")
        continue
    }
    $actionName = $Matches[1]
    $commitSha = $Matches[2]
    if ($commitSha -ceq ('0' * 40)) {
        $useErrors.Add("external action uses an all-zero SHA: $use")
        continue
    }
    $record = @($expected | Where-Object { $_.name -ceq $actionName })
    if ($record.Count -ne 1 -or $record[0].commitSha -cne $commitSha) {
        $useErrors.Add("external action is absent from the exact central policy: $use")
        continue
    }
    $externalUses.Add([pscustomobject]@{ name = $actionName; commitSha = $commitSha })
}
Assert-ActionPin 'all-workflow-uses-are-full-nonzero-reviewed-shas' (
    $uses.Count -gt 0 -and
    $internalReusableUses -gt 0 -and
    $useErrors.Count -eq 0 -and
    @($expected | Where-Object {
            $name = $_.name
            @($externalUses | Where-Object { $_.name -ceq $name }).Count -lt 1
        }).Count -eq 0
) "Workflow action pin errors: $($useErrors -join '; ')"

Assert-ActionPin 'invalid-r9-action-pins-are-absent' (
    -not $workflowText.Contains('96b4a1ef7235a096b17240c259729fdd70c83d45') -and
    -not $workflowText.Contains('858f4093d287a904987dfd22abd163280f939550') -and
    -not (Get-Content -LiteralPath $policyPath -Raw).Contains(
        '96b4a1ef7235a096b17240c259729fdd70c83d45') -and
    -not (Get-Content -LiteralPath $policyPath -Raw).Contains(
        '858f4093d287a904987dfd22abd163280f939550')
) 'An unsupported R9 login or attestation pin remains.'

if ($VerifyUpstream) {
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    Assert-ActionPin 'git-is-available-for-readonly-upstream-resolution' (
        $null -ne $git
    ) 'git is unavailable for the requested read-only upstream action resolution.'

    if ($null -ne $git) {
        foreach ($record in $expected) {
            $output = @(& $git.Source ls-remote $record.repositoryUrl `
                    "refs/tags/$($record.tag)" "refs/tags/$($record.tag)^{}" 2>&1)
            $exitCode = $LASTEXITCODE
            $resolved = @{}
            foreach ($line in $output) {
                if ([string]$line -match '^([a-f0-9]{40})\s+(refs/tags/.+)$') {
                    $resolved[$Matches[2]] = $Matches[1]
                }
            }
            $tagRef = "refs/tags/$($record.tag)"
            $peeledRef = "$tagRef^{}"
            $actualCommit = if ($resolved.ContainsKey($peeledRef)) {
                [string]$resolved[$peeledRef]
            }
            elseif ($resolved.ContainsKey($tagRef)) {
                [string]$resolved[$tagRef]
            }
            else {
                ''
            }
            $tagObjectValid = if ([string]::IsNullOrWhiteSpace($record.tagObjectSha)) {
                $resolved.ContainsKey($tagRef) -and
                [string]$resolved[$tagRef] -ceq $record.commitSha -and
                -not $resolved.ContainsKey($peeledRef)
            }
            else {
                $resolved.ContainsKey($tagRef) -and
                [string]$resolved[$tagRef] -ceq $record.tagObjectSha -and
                $resolved.ContainsKey($peeledRef)
            }
            Assert-ActionPin "upstream-tag-resolves-exactly-$($record.name)" (
                $exitCode -eq 0 -and
                $actualCommit -ceq $record.commitSha -and
                $tagObjectValid -and
                @($resolved.Keys | Where-Object { $_ -notin @($tagRef, $peeledRef) }).Count -eq 0
            ) (
                "Read-only resolution mismatch for $($record.repositoryUrl) $($record.tag): " +
                "exit=$exitCode expected=$($record.commitSha) actual=$actualCommit " +
                "output=$($output -join ' | ')"
            )
            if ($exitCode -eq 0 -and $actualCommit -ceq $record.commitSha) {
                Write-Output (
                    "PIN-UPSTREAM name=$($record.name) tag=$($record.tag) " +
                    "commit=$actualCommit source=$($record.repositoryUrl)"
                )
            }
        }
    }
}

Write-Output (
    "SUMMARY github-action-pins total=$($passed + $failed) passed=$passed failed=$failed " +
    "workflowUses=$($uses.Count) externalUses=$($externalUses.Count) " +
    "internalReusableUses=$internalReusableUses upstream=$($VerifyUpstream.IsPresent) " +
    "auth=0 dispatches=0 deployments=0 database=0 resources=0 installs=0 restores=0"
)
if ($failed -gt 0) { exit 1 }
