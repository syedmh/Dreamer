$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$evidenceRoot = $PSScriptRoot
$temporaryRoot = Join-Path $evidenceRoot "temporary"
$wheelhouse = Join-Path $temporaryRoot "wheelhouse"
$installTarget = Join-Path $temporaryRoot "offline-install"
$tamperedDownload = Join-Path $temporaryRoot "tampered-download"
$requirements = Join-Path $project "requirements.txt"
$readme = Join-Path $project "README.md"
$validator = Join-Path $project "FrameExtraction\validate_release.py"
$baselinePath = Join-Path $evidenceRoot "..\baseline\protected-files.json"
$proxy = "https://packagefeedproxy.microsoft.io/pypi/simple/"
$results = New-Object System.Collections.Generic.List[object]
$savedPipEnvironment = @{}

$expectedArtifacts = [ordered]@{
    "numpy-2.5.2-cp313-cp313-win_amd64.whl" =
        "85AACCB24182C25DF891AD0EC333585967E115269D5F1B17F2C9AE005BC96657"
    "opencv_python-5.0.0.93-cp37-abi3-win_amd64.whl" =
        "F90BA04B8F73BC5C3814037699739F0156F597338A98F05956C684E7C3CA10D2"
    "pillow-12.3.0-cp313-cp313-win_amd64.whl" =
        "1CCA606CD25738DF4ED873D5AD46BBDB3D83B5CBCA291F6B4FF13A4DF6B0BBE8"
}

function Add-Result
{
    param([string] $Name, [bool] $Passed, [string] $Detail)

    $script:results.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })
    Write-Output "RW4_TEST name=$Name passed=$Passed detail=$Detail"
    if (!$Passed)
    {
        throw "RW4 self-test failed: $Name - $Detail"
    }
}

function Write-Utf8NoBom
{
    param([string] $Path, [string] $Text)

    [IO.File]::WriteAllText(
        $Path,
        $Text,
        (New-Object Text.UTF8Encoding($false))
    )
}

function Invoke-Python
{
    param([string[]] $Arguments)

    $output = @(& python @Arguments 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = @($output | ForEach-Object { $_.ToString() })
    }
}

function Get-ProtectedComparison
{
    $baseline = Get-Content -LiteralPath $baselinePath -Raw |
        ConvertFrom-Json
    $missing = New-Object System.Collections.Generic.List[string]
    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($record in $baseline.files)
    {
        $path = Join-Path $project $record.path
        if (!(Test-Path -LiteralPath $path -PathType Leaf))
        {
            $missing.Add($record.path)
            continue
        }
        $item = Get-Item -LiteralPath $path -Force
        $hash = (
            Get-FileHash -LiteralPath $path -Algorithm SHA256
        ).Hash
        if (
            $item.Length -ne [int64] $record.length -or
            $hash -cne [string] $record.sha256
        )
        {
            $changed.Add($record.path)
        }
    }
    return [pscustomobject]@{
        status = if ($missing.Count -eq 0 -and $changed.Count -eq 0)
        {
            "PASS"
        }
        else
        {
            "FAIL"
        }
        checked = $baseline.files.Count
        missing = @($missing)
        changed = @($changed)
    }
}

foreach ($entry in @(Get-ChildItem Env:))
{
    if ($entry.Name -like "PIP_*")
    {
        $savedPipEnvironment[$entry.Name] = $entry.Value
        Remove-Item -LiteralPath ("Env:" + $entry.Name)
    }
}
$env:PIP_CONFIG_FILE = "nul"

try
{
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($wheelhouse) | Out-Null
    [IO.Directory]::CreateDirectory($installTarget) | Out-Null
    [IO.Directory]::CreateDirectory($tamperedDownload) | Out-Null

    $requirementsText = [IO.File]::ReadAllText($requirements)
    $logicalLines = @(
        $requirementsText -split "`r?`n" |
            Where-Object { $_.Trim().Length -gt 0 }
    )
    Add-Result "static-lock-parse" (
        @($logicalLines | Where-Object {
            $_ -ceq "--index-url $proxy"
        }).Count -eq 1 -and
        @($logicalLines | Where-Object {
            $_ -ceq "--only-binary=:all:"
        }).Count -eq 1 -and
        !$requirementsText.Contains("pypi.org") -and
        !$requirementsText.Contains("--extra-index-url") -and
        !$requirementsText.Contains("--trusted-host") -and
        @($requirementsText -split "`r?`n" | Where-Object {
            $_ -match "^[A-Za-z].*=="
        }).Count -eq 3
    ) "index=microsoft_proxy binary_only=true packages=3 hashes=3"

    $config = Invoke-Python @("-m", "pip", "config", "list")
    $configText = $config.Output -join "`n"
    Add-Result "ambient-pip-disabled" (
        $config.ExitCode -eq 0 -and
        !$configText.Contains("index-url") -and
        !$configText.Contains("extra-index") -and
        !$configText.Contains("trusted-host")
    ) "PIP_CONFIG_FILE=nul pip_overrides_removed=true"

    $download = Invoke-Python @(
        "-m", "pip", "download",
        "--disable-pip-version-check",
        "--no-cache-dir",
        "--index-url", $proxy,
        "--require-hashes",
        "--only-binary=:all:",
        "--platform", "win_amd64",
        "--python-version", "3.13",
        "--implementation", "cp",
        "--dest", $wheelhouse,
        "-r", $requirements
    )
    Add-Result "proxy-download" (
        $download.ExitCode -eq 0
    ) "exit=$($download.ExitCode) explicit_proxy=true isolated_wheelhouse=true"

    $artifacts = @(
        Get-ChildItem -LiteralPath $wheelhouse -File -Filter "*.whl" |
            Sort-Object Name
    )
    $actualArtifactNames = @($artifacts.Name)
    $expectedArtifactNames = @($expectedArtifacts.Keys | Sort-Object)
    $hashesMatch = $actualArtifactNames.Count -eq 3 -and
        (Compare-Object $expectedArtifactNames $actualArtifactNames).Count -eq 0
    if ($hashesMatch)
    {
        foreach ($artifact in $artifacts)
        {
            $actualHash = (
                Get-FileHash -LiteralPath $artifact.FullName -Algorithm SHA256
            ).Hash
            if ($actualHash -cne $expectedArtifacts[$artifact.Name])
            {
                $hashesMatch = $false
                break
            }
        }
    }
    Add-Result "artifact-hash-set" $hashesMatch (
        "artifacts=$($artifacts.Count) filenames=exact hashes=exact " +
        "cp313_win_amd64=true"
    )

    $dryRun = Invoke-Python @(
        "-m", "pip", "install",
        "--disable-pip-version-check",
        "--dry-run",
        "--ignore-installed",
        "--no-index",
        "--find-links", $wheelhouse,
        "--require-hashes",
        "--only-binary=:all:",
        "-r", $requirements
    )
    Add-Result "offline-dry-run" (
        $dryRun.ExitCode -eq 0
    ) (
        "exit=$($dryRun.ExitCode) no_index=true find_links=true " +
        "require_hashes=true"
    )

    $install = Invoke-Python @(
        "-m", "pip", "install",
        "--disable-pip-version-check",
        "--no-index",
        "--find-links", $wheelhouse,
        "--require-hashes",
        "--only-binary=:all:",
        "--target", $installTarget,
        "-r", $requirements
    )
    Add-Result "offline-install" (
        $install.ExitCode -eq 0
    ) (
        "exit=$($install.ExitCode) no_index=true find_links=true " +
        "require_hashes=true target=mission_local"
    )

    $escapedTarget = $installTarget.Replace("\", "\\")
    $import = Invoke-Python @(
        "-c",
        (
            "import sys; sys.path.insert(0, r'$escapedTarget'); " +
            "import numpy, cv2, PIL; " +
            "assert numpy.__version__ == '2.5.2'; " +
            "assert cv2.__version__ == '5.0.0'; " +
            "assert PIL.__version__ == '12.3.0'; " +
            "print('RW4_OFFLINE_IMPORT_PASS')"
        )
    )
    Add-Result "offline-import" (
        $import.ExitCode -eq 0 -and
        ($import.Output -contains "RW4_OFFLINE_IMPORT_PASS")
    ) "numpy=2.5.2 cv2=5.0.0 Pillow=12.3.0"

    $tamperedLock = Join-Path $temporaryRoot "requirements-tampered.txt"
    $tamperedText = $requirementsText.Replace(
        "85aaccb24182c25df891ad0ec333585967e115269d5f1b17f2c9ae005bc96657",
        ("0" * 64)
    )
    Write-Utf8NoBom -Path $tamperedLock -Text $tamperedText
    $tampered = Invoke-Python @(
        "-m", "pip", "download",
        "--disable-pip-version-check",
        "--no-index",
        "--find-links", $wheelhouse,
        "--require-hashes",
        "--only-binary=:all:",
        "--dest", $tamperedDownload,
        "-r", $tamperedLock
    )
    $tamperedTextOutput = $tampered.Output -join "`n"
    Add-Result "tampered-hash" (
        $tampered.ExitCode -ne 0 -and
        $tamperedTextOutput.Contains("DO NOT MATCH THE HASHES") -and
        @(
            Get-ChildItem -LiteralPath $tamperedDownload -File
        ).Count -eq 0
    ) "exit=$($tampered.ExitCode) hash_mismatch=true artifacts_written=0"

    $readmeText = [IO.File]::ReadAllText($readme)
    Add-Result "readme-audit" (
        $readmeText.Contains($proxy) -and
        $readmeText.Contains('$env:PIP_CONFIG_FILE = "nul"') -and
        $readmeText.Contains("--no-index --find-links") -and
        $readmeText.Contains("--require-hashes") -and
        $readmeText.Contains("--only-binary=:all:") -and
        $readmeText.Contains("authenticates Godot") -and
        $readmeText.Contains("isolated stage") -and
        $readmeText.Contains("Build\TCFAnimation.exe") -and
        !$readmeText.Contains("https://pypi.org/simple")
    ) (
        "proxy=true ambient_disabled=true offline=true " +
        "authenticated_stage_and_output=true"
    )

    Push-Location ([IO.Path]::GetPathRoot($project))
    try
    {
        $validation = Invoke-Python @("-B", $validator)
    }
    finally
    {
        Pop-Location
    }
    $validationText = $validation.Output -join "`n"
    Add-Result "release-validator" (
        $validation.ExitCode -eq 0 -and
        $validationText.Contains(
            "requirements_lock_ok=index=microsoft_proxy"
        ) -and
        $validationText.Contains(
            "ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true " +
            "export_resources=34 artifact_manifest=checked_if_present"
        )
    ) "exit=$($validation.ExitCode) lock_and_release_markers=true"

    $protected = Get-ProtectedComparison
    Write-Utf8NoBom `
        -Path (Join-Path $evidenceRoot "protected-comparison.json") `
        -Text ($protected | ConvertTo-Json -Depth 5)
    Add-Result "protected-baseline" (
        $protected.status -ceq "PASS"
    ) (
        "checked=$($protected.checked) missing=$($protected.missing.Count) " +
        "changed=$($protected.changed.Count)"
    )

    $exe = Join-Path $project "Build\TCFAnimation.exe"
    $dll = Join-Path $project (
        "Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll"
    )
    $stages = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $project "Build") `
            -Directory `
            -Filter ".release-stage-*" `
            -Force `
            -ErrorAction SilentlyContinue
    )
    Add-Result "final-release-contract" (
        (Test-Path -LiteralPath $exe -PathType Leaf) -and
        (Get-Item -LiteralPath $exe).Length -gt 0 -and
        (Test-Path -LiteralPath $dll -PathType Leaf) -and
        (Get-Item -LiteralPath $dll).Length -gt 0 -and
        $stages.Count -eq 0
    ) (
        "exe=$((Get-Item -LiteralPath $exe).Length) " +
        "dll=$((Get-Item -LiteralPath $dll).Length) stages=0"
    )

    $artifactEvidence = @(
        $artifacts | ForEach-Object {
            [pscustomobject]@{
                name = $_.Name
                length = $_.Length
                sha256 = (
                    Get-FileHash `
                        -LiteralPath $_.FullName `
                        -Algorithm SHA256
                ).Hash
            }
        }
    )
    $summary = [pscustomobject]@{
        status = "PASS"
        tests = $results.Count
        python = (& python --version 2>&1).ToString()
        index = $proxy
        ambientPipConfigDisabled = $true
        artifacts = $artifactEvidence
        results = $results
    }
    Write-Utf8NoBom `
        -Path (Join-Path $evidenceRoot "self-test-results.json") `
        -Text ($summary | ConvertTo-Json -Depth 7)
    Write-Output "RW4_SELF_TEST_PASS tests=$($results.Count)"
}
finally
{
    Get-ChildItem Env: |
        Where-Object Name -Like "PIP_*" |
        ForEach-Object {
            Remove-Item -LiteralPath ("Env:" + $_.Name)
        }
    foreach ($entry in $savedPipEnvironment.GetEnumerator())
    {
        [Environment]::SetEnvironmentVariable(
            $entry.Key,
            $entry.Value,
            "Process"
        )
    }
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

exit 0
