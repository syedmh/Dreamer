# ADR-004: Use ASP.NET Core Identity with six deny-by-default policies
Date: 2026-08-15     Status: Accepted

## Context
The prior app grants admin access to any authenticated user. The frozen contract names six
least-privilege roles and requires auditing of allowed and denied privileged attempts.

## Decision
Use ASP.NET Core Identity in Azure SQL, disable public registration, require MFA for privileged
roles, map each capability to an authorization policy, recheck policy in Application use cases, and
write append-only audit events with correlation IDs.

## Consequences
No additional identity service is required and authorization is testable. The application owns
account lifecycle and recovery operations.

## Alternatives considered
- Authentication-only `[Authorize]` - rejected as explicitly noncompliant.
- Microsoft Entra-only administration - deferred because tenant/access assumptions are unverified.
- Claims scattered in controllers - rejected because policy drift would be likely.

