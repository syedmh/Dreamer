# T09 Independent Test Results

## TEST RESULT

### Commands

```text
dotnet restore HusayniaSite.sln -p:NuGetAudit=false --ignore-failed-sources --locked-mode
dotnet build HusayniaSite.sln -c Release --no-restore -warnaserror
dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Social" --logger "console;verbosity=normal"
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Social" --logger "console;verbosity=normal"
dotnet test tests/Husaynia.Application.Tests/Husaynia.Application.Tests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
dotnet test tests/Husaynia.IntegrationTests/Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

### Results

- Strict build: succeeded, 0 warnings, 0 errors.
- Social application tests: 12 passed, 1 failed, 0 skipped.
- Social integration/LocalDB tests: 10 passed, 0 failed, 0 skipped.
- Atomic concurrency test repeated five times: 5 passed, 0 failed.
- Full application suite: 53 passed, 1 failed, 0 skipped.
- Full integration suite: 84 passed, 2 failed, 0 skipped.

### T09 failure

`EntirelyMalformedItemSetDoesNotReplaceLastKnownGoodSnapshot` expected `Malformed` and preservation
of the active snapshot, but received `Empty` at
`tests/Husaynia.Application.Tests/Social/SocialFeedServiceTests.cs:159`.

Root cause: normalization rejects every malformed item but still constructs an empty successful
snapshot. Refresh then replaces the last-known-good snapshot and reports `Empty`, conflating an
invalid non-empty provider payload with a legitimate empty feed.

### Unrelated failures

- T19 health: `OperationsHealthEndpointTests.DegradedReadinessReturns503WithoutDiagnosticLeakage`
  failed because `TimeProvider` was not registered.
- Identity MFA test failed once with HTTP 401 during the full concurrent run and passed when rerun
  alone. Treat as an unrelated flake finding.
- NU1900 occurred when NuGet vulnerability data could not reach nuget.org. Locked restore with
  `NuGetAudit=false` succeeded; this environmental audit failure is separate from T09.

### Acceptance coverage

- AC-11: GAP. Text items and outbound-link filtering are tested, but no social media metadata,
  playback/lightbox, or pagination/load-more contract exists in T09 code.
- AC-12: FAIL. Timeout, rate-limit, empty, stale, and safe diagnostics pass; all-malformed items
  incorrectly replace saved content with an empty snapshot.
- AC-25: PASS for T09. Public reader depends only on the snapshot store and recorded zero provider
  calls; provider failures preserve unrelated read availability except the malformed-item defect.
- AC-27: PASS for T09 embed descriptors. Default deny and consent-enabled loading are tested.
- Atomic publication and retention: PASS, including five repeated concurrency runs.
- Recovery: PASS.

Conclusion: FAIL

## Standard Status

STATUS:          FAIL
SUMMARY:         T09 is not approvable: malformed item-only payloads erase the last-known-good snapshot, and AC-11 media/pagination behavior is absent.
WORK_COMPLETED:  Added and executed adversarial malformed-payload and disallowed-link tests; ran strict build, focused suites, full suites, LocalDB concurrency repetitions, and isolated unrelated failures.
EVIDENCE:        12/13 social application tests; 10/10 social integration tests; concurrency 5/5; strict build 0 warnings/errors.
ARTIFACTS:       tests/Husaynia.Application.Tests/Social/SocialFeedServiceTests.cs; .ai-org/missions/2026-08-20-husaynia-t09-independent-validation/test-results.md
FINDINGS:        Malformed payload is misclassified as empty; AC-11 media/playback/lightbox/pagination remains unimplemented; unrelated identity test flaked once.
RISKS:           A provider schema regression can silently erase usable saved content and present a false empty state.
BLOCKERS:        T09 malformed-item handling and missing R-12/AC-11 social media behavior.
NEXT_ACTION:     Preserve the active snapshot and record `Malformed` when a non-empty payload yields zero valid items; implement and test the frozen media/playback/lightbox/pagination contract.
