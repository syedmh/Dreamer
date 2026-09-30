MISSION: Add azure_openai to TCFComic while preserving existing providers and satisfying the agreed offline Azure contract.

REQUIREMENTS: PASS
- Provider selection and legacy behavior: config.py and providers/worker.py retain openai/fake; Azure tests verify legacy example and exact legacy identity; recorded nonperformance regression evidence is green.
- Endpoint/deployment: config.azure.example.yaml uses https://sweepertestai.openai.azure.com and explicitly labels tcfcomic-gpt-image-2 as an unverified deployment-name placeholder; Azure requires explicit endpoint and model.
- Credentials/security: azure_endpoint.py strictly validates raw ASCII HTTPS resource roots; environment-only Azure key has no public fallback; unsupported ambient SDK settings are rejected; redaction.py covers raw/trimmed public and Azure keys.
- SDK contract: providers/openai.py uses the real pinned OpenAI SDK; tests assert API-key-only multipart POST /openai/v1/images/edits?api-version=preview, exact deployment, MIME types, timeout, zero SDK retries, and disabled redirects/environment inheritance.
- Conversion: bounded PNG encoding preserves WebP decoded pixels/alpha and original/staged bytes; negative tests prove rejection before HTTP and closed buffers.
- Durable identity: processor.py hashes canonical Azure endpoint and API contract into the existing identity and rejects READY/READY_RETRY endpoint drift before dispatch. Legacy identity and database schema are retained; no migration added.
- Documentation/offline coverage: README.md and the separate Azure example match implementation and clearly exclude remote provisioning/verification.

IMPLEMENTATION: PASS - Independently read configuration, endpoint validation, provider, worker, redaction, identity/dispatch, documentation, and contract/integration tests. TCFComic is untracked, so a tracked prechange diff is unavailable.

TESTS: PASS
Judge rerun from TCFComic (PYTHONDONTWRITEBYTECODE=1):
python -m pytest tests\unit\test_azure_provider.py tests\integration\test_azure_cli.py -q -p no:cacheprovider
Actual output: 99 passed in 16.51s; exit 0.
Verified existing azure-targeted.xml: 99 tests, zero failures/errors/skips.
Verified existing azure-nonperformance.xml: 518 tests, zero failures/errors, two skips (516 passed). Both skips explicitly cite Windows symlink privilege WinError 1314. Recorded command: python -m pytest -m "not performance" -q --junitxml=C:\Users\syedhu\.copilot\session-state\cda7f4d7-b31e-4442-b424-ccb369f30802\files\azure-nonperformance.xml ; recorded output: 516 passed, 2 skipped, 3 deselected in 50.73s.
Verified existing azure-probe.xml: one test, zero failures/errors/skips. azure-validation-results.txt records the independent decoded-output boundary command and successful result.
No full suite or network requests were run by this judge; no dependencies were installed.

SECURITY: PASS - Independently inspected endpoint allowlist, environment-only auth, header omission, redirect/proxy isolation, and redaction; relevant negative tests passed in the judge rerun. Parent reports an independent security PASS, zero findings, 111 focused passes plus proxy/TLS probe.
CODE REVIEW: PASS - Independent source/test inspection supports the requirements; parent additionally reports reviewer approval, 263 targeted passes and a transport-error probe. No acceptance-blocking issue found.
E2E: PASS (offline) - Judge reran all nine Azure integration cases, covering real SDK/MockTransport spawned-worker publication and restart, bounded-conversion rejection, queued/retry identity drift, and keyless validation/missing-key process behavior. Parent additionally reports QA 16/16 including missing-key watch exit 3. Live Azure E2E is N/A by explicit scope exclusion.
DEFINITION OF DONE: PASS - Provider compatibility, configured example, credentials/endpoint/redaction controls, exact SDK request contract, bounded non-mutating conversion, durable identity without migration, docs and offline unit/integration evidence are satisfied. No commit/push or production application/test edits were performed by this judge.

RISKS: Actual deployment existence, model access, credentials, Azure service compatibility and live conversion remain unverified by design. Two existing symlink checks lack Windows privilege; three performance tests were excluded in prior regression evidence. Untracked project prevents tracked before/after comparison. These are disclosed limits, not claims of live readiness.
REMAINING WORK: none within the agreed offline scope.

FINAL: APPROVED