# Implementation Evidence

Updated: 2026-08-14T20:14:25.3061210-07:00

## Implemented contracts

- Fail-closed HTTP boundary: camel-case string enums with numeric rejection, authenticated fallback policy, and binding failures routed through sanitized problem handling (`src/HusayniaTabruk.Api/Configuration/ApiServiceCollectionExtensions.cs:20,30-34`).
- Protected composition: endpoint-data-source OpenAPI generation, explicit `/api/v1` authorization metadata, injected endpoint seam, and Testing-only excluded probes (`src/HusayniaTabruk.Api/Program.cs:21-24,40-55,61-101`).
- Server-owned correlation: fresh random API trace ID on every request (`src/HusayniaTabruk.Api/Middleware/RequestTraceMiddleware.cs:10-14`).
- Safe failures: fixed malformed and unexpected RFC 9457 details, response-started rethrow, request-abort passthrough, and stable Warning/Error event IDs (`src/HusayniaTabruk.Api/Middleware/ApiProblemDetailsMiddleware.cs:23-103,130-167`).
- Deterministic runtime OpenAPI: endpoint discovery, exclusion, stable sorting, duplicate detection, route-constraint normalization, route parameters, response metadata, root bearer security, and enum conventions (`src/HusayniaTabruk.Api/OpenApi/ApiOpenApiDocument.cs:33-64,67-123,126-236,345-349`).
- Test-only host and probes: isolated authentication claims, captured logs, unknown-length streaming content, seven accepted probes, duplicate/constraint OpenAPI checks, enum round trip/rejection, and sanitized error/log checks (`tests/HusayniaTabruk.Api.ContractTests/Conventions/ContractApiHost.cs:19-191`; `tests/HusayniaTabruk.Api.ContractTests/Conventions/ApiConventionTests.cs:157-342`; `tests/HusayniaTabruk.Api.ContractTests/Conventions/JsonAndProblemDetailsContractTests.cs:12-94`).
- Snapshot intentionally updated and remains comparison-only (`docs/api/openapi.json`; `tests/HusayniaTabruk.Api.ContractTests/Conventions/ApiConventionTests.cs:16-24`). Repository search found no snapshot write/update API in the contract tests.

## Independent T3 validation rework

The authoritative independent follow-up identified two production gaps and one test-evidence gap:

1. `JsonStringEnumConverter(... allowIntegerValues: false)` rejected numeric JSON tokens but accepted `"active, disabled"` for the non-`[Flags]` `MembershipStatus`. Binding produced the undefined value `3`; response serialization then failed and the request became a sanitized `500` instead of the required `400`.
2. Exception handling rewrote an unstarted response without clearing it. A stale `Content-Length`, `Content-Encoding`, content type, or sentinel header could truncate or corrupt the fixed problem response.
3. The numeric contract used defined value `1`. The required independent undefined-value probe (`999`) was absent.

The minimal rework:

- Replaced the built-in enum converter registration with a strict wrapper that preserves camel-case named input/output and numeric-token rejection, rejects comma-delimited names for non-`[Flags]` enums, and validates values on both read and write (`src/HusayniaTabruk.Api/Configuration/ApiServiceCollectionExtensions.cs:19`; `src/HusayniaTabruk.Api/Configuration/StrictJsonStringEnumConverter.cs:8-89`).
- Cleared only unstarted malformed/unexpected exception responses after the existing `HasStarted` checks and before writing the fixed problem. Response-started rethrow, request-abort passthrough, safe logging, and trace `OnStarting` behavior remain unchanged (`src/HusayniaTabruk.Api/Middleware/ApiProblemDetailsMiddleware.cs:31-58,65-92`).
- Retained the defined numeric-token test and added independent tests for numeric `999`, non-flags `"active, disabled"`, and an endpoint that sets stale content length/encoding/content type/sentinel metadata before throwing (`tests/HusayniaTabruk.Api.ContractTests/Conventions/JsonAndProblemDetailsContractTests.cs:27-166`).

### Regression reproduction before production changes

Command:

```powershell
dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal --filter "FullyQualifiedName~UndefinedNumericEnumInputReturnsSanitizedBadRequest|FullyQualifiedName~CompositeNonFlagsEnumInputReturnsSanitizedBadRequest|FullyQualifiedName~UnexpectedExceptionClearsUnstartedResponseMetadataBeforeWritingProblemDetails"
```

Real result:

```text
Failed: 2, Passed: 1, Skipped: 0, Total: 3
CompositeNonFlagsEnumInputReturnsSanitizedBadRequest: expected 400, actual 500.
UnexpectedExceptionClearsUnstartedResponseMetadataBeforeWritingProblemDetails: response body contained no JSON tokens.
```

The `999` probe passed before the production fix because the existing converter already rejected every numeric JSON token; it closes the independent test-evidence gap without weakening the retained defined-value `1` test. The other two probes failed on the old production behavior.

### Focused rework verification

The same command after the production fixes:

```text
Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 562 ms
```

### Full API contracts after rework

`dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal`

```text
Passed! - Failed: 0, Passed: 29, Skipped: 0, Total: 29, Duration: 876 ms
```

### Full solution after rework

`dotnet test .\HusayniaTabruk.sln --no-restore --nologo --verbosity minimal`

```text
Domain: Failed: 0, Passed: 13, Skipped: 0, Total: 13
API contract: Failed: 0, Passed: 29, Skipped: 0, Total: 29
Application: Failed: 0, Passed: 77, Skipped: 0, Total: 77
Aggregate executed: 119 passed, 0 failed, 0 skipped
Integration project: no tests discovered
```

### Release build and format after rework

`dotnet build .\HusayniaTabruk.sln --configuration Release --no-restore --nologo --verbosity minimal -warnaserror`

```text
Build succeeded.
0 Warning(s)
0 Error(s)
Time Elapsed 00:00:01.79
```

The first format verification found only LF end-of-line diagnostics in the newly created converter. The file was formatted with the scoped repository tool:

`dotnet format .\HusayniaTabruk.sln --no-restore --include .\src\HusayniaTabruk.Api\Configuration\StrictJsonStringEnumConverter.cs --verbosity minimal`

Final verification:

`dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal`

```text
Exit code 0; no diagnostics.
```

### Repository baseline

`git status --short` at rework start reported:

```text
?? ../.ai-org/
?? ../BookWriter/
?? ./
```

The whole product and mission artifacts were already untracked in the supplied environment. Per the CTO constraint, this work treats tests as promoted into the repository test source tree only; no `git add`, commit, push, reset, stash, or history operation was performed.

## Executed evidence

### Baseline

`dotnet test C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal`

Result before implementation: `Failed: 0, Passed: 13, Skipped: 0, Total: 13`.

### Accepted remediation probes

`dotnet test C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal --filter 'FullyQualifiedName~AuthorizationFailsClosedForVersionedAndFallbackEndpoints|FullyQualifiedName~InjectedEndpointAppearsInGeneratedOpenApi|FullyQualifiedName~InboundTraceparentCannotChooseResponseTraceId|FullyQualifiedName~StreamedBodyWithoutContentLengthAcceptsExactLimit|FullyQualifiedName~StreamedBodyWithoutContentLengthRejectsLimitPlusOne|FullyQualifiedName~RejectedAccountPartitionDoesNotConsumeOrganizationPartition|FullyQualifiedName~RejectedOrganizationPartitionDoesNotConsumeAccountPartition|FullyQualifiedName~JsonAndProblemDetailsContractTests'`

Final result: `Failed: 0, Passed: 11, Skipped: 0, Total: 11, Duration: 489 ms`.

### Full .NET tests

`dotnet test C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\HusayniaTabruk.sln --no-restore --nologo --verbosity minimal`

Final results:

- Domain: `Failed: 0, Passed: 13, Skipped: 0, Total: 13`.
- Application: `Failed: 0, Passed: 77, Skipped: 0, Total: 77`.
- API contract: `Failed: 0, Passed: 26, Skipped: 0, Total: 26`.
- Integration project: 0 discovered tests; runner reported no tests available.
- Aggregate executed tests: `116 passed, 0 failed, 0 skipped`.

### Warnings-as-errors build

`dotnet build C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\HusayniaTabruk.sln --configuration Release --no-restore --nologo --verbosity minimal -warnaserror`

Final result: `Build succeeded. 0 Warning(s), 0 Error(s).`

### Formatting

Initial `dotnet format ... --verify-no-changes` found only changed-file end-of-line formatting errors. Changed C# files were formatted with scoped `dotnet format --include`; no hand formatting or generated-lock edits were used.

`dotnet format C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal`

Final result: exit code `0`, no diagnostics.

### Dependency/security tooling

`dotnet list C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\HusayniaTabruk.sln package --vulnerable --include-transitive --no-restore`

Result: every solution project reported `no vulnerable packages given the current sources`.

### Environment-limited gates

- `npm --prefix C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile run lint` -> exit `1`: `npm` is not recognized.
- `npm --prefix C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile run typecheck` -> exit `1`: `npm` is not recognized.
- `npm --prefix C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\apps\mobile test -- --runInBand` -> exit `1`: `npm` is not recognized.
- `docker compose -f C:\Users\syedhu\source\repos\Dreamer\HusayniaTabruk\docker-compose.yml config` -> exit `1`: `docker` is not recognized.
- Common Node/npm and Docker executable locations were checked and were absent. No package installation or infrastructure change was attempted.
- These checks remain non-blocking because Node/npm and Docker are absent and the DoD qualifies mobile and Compose gates with `where the environment permits`.

## Dictionary-key regression rework

The second independent review identified that `StrictJsonStringEnumConverter.StrictEnumConverter<TEnum>` overrode only value `Read` and `Write`. `JsonConverter<TEnum>` therefore used its unsupported default property-name behavior for dictionary keys instead of the built-in `JsonStringEnumConverter`, causing valid enum-key dictionary input and output to fail with `NotSupportedException`.

The minimal correction delegates `ReadAsPropertyName` and `WriteAsPropertyName` to the cached built-in converter while applying the same non-flags composite rejection and defined-value validation used for ordinary enum values (`src/HusayniaTabruk.Api/Configuration/StrictJsonStringEnumConverter.cs:62-105`). The existing converter factory, camel-case naming policy, numeric rejection, static per-enum flags/defined-bit caches, and thread-safe immutable state are unchanged.

Contract coverage now proves:

- valid `{"active":"ok"}` dictionary-key deserialization;
- valid enum-key dictionary serialization as exactly `{"active":"ok"}`;
- sanitized `400 bad_request` responses for defined numeric (`"1"`), undefined numeric (`"999"`), and composite non-flags (`"active, disabled"`) input keys; and
- sanitized `500 internal_error` responses with server-side `JsonException` validation for undefined (`999`) and composite non-flags (`3`) output keys (`tests/HusayniaTabruk.Api.ContractTests/Conventions/JsonAndProblemDetailsContractTests.cs:30-122`).

### Regression reproduction before the converter fix

`dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal --filter "FullyQualifiedName~EnumDictionaryKey"`

```text
Failed! - Failed: 7, Passed: 0, Skipped: 0, Total: 7, Duration: 454 ms
Valid dictionary-key read/write returned 500 instead of 200.
Invalid input keys returned 500 instead of the required sanitized 400.
Invalid output keys logged NotSupportedException instead of JsonException from strict value validation.
```

### Focused verification after the converter fix

The exact reproduction command was rerun:

```text
Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 784 ms
```

### Full API contracts

`dotnet test .\tests\HusayniaTabruk.Api.ContractTests\HusayniaTabruk.Api.ContractTests.csproj --no-restore --nologo --verbosity minimal`

```text
Passed! - Failed: 0, Passed: 36, Skipped: 0, Total: 36, Duration: 850 ms
```

### Full solution

`dotnet test .\HusayniaTabruk.sln --no-restore --nologo --verbosity minimal`

```text
Domain: Failed: 0, Passed: 13, Skipped: 0, Total: 13
API contract: Failed: 0, Passed: 36, Skipped: 0, Total: 36
Application: Failed: 0, Passed: 77, Skipped: 0, Total: 77
Aggregate executed: 126 passed, 0 failed, 0 skipped
Integration project: no tests discovered
```

### Release warnings-as-errors

`dotnet build .\HusayniaTabruk.sln --configuration Release --no-restore --nologo --verbosity minimal -warnaserror`

```text
Build succeeded.
0 Warning(s)
0 Error(s)
Time Elapsed 00:00:01.86
```

### Format verification

`dotnet format .\HusayniaTabruk.sln --no-restore --verify-no-changes --verbosity minimal`

```text
Exit code 0; no diagnostics.
```

Repository status remains the supplied untracked baseline (`?? ../.ai-org/`, `?? ../BookWriter/`, `?? ./`). No package, T4, domain, database, deployment, or git-history change was made.

## Independent gates and current status

- The earlier approval summary predates the authoritative follow-up failures in `test-results-independent.md` and the additional enum-composite/response-reset review evidence.
- This implementation now passes the dictionary-key focused regression, full API, full solution, warnings-as-errors build, and format gates recorded above.
- Fail-closed authorization, closed CORS, server-owned trace IDs, body/rate behavior, deterministic OpenAPI, response-started rethrow, request-abort passthrough, and exception-text sanitization were not weakened.
- Current mission status: implementation rework complete; independent test/code-review/final gates must be rerun.
