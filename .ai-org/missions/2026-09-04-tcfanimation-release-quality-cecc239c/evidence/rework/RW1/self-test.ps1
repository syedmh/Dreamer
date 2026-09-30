$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$tooling = Join-Path $project "release-tooling.ps1"
$batch = Join-Path $project "run-animation.bat"
$evidenceRoot = $PSScriptRoot
$temporaryRoot = Join-Path $evidenceRoot "temporary"
$bundledRoot = Join-Path $project (
    ".tools\godot-4.5.1-mono\" +
    "Godot_v4.5.1-stable_mono_win64"
)
$console = Join-Path $bundledRoot (
    "Godot_v4.5.1-stable_mono_win64_console.exe"
)
$editor = Join-Path $bundledRoot "Godot_v4.5.1-stable_mono_win64.exe"
$results = New-Object System.Collections.Generic.List[object]

function Add-Result
{
    param(
        [string] $Name,
        [bool] $Passed,
        [string] $Detail
    )

    $script:results.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })
    Write-Output "RW1_TEST name=$Name passed=$Passed detail=$Detail"
    if (!$Passed)
    {
        throw "RW1 self-test failed: $Name - $Detail"
    }
}

function Get-Failure
{
    param([scriptblock] $Action)

    try
    {
        & $Action | Out-Null
    }
    catch [System.InvalidOperationException]
    {
        return $_.Exception
    }
    throw "Expected InvalidOperationException."
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

function Copy-FixtureFiles
{
    param([string] $Root)

    [IO.Directory]::CreateDirectory($Root) | Out-Null
    Copy-Item -LiteralPath $tooling -Destination $Root
    Copy-Item `
        -LiteralPath (Join-Path $project "release-provenance.json") `
        -Destination $Root
}

if (Test-Path -LiteralPath $temporaryRoot)
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

$oldGodotExe = $env:GODOT_EXE
$oldPath = $env:PATH
try
{
    . $tooling

    $batchText = [IO.File]::ReadAllText($batch)
    Add-Result "batch-static" (
        !$batchText.Contains("GODOT_EXE") -and
        ![regex]::IsMatch($batchText, "(?im)\bcall\b") -and
        !$batchText.Contains("DelayedExpansion")
    ) "no environment reference, call, or delayed expansion"

    $sentinel = Join-Path $temporaryRoot "batch-injection-sentinel.txt"
    $escapedSentinel = $sentinel.Replace("'", "''")
    $env:GODOT_EXE = (
        'C:\missing\candidate.exe" & ' +
        'powershell.exe -NoProfile -Command "' +
        "[IO.File]::WriteAllText('$escapedSentinel','executed')" +
        '" & rem "'
    )
    Push-Location ([IO.Path]::GetPathRoot($project))
    try
    {
        $batchOutput = @(& $env:ComSpec /d /c "`"$batch`"" 2>&1)
        $batchExit = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    $failCount = @(
        $batchOutput |
            Where-Object { $_.ToString().Contains("GODOT_RESOLUTION_FAIL") }
    ).Count
    Add-Result "batch-injection" (
        $batchExit -eq 1 -and
        $failCount -eq 1 -and
        !($batchOutput -match "GODOT_SELECTED") -and
        !(Test-Path -LiteralPath $sentinel)
    ) "exit=$batchExit fail_markers=$failCount sentinel=false"

    $scriptSentinel = Join-Path $temporaryRoot "script-sentinel.txt"
    $cmdCandidate = Join-Path $temporaryRoot "fake.cmd"
    Write-Utf8NoBom $cmdCandidate (
        "@echo off`r`n" +
        "powershell.exe -NoProfile -Command " +
        '"[IO.File]::WriteAllText(''' +
        $scriptSentinel.Replace("'", "''") +
        ''',''executed'')"`r`n' +
        "echo 4.5.1.stable.mono.fake`r`n"
    )
    $env:GODOT_EXE = $cmdCandidate
    $failure = Get-Failure {
        Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    }
    Add-Result "script-candidate" (
        $failure.Data["Reason"] -ceq "candidate_not_exe" -and
        !(Test-Path -LiteralPath $scriptSentinel)
    ) "reason=$($failure.Data["Reason"]) sentinel=false"

    $mutatedDirectory = Join-Path $temporaryRoot "mutated-candidate"
    [IO.Directory]::CreateDirectory($mutatedDirectory) | Out-Null
    $mutatedConsole = Join-Path $mutatedDirectory "renamed.exe"
    Copy-Item -LiteralPath $console -Destination $mutatedConsole
    $bytes = [IO.File]::ReadAllBytes($mutatedConsole)
    $bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 1
    [IO.File]::WriteAllBytes($mutatedConsole, $bytes)
    $env:GODOT_EXE = $mutatedConsole
    $failure = Get-Failure {
        Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    }
    Add-Result "mutated-candidate" (
        $failure.Data["Reason"] -ceq "unapproved_hash"
    ) "reason=$($failure.Data["Reason"])"

    $consoleDirectory = Join-Path $temporaryRoot "console"
    [IO.Directory]::CreateDirectory($consoleDirectory) | Out-Null
    $consoleCopy = Join-Path $consoleDirectory "approved-console.exe"
    Copy-Item -LiteralPath $console -Destination $consoleCopy
    $env:GODOT_EXE = $consoleCopy
    $failure = Get-Failure {
        Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    }
    Add-Result "missing-companion" (
        $failure.Data["Reason"] -ceq "companion_missing"
    ) "reason=$($failure.Data["Reason"])"

    $companionPath = Join-Path $consoleDirectory (
        "Godot_v4.5.1-stable_mono_win64.exe"
    )
    Copy-Item -LiteralPath $editor -Destination $companionPath
    $companionStream = [IO.File]::Open(
        $companionPath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    try
    {
        $companionStream.Seek(-1, [IO.SeekOrigin]::End) | Out-Null
        $lastByte = $companionStream.ReadByte()
        $companionStream.Seek(-1, [IO.SeekOrigin]::End) | Out-Null
        $companionStream.WriteByte($lastByte -bxor 1)
    }
    finally
    {
        $companionStream.Dispose()
    }
    $failure = Get-Failure {
        Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    }
    Add-Result "mutated-companion" (
        $failure.Data["Reason"] -ceq "companion_unapproved"
    ) "reason=$($failure.Data["Reason"])"

    $env:GODOT_EXE = Join-Path $temporaryRoot "missing.exe"
    $failure = Get-Failure {
        Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    }
    Add-Result "configured-authoritative" (
        $failure.Data["Reason"] -ceq "configured_missing"
    ) "reason=$($failure.Data["Reason"]) no_fallback=true"

    $configuredDirectory = Join-Path $temporaryRoot "configured-copy"
    [IO.Directory]::CreateDirectory($configuredDirectory) | Out-Null
    $configuredCopy = Join-Path $configuredDirectory "renamed-approved.exe"
    Copy-Item -LiteralPath $editor -Destination $configuredCopy
    $env:GODOT_EXE = $configuredCopy
    $approved = Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    Add-Result "configured-approved-copy" (
        $approved.Source -ceq "GODOT_EXE" -and
        $approved.Sha256 -ceq
            "C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B"
    ) "source=$($approved.Source) version=$($approved.Version)"

    $pathRoot = Join-Path $temporaryRoot "path-project"
    Copy-FixtureFiles $pathRoot
    $pathBin = Join-Path $temporaryRoot "path-bin"
    [IO.Directory]::CreateDirectory($pathBin) | Out-Null
    Copy-Item `
        -LiteralPath $editor `
        -Destination (Join-Path $pathBin "godot4.exe")
    $env:GODOT_EXE = $null
    $env:PATH = "$pathBin;$oldPath"
    $approved = Get-ApprovedGodot -ProjectRoot $pathRoot -Purpose Run
    Add-Result "path-approved-copy" (
        $approved.Source -ceq "PATH" -and
        $approved.Executable -ceq (Join-Path $pathBin "godot4.exe") -and
        $approved.Sha256 -ceq
            "C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B"
    ) "source=$($approved.Source) version=$($approved.Version)"

    $env:PATH = $oldPath
    $env:GODOT_EXE = $null
    $approved = Get-ApprovedGodot -ProjectRoot $project -Purpose Run
    Add-Result "bundled-approved" (
        $approved.Source -ceq "bundled" -and
        $approved.Sha256 -ceq
            "FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F"
    ) "source=$($approved.Source) version=$($approved.Version)"

    $exportFixture = Join-Path $temporaryRoot "export-project"
    Copy-FixtureFiles $exportFixture
    Copy-Item `
        -LiteralPath (Join-Path $project "export-release.ps1") `
        -Destination $exportFixture
    $buildFixture = Join-Path $exportFixture "Build"
    [IO.Directory]::CreateDirectory($buildFixture) | Out-Null
    $buildSentinel = Join-Path $buildFixture "sentinel.bin"
    [IO.File]::WriteAllBytes($buildSentinel, [byte[]](1, 2, 3, 4))
    $env:GODOT_EXE = $configuredCopy
    Push-Location ([IO.Path]::GetPathRoot($project))
    try
    {
        $exportOutput = @(
            & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File (Join-Path $exportFixture "export-release.ps1") 2>&1
        )
        $exportExit = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    Add-Result "template-before-clean" (
        $exportExit -eq 1 -and
        ($exportOutput -match "reason=template_missing") -and
        (Test-Path -LiteralPath $buildSentinel) -and
        ([IO.File]::ReadAllBytes($buildSentinel).Length -eq 4)
    ) "exit=$exportExit reason=template_missing build_sentinel=preserved"

    $templateSource = Join-Path $bundledRoot (
        "editor_data\export_templates\4.5.1.stable.mono\" +
        "windows_release_x86_64.exe"
    )
    $templateDirectory = Join-Path $configuredDirectory (
        "editor_data\export_templates\4.5.1.stable.mono"
    )
    [IO.Directory]::CreateDirectory($templateDirectory) | Out-Null
    $templateCopy = Join-Path $templateDirectory "windows_release_x86_64.exe"
    Copy-Item -LiteralPath $templateSource -Destination $templateCopy
    $templateStream = [IO.File]::Open(
        $templateCopy,
        [IO.FileMode]::Open,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None
    )
    try
    {
        $templateStream.Seek(-1, [IO.SeekOrigin]::End) | Out-Null
        $lastByte = $templateStream.ReadByte()
        $templateStream.Seek(-1, [IO.SeekOrigin]::End) | Out-Null
        $templateStream.WriteByte($lastByte -bxor 1)
    }
    finally
    {
        $templateStream.Dispose()
    }
    $exportOutput = @(
        & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -NoProfile `
            -NonInteractive `
            -ExecutionPolicy Bypass `
            -File (Join-Path $exportFixture "export-release.ps1") 2>&1
    )
    $exportExit = $LASTEXITCODE
    Add-Result "mutated-template-before-clean" (
        $exportExit -eq 1 -and
        ($exportOutput -match "reason=template_unapproved") -and
        (Test-Path -LiteralPath $buildSentinel) -and
        ([IO.File]::ReadAllBytes($buildSentinel).Length -eq 4)
    ) "exit=$exportExit reason=template_unapproved build_sentinel=preserved"

    $exitFixture = Join-Path $temporaryRoot "exit-project"
    Copy-FixtureFiles $exitFixture
    Copy-Item `
        -LiteralPath (Join-Path $project "run-animation.ps1") `
        -Destination $exitFixture
    Write-Utf8NoBom (Join-Path $exitFixture "project.godot") (
        @(
            "config_version=5",
            "",
            "[application]",
            'run/main_scene="res://Main.tscn"',
            "",
            "[display]",
            "window/size/viewport_width=1",
            "window/size/viewport_height=1",
            "",
            "[rendering]",
            'renderer/rendering_method="gl_compatibility"'
        ) -join "`n"
    )
    Write-Utf8NoBom (Join-Path $exitFixture "Main.tscn") (
        @(
            "[gd_scene load_steps=2 format=3]",
            "",
            '[ext_resource path="res://Exit.gd" type="Script" id="1"]',
            "",
            '[node name="Exit" type="Node"]',
            'script = ExtResource("1")'
        ) -join "`n"
    )
    Write-Utf8NoBom (Join-Path $exitFixture "Exit.gd") (
        @(
            "extends Node",
            "",
            "func _ready():",
            "    get_tree().quit(37)"
        ) -join "`n"
    )
    $env:GODOT_EXE = $console
    Push-Location ([IO.Path]::GetPathRoot($project))
    try
    {
        $exitOutput = @(
            & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File (Join-Path $exitFixture "run-animation.ps1") 2>&1
        )
        $launcherExit = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    Add-Result "launcher-exit-propagation" (
        $launcherExit -eq 37 -and
        @(
            $exitOutput |
                Where-Object { $_.ToString().Contains("GODOT_SELECTED") }
        ).Count -eq 1
    ) "unrelated_cwd=true child_exit=37 launcher_exit=$launcherExit"

    $summary = [pscustomobject]@{
        status = "PASS"
        tests = $results.Count
        results = $results
    } | ConvertTo-Json -Depth 5
    Write-Utf8NoBom (Join-Path $evidenceRoot "self-test-results.json") $summary
    Write-Output "RW1_SELF_TEST_PASS tests=$($results.Count)"
}
finally
{
    $env:GODOT_EXE = $oldGodotExe
    $env:PATH = $oldPath
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        $removed = $false
        for ($attempt = 0; $attempt -lt 20 -and !$removed; $attempt++)
        {
            try
            {
                Remove-Item `
                    -LiteralPath $temporaryRoot `
                    -Recurse `
                    -Force `
                    -ErrorAction Stop
                $removed = $true
            }
            catch
            {
                Start-Sleep -Milliseconds 500
            }
        }
        if (!$removed)
        {
            Write-Warning "RW1 temporary fixture cleanup is pending."
        }
    }
}
