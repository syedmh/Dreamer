# SECURITY RESULT

Scope: Final current-file T9/T10-readiness review of authentication, authorization, bounded rate limiting, invitation concurrency, access/refresh/logout token lifecycle, lifecycle cleanup ordering/concurrency, configuration, and dependency evidence.

Critical: 0  
High: 0  
Medium: 0  
Low: 1  
Informational: 1

Blocking findings: None

## Threat model

- Entry points: anonymous invitation/login/refresh/logout JSON requests, authenticated bearer and step-up requests, installation ID headers, and deployment configuration.
- Trust boundaries: HTTP client to API; bearer/refresh tokens to identity state; API process to PostgreSQL; application instance to process-local limiter state.
- Assets: credentials, invitation and refresh tokens, organization membership/roles, token-reuse history, and service availability.
- Dangerous sinks: authentication decisions, token issuance/rotation/revocation, PostgreSQL token rows, lifecycle deletion SQL, and retained limiter buckets.
- Actors: anonymous attacker, credential or token thief, authenticated cross-tenant user, concurrent requester, and network-adjacent development-host attacker.

## All findings

### [LOW] Development database uses a committed known credential and an all-interface port mapping

Location: `HusayniaTabruk/src/HusayniaTabruk.Infrastructure/Identity/Services/IdentityServiceCollectionExtensions.cs:27-28`; `HusayniaTabruk/docker-compose.yml:5-10`

Issue: The API falls back to a committed database credential when `ConnectionStrings:Tabruk` is absent, and the development Compose service publishes PostgreSQL on the host without a loopback-only bind.

Attack path: A developer starts the supplied Compose stack on a networked host -> host firewall/network policy permits inbound port 5432 -> an attacker who can read or infer the repository default authenticates directly to PostgreSQL.

Impact: Unauthorized read/write access to development database contents. The path is constrained to a reachable development host and is not evidence of a production database exposure.

Fix: Bind the Compose port to `127.0.0.1`, source the development password from an ignored environment/secret file, and fail closed outside Development when the application connection string is absent.

Confidence: High

### [INFORMATIONAL] Process-local limits multiply across application instances

Location: `HusayniaTabruk/src/HusayniaTabruk.Api/Configuration/ApiServiceCollectionExtensions.cs:31`; `HusayniaTabruk/src/HusayniaTabruk.Api/Middleware/ApiRateLimitMiddleware.cs:211-230`

Issue: Each application instance has independent counters and an independent 10,000-bucket capacity.

Attack path: Authentication requests -> load balancer distributes attempts across multiple instances -> each instance enforces only its local counters.

Impact: Horizontal scaling multiplies effective quotas. The current single-instance deployment assumption prevents a present bypass.

Fix: Before multi-instance deployment, use a shared atomic limiter or trusted gateway enforcement.

Confidence: High

## Rechecked controls

- Bearer authentication verifies HS256 signatures, issuer, audience, issuance/expiry, and re-resolves the exact active user/membership/organization tuple on every request.
- Access tokens expire after 10 minutes; refresh tokens are random opaque 48-byte values stored only as SHA-256 hashes.
- Rotation is row-locked, device/family-bound, detects consumed-token reuse, revokes the family on reuse, and caps consumed history.
- Login replaces active refresh families for the same user/device. Logout only revokes a token that cryptographically belongs to the supplied device/family.
- Refresh limiter keys remain stable across family rotations. Malformed tokens collapse to an address-derived fallback rather than creating unbounded retained keys.
- The limiter is lock-protected, atomic across partitions, capped at 10,000 retained buckets, and opportunistically evicts expired buckets.
- Lifecycle cleanup uses parameterized SQL, inclusive cutoffs, deterministic oldest-first ordering, 100-row batches, and `FOR UPDATE SKIP LOCKED`.
- Invitation lookup and acceptance are transactional and concurrency-safe; only one concurrent acceptance can commit.
- Request bodies are bounded before rate-key JSON parsing. No raw password, invitation token, refresh token, or email logging path was identified.
- Authorization defaults to authenticated, with only the intended invitation/login/refresh/logout endpoints anonymous.

Conclusion: PASS

---

STATUS:          PASS

SUMMARY:
Zero unresolved Critical or High findings. Current T9 is security-ready for T10 under the documented single-instance assumption.

WORK_COMPLETED:
Rechecked previous authorization, rate-limit, invitation race, stable refresh-family key, token rotation/reuse/revocation, lifecycle retention, cleanup boundary, and cleanup concurrency concerns. Reviewed configuration and dependency evidence.

EVIDENCE:
- Source review: `AuthEndpoints.cs`, `TabrukBearerAuthenticationHandler.cs`, `ApiRateLimitMiddleware.cs`, `RequestBodyLimitMiddleware.cs`, `AccessTokenCodec.cs`, `IdentityTokenService.cs`, `AuthenticationLifecycleCleanup.cs`, `PostgresAuthenticationMembershipRepository.cs`, `AspNetIdentityService.cs`, `TabrukAuthOptions.cs`, and service registration/configuration.
- `dotnet test ...Api.ContractTests.csproj --filter "FullyQualifiedName~ApiRateLimitStoreTests|FullyQualifiedName~AuthRateLimitKeyPrivacyTests"`: 12 passed, 0 failed, 0 skipped.
- `dotnet test ...Application.Tests.csproj --filter "FullyQualifiedName~Authentication"`: 11 passed, 0 failed, 0 skipped.
- `dotnet list HusayniaTabruk.sln package --vulnerable --include-transitive`: no vulnerable NuGet packages reported.
- Independent current snapshot evidence: full .NET 810 passed, 0 failed, 0 skipped; Auth 33 passed; lifecycle subset 3 passed; concurrent cleanup 20/20; build and format passed.
- npm production audit was attempted but the registry audit endpoint failed with a TLS handshake error; no vulnerability result was claimed. The current `npm ci`, lint, typecheck, and Jest gates passed independently.

ARTIFACTS:
- `.ai-org/missions/2026-08-16-husaynia-t9-security-rereview/security-review.md`

FINDINGS:
- Low: committed development database fallback credential plus non-loopback Compose port publication.
- Informational: process-local limiter quotas multiply when horizontally scaled.

RISKS:
- npm vulnerability status could not be refreshed because the registry audit endpoint failed.
- The lockfile contains a transitive package with an install script; current independent installation/tests passed, but CI should continue using the pinned lockfile and controlled scripts.
- The repository snapshot is untracked from the parent repository, so current-file inspection rather than a commit diff was authoritative.

BLOCKERS:
None.

NEXT_ACTION:
Proceed to T10. Harden the local database defaults before exposing development hosts, and move rate limiting to shared enforcement before multi-instance deployment.
