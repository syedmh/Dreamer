# RW2 blocked: frozen 50-file stage cannot export the Mono project

Date: 2026-09-04

## Frozen-contract reproduction

The frozen `release-stage-manifest.txt` was parsed as exactly 50 ordered,
duplicate-free, UTF-8-without-BOM entries. `New-ReleaseStage` copied exactly
those 50 files and its pre-import inventory check passed.

Command:

```powershell
. .\release-tooling.ps1
$m = Read-ReleaseStageManifest `
  -ProjectRoot C:\Users\syedhu\source\repos\Dreamer\TCFAnimation `
  -LiteralPath C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\release-stage-manifest.txt
$s = New-ReleaseStage `
  -ProjectRoot C:\Users\syedhu\source\repos\Dreamer\TCFAnimation `
  -BuildRoot C:\Users\syedhu\source\repos\Dreamer\TCFAnimation\Build `
  -ManifestEntries $m
```

Observed:

```text
manifest_count=50
stage_count=50
```

The required import command succeeded, generated stage-local import/UID
state, and did not generate a solution or any managed build output:

```powershell
& $approvedGodot --headless --path $s.StageProjectRoot --import
```

Observed:

```text
import_exit=0
fileCount=159
solutionPresent=false
globalInputUidPresent=true
monoFiles=0
godotFiles=75
```

The exact required export command then emitted the following errors but
returned zero and produced a nonempty executable:

```text
ERROR: Export .NET Project: This project contains C# files but no solution file
was found at .../project/TCFAnimation.sln
A solution file is required for projects with C# files.
ERROR: System.InvalidOperationException: res://TurnController.cs is a C# file
but no solution file exists.
ERROR: System.InvalidOperationException: res://DialogueUi.cs is a C# file but
no solution file exists.
WARNING: Project export for preset "Windows Desktop" completed with warnings.
export_exit=0 output_exists=True output_size=100240024
```

The resulting package contained the complete source text instead of the
required one-byte script placeholders:

```text
dialogue_source_token=True
turn_source_token=True
RuntimeError: TCFAnimation.exe contains C# source script content:
{'DialogueUi.cs': 10662, 'TurnController.cs': 25886}.
```

Runtime smoke of that artifact crashed:

```text
runtime_smoke_exit=-1073741819
```

## Root-cause proof and minimal design amendment

As a diagnostic only, a minimal `TCFAnimation.sln` containing only
`TCFAnimation.csproj` was generated inside the retained stage after the exact
50-file pre-import check. No production contract was changed.

```powershell
dotnet new sln --name TCFAnimation --format sln --force
dotnet sln TCFAnimation.sln add TCFAnimation.csproj
& $approvedGodot --headless --path $stage `
  --export-release "Windows Desktop" $diagnosticOutput
```

Observed:

```text
generated_solution_size=1727
generated_solution_sha256=4A34F781DFA01ACB3AC884C89EE7AC2AE9DD99C4C4E27F02F47FECC95E8A3C58
export_with_generated_solution_exit=0
output_size=100203544
DialogueUi.cs_size=1
TurnController.cs_size=1
RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true
generated_solution_runtime_exit=0
```

This proves the missing solution is causal. The repository's existing
`TCFAnimation.sln` cannot simply be copied under the frozen manifest because
it also references `ControllerProbe\ControllerProbe.csproj`, which is
explicitly excluded from release staging.

## Required decision

The frozen interface says the stage seed is exactly 50 files, lists no
solution, permits only Godot-generated UID/import state after the seed check,
and requires the exact sequence to proceed directly from import to export.
Godot 4.5.1 Mono requires `TCFAnimation.sln` to export C# and does not generate
it during the specified `--import` step.

Recommended amendment: explicitly permit deterministic creation of a
stage-local minimal `TCFAnimation.sln`, containing only
`TCFAnimation.csproj`, after the 50-file seed inventory check and before
Godot import/export. This preserves the frozen source-copy allowlist and keeps
`ControllerProbe` outside the stage. Alternatively, amend the manifest and
all exact-count contracts to include a separately reviewed release-only
solution file.
