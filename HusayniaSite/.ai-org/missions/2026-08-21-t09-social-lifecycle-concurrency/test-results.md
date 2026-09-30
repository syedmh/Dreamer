# Test results

## Causal red/green proof

Each new causal test was executed once against the corresponding pre-fix behavior, then the fix was restored and the surrounding suites were rerun.

- Historical expiry old behavior:
  - `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --filter "FullyQualifiedName=Husaynia.Application.Tests.Social.SocialFeedServiceTests.HistoricalProviderFetchUsesLocalExpiryAndSchedulesFutureSuccessor" --logger "console;verbosity=minimal"`
  - Expected failure: 0 passed, 1 failed; expected local expiry `2026-08-20T18:00Z`, actual provider-based expiry `2026-08-20T06:00Z`.
- State-version successor key old behavior:
  - `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --filter "FullyQualifiedName=Husaynia.Application.Tests.Social.SocialRefreshJobTests.HandlerRetryUsesOneSuccessorAndSuccessorStartsNextGeneration" --logger "console;verbosity=minimal"`
  - Expected failure: 0 passed, 1 failed; retry keys differed at `v2` versus `v3`.
- Startup without registration retry:
  - `dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName=Husaynia.IntegrationTests.Social.SocialModuleTests.HostedCatchUpRetriesOnePersistenceFailureThenSucceeds" --logger "console;verbosity=minimal"`
  - Expected failure: 0 passed, 1 failed; first `PersistenceFailure` escaped.

## Final validation

- `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~Husaynia.Application.Tests.Social" --logger "console;verbosity=minimal"`
  - PASS: 49 passed, 0 failed, 0 skipped.
- `dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Husaynia.IntegrationTests.Social" --logger "console;verbosity=minimal"`
  - PASS: 60 passed, 0 failed, 0 skipped.
- `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj --no-restore --filter "FullyQualifiedName~Husaynia.Application.Tests.Operations.Jobs" --logger "console;verbosity=minimal"`
  - PASS: 22 passed, 0 failed, 0 skipped.
- `dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Husaynia.IntegrationTests.Operations.Jobs" --logger "console;verbosity=minimal"`
  - PASS: 9 passed, 0 failed, 0 skipped.
- `dotnet build .\HusayniaSite.sln --no-restore --verbosity minimal`
  - PASS: build succeeded, 0 warnings, 0 errors.

## NU1900

No restore command was run. Earlier `--no-restore` test/build invocations still emitted NU1900 because NuGet vulnerability metadata at `https://api.nuget.org/v3/index.json` was unavailable. The final cached strict build emitted no warnings.

## Rework evidence

- First integration compile exposed a missing `JobStoreErrorCode` using; fixed in Social infrastructure.
- First startup integration run exposed a test DI fixture missing `ISocialSnapshotStore`; fixed in Social integration tests.
