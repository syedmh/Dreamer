# T6R Independent Test Gate

Date: 2026-08-14  
Scope: `Domain/Signups` only  
Verdict: **PASS**

## TEST RESULT

### Focused Signups suite

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "FullyQualifiedName~Signups" --logger "console;verbosity=normal"
```

Result:

```text
Test Run Successful.
Total tests: 139
     Passed: 139
 Total time: 0.7030 Seconds
```

Passed: 139  
Failed: 0  
Skipped: 0

### Independent T6 attack suite

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "FullyQualifiedName~T6IndependentSignupTests" --logger "console;verbosity=minimal"
```

Result:

```text
Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43, Duration: 74 ms - HusayniaTabruk.Domain.Tests.dll (net10.0)
```

Passed: 43  
Failed: 0  
Skipped: 0

### Full Domain suite

Commands:

```powershell
dotnet test .\tests\HusayniaTabruk.Domain.Tests --logger "console;verbosity=normal"
dotnet test .\tests\HusayniaTabruk.Domain.Tests --filter "Category!=Slow" --logger "console;verbosity=minimal"
```

Results:

```text
Test Run Successful.
Total tests: 337
     Passed: 337
 Total time: 1.3391 Seconds
```

```text
Passed!  - Failed:     0, Passed:   337, Skipped:     0, Total:   337, Duration: 588 ms - HusayniaTabruk.Domain.Tests.dll (net10.0)
```

Passed: 337  
Failed: 0  
Skipped: 0

No `Category=Slow` Domain tests exist, so the filtered and unfiltered runs cover the same 337 cases.

### Warnings-as-errors solution build

Command:

```powershell
dotnet build .\HusayniaTabruk.sln --configuration Release -warnaserror
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.05
```

### Full solution tests

Command:

```powershell
dotnet test .\HusayniaTabruk.sln --configuration Release --no-build --logger "trx"
```

Result:

```text
Passed!  - Failed:     0, Passed:   337, Skipped:     0, Total:   337, Duration: 666 ms - HusayniaTabruk.Domain.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:    36, Skipped:     0, Total:    36, Duration: 1 s - HusayniaTabruk.Api.ContractTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    77, Skipped:     0, Total:    77, Duration: 4 s - HusayniaTabruk.Application.Tests.dll (net10.0)
No test is available in ...\HusayniaTabruk.IntegrationTests.dll.
```

Passed: 450  
Failed: 0  
Skipped: 0  
Integration tests discovered: 0

### Format verification

Command:

```powershell
dotnet format .\HusayniaTabruk.sln --verify-no-changes
```

Result: exit code 0, no output.

### Mobile dependency installation and gates

The repository-pinned Node.js `24.19.0` and npm `11.17.0` were used from a checksum-verified temporary portable installation.

Commands:

```powershell
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
```

Results:

```text
added 1028 packages in 40s
```

```text
> tabruk-mobile@0.1.0 lint
> eslint --config node_modules/eslint-config-expo/flat.js app --max-warnings=0
```

```text
> tabruk-mobile@0.1.0 typecheck
> tsc --noEmit
```

```text
PASS tests/root-layout.test.tsx (27.041 s)
Test Suites: 1 passed, 1 total
Tests:       1 passed, 1 total
Snapshots:   0 total
Time:        32.341 s
Ran all test suites.
```

Passed: 1  
Failed: 0  
Skipped: 0

### Docker Compose configuration

Docker CLI `29.7.2` and Compose `v5.4.0` were used from a temporary portable installation.

Command:

```powershell
docker compose config
```

Result: exit code 0. The rendered project is `tabruk`, with one `postgres:18.6-alpine` service, its health check, port `5432`, and `tabruk-postgres` volume.

## Failures

None.

## Coverage of acceptance criteria and attacks

| Contract/attack | Executed evidence |
|---|---|
| Overbooking and exact capacity | `SignupTests.ApprovalUsesAllOwnedParticipantSlotsAndHonorsExactCapacityBoundary` (`tests/HusayniaTabruk.Domain.Tests/Signups/SignupTests.cs:436`) and `T6IndependentSignupTests.AggregateRehydrateRejectsAlreadyOverallocatedState` (`T6IndependentSignupTests.cs:485`) passed. Capacity is computed from owned approved children in `HelpNeedSignups.cs:481`. |
| Omission/partial-snapshot attack | `T6IndependentSignupTests.OmissionResistantApprovalAndReassignmentAlwaysUseOwnedCompleteSet` (`T6IndependentSignupTests.cs:659`) passed and reflects that managed commands expose no caller-supplied signup collection. |
| Counterfeit same-organization date | `T6IndependentSignupTests.SameOrganizationForeignDateCannotSupplyAReplacementDeadline` (`T6IndependentSignupTests.cs:189`) passed. `Withdraw` accepts only `SignupId` and UTC time (`HelpNeedSignups.cs:263`). |
| Chronology | `ChronologyRejectsPreSubmissionAndPrePriorTransitionAtomically` and `ChronologyAllowsEqualityForSubmissionAndPriorTransition` (`T6IndependentSignupTests.cs:515,547`) passed. Enforcement is in `Signup.cs:262`. |
| Exact reachable versions | All reachable and boundary-unreachable cases passed in `RehydrateAcceptsEveryReachableStatusVersion` and `RehydrateRejectsEveryUnreachableStatusVersion` (`T6IndependentSignupTests.cs:287,316`), including `Cancelled` v3. The table is at `Signup.cs:334`. |
| Mixed organization/date/need identity | `AggregateRehydrateRejectsMixedIdentityDuplicateIdsOrdersAndActivePrimary` (`T6IndependentSignupTests.cs:428`) passed against `HelpNeedSignups.Rehydrate` (`HelpNeedSignups.cs:52`). |
| Active-primary uniqueness | Rehydration and submit cases passed in `T6IndependentSignupTests.cs:428` and `SignupTests.cs:267`. |
| Deterministic waitlist and selected reassignment | `WaitlistOrderFollowsOwnedMaximumAndOrderedWaitlistIsDeterministic` and `SelectedWaitlistReassignmentMaySkipEarlierEntryAndRequiresCapacity` (`SignupTests.cs:480,496`) passed. Ordering/allocation are at `HelpNeedSignups.cs:404,499`. |
| Root, child, and waitlist overflow | `RootChildAndWaitlistOverflowFailWithoutMutation` (`T6IndependentSignupTests.cs:566`) passed. |
| Defensive copies | `DefensiveCopiesDetachInputsSnapshotsAndOrderedWaitlist` (`T6IndependentSignupTests.cs:603`) passed. Defensive copy surfaces are at `HelpNeedSignups.cs:49,404` and `Signup.cs:282`. |
| Version increments and failure atomicity | `SuccessfulCommandsIncrementRootAndChildExactlyOnceAndPreserveOriginalVersion` and `DuplicateRetryAndMalformedAggregateStateDoNotMutate` (`T6IndependentSignupTests.cs:626,47`) passed, along with snapshot assertions on all attack failures. |
| Unsafe public APIs and obsolete helpers | `UnsafePublicChildAndCollectionAuthorityApisAreAbsent` (`T6IndependentSignupTests.cs:692`) passed. Filesystem checks confirmed `SignupCapacity.cs` and `HelpNeedSignupExtensions.cs` do not exist. The production Signups folder contains only `HelpNeedSignups.cs`, `Signup.cs`, and `SignupErrorCodes.cs`. |

## Ownership evidence

`git status` reports the whole product as untracked, while `git ls-files` and `git diff` provide no tracked baseline for these paths. Historical attribution therefore cannot be proven with Git.

Filesystem evidence is consistent with exact T6R ownership: excluding build/dependency/test-output directories, every file written in the observed T6R window `2026-08-15 05:35:00Z` through `05:46:00Z` was one of the five frozen T6R files:

```text
2026-08-15 05:45:25Z src\HusayniaTabruk.Domain\Signups\HelpNeedSignups.cs
2026-08-15 05:45:25Z src\HusayniaTabruk.Domain\Signups\Signup.cs
2026-08-15 05:39:52Z src\HusayniaTabruk.Domain\Signups\SignupErrorCodes.cs
2026-08-15 05:45:25Z tests\HusayniaTabruk.Domain.Tests\Signups\SignupTests.cs
2026-08-15 05:45:25Z tests\HusayniaTabruk.Domain.Tests\Signups\T6IndependentSignupTests.cs
```

No production or test source was changed by this independent validation. The existing independent T6 tests were retained.

## Findings and risks

- No T6R contract failure was reproduced.
- The solution contains an IntegrationTests assembly with zero discovered tests. This does not invalidate the Domain-only T6R gate but remains a later persistence/concurrency coverage gap.
- `npm ci` emitted dependency deprecation and one unreviewed install-script warning; lint, typecheck, and tests still passed. Dependency audit was not part of this requested T6R gate.
- Because the repository content is untracked, filesystem timestamps are circumstantial ownership evidence, not a substitute for a tracked diff.

## Conclusion

**PASS**

The frozen T6R Domain contract and required attack coverage passed every requested executed gate.
