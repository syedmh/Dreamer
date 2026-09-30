# Independent Test Results — T3 API Remediations

Date: 2026-08-14

## TEST RESULT

### Targeted remediation contracts

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal --filter "FullyQualifiedName~AuthorizationFailsClosedForVersionedAndFallbackEndpoints|FullyQualifiedName~InjectedEndpointAppearsInGeneratedOpenApi|FullyQualifiedName~InboundTraceparentCannotChooseResponseTraceId|FullyQualifiedName~StreamedBodyWithoutContentLengthAcceptsExactLimit|FullyQualifiedName~StreamedBodyWithoutContentLengthRejectsLimitPlusOne|FullyQualifiedName~RejectedAccountPartitionDoesNotConsumeOrganizationPartition|FullyQualifiedName~RejectedOrganizationPartitionDoesNotConsumeAccountPartition|FullyQualifiedName~JsonAndProblemDetailsContractTests|FullyQualifiedName~GeneratedOpenApiMatchesTheCheckedInSnapshot|FullyQualifiedName~OpenApiEndpointIsDeterministicVersionedAndContainsFrozenConventions"
```

Result:

```text
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 540 ms - HusayniaTabruk.Api.ContractTests.dll (net10.0)
```

Passed: 13  
Failed: 0  
Skipped: 0

### Full solution

Command:

```powershell
dotnet test .\HusayniaTabruk.sln --no-restore --nologo --verbosity minimal
```

Result:

```text
Passed!  - Failed:     0, Passed:    13, Skipped:     0, Total:    13, Duration: 1 s - HusayniaTabruk.Domain.Tests.dll (net10.0)
No test is available in ...\HusayniaTabruk.IntegrationTests.dll.
Passed!  - Failed:     0, Passed:    26, Skipped:     0, Total:    26, Duration: 1 s - HusayniaTabruk.Api.ContractTests.dll (net10.0)
Passed!  - Failed:     0, Passed:    77, Skipped:     0, Total:    77, Duration: 5 s - HusayniaTabruk.Application.Tests.dll (net10.0)
```

Passed: 116  
Failed: 0  
Skipped: 0  
No tests discovered: IntegrationTests project

### Release warnings-as-errors build

Command:

```powershell
dotnet build .\HusayniaTabruk.sln --configuration Release --no-restore --nologo --verbosity minimal -warnaserror
```

Result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.95
```

### Format verification

Command:

```powershell
dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal
```

Result: exit code `0`, no diagnostics.

### Mobile and Compose availability

Commands:

```powershell
npm --prefix .\apps\mobile run lint
npm --prefix .\apps\mobile run typecheck
npm --prefix .\apps\mobile test -- --runInBand
docker compose -f .\docker-compose.yml config
```

Real result for each mobile command:

```text
The term 'npm' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

Real Compose result:

```text
The term 'docker' is not recognized as a name of a cmdlet, function, script file, or executable program.
```

These gates are unavailable in this environment, precisely because `npm` and `docker` are absent.

## Criterion-to-test mapping

| Criterion/probe | Executed evidence | Result |
|---|---|---|
| Fail-closed `/api/v1` group and fallback endpoint | `AuthorizationFailsClosedForVersionedAndFallbackEndpoints` asserts `401` problems for a fake bearer request and `/openapi/v1.json`, no `IAllowAnonymous`, group authorization metadata, and no explicit authorization metadata on OpenAPI (therefore fallback is exercised). | PASS |
| Injected endpoint auto-discovery | `InjectedEndpointAppearsInGeneratedOpenApi` asserts injected path, GET operation, operation ID, and responses. | PASS |
| Hostile inbound `traceparent` cannot choose API trace ID | `InboundTraceparentCannotChooseResponseTraceId` asserts a fresh 32-character ID, inequality with inbound ID, and response/problem correlation. | PASS |
| Unknown-length body accepts exact limit | `StreamedBodyWithoutContentLengthAcceptsExactLimit`. | PASS |
| Unknown-length body rejects limit + 1 | `StreamedBodyWithoutContentLengthRejectsLimitPlusOne` asserts `413` and `payload_too_large`. | PASS |
| Rejected account partition does not consume organization permit | `RejectedAccountPartitionDoesNotConsumeOrganizationPartition`. | PASS |
| Rejected organization partition does not consume account permit | `RejectedOrganizationPartitionDoesNotConsumeAccountPartition`. | PASS |
| Enum request/response uses camel-case strings | `EnumRequestAndResponseUseCamelCaseStrings`. | PASS |
| Undefined numeric enum value is rejected | Existing `NumericEnumInputReturnsSanitizedBadRequest` sends `1`; `MembershipStatus.Active` is defined as numeric `1`. It proves all-numeric rejection for a defined numeric value, not the specifically required undefined numeric probe. | **GAP** |
| Malformed JSON is sanitized and warning logged | `MalformedJsonReturnsSanitizedBadRequestAndWarningLog` asserts fixed detail, no parser location leakage, Warning event 1001, and trace correlation. | PASS |
| Unexpected exception is sanitized and error logged | `UnexpectedExceptionReturnsSanitizedProblemAndCorrelatedErrorLog` asserts fixed `500/internal_error`, no client sentinel leakage, Error event 1002, trace correlation, and server exception capture. | PASS |
| Deterministic, read-only snapshot | `GeneratedOpenApiMatchesTheCheckedInSnapshot` and `OpenApiEndpointIsDeterministicVersionedAndContainsFrozenConventions` passed. Test source only calls `File.ReadAllText`; repository search found no snapshot write/update mechanism. Snapshot SHA-256: `BE13DB4C76343D8652A2EBAF7DA3A7FF5A4012CBF879464659C6AD1C0243FB19`, size 5237 bytes. | PASS |
| Seven named missing probes are committed | All seven named methods are present in the discovered 26-test API contract list and executed in the targeted run. | PASS |
| Full tests/build/warnings/format | 116 tests passed; Release build has 0 warnings/errors; format verification exited 0. | PASS |
| Mobile/Compose where available | `npm` and `docker` unavailable; commands failed before execution. | UNAVAILABLE, non-code blocker |

## Failures

No executed test failed.

Validation failure:

- **Undefined numeric enum acceptance probe is missing.**
  - Expected: a contract test sends a value outside `MembershipStatus` (for example `999`) and asserts the fixed `400 bad_request` problem.
  - Actual: `NumericEnumInputReturnsSanitizedBadRequest` sends `1`, which is the defined `MembershipStatus.Active` value (`DomainEnums.cs:3-8`; test at `JsonAndProblemDetailsContractTests.cs:28-45`).
  - Root cause: the test validates rejection of numeric tokens generally, but does not execute the specifically required undefined-numeric boundary case.

Conclusion: **FAIL**

---

STATUS: FAIL

SUMMARY:
All executable .NET gates are green: 13/13 targeted contracts, 26/26 API contracts, and 116/116 total tests; Release warnings-as-errors build and format verification pass. The independent gate nevertheless fails because the explicitly required undefined numeric enum probe is absent/false-mapped.

WORK_COMPLETED:
- Read the Definition of Done, architecture, implementation evidence, source, test host, contract assertions, and snapshot.
- Independently executed targeted tests, full solution tests, Release build, format verification, and environment availability checks.
- Inspected all 26 discovered API contract names and mapped required criteria to assertions.
- Verified the snapshot test is comparison-only and recorded the snapshot hash.

EVIDENCE:
The exact commands, real summaries, counts, and criterion mapping above.

ARTIFACTS:
- `.ai-org/missions/2026-08-14-husaynia-t3-review-remediation/test-results-independent.md`
- No production code, tests, or snapshot were modified.

FINDINGS:
1. Required undefined numeric enum rejection is not directly tested; the current test uses defined numeric value `1`.
2. Malformed/unexpected logging tests verify event level, event ID, trace correlation, and exception containment, but do not explicitly inject and assert exclusion of query-string or authorization-header secrets.
3. The IntegrationTests project builds but discovers zero tests.

RISKS:
- A future converter change that rejects defined numeric enum values but mishandles undefined numeric values could escape the current contract suite.
- Sensitive-log exclusion is supported by the current fixed logging template, but lacks an adversarial executed regression test.

BLOCKERS:
- `npm` is unavailable, blocking mobile lint/typecheck/test.
- `docker` is unavailable, blocking Compose config validation.

NEXT_ACTION:
Developer must add an independent API contract case using an undefined numeric enum value (for example `999`) and assert the fixed sanitized `400 bad_request` response, without weakening the existing numeric-token test. Re-run targeted and full gates afterward.
