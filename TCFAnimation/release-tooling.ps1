function New-ReleaseToolingFailure
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Reason,
        [string] $Source = "",
        [string] $Executable = "",
        [string] $Message = ""
    )

    if ([string]::IsNullOrEmpty($Message))
    {
        $Message = "Release tooling failed: $Reason."
    }
    $exception = New-Object System.InvalidOperationException($Message)
    $exception.Data["Reason"] = $Reason
    if (![string]::IsNullOrEmpty($Source))
    {
        $exception.Data["Source"] = $Source
    }
    if (![string]::IsNullOrEmpty($Executable))
    {
        $exception.Data["Executable"] = $Executable
    }
    throw $exception
}

function Test-ExactPropertySet
{
    param(
        [Parameter(Mandatory = $true)]
        [object] $Value,
        [Parameter(Mandatory = $true)]
        [string[]] $Expected
    )

    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count)
    {
        return $false
    }
    for ($index = 0; $index -lt $actual.Count; $index++)
    {
        if ($actual[$index] -cne $wanted[$index])
        {
            return $false
        }
    }
    return $true
}

function Test-JsonInteger
{
    param([object] $Value)

    return (
        $Value -is [byte] -or
        $Value -is [sbyte] -or
        $Value -is [int16] -or
        $Value -is [uint16] -or
        $Value -is [int32] -or
        $Value -is [uint32] -or
        $Value -is [int64]
    )
}

function Get-ReleaseProvenance
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot
    )

    try
    {
        $provenancePath = Join-Path $ProjectRoot "release-provenance.json"
        $raw = [IO.File]::ReadAllText(
            $provenancePath,
            [Text.Encoding]::UTF8
        )
        $document = $raw | ConvertFrom-Json

        if (
            !(Test-ExactPropertySet $document @(
                "schemaVersion",
                "supportedVersion",
                "requiredVersionToken",
                "executables",
                "exportTemplate"
            )) -or
            !(Test-JsonInteger $document.schemaVersion) -or
            [int64]$document.schemaVersion -ne 1 -or
            $document.supportedVersion -cne "4.5.1" -or
            $document.requiredVersionToken -cne "mono"
        )
        {
            throw "Invalid top-level provenance record."
        }

        $executables = @($document.executables)
        if ($executables.Count -ne 2)
        {
            throw "Invalid executable record count."
        }
        $byId = @{}
        foreach ($record in $executables)
        {
            $expectedProperties = @("id", "fileName", "size", "sha256")
            if ($record.id -ceq "godot-console-win64")
            {
                $expectedProperties += "companionId"
            }
            if (!(Test-ExactPropertySet $record $expectedProperties))
            {
                throw "Invalid executable record shape."
            }
            if (
                [string]::IsNullOrEmpty($record.id) -or
                $byId.ContainsKey($record.id) -or
                !(Test-JsonInteger $record.size) -or
                [int64]$record.size -le 0 -or
                $record.sha256 -cnotmatch "^[0-9A-F]{64}$"
            )
            {
                throw "Invalid executable record."
            }
            $byId[$record.id] = $record
        }

        if (
            $byId.Count -ne 2 -or
            !$byId.ContainsKey("godot-console-win64") -or
            !$byId.ContainsKey("godot-editor-win64")
        )
        {
            throw "Unknown executable identity."
        }
        $console = $byId["godot-console-win64"]
        $editor = $byId["godot-editor-win64"]
        if (
            $console.fileName -cne
                "Godot_v4.5.1-stable_mono_win64_console.exe" -or
            $console.companionId -cne "godot-editor-win64" -or
            [int64]$console.size -ne 197640 -or
            $console.sha256 -cne
                "FD5C88FD05AFC7C2D965777320DEAEC5A80C31363C0BB33D7B48025376F96C5F" -or
            $editor.fileName -cne
                "Godot_v4.5.1-stable_mono_win64.exe" -or
            [int64]$editor.size -ne 163933704 -or
            $editor.sha256 -cne
                "C369B7B92C30100F3EEDE92410BD02A4BB024562860DEE94C33399BEA1C77C9B"
        )
        {
            throw "Executable identity differs from the frozen contract."
        }

        $template = $document.exportTemplate
        if (
            !(Test-ExactPropertySet $template @(
                "id",
                "relativePath",
                "size",
                "sha256"
            )) -or
            $template.id -cne "windows-release-x86_64" -or
            $template.relativePath -cne
                "editor_data/export_templates/4.5.1.stable.mono/windows_release_x86_64.exe" -or
            !(Test-JsonInteger $template.size) -or
            [int64]$template.size -ne 96965120 -or
            $template.sha256 -cne
                "9186C4AA21D659035A6BCC33BEA644D7399DDA42AC24F9E0D3A9E5965E92E00F"
        )
        {
            throw "Export template identity differs from the frozen contract."
        }

        return [pscustomobject]@{
            Document = $document
            ExecutablesById = $byId
        }
    }
    catch [System.InvalidOperationException]
    {
        throw
    }
    catch
    {
        New-ReleaseToolingFailure `
            -Reason "provenance_invalid" `
            -Message "Release provenance is missing or invalid."
    }
}

function Get-CanonicalRegularFile
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,
        [Parameter(Mandatory = $true)]
        [string] $Source,
        [Parameter(Mandatory = $true)]
        [string] $MissingReason
    )

    try
    {
        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    }
    catch
    {
        New-ReleaseToolingFailure -Reason $MissingReason -Source $Source
    }

    if ($item -isnot [IO.FileInfo])
    {
        New-ReleaseToolingFailure `
            -Reason "candidate_not_application" `
            -Source $Source
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        New-ReleaseToolingFailure `
            -Reason "candidate_reparse" `
            -Source $Source
    }
    if (![string]::Equals(
        $item.Extension,
        ".exe",
        [StringComparison]::OrdinalIgnoreCase
    ))
    {
        New-ReleaseToolingFailure `
            -Reason "candidate_not_exe" `
            -Source $Source
    }

    return [IO.Path]::GetFullPath($item.FullName)
}

function Get-StreamingSha256
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,
        [Parameter(Mandatory = $true)]
        [Int64] $ExpectedLength
    )

    if ($ExpectedLength -le 0)
    {
        New-ReleaseToolingFailure -Reason "size_mismatch"
    }
    try
    {
        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    }
    catch
    {
        New-ReleaseToolingFailure -Reason "size_mismatch"
    }
    if ($item -isnot [IO.FileInfo])
    {
        New-ReleaseToolingFailure -Reason "candidate_not_application"
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        New-ReleaseToolingFailure -Reason "candidate_reparse"
    }
    if ([int64]$item.Length -ne $ExpectedLength)
    {
        New-ReleaseToolingFailure -Reason "size_mismatch"
    }

    $stream = $null
    $algorithm = $null
    try
    {
        $stream = New-Object IO.FileStream(
            $item.FullName,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::Read,
            1048576,
            [IO.FileOptions]::SequentialScan
        )
        if ($stream.Length -ne $ExpectedLength)
        {
            New-ReleaseToolingFailure -Reason "size_mismatch"
        }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        $buffer = New-Object byte[] 1048576
        [int64]$total = 0
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0)
        {
            [void]$algorithm.TransformBlock($buffer, 0, $read, $null, 0)
            $total += $read
        }
        [void]$algorithm.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        if ($total -ne $ExpectedLength -or $stream.Length -ne $ExpectedLength)
        {
            New-ReleaseToolingFailure -Reason "size_mismatch"
        }
        return (
            [BitConverter]::ToString($algorithm.Hash).Replace("-", "")
        )
    }
    finally
    {
        if ($null -ne $algorithm)
        {
            $algorithm.Dispose()
        }
        if ($null -ne $stream)
        {
            $stream.Dispose()
        }
    }
}

function Get-MatchedExecutableRecord
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath,
        [Parameter(Mandatory = $true)]
        [object[]] $Records,
        [Parameter(Mandatory = $true)]
        [string] $Source
    )

    $length = [int64](Get-Item -LiteralPath $LiteralPath -Force).Length
    $sizeMatches = @(
        $Records |
            Where-Object { [int64]$_.size -eq $length }
    )
    if ($sizeMatches.Count -eq 0)
    {
        New-ReleaseToolingFailure `
            -Reason "size_mismatch" `
            -Source $Source `
            -Executable $LiteralPath
    }

    foreach ($record in $sizeMatches)
    {
        $actualHash = Get-StreamingSha256 `
            -LiteralPath $LiteralPath `
            -ExpectedLength ([int64]$record.size)
        if ($actualHash -ceq $record.sha256)
        {
            return [pscustomobject]@{
                Record = $record
                Sha256 = $actualHash
            }
        }
    }
    New-ReleaseToolingFailure `
        -Reason "unapproved_hash" `
        -Source $Source `
        -Executable $LiteralPath
}

function Assert-ApprovedCompanion
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Candidate,
        [Parameter(Mandatory = $true)]
        [object] $CompanionRecord,
        [Parameter(Mandatory = $true)]
        [string] $Source
    )

    $path = Join-Path (Split-Path -Parent $Candidate) $CompanionRecord.fileName
    if (!(Test-Path -LiteralPath $path))
    {
        New-ReleaseToolingFailure `
            -Reason "companion_missing" `
            -Source $Source `
            -Executable $Candidate
    }
    try
    {
        $canonical = Get-CanonicalRegularFile `
            -LiteralPath $path `
            -Source $Source `
            -MissingReason "companion_missing"
        $hash = Get-StreamingSha256 `
            -LiteralPath $canonical `
            -ExpectedLength ([int64]$CompanionRecord.size)
        if ($hash -cne $CompanionRecord.sha256)
        {
            New-ReleaseToolingFailure `
                -Reason "companion_unapproved" `
                -Source $Source `
                -Executable $Candidate
        }
    }
    catch [System.InvalidOperationException]
    {
        if ($_.Exception.Data["Reason"] -ceq "companion_missing")
        {
            throw
        }
        New-ReleaseToolingFailure `
            -Reason "companion_unapproved" `
            -Source $Source `
            -Executable $Candidate
    }
}

function Assert-ApprovedExportTemplate
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Candidate,
        [Parameter(Mandatory = $true)]
        [object] $TemplateRecord,
        [Parameter(Mandatory = $true)]
        [string] $Source
    )

    $relative = $TemplateRecord.relativePath.Replace(
        "/",
        [IO.Path]::DirectorySeparatorChar
    )
    $path = Join-Path (Split-Path -Parent $Candidate) $relative
    if (!(Test-Path -LiteralPath $path))
    {
        New-ReleaseToolingFailure `
            -Reason "template_missing" `
            -Source $Source `
            -Executable $Candidate
    }
    try
    {
        $canonical = Get-CanonicalRegularFile `
            -LiteralPath $path `
            -Source $Source `
            -MissingReason "template_missing"
        $hash = Get-StreamingSha256 `
            -LiteralPath $canonical `
            -ExpectedLength ([int64]$TemplateRecord.size)
        if ($hash -cne $TemplateRecord.sha256)
        {
            New-ReleaseToolingFailure `
                -Reason "template_unapproved" `
                -Source $Source `
                -Executable $Candidate
        }
    }
    catch [System.InvalidOperationException]
    {
        if ($_.Exception.Data["Reason"] -ceq "template_missing")
        {
            throw
        }
        New-ReleaseToolingFailure `
            -Reason "template_unapproved" `
            -Source $Source `
            -Executable $Candidate
    }
}

function Invoke-ApprovedGodotVersion
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,
        [Parameter(Mandatory = $true)]
        [string] $Source
    )

    $process = $null
    try
    {
        $startInfo = New-Object Diagnostics.ProcessStartInfo
        $startInfo.FileName = $Executable
        $startInfo.Arguments = "--version"
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.CreateNoWindow = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $startInfo
        if (!$process.Start())
        {
            New-ReleaseToolingFailure `
                -Reason "version_command_failed" `
                -Source $Source `
                -Executable $Executable
        }
        $standardOutput = $process.StandardOutput.ReadToEnd()
        $standardError = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0)
        {
            New-ReleaseToolingFailure `
                -Reason "version_command_failed" `
                -Source $Source `
                -Executable $Executable
        }
        return @(
            ($standardOutput + [Environment]::NewLine + $standardError) `
                -split "\r?\n" |
                ForEach-Object { $_.Trim() } |
                Where-Object { $_.Length -gt 0 }
        )
    }
    catch [System.InvalidOperationException]
    {
        throw
    }
    catch
    {
        New-ReleaseToolingFailure `
            -Reason "version_command_failed" `
            -Source $Source `
            -Executable $Executable
    }
    finally
    {
        if ($null -ne $process)
        {
            $process.Dispose()
        }
    }
}

function Get-ApprovedGodot
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot,
        [Parameter(Mandatory = $true)]
        [ValidateSet("Run", "Export")]
        [string] $Purpose
    )

    $root = [IO.Path]::GetFullPath($ProjectRoot)
    $provenance = Get-ReleaseProvenance -ProjectRoot $root
    $source = ""
    $candidate = ""
    $configured = $env:GODOT_EXE
    $bundled = Join-Path $root (
        ".tools\godot-4.5.1-mono\" +
        "Godot_v4.5.1-stable_mono_win64\" +
        "Godot_v4.5.1-stable_mono_win64_console.exe"
    )

    if (![string]::IsNullOrEmpty($configured))
    {
        $source = "GODOT_EXE"
        if (!(Test-Path -LiteralPath $configured))
        {
            New-ReleaseToolingFailure `
                -Reason "configured_missing" `
                -Source $source
        }
        $candidate = $configured
    }
    elseif (Test-Path -LiteralPath $bundled)
    {
        $source = "bundled"
        $candidate = $bundled
    }
    else
    {
        foreach ($name in @("godot4", "godot"))
        {
            $command = Get-Command $name -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -ne $command)
            {
                $source = "PATH"
                if ($command.CommandType -cne "Application")
                {
                    New-ReleaseToolingFailure `
                        -Reason "candidate_not_application" `
                        -Source $source
                }
                $candidate = $command.Source
                break
            }
        }
        if ([string]::IsNullOrEmpty($candidate))
        {
            New-ReleaseToolingFailure -Reason "not_found"
        }
    }

    $canonical = Get-CanonicalRegularFile `
        -LiteralPath $candidate `
        -Source $source `
        -MissingReason "configured_missing"
    $matched = Get-MatchedExecutableRecord `
        -LiteralPath $canonical `
        -Records @($provenance.Document.executables) `
        -Source $source
    $record = $matched.Record

    if ($record.id -ceq "godot-console-win64")
    {
        Assert-ApprovedCompanion `
            -Candidate $canonical `
            -CompanionRecord $provenance.ExecutablesById[$record.companionId] `
            -Source $source
    }
    if ($Purpose -ceq "Export")
    {
        Assert-ApprovedExportTemplate `
            -Candidate $canonical `
            -TemplateRecord $provenance.Document.exportTemplate `
            -Source $source
    }

    $versionLines = @(
        Invoke-ApprovedGodotVersion `
            -Executable $canonical `
            -Source $source
    )
    if ($versionLines.Count -ne 1)
    {
        New-ReleaseToolingFailure `
            -Reason "version_unparseable" `
            -Source $source `
            -Executable $canonical
    }
    $version = $versionLines[0]
    $parts = @($version -split "\.")
    if (
        $parts.Count -lt 4 -or
        $parts[0] -cne "4" -or
        $parts[1] -cne "5" -or
        $parts[2] -cne "1" -or
        !($parts -ccontains $provenance.Document.requiredVersionToken)
    )
    {
        New-ReleaseToolingFailure `
            -Reason "unsupported_version" `
            -Source $source `
            -Executable $canonical
    }

    return [pscustomobject]@{
        Executable = $canonical
        Source = $source
        Version = $version
        Sha256 = $matched.Sha256
    }
}

function Get-FrozenReleaseStageEntries
{
    return @(
        "project.godot",
        "Main.tscn",
        "TCFAnimation.csproj",
        "export_presets.cfg",
        "ActionMessages.json",
        "ActionLegendLayout.cs",
        "ActionLegendUi.cs",
        "ActionMessageCatalog.cs",
        "AnimationGeometry.cs",
        "CapturePathPolicy.cs",
        "CelebrationStateMachine.cs",
        "DialogueModel.cs",
        "DialogueUi.cs",
        "DirectionalTurnStateMachine.cs",
        "FireworksLayer.cs",
        "FireworksSimulation.cs",
        "GlobalInputPolicy.cs",
        "PresentationInputPolicy.cs",
        "SchoolSceneStateMachine.cs",
        "TurnController.cs",
        "ActionLegendLayout.cs.uid",
        "ActionLegendUi.cs.uid",
        "ActionMessageCatalog.cs.uid",
        "AnimationGeometry.cs.uid",
        "CapturePathPolicy.cs.uid",
        "CelebrationStateMachine.cs.uid",
        "DialogueModel.cs.uid",
        "DialogueUi.cs.uid",
        "DirectionalTurnStateMachine.cs.uid",
        "FireworksLayer.cs.uid",
        "FireworksSimulation.cs.uid",
        "PresentationInputPolicy.cs.uid",
        "SchoolSceneStateMachine.cs.uid",
        "TurnController.cs.uid",
        "Frames/LeftTurn/turn_0.png",
        "Frames/LeftTurn/turn_1.png",
        "Frames/LeftTurn/turn_2.png",
        "Frames/RightTurn/turn_0.png",
        "Frames/RightTurn/turn_1.png",
        "Frames/RightTurn/turn_2.png",
        "Frames/LeftWalk/walk_00.png",
        "Frames/LeftWalk/walk_01.png",
        "Frames/LeftWalk/walk_02.png",
        "Frames/LeftWalk/walk_03.png",
        "Frames/LeftWalk/walk_04.png",
        "Frames/LeftWalk/walk_05.png",
        "Frames/RightWalk/walk_00.png",
        "Frames/RightWalk/walk_01.png",
        "Frames/RightWalk/walk_02.png",
        "Frames/RightWalk/walk_03.png",
        "Frames/RightWalk/walk_04.png",
        "Frames/RightWalk/walk_05.png",
        "Frames/Clap/clap_00.png",
        "Frames/Clap/clap_01.png",
        "Frames/Clap/clap_02.png",
        "Frames/Clap/clap_03.png",
        "Frames/Clap/clap_04.png",
        "Frames/Clap/clap_05.png",
        "Frames/CrossArm/cross_00.png",
        "Frames/CrossArm/cross_01.png",
        "Frames/CrossArm/cross_02.png",
        "Frames/CrossArmRelease/release_00.png",
        "Frames/CrossArmRelease/release_01.png",
        "Frames/CrossArmRelease/release_02.png",
        "Frames/CrossArmRelease/release_03.png",
        "Frames/CrossArmRelease/release_04.png",
        "Frames/CrossArmRelease/release_05.png",
        "Frames/Backgrounds/school1.png",
        "Frames/Backgrounds/school2.png",
        "Frames/Backgrounds/school3.png",
        "Frames/Backgrounds/school4.png",
        "Frames/Backgrounds/school5.png",
        "Frames/Backgrounds/school6.png",
        "Frames/SchoolCharacter/LeftTurn/turn_0.png",
        "Frames/SchoolCharacter/LeftTurn/turn_1.png",
        "Frames/SchoolCharacter/LeftTurn/turn_2.png",
        "Frames/SchoolCharacter/RightTurn/turn_0.png",
        "Frames/SchoolCharacter/RightTurn/turn_1.png",
        "Frames/SchoolCharacter/RightTurn/turn_2.png",
        "Frames/SchoolCharacter/LeftWalk/walk_00.png",
        "Frames/SchoolCharacter/LeftWalk/walk_01.png",
        "Frames/SchoolCharacter/LeftWalk/walk_02.png",
        "Frames/SchoolCharacter/LeftWalk/walk_03.png",
        "Frames/SchoolCharacter/LeftWalk/walk_04.png",
        "Frames/SchoolCharacter/LeftWalk/walk_05.png",
        "Frames/SchoolCharacter/RightWalk/walk_00.png",
        "Frames/SchoolCharacter/RightWalk/walk_01.png",
        "Frames/SchoolCharacter/RightWalk/walk_02.png",
        "Frames/SchoolCharacter/RightWalk/walk_03.png",
        "Frames/SchoolCharacter/RightWalk/walk_04.png",
        "Frames/SchoolCharacter/RightWalk/walk_05.png",
        "Frames/SchoolCharacter/Clap/clap_00.png",
        "Frames/SchoolCharacter/Clap/clap_01.png",
        "Frames/SchoolCharacter/Clap/clap_02.png",
        "Frames/SchoolCharacter/Clap/clap_03.png",
        "Frames/SchoolCharacter/Clap/clap_04.png",
        "Frames/SchoolCharacter/Clap/clap_05.png",
        "Frames/SchoolCharacter/CrossArm/cross_00.png",
        "Frames/SchoolCharacter/CrossArm/cross_01.png",
        "Frames/SchoolCharacter/CrossArm/cross_02.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_00.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_01.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_02.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_03.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_04.png",
        "Frames/SchoolCharacter/CrossArmRelease/release_05.png"
    )
}

function Read-ReleaseStageManifest
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot,
        [Parameter(Mandatory = $true)]
        [string] $LiteralPath
    )

    try
    {
        $root = [IO.Path]::GetFullPath($ProjectRoot)
        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
        if (
            $item -isnot [IO.FileInfo] -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
        )
        {
            throw "Manifest is not a regular file."
        }
        $bytes = [IO.File]::ReadAllBytes($item.FullName)
        if (
            $bytes.Length -ge 3 -and
            $bytes[0] -eq 0xEF -and
            $bytes[1] -eq 0xBB -and
            $bytes[2] -eq 0xBF
        )
        {
            throw "Manifest must not contain a UTF-8 BOM."
        }
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        $text = $utf8.GetString($bytes)
        $lines = @($text -split "\r?\n")
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1] -ceq "")
        {
            $lines = @($lines[0..($lines.Count - 2)])
        }
        $expected = @(Get-FrozenReleaseStageEntries)
        if ($lines.Count -ne $expected.Count)
        {
            throw "Manifest line count is not exact."
        }

        $seen = @{}
        for ($index = 0; $index -lt $lines.Count; $index++)
        {
            $entry = $lines[$index]
            if (
                [string]::IsNullOrEmpty($entry) -or
                $entry.Trim() -cne $entry -or
                $entry.Contains("\") -or
                [IO.Path]::IsPathRooted($entry) -or
                $entry -match "^[A-Za-z]:" -or
                $entry -match "^(//|\\\\|//\?/|//\./)" -or
                $entry -match "[*?]" -or
                @($entry -split "/") -contains "." -or
                @($entry -split "/") -contains ".." -or
                $seen.ContainsKey($entry) -or
                $entry -cne $expected[$index]
            )
            {
                throw "Manifest entry is unsafe or differs from the contract."
            }
            $seen[$entry] = $true
            $source = [IO.Path]::GetFullPath(
                (Join-Path $root $entry.Replace(
                    "/",
                    [IO.Path]::DirectorySeparatorChar
                ))
            )
            $rootPrefix = $root.TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar
            ) + [IO.Path]::DirectorySeparatorChar
            if (
                !$source.StartsWith(
                    $rootPrefix,
                    [StringComparison]::OrdinalIgnoreCase
                )
            )
            {
                throw "Manifest entry escapes the project."
            }
            $sourceItem = Get-Item `
                -LiteralPath $source `
                -Force `
                -ErrorAction Stop
            if ($sourceItem -isnot [IO.FileInfo])
            {
                throw "Manifest entry is not a file."
            }
        }
        return [string[]]$lines
    }
    catch
    {
        New-ReleaseToolingFailure `
            -Reason "stage_manifest_invalid" `
            -Message "Release stage manifest is invalid."
    }
}

function Assert-ProjectPathHasNoReparsePoint
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot,
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $current = [IO.Path]::GetFullPath($ProjectRoot)
    $rootItem = Get-Item -LiteralPath $current -Force -ErrorAction Stop
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Project root is a reparse point."
    }
    foreach ($segment in @($RelativePath -split "/"))
    {
        $current = Join-Path $current $segment
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Manifest source path contains a reparse point."
        }
    }
}

function Remove-ReleaseStage
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $BuildRoot,
        [Parameter(Mandatory = $true)]
        [string] $StageRoot
    )

    try
    {
        $build = [IO.Path]::GetFullPath($BuildRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        )
        $stage = [IO.Path]::GetFullPath($StageRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        )
        $name = [IO.Path]::GetFileName($stage)
        if (
            $name -cnotmatch "^\.release-stage-[0-9a-f]{32}$" -or
            ![string]::Equals(
                [IO.Path]::GetDirectoryName($stage),
                $build,
                [StringComparison]::OrdinalIgnoreCase
            )
        )
        {
            throw "Stage path is outside the guarded Build root."
        }
        if (Test-Path -LiteralPath $build)
        {
            $buildItem = Get-Item -LiteralPath $build -Force -ErrorAction Stop
            if (
                $buildItem -isnot [IO.DirectoryInfo] -or
                ($buildItem.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            )
            {
                throw "Build root is not a regular directory."
            }
        }
        if (!(Test-Path -LiteralPath $stage))
        {
            return
        }
        $stageItem = Get-Item -LiteralPath $stage -Force -ErrorAction Stop
        if (
            $stageItem -isnot [IO.DirectoryInfo] -or
            ($stageItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        )
        {
            throw "Stage root is not a regular directory."
        }
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction Stop
        if (Test-Path -LiteralPath $stage)
        {
            throw "Stage root remains after cleanup."
        }
    }
    catch
    {
        New-ReleaseToolingFailure `
            -Reason "stage_cleanup_failed" `
            -Message "Release stage cleanup failed."
    }
}

function Get-ReleaseStageFileInventory
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $StageProjectRoot
    )

    $project = [IO.Path]::GetFullPath($StageProjectRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $projectItem = Get-Item -LiteralPath $project -Force -ErrorAction Stop
    if (
        $projectItem -isnot [IO.DirectoryInfo] -or
        ($projectItem.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0
    )
    {
        throw "Stage project root is not a regular directory."
    }

    $prefix = $project + [IO.Path]::DirectorySeparatorChar
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($project)
    $files = New-Object System.Collections.Generic.List[string]
    while ($pending.Count -gt 0)
    {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries(
            $directory
        ))
        {
            $item = Get-Item -LiteralPath $entry -Force -ErrorAction Stop
            if (
                ($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            )
            {
                throw "Stage inventory contains a reparse point."
            }
            $fullName = [IO.Path]::GetFullPath($item.FullName)
            if (
                !$fullName.StartsWith(
                    $prefix,
                    [StringComparison]::OrdinalIgnoreCase
                )
            )
            {
                throw "Stage inventory escaped the stage root."
            }
            if ($item -is [IO.DirectoryInfo])
            {
                $pending.Push($fullName)
            }
            elseif ($item -is [IO.FileInfo])
            {
                $files.Add(
                    $fullName.Substring($prefix.Length).Replace("\", "/")
                )
            }
            else
            {
                throw "Stage inventory contains an unsupported item."
            }
        }
    }
    return [string[]]@($files | Sort-Object)
}

function Assert-ReleaseStageInventory
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $StageProjectRoot,
        [Parameter(Mandatory = $true)]
        [string[]] $ExpectedEntries
    )

    $actual = @(Get-ReleaseStageFileInventory `
        -StageProjectRoot $StageProjectRoot)
    $expected = @($ExpectedEntries | Sort-Object)
    if ($actual.Count -ne $expected.Count)
    {
        throw "Stage file count differs from the expected inventory."
    }
    for ($index = 0; $index -lt $actual.Count; $index++)
    {
        if ($actual[$index] -cne $expected[$index])
        {
            throw "Stage file set differs from the expected inventory."
        }
    }
}

function New-ReleaseStage
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectRoot,
        [Parameter(Mandatory = $true)]
        [string] $BuildRoot,
        [Parameter(Mandatory = $true)]
        [string[]] $ManifestEntries
    )

    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $build = [IO.Path]::GetFullPath($BuildRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $expectedBuild = Join-Path $root "Build"
    if (![string]::Equals(
        $build,
        $expectedBuild,
        [StringComparison]::OrdinalIgnoreCase
    ))
    {
        New-ReleaseToolingFailure -Reason "stage_create_failed"
    }

    $stageRoot = $null
    try
    {
        $buildItem = Get-Item -LiteralPath $build -Force -ErrorAction Stop
        if (
            $buildItem -isnot [IO.DirectoryInfo] -or
            ($buildItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        )
        {
            throw "Build root is not a regular directory."
        }
        $stageRoot = Join-Path $build (
            ".release-stage-" + [Guid]::NewGuid().ToString("N")
        )
        New-Item `
            -ItemType Directory `
            -Path $stageRoot `
            -ErrorAction Stop | Out-Null
        $stageProjectRoot = Join-Path $stageRoot "project"
        New-Item `
            -ItemType Directory `
            -Path $stageProjectRoot `
            -ErrorAction Stop | Out-Null
    }
    catch
    {
        New-ReleaseToolingFailure -Reason "stage_create_failed"
    }

    try
    {
        foreach ($entry in $ManifestEntries)
        {
            Assert-ProjectPathHasNoReparsePoint `
                -ProjectRoot $root `
                -RelativePath $entry
            $relative = $entry.Replace(
                "/",
                [IO.Path]::DirectorySeparatorChar
            )
            $source = Join-Path $root $relative
            $sourceItem = Get-Item `
                -LiteralPath $source `
                -Force `
                -ErrorAction Stop
            if ($sourceItem -isnot [IO.FileInfo])
            {
                throw "Manifest source is not a regular file."
            }
            $destination = Join-Path $stageProjectRoot $relative
            $destinationDirectory = [IO.Path]::GetDirectoryName($destination)
            [IO.Directory]::CreateDirectory($destinationDirectory) | Out-Null
            [IO.File]::Copy($sourceItem.FullName, $destination, $false)
        }
    }
    catch
    {
        try
        {
            Remove-ReleaseStage -BuildRoot $build -StageRoot $stageRoot
        }
        catch
        {
            throw
        }
        New-ReleaseToolingFailure -Reason "stage_copy_failed"
    }

    try
    {
        Assert-ReleaseStageInventory `
            -StageProjectRoot $stageProjectRoot `
            -ExpectedEntries $ManifestEntries
    }
    catch
    {
        try
        {
            Remove-ReleaseStage -BuildRoot $build -StageRoot $stageRoot
        }
        catch
        {
            throw
        }
        New-ReleaseToolingFailure -Reason "stage_inventory_mismatch"
    }

    return [pscustomobject]@{
        StageRoot = $stageRoot
        StageProjectRoot = $stageProjectRoot
    }
}

function New-ReleaseStageSolution
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $BuildRoot,
        [Parameter(Mandatory = $true)]
        [string] $StageRoot,
        [Parameter(Mandatory = $true)]
        [string] $StageProjectRoot,
        [Parameter(Mandatory = $true)]
        [string[]] $ManifestEntries
    )

    $expectedLength = [int64]994
    $expectedHash = (
        "FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79"
    )
    try
    {
        $build = [IO.Path]::GetFullPath($BuildRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        )
        $stage = [IO.Path]::GetFullPath($StageRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        )
        $project = [IO.Path]::GetFullPath($StageProjectRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar
        )
        $stageName = [IO.Path]::GetFileName($stage)
        if (
            $stageName -cnotmatch "^\.release-stage-[0-9a-f]{32}$" -or
            ![string]::Equals(
                [IO.Path]::GetDirectoryName($stage),
                $build,
                [StringComparison]::OrdinalIgnoreCase
            ) -or
            ![string]::Equals(
                $project,
                (Join-Path $stage "project"),
                [StringComparison]::OrdinalIgnoreCase
            )
        )
        {
            throw "Stage solution path is outside the owned stage."
        }

        foreach ($directory in @($build, $stage, $project))
        {
            $item = Get-Item `
                -LiteralPath $directory `
                -Force `
                -ErrorAction Stop
            if (
                $item -isnot [IO.DirectoryInfo] -or
                ($item.Attributes -band
                    [IO.FileAttributes]::ReparsePoint) -ne 0
            )
            {
                throw "Stage solution directory is not regular."
            }
        }

        $frozen = @(Get-FrozenReleaseStageEntries)
        if ($ManifestEntries.Count -ne $frozen.Count)
        {
            throw "Stage solution manifest count differs from the contract."
        }
        for ($index = 0; $index -lt $frozen.Count; $index++)
        {
            if ($ManifestEntries[$index] -cne $frozen[$index])
            {
                throw "Stage solution manifest differs from the contract."
            }
        }
        Assert-ReleaseStageInventory `
            -StageProjectRoot $project `
            -ExpectedEntries $ManifestEntries

        $projectFile = Join-Path $project "TCFAnimation.csproj"
        $projectItem = Get-Item `
            -LiteralPath $projectFile `
            -Force `
            -ErrorAction Stop
        if (
            $projectItem -isnot [IO.FileInfo] -or
            ($projectItem.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0
        )
        {
            throw "Staged project file is not regular."
        }

        $solutionPath = Join-Path $project "TCFAnimation.sln"
        if (Test-Path -LiteralPath $solutionPath)
        {
            throw "Stage solution already exists."
        }
        $solutionText = (
            @(
                "Microsoft Visual Studio Solution File, Format Version 12.00",
                "# Visual Studio Version 17",
                "VisualStudioVersion = 17.0.31903.59",
                "MinimumVisualStudioVersion = 10.0.40219.1",
                (
                    'Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = ' +
                    '"TCFAnimation", "TCFAnimation.csproj", ' +
                    '"{C9221F19-51A6-4664-BC51-3755959B26FE}"'
                ),
                "EndProject",
                "Global",
                (
                    "`tGlobalSection(SolutionConfigurationPlatforms) = " +
                    "preSolution"
                ),
                "`t`tDebug|Any CPU = Debug|Any CPU",
                "`t`tRelease|Any CPU = Release|Any CPU",
                "`tEndGlobalSection",
                (
                    "`tGlobalSection(ProjectConfigurationPlatforms) = " +
                    "postSolution"
                ),
                (
                    "`t`t{C9221F19-51A6-4664-BC51-3755959B26FE}." +
                    "Debug|Any CPU.ActiveCfg = Debug|Any CPU"
                ),
                (
                    "`t`t{C9221F19-51A6-4664-BC51-3755959B26FE}." +
                    "Debug|Any CPU.Build.0 = Debug|Any CPU"
                ),
                (
                    "`t`t{C9221F19-51A6-4664-BC51-3755959B26FE}." +
                    "Release|Any CPU.ActiveCfg = Release|Any CPU"
                ),
                (
                    "`t`t{C9221F19-51A6-4664-BC51-3755959B26FE}." +
                    "Release|Any CPU.Build.0 = Release|Any CPU"
                ),
                "`tEndGlobalSection",
                "`tGlobalSection(SolutionProperties) = preSolution",
                "`t`tHideSolutionNode = FALSE",
                "`tEndGlobalSection",
                "EndGlobal"
            ) -join "`r`n"
        ) + "`r`n"
        $encoding = New-Object Text.UTF8Encoding($false, $true)
        $bytes = $encoding.GetBytes($solutionText)
        if ($bytes.Length -ne $expectedLength)
        {
            throw "Canonical solution byte count differs from the contract."
        }

        $stream = $null
        try
        {
            $stream = New-Object IO.FileStream(
                $solutionPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None
            )
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally
        {
            if ($null -ne $stream)
            {
                $stream.Dispose()
            }
        }

        $actualHash = Get-StreamingSha256 `
            -LiteralPath $solutionPath `
            -ExpectedLength $expectedLength
        if ($actualHash -cne $expectedHash)
        {
            throw "Canonical solution hash differs from the contract."
        }
        $expectedInventory = @($ManifestEntries) + "TCFAnimation.sln"
        Assert-ReleaseStageInventory `
            -StageProjectRoot $project `
            -ExpectedEntries $expectedInventory

        return [pscustomobject]@{
            Path = $solutionPath
            Length = $expectedLength
            Sha256 = $actualHash
        }
    }
    catch
    {
        New-ReleaseToolingFailure `
            -Reason "stage_solution_failed" `
            -Message "Release stage solution creation or validation failed."
    }
}

function ConvertTo-WindowsCommandLineArgument
{
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]')
    {
        return $Value
    }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    [int]$backslashes = 0
    foreach ($character in $Value.ToCharArray())
    {
        if ($character -eq "\")
        {
            $backslashes++
            continue
        }
        if ($character -eq '"')
        {
            [void]$builder.Append("\" * ($backslashes * 2 + 1))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0)
        {
            [void]$builder.Append("\" * $backslashes)
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0)
    {
        [void]$builder.Append("\" * ($backslashes * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Initialize-ReleaseGodotProcessRunner
{
    if ($null -ne ("ReleaseGodotProcessRunner" -as [type]))
    {
        return
    }

    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;

public sealed class ReleaseGodotProcessResult
{
    public int ExitCode { get; set; }
    public int ErrorCount { get; set; }
    public string[] DiagnosticTail { get; set; }
}

public static class ReleaseGodotProcessRunner
{
    public static ReleaseGodotProcessResult Run(
        string executable,
        string arguments)
    {
        var diagnostics = new ConcurrentQueue<string>();
        var errorCount = 0;
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            DataReceivedEventHandler outputHandler = (sender, eventArgs) =>
            {
                Relay(
                    eventArgs.Data,
                    false,
                    diagnostics,
                    ref errorCount);
            };
            DataReceivedEventHandler errorHandler = (sender, eventArgs) =>
            {
                Relay(
                    eventArgs.Data,
                    true,
                    diagnostics,
                    ref errorCount);
            };
            process.OutputDataReceived += outputHandler;
            process.ErrorDataReceived += errorHandler;
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Godot process did not start.");
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            process.WaitForExit();
            return new ReleaseGodotProcessResult
            {
                ExitCode = process.ExitCode,
                ErrorCount = errorCount,
                DiagnosticTail = diagnostics.ToArray(),
            };
        }
    }

    private static void Relay(
        string line,
        bool isErrorStream,
        ConcurrentQueue<string> diagnostics,
        ref int errorCount)
    {
        if (line == null)
        {
            return;
        }
        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref errorCount);
        }
        var boundedLine = line.Length > 2048
            ? line.Substring(0, 2048)
            : line;
        diagnostics.Enqueue(boundedLine);
        string discarded;
        while (diagnostics.Count > 40)
        {
            diagnostics.TryDequeue(out discarded);
        }
        if (isErrorStream)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.Out.WriteLine(line);
        }
    }
}
'@
}

function Invoke-ApprovedGodotProcess
{
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    Initialize-ReleaseGodotProcessRunner
    $commandLine = (
        @(
            $Arguments |
                ForEach-Object {
                    ConvertTo-WindowsCommandLineArgument $_
                }
        ) -join " "
    )
    return [ReleaseGodotProcessRunner]::Run($Executable, $commandLine)
}
