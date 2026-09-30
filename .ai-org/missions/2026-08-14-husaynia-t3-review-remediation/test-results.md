# Test Results

Status: PASS for all executable repository tests; independent test-agent dispatch unavailable.

- Accepted remediation filter: 11 passed, 0 failed, 0 skipped.
- Full solution: 116 passed, 0 failed, 0 skipped (Domain 13, Application 77, API contract 26); Integration project contains no discovered tests.
- Release warnings-as-errors build: 0 warnings, 0 errors.
- Format verification: pass, exit 0.
- Mobile lint/typecheck/Jest: not executable because Node/npm is absent.
- Compose validation: not executable because Docker is absent.

Acceptance mapping:

- Authorization group + fallback: `ApiConventionTests.cs:157-191`.
- Runtime OpenAPI discovery/snapshot/constraint/duplicate behavior: `ApiConventionTests.cs:16-24,194-254`.
- Trace ownership: `ApiConventionTests.cs:256-273`.
- Unknown-length body boundaries: `ApiConventionTests.cs:276-301`.
- Multi-partition isolation: `ApiConventionTests.cs:304-332`.
- Enum and safe problem/log contracts: `JsonAndProblemDetailsContractTests.cs:12-96`.

Exact commands and outputs are recorded in `implementation-evidence.md`.
