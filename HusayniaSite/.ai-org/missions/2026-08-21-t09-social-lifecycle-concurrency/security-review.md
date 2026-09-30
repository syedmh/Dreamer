# Security Review — T09 Social Scheduling

## SECURITY RESULT

Scope: Current Social Domain/Application/Infrastructure and Social tests; canonical persisted scheduling state and preflight; legacy state-key compatibility; recovery anchors; and the T19 durable-job seam.

Critical: 0   High: 0   Medium: 0   Low: 0   Informational: 0

Blocking findings: None

All findings: None.

Conclusion: PASS

## Threat model and evidence

Untrusted provider responses and persisted job state cross into Social refresh scheduling and then the durable-job enqueue seam. Relevant assets are job-execution capacity, provider credentials/tokens, outbound-network policy, diagnostics, and metrics. The assessed sinks are persisted scheduling state, locking, durable-job creation, HTTP requests, logs, and metrics.

* `src/Husaynia.Application/Social/SocialRefreshJobs.cs:43-86,284-355,398-423` performs canonical scheduling preflight and applies bounded payload, provider, and state-key handling before durable scheduling. Its legacy-key handling does not allow an unbounded set of caller-controlled keys to create new job chains.
* `src/Husaynia.Infrastructure/Social/SocialPersistence.cs:90-145,228-390` obtains state and snapshot atomically and holds one provider-scoped transactional application lock. The lock is released before remote provider requests and T19 durable-job enqueue, preventing lock-order inversion or a remote/enqueue call from extending the lock lifetime.
* `src/Husaynia.Application/Social/SocialFeedServices.cs:238,308,636-669` bounds provider and stored `RetryAfter` by the configured recovery horizon, preventing a stale or malicious persisted delay from indefinitely suppressing refresh.
* Existing outbound boundary controls remain in `src/Husaynia.Infrastructure/Social/SocialHttpResilience.cs:45-61,122-136,197,345-390`: redirects, DNS resolution/pinning, and private/IPv6 destination restrictions are enforced before outbound access.
* Existing response bounds remain in `src/Husaynia.Infrastructure/Social/JsonSocialFeedProvider.cs:27-85,163-168`; JSON byte size, depth, and item counts are constrained.
* Persisted URL validation and sensitive-query stripping remain in `src/Husaynia.Domain/Social/SocialFeedEntities.cs:295-415`. Consent gating and bounded Social metrics remain in `src/Husaynia.Application/Social/SocialFeedServices.cs:123-153,376,692-723`, with no reviewed diagnostic or metric emission of tokens or unbounded payload/state values.

No realistic entry-point-to-sink path was identified for job amplification/replay, arbitrary legacy key creation, lock-held remote/enqueue work, stale-`RetryAfter` denial of service, outbound SSRF, token exposure, excessive JSON processing, bypassed IPv6 filtering, consent bypass, or diagnostics/metrics leakage.

## Commands and limitations

Read-only evidence collection: Git status/log/diff/ls-files; solution project list; file discovery and identifier searches over current Social production/tests, T19 seam, telemetry/health, and T09 artifacts.

Two filtered `dotnet test --no-restore` attempts did not execute tests because package vulnerability metadata retrieval failed with `NU1900`. No live provider or third-party calls were made. This is an environmental validation limitation, not a security finding in the reviewed code.
