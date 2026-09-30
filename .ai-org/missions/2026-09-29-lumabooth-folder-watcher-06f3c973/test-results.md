# Current Fatal-Transition Rework Test Evidence

Date: 2026-09-29

## Verdict

**PASS**. Final mission approval remains pending.

## Commands and Results

```powershell
dotnet restore .\TCFUploader.slnx
dotnet build .\TCFUploader.slnx --configuration Release --no-restore
dotnet test .\tests\TCFUploader.Tests\TCFUploader.Tests.csproj `
  --configuration Release --no-build `
  --filter '<four fatal-transition tests>'
dotnet test .\TCFUploader.slnx --configuration Release --no-build
```

Results:

- Restore exit 0; locked restore also succeeded in final package validation.
- Release build: **0 warnings, 0 errors**.
- Four fatal-transition regressions: **4 passed, 0 failed, 0 skipped**.
- Feed-stop regression repetitions: **10 passed, 0 failed**.
- Identity-drift concurrent isolated-process stress: **100 passed, 0 failed**.
- Full Release suite run 1: **96 passed, 0 failed, 0 skipped**.
- Full Release suite run 2: **96 passed, 0 failed, 0 skipped**.
- Aggregate final validation: **306 passed, 0 failed, 0 skipped** across 113 TRX files.

Focused filter covered:

- `ChangeFeedStartFailure_ClosesAdmissionBeforeQueuedOrPostHttpStarts`
- `WatchedRootLoss_ClosesAdmissionBeforeQueuedOrPostHttpStarts`
- `GeneralCoordinatorFatal_ClosesAdmissionBeforeQueuedOrPostHttpStarts`
- `ChangeFeedStopFatal_ClosesAdmissionBeforeQueuedOrPostHttpStarts`

```powershell
dotnet publish .\src\TCFUploader\TCFUploader.csproj `
  --configuration Release --runtime win-x64 --self-contained false `
  --no-restore --output .\artifacts\independent-root-fix-validation-20260929\publish-win-x64

dotnet list .\src\TCFUploader\TCFUploader.csproj package `
  --include-transitive --no-restore
```

Publish/package results:

- Exactly 5 production files.
- 0 test/MSTest assemblies.
- ZIP contains the same 5 files.
- 0 SHA-256 payload mismatches.
- 0 direct or transitive production package rows.
- Published and ZIP-extracted `--help` smokes exited 0.

## Artifact Hashes

| Artifact | SHA-256 |
|---|---|
| `artifacts\independent-root-fix-validation-20260929\fatal-four.trx` | `AAA1E7E533EEF0D22F14A0846FEA274EACB7E5FB9C0360CE6F930B1ADA7BE4C9` |
| `artifacts\independent-root-fix-validation-20260929\full-release-1.trx` | `56E5BB3B302AF96E7E8DBDF667D41B5A243D8FFA771C69EBC2B6C4125490EE14` |
| `artifacts\independent-root-fix-validation-20260929\full-release-2.trx` | `46062587AB61BAD3D3F9A433EC799A91AEE79BBD8C9D3383FBFA06695826E1F7` |
| `artifacts\independent-root-fix-validation-20260929\TCFUploader-win-x64-framework-dependent.zip` | `4F0966FDF5015C54013E26D8C534F66E02DBE1136016A3DBD94243C9B5BADB51` |

## Limitations

- No live credentials or upstream requests were used.
- The project is untracked by the enclosing repository, so evidence validates the current
  filesystem tree rather than a committed diff.

STATUS: PASS
NEXT_ACTION: Keep final independent Engineering Judge judgment pending and mission state REWORK.
