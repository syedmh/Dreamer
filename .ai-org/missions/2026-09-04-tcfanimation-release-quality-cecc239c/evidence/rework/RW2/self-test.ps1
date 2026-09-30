$ErrorActionPreference = "Stop"

$project = "C:\Users\syedhu\source\repos\Dreamer\TCFAnimation"
$mission = (
    "C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions\" +
    "2026-09-04-tcfanimation-release-quality-cecc239c"
)
$evidenceRoot = $PSScriptRoot
$temporaryRoot = Join-Path $evidenceRoot "temporary"
$approvedConsole = Join-Path $project (
    ".tools\godot-4.5.1-mono\" +
    "Godot_v4.5.1-stable_mono_win64\" +
    "Godot_v4.5.1-stable_mono_win64_console.exe"
)
$results = New-Object System.Collections.Generic.List[object]
$oldGodotExe = $env:GODOT_EXE

function Add-Result
{
    param([string] $Name, [bool] $Passed, [string] $Detail)

    $script:results.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })
    Write-Output "RW2_TEST name=$Name passed=$Passed detail=$Detail"
    if (!$Passed)
    {
        throw "RW2 self-test failed: $Name - $Detail"
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

function Get-TreeSnapshot
{
    param([string[]] $Roots)

    $records = @{}
    foreach ($rootPath in $Roots)
    {
        if (!(Test-Path -LiteralPath $rootPath))
        {
            continue
        }
        $rootItem = Get-Item -LiteralPath $rootPath -Force
        $files = if ($rootItem -is [IO.FileInfo])
        {
            @($rootItem)
        }
        else
        {
            @(Get-ChildItem -LiteralPath $rootPath -Recurse -Force -File)
        }
        foreach ($file in $files)
        {
            $relative = $file.FullName.Substring($project.Length).TrimStart("\")
            $records[$relative] = (
                "$($file.Length):" +
                (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            )
        }
    }
    return $records
}

function Test-SnapshotEqual
{
    param([hashtable] $Before, [hashtable] $After)

    if ($Before.Count -ne $After.Count)
    {
        return $false
    }
    foreach ($key in $Before.Keys)
    {
        if (!$After.ContainsKey($key) -or $Before[$key] -cne $After[$key])
        {
            return $false
        }
    }
    return $true
}

function Get-ProtectedComparison
{
    $baselinePath = Join-Path $mission (
        "evidence\rework\baseline\protected-files.json"
    )
    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    $missing = New-Object System.Collections.Generic.List[string]
    $changed = New-Object System.Collections.Generic.List[string]
    foreach ($record in $baseline.files)
    {
        $path = Join-Path $project $record.path
        if (!(Test-Path -LiteralPath $path -PathType Leaf))
        {
            $missing.Add([string]$record.path)
            continue
        }
        $item = Get-Item -LiteralPath $path -Force
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if (
            [int64]$item.Length -ne [int64]$record.length -or
            $hash -cne [string]$record.sha256
        )
        {
            $changed.Add([string]$record.path)
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
        checked = @($baseline.files).Count
        missing = [string[]]$missing
        changed = [string[]]$changed
    }
}

function New-FixtureProject
{
    param([string] $Name)

    $root = Join-Path $temporaryRoot $Name
    [IO.Directory]::CreateDirectory($root) | Out-Null
    . (Join-Path $project "release-tooling.ps1")
    $manifest = Read-ReleaseStageManifest `
        -ProjectRoot $project `
        -LiteralPath (Join-Path $project "release-stage-manifest.txt")
    foreach ($entry in $manifest)
    {
        $relative = $entry.Replace("/", "\")
        $destination = Join-Path $root $relative
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destination)
        ) | Out-Null
        Copy-Item -LiteralPath (Join-Path $project $relative) -Destination $destination
    }
    foreach ($name in @(
        "release-stage-manifest.txt",
        "release-provenance.json",
        "export-release.ps1"
    ))
    {
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $root
    }
    $baseTooling = [IO.File]::ReadAllText(
        (Join-Path $project "release-tooling.ps1")
    )
    $fixtureOverrides = @'

$script:RW2MockInvocationCount = 0
$script:RW2OriginalRemoveReleaseStage = ${function:Remove-ReleaseStage}

function Invoke-ApprovedGodotProcess
{
    param([string] $Executable, [string[]] $Arguments)

    $script:RW2MockInvocationCount++
    $mode = $env:RW2_FIXTURE_MODE
    $isExport = $Arguments -contains "--export-release"
    if ($isExport -and $mode -in @(
        "success",
        "export-error",
        "missing-dll",
        "cleanup-failure"
    ))
    {
        $output = $Arguments[$Arguments.Count - 1]
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($output)
        ) | Out-Null
        [IO.File]::WriteAllBytes($output, [byte[]](1, 2, 3))
        if ($mode -ne "missing-dll")
        {
            $managed = Join-Path (
                [IO.Path]::GetDirectoryName($output)
            ) "data_TCFAnimation_windows_x86_64\TCFAnimation.dll"
            [IO.Directory]::CreateDirectory(
                [IO.Path]::GetDirectoryName($managed)
            ) | Out-Null
            [IO.File]::WriteAllBytes($managed, [byte[]](4, 5, 6))
        }
    }
    if ($mode -eq "import-failure" -and !$isExport)
    {
        return [pscustomobject]@{
            ExitCode = 37
            ErrorCount = 0
            DiagnosticTail = @("import failed")
        }
    }
    if ($mode -eq "export-failure" -and $isExport)
    {
        return [pscustomobject]@{
            ExitCode = 29
            ErrorCount = 0
            DiagnosticTail = @("export failed")
        }
    }
    if ($mode -eq "import-error" -and !$isExport)
    {
        return [pscustomobject]@{
            ExitCode = 0
            ErrorCount = 1
            DiagnosticTail = @("ERROR: import diagnostic")
        }
    }
    if ($mode -eq "export-error" -and $isExport)
    {
        return [pscustomobject]@{
            ExitCode = 0
            ErrorCount = 1
            DiagnosticTail = @("ERROR: export diagnostic")
        }
    }
    return [pscustomobject]@{
        ExitCode = 0
        ErrorCount = 0
        DiagnosticTail = @()
    }
}

function Remove-ReleaseStage
{
    param([string] $BuildRoot, [string] $StageRoot)

    if ($env:RW2_FIXTURE_MODE -eq "cleanup-failure")
    {
        New-ReleaseToolingFailure -Reason "stage_cleanup_failed"
    }
    & $script:RW2OriginalRemoveReleaseStage @PSBoundParameters
}
'@
    Write-Utf8NoBom `
        -Path (Join-Path $root "release-tooling.ps1") `
        -Text ($baseTooling + "`r`n" + $fixtureOverrides)
    return $root
}

function Invoke-FixtureExport
{
    param([string] $Mode)

    $fixture = New-FixtureProject -Name $Mode
    $outsideSentinel = Join-Path $fixture "outside-sentinel.bin"
    [IO.File]::WriteAllBytes($outsideSentinel, [byte[]](9, 8, 7))
    $env:RW2_FIXTURE_MODE = $Mode
    $env:GODOT_EXE = $approvedConsole
    Push-Location C:\
    try
    {
        $output = @(
            & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File (Join-Path $fixture "export-release.ps1") 2>&1
        )
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    $build = Join-Path $fixture "Build"
    return [pscustomobject]@{
        Root = $fixture
        Output = [string[]]@($output | ForEach-Object ToString)
        ExitCode = $exitCode
        HasExecutable = Test-Path -LiteralPath (
            Join-Path $build "TCFAnimation.exe"
        )
        HasManagedDll = Test-Path -LiteralPath (
            Join-Path $build (
                "data_TCFAnimation_windows_x86_64\TCFAnimation.dll"
            )
        )
        Stages = @(
            Get-ChildItem `
                -LiteralPath $build `
                -Directory `
                -Filter ".release-stage-*" `
                -Force `
                -ErrorAction SilentlyContinue
        ).Count
        OutsideSentinel = (
            (Test-Path -LiteralPath $outsideSentinel) -and
            [IO.File]::ReadAllBytes($outsideSentinel).Length -eq 3
        )
    }
}

if (Test-Path -LiteralPath $temporaryRoot)
{
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

try
{
    . (Join-Path $project "release-tooling.ps1")
    $manifest = Read-ReleaseStageManifest `
        -ProjectRoot $project `
        -LiteralPath (Join-Path $project "release-stage-manifest.txt")
    $uidEntries = @($manifest | Where-Object { $_.EndsWith(".cs.uid") })
    $deniedManifest = @(
        $manifest |
            Where-Object {
                $_ -match (
                    "TCFAnimation\.sln|GlobalInputPolicy\.cs\.uid|" +
                    "ControllerProbe|FrameExtraction|Waving|\.ai-org|" +
                    "\.tools|\.vs|Build|Captures|README|requirements"
                )
            }
    )
    Add-Result "manifest-parser" (
        $manifest.Count -eq 50 -and
        $uidEntries.Count -eq 6 -and
        $deniedManifest.Count -eq 0
    ) (
        "entries=$($manifest.Count) seed_uids=$($uidEntries.Count) " +
        "denied=$($deniedManifest.Count)"
    )

    $build = Join-Path $project "Build"
    if (Test-Path -LiteralPath $build)
    {
        Remove-Item -LiteralPath $build -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($build) | Out-Null
    $stage = New-ReleaseStage `
        -ProjectRoot $project `
        -BuildRoot $build `
        -ManifestEntries $manifest
    $seedInventory = @(Get-ReleaseStageFileInventory `
        -StageProjectRoot $stage.StageProjectRoot)
    Add-Result "copied-seed" (
        $seedInventory.Count -eq 50 -and
        $seedInventory -cnotcontains "TCFAnimation.sln"
    ) "files=$($seedInventory.Count) solution=false"

    $solution = New-ReleaseStageSolution `
        -BuildRoot $build `
        -StageRoot $stage.StageRoot `
        -StageProjectRoot $stage.StageProjectRoot `
        -ManifestEntries $manifest
    $solutionBytes = [IO.File]::ReadAllBytes($solution.Path)
    $solutionText = [Text.UTF8Encoding]::new(
        $false,
        $true
    ).GetString($solutionBytes)
    $inventory = @(Get-ReleaseStageFileInventory `
        -StageProjectRoot $stage.StageProjectRoot)
    Add-Result "generated-solution" (
        $solution.Length -eq 994 -and
        $solution.Sha256 -ceq
            "FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79" -and
        $inventory.Count -eq 51 -and
        @([regex]::Matches($solutionText, "(?m)^Project\(")).Count -eq 1 -and
        $solutionText.Contains('"TCFAnimation.csproj"') -and
        !$solutionText.Contains("ControllerProbe") -and
        !(
            $solutionBytes.Length -ge 3 -and
            $solutionBytes[0] -eq 0xEF -and
            $solutionBytes[1] -eq 0xBB -and
            $solutionBytes[2] -eq 0xBF
        ) -and
        $solutionBytes[$solutionBytes.Length - 2] -eq 13 -and
        $solutionBytes[$solutionBytes.Length - 1] -eq 10
    ) (
        "length=$($solution.Length) hash=$($solution.Sha256) " +
        "inventory=$($inventory.Count) projects=1 controller=false"
    )

    try
    {
        New-ReleaseStageSolution `
            -BuildRoot $build `
            -StageRoot $stage.StageRoot `
            -StageProjectRoot $stage.StageProjectRoot `
            -ManifestEntries $manifest | Out-Null
        throw "Existing solution was accepted."
    }
    catch [System.InvalidOperationException]
    {
        Add-Result "existing-solution" (
            $_.Exception.Data["Reason"] -ceq "stage_solution_failed"
        ) "reason=$($_.Exception.Data["Reason"]) godot_executions=0"
    }
    Remove-ReleaseStage -BuildRoot $build -StageRoot $stage.StageRoot

    $stage = New-ReleaseStage `
        -ProjectRoot $project `
        -BuildRoot $build `
        -ManifestEntries $manifest
    [IO.File]::WriteAllText(
        (Join-Path $stage.StageProjectRoot "extra.txt"),
        "extra"
    )
    try
    {
        New-ReleaseStageSolution `
            -BuildRoot $build `
            -StageRoot $stage.StageRoot `
            -StageProjectRoot $stage.StageProjectRoot `
            -ManifestEntries $manifest | Out-Null
        throw "Extra stage file was accepted."
    }
    catch [System.InvalidOperationException]
    {
        Add-Result "generated-extra" (
            $_.Exception.Data["Reason"] -ceq "stage_solution_failed"
        ) "reason=$($_.Exception.Data["Reason"]) godot_executions=0"
    }
    Remove-ReleaseStage -BuildRoot $build -StageRoot $stage.StageRoot

    $developmentRoots = @(
        (Join-Path $project ".godot"),
        (Join-Path $project "AnimationGeometry.cs.uid"),
        (Join-Path $project "CapturePathPolicy.cs.uid"),
        (Join-Path $project "DialogueModel.cs.uid"),
        (Join-Path $project "DialogueUi.cs.uid"),
        (Join-Path $project "DirectionalTurnStateMachine.cs.uid"),
        (Join-Path $project "GlobalInputPolicy.cs.uid"),
        (Join-Path $project "TurnController.cs.uid")
    )
    $developmentBefore = Get-TreeSnapshot -Roots $developmentRoots
    Push-Location C:\
    try
    {
        $exportOutput = @(
            & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
                -NoProfile `
                -NonInteractive `
                -ExecutionPolicy Bypass `
                -File (Join-Path $project "export-release.ps1") 2>&1
        )
        $exportExit = $LASTEXITCODE
    }
    finally
    {
        Pop-Location
    }
    $developmentAfter = Get-TreeSnapshot -Roots $developmentRoots
    $exe = Join-Path $project "Build\TCFAnimation.exe"
    $dll = Join-Path $project (
        "Build\data_TCFAnimation_windows_x86_64\TCFAnimation.dll"
    )
    $remainingStages = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $project "Build") `
            -Directory `
            -Filter ".release-stage-*" `
            -Force `
            -ErrorAction SilentlyContinue
    )
    $errorLines = @(
        $exportOutput |
            Where-Object { $_.ToString().StartsWith("ERROR:") }
    )
    $passMarkers = @(
        $exportOutput |
            Where-Object { $_.ToString().Contains("RELEASE_EXPORT_PASS") }
    )
    Add-Result "real-export" (
        $exportExit -eq 0 -and
        $errorLines.Count -eq 0 -and
        $passMarkers.Count -eq 1 -and
        (Get-Item -LiteralPath $exe).Length -gt 0 -and
        (Get-Item -LiteralPath $dll).Length -gt 0 -and
        $remainingStages.Count -eq 0
    ) (
        "exit=$exportExit errors=$($errorLines.Count) pass_markers=" +
        "$($passMarkers.Count) exe=$((Get-Item $exe).Length) " +
        "dll=$((Get-Item $dll).Length) stages=$($remainingStages.Count)"
    )
    Add-Result "development-state" (
        Test-SnapshotEqual $developmentBefore $developmentAfter
    ) (
        "before=$($developmentBefore.Count) after=$($developmentAfter.Count) " +
        "unchanged=true"
    )

    $escapedExe = $exe.Replace("'", "''")
    $smokeCommand = (
        '$process = Start-Process -FilePath ''' + $escapedExe +
        ''' -ArgumentList @(''--headless'',''--'',''--verify-runtime'') ' +
        '-NoNewWindow -Wait -PassThru; ' +
        'Write-Output (''SMOKE_CHILD_EXIT='' + $process.ExitCode); ' +
        'exit $process.ExitCode'
    )
    $smokeOutput = @(
        & "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -NoProfile `
            -NonInteractive `
            -Command $smokeCommand 2>&1
    )
    $smokeExit = $LASTEXITCODE
    Add-Result "runtime-smoke" (
        $smokeExit -eq 0 -and
        @(
            $smokeOutput |
                Where-Object {
                    $_.ToString().Contains(
                        "RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true"
                    )
                }
        ).Count -eq 1
    ) "exit=$smokeExit marker=true"

    foreach ($case in @(
        [pscustomobject]@{
            Name = "import-failure"
            Mode = "import-failure"
            Exit = 37
            Reason = "stage_import_failed"
        },
        [pscustomobject]@{
            Name = "export-failure"
            Mode = "export-failure"
            Exit = 29
            Reason = "stage_export_failed"
        },
        [pscustomobject]@{
            Name = "zero-exit-import-error"
            Mode = "import-error"
            Exit = 1
            Reason = "stage_import_failed"
        },
        [pscustomobject]@{
            Name = "zero-exit-export-error"
            Mode = "export-error"
            Exit = 1
            Reason = "stage_export_failed"
        },
        [pscustomobject]@{
            Name = "missing-executable"
            Mode = "missing-exe"
            Exit = 1
            Reason = "missing_output"
        },
        [pscustomobject]@{
            Name = "missing-managed-dll"
            Mode = "missing-dll"
            Exit = 1
            Reason = "missing_output"
        },
        [pscustomobject]@{
            Name = "cleanup-failure"
            Mode = "cleanup-failure"
            Exit = 1
            Reason = "stage_cleanup_failed"
        }
    ))
    {
        $fixtureResult = Invoke-FixtureExport -Mode $case.Mode
        $failMarkers = @(
            $fixtureResult.Output |
                Where-Object {
                    $_.Contains(
                        "RELEASE_EXPORT_FAIL reason=$($case.Reason)"
                    )
                }
        )
        $successMarkers = @(
            $fixtureResult.Output |
                Where-Object { $_.Contains("RELEASE_EXPORT_PASS") }
        )
        Add-Result $case.Name (
            $fixtureResult.ExitCode -eq $case.Exit -and
            $failMarkers.Count -eq 1 -and
            $successMarkers.Count -eq 0 -and
            !$fixtureResult.HasExecutable -and
            !$fixtureResult.HasManagedDll -and
            $fixtureResult.Stages -eq 0 -and
            $fixtureResult.OutsideSentinel
        ) (
            "exit=$($fixtureResult.ExitCode) reason=$($case.Reason) " +
            "fail_markers=$($failMarkers.Count) success=0 output=false " +
            "stages=$($fixtureResult.Stages) outside_sentinel=true"
        )
    }

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

    $summary = [pscustomobject]@{
        status = "PASS"
        tests = $results.Count
        results = $results
    }
    Write-Utf8NoBom `
        -Path (Join-Path $evidenceRoot "self-test-results.json") `
        -Text ($summary | ConvertTo-Json -Depth 6)
    Write-Output "RW2_SELF_TEST_PASS tests=$($results.Count)"
}
finally
{
    $env:GODOT_EXE = $oldGodotExe
    Remove-Item Env:RW2_FIXTURE_MODE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporaryRoot)
    {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

exit 0
