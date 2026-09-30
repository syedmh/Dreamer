# Decisions

- 2026-08-14: Mission is medium because it changes API-wide security/error/serialization/OpenAPI contracts across multiple files.
- 2026-08-14: E2E is not a separate gate because T3 owns infrastructure-free API contract behavior; independent contract tests exercise the complete in-process HTTP pipeline.

# ADR-1: Protect the API with both fallback and route-group authorization
Date: 2026-08-14     Status: Accepted

## Context
The placeholder bearer handler accepts no identity, but neither `/api/v1` nor endpoints mapped outside it currently require authentication. Future endpoint omission must fail closed, while later invitation acceptance, login, and refresh must be able to opt out explicitly.

## Decision
Set an authenticated fallback policy and apply `RequireAuthorization()` to the `/api/v1` route group. Keep every T3 endpoint protected; permit future anonymous access only with endpoint-level `AllowAnonymous()`.

## Consequences
Both discovered endpoints and accidentally separately mapped endpoints return `401` by default. OpenAPI is protected, and test infrastructure must install a test authentication scheme.

## Alternatives considered
Fallback policy only - rejected because endpoint metadata would not express the versioned API contract.  
Route-group policy only - rejected because endpoints mapped outside the group would remain anonymous.  
Per-endpoint authorization - rejected because omission remains unsafe.

# ADR-2: Generate OpenAPI paths from runtime endpoint metadata
Date: 2026-08-14     Status: Accepted

## Context
The static document and its snapshot test both produce an empty `paths` object independently of endpoint discovery. The remediation must prove that an injected mapped endpoint appears, remain deterministic, and add no external service or package.

## Decision
Build OpenAPI paths from the running application's `EndpointDataSource` values, using only stable route/HTTP/explicit metadata, deterministic sorting, and duplicate detection. Keep the checked-in snapshot comparison read-only.

## Consequences
The current Production-like snapshot may legitimately have empty paths, but future mapped endpoints cannot be omitted silently. Runtime metadata errors fail the contract test instead of producing an arbitrary document.

## Alternatives considered
Continue reflection over `IApiEndpoint` types - rejected because types do not prove what routes were actually mapped.  
Maintain a handwritten path registry - rejected because it creates a second source of truth.  
Add Swashbuckle, NSwag, or another package - rejected because T3 needs only existing shared-framework metadata and deterministic paths.

# ADR-3: Use canonical string enums at the HTTP boundary
Date: 2026-08-14     Status: Accepted

## Context
System.Text.Json currently serializes enums numerically, which couples clients to declaration order and permits undefined numeric values.

## Decision
Configure the built-in `JsonStringEnumConverter` with camel-case naming and `allowIntegerValues: false` for all HTTP JSON.

## Consequences
Responses are stable named strings and all numeric enum tokens are rejected. Named input retains the framework converter's case-insensitive matching; no custom converter is introduced.

## Alternatives considered
Keep numeric enums - rejected because enum reordering becomes an accidental wire change.  
Allow defined numeric values - rejected because clients would still depend on ordinals.  
Build a strict-case custom converter - rejected because strict input casing is not required.

# ADR-4: Separate server-owned API correlation from inbound distributed tracing
Date: 2026-08-14     Status: Accepted

## Context
ASP.NET Core can derive `Activity.TraceId` from inbound `traceparent`; copying it to `X-Trace-Id` lets an untrusted caller choose the identifier returned in problems and logs.

## Decision
Generate a fresh server-owned API correlation ID for every request and use it for `HttpContext.TraceIdentifier`, `X-Trace-Id`, problem details, and middleware log correlation. Do not replace the platform Activity or echo inbound trace headers.

## Consequences
Client-controlled trace IDs cannot spoof API correlation. Platform distributed-trace IDs and the API-visible correlation ID may differ and must be labeled distinctly in logs.

## Alternatives considered
Continue echoing `Activity.TraceId` - rejected because it is caller-influenced.  
Discard inbound `traceparent` and start a new platform trace - rejected because the security requirement concerns the API-visible ID and distributed tracing can remain useful.
