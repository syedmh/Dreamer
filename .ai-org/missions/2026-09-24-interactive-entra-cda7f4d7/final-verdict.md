MISSION: Explicit interactive Microsoft Entra browser sign-in before processing, followed by bearer-only Azure image edits; preserve existing providers and offline commands.

REQUIREMENTS: PASS (offline implementation scope)
- Startup/browser: cli.py main starts AuthenticationBroker before Processor for process and watch, including empty watch. authentication.py _auth_child uses Microsoft InteractiveBrowserCredential, not a password form or credential chain. Startup ordering and failure/cancellation tests passed.
- Memory/silent/private handoff: authentication.py _serve owns a single credential with no cache persistence options, calls authenticate once, then get_token with disable_automatic_authentication=True. Spawned worker handoff carries AccessToken only. Broker, constructor, redaction, and artifact-leak tests passed.
- Bearer-only image edits: providers/openai.py uses the access token and omits api-key on the Azure v1 images/edits route. Spawned transport tests assert the exact Authorization header, absent api-key, and exact route. Missing interactive tokens cannot fall back to either API key.
- Offline/configuration: help/validate return without credential construction. config.py validates explicit Azure-only authentication, optional tenant GUID and paired public-client GUID/root localhost loopback redirect. Schema and redirect attack-matrix tests passed.
- Failure/queue safety: processor.py acquires and validates tokens before queue claims. Tests cover READY/READY_RETRY preservation, cancellation, insufficient lifetime, bounded startup/refresh, terminal 401 without replay, ambiguous outcomes, process cleanup, identity drift, and unchanged terminal jobs.
- Compatibility/documentation/dependencies: default api_key and legacy identity bytes remain unchanged; Azure/public/fake regression tests passed. README, interactive example with labeled deployment placeholder, pyproject.toml, and constraints.txt inspected. Documentation tests and pip check passed.

IMPLEMENTATION: PASS - Read actual authentication, CLI, configuration, processor, worker, provider, documentation, dependency, and regression-test sources. No production edits made by this judge.

TESTS: PASS - Independently executed:
1. python -m pytest -q tests\integration\test_interactive_auth.py tests\unit\test_interactive_config.py tests\unit\test_azure_provider.py tests\integration\test_azure_cli.py tests\unit\test_documentation.py -p no:cacheprovider
   Output: 205 passed in 106.54s (0:01:46).
2. python -m pytest -q tests\unit\test_config.py tests\unit\test_providers_logging.py tests\integration\test_cli.py -p no:cacheprovider
   Output: 190 passed in 8.27s.
3. python -m pip check
   Output: No broken requirements found.
Total independently rerun here: 395 passed, no skips or deselections in these scoped runs. The supplied 622-test nonperformance result and separate broker death/EOF probes are prior evidence, not reruns by this judge.

SECURITY: PASS (offline scope) - Production credential options explicitly set use_env_settings=False. Rerun includes the real SDK constructor/transport test asserting trust_env=False, no inherited proxy, and unchanged TLS verification despite CA override variables. Fixed public-cloud authority/scope, memory-only cache configuration, bounded IPC, token redaction, and no-key-fallback behavior inspected and tested. No remaining blocking finding identified; supplied security recheck reports zero findings.

CODE REVIEW: PASS - Independent source inspection agrees with the supplied approved review; core requirements are wired through CLI, broker, processor, and image worker. No missing implementation or weakened assertion found in inspected coverage.

E2E: PASS for offline integration - Real spawned broker and image-worker flow through CLI and publication with mocked credential/network boundaries passed. Live browser sign-in and Azure image edit are NOT CERTIFIED: explicitly excluded from this gate, not represented as a successful live test.

DEFINITION OF DONE: PASS for agreed offline scope - explicit opt-in/startup ordering; Microsoft browser credential; memory-only silent renewal/private handoff; bearer-only transport; offline help/validation; validated corporate app options; cancellation/error/queue safety; legacy compatibility; docs/example/pinned dependencies; independent tests, review, and security recheck. Full performance suite and live connectivity were intentionally out of scope.

RISKS: Corporate tenant consent, Conditional Access/MFA, app registration, loopback compatibility, RBAC, endpoint reachability, actual deployment/model access, and paid image-edit success remain unverified. Environments requiring inherited proxies/custom CA settings are intentionally unsupported by these transports. The example deployment must be replaced by the operator. The README header formatting observation is nonblocking and does not affect verified wire behavior.

REMAINING WORK: None for the offline implementation scope. Before claiming live readiness, supply the real deployment and any tenant-approved public-client settings, then perform an authorized Microsoft sign-in and Azure image edit in the target environment.

FINAL: APPROVED - offline implementation only; live tenant connectivity not certified.