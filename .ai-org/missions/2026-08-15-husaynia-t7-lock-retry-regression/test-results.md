# TEST RESULT

## Commands

```powershell
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --filter "FullyQualifiedName~Lock" --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --filter "FullyQualifiedName=HusayniaTabruk.Domain.Tests.Threads.DateThreadTests.ConflictingLockRetryStillRequiresCurrentManagerAuthorization" --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.Domain.Tests\HusayniaTabruk.Domain.Tests.csproj --filter "FullyQualifiedName~Lock" --logger "console;verbosity=minimal"
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet test .\HusayniaTabruk.sln --no-build --logger "console;verbosity=minimal"
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity diagnostic
```

## Results

- Focused lock tests before mutation: 15 passed, 0 failed, 0 skipped.
- Isolated mutation moving the conflict check before authorization: 0 passed, 1 failed, 0 skipped, as required. Expected `NotFound`; actual `Conflict` at `DateThreadTests.cs:457`.
- Focused lock tests after restoring production: 15 passed, 0 failed, 0 skipped.
- Full solution build: succeeded with 0 warnings and 0 errors.
- Full solution tests: 508 passed, 0 failed, 0 skipped. The IntegrationTests assembly contains no discoverable tests.
- Format verification: formatted 0 of 91 files; exit code 0.

## T7 lock retry matrix

| Actor | Timestamp | Test | Result |
|---|---|---|---|
| Authorized | Identical | `IdenticalLockRetrySucceedsWithoutMutatingThreadAgain` | PASS |
| Authorized | Conflicting | `ConflictingLockRetryFailsAtomically` | PASS |
| Unauthorized | Identical | `IdenticalLockRetryStillRequiresCurrentManagerAuthorization` | PASS |
| Unauthorized | Conflicting | `ConflictingLockRetryStillRequiresCurrentManagerAuthorization` | PASS |

## Coverage

- Authorization is evaluated before lock timestamp conflict: PASS.
- Unauthorized conflicting retry returns concealed `NotFound`, not `Conflict`: PASS.
- Locked timestamp remains unchanged: PASS.
- Version remains unchanged: PASS.
- Regression test is mutation-proven: PASS.

Conclusion: PASS. T7 matrix is complete and T8 is ready.
