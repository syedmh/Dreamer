# T11 Forms stale-session security rework

Date: 2026-08-28  
Finding severity: High  
Status: remediated and locally verified; independent re-review pending

## Finding

`FormsEndpoints.AuthorizeAdminAsync` built the actor from the ambient cookie principal before
checking the current Identity user. A disabled/deleted user, revoked SiteAdministrator/MFA state,
changed security stamp, or missing stamp could retain stale role/MFA claims.

## Remediation

Forms now follows and strengthens the established module-local Prayer/Identity pattern:

1. call `AuthenticateAsync(IdentityConstants.ApplicationScheme)`;
2. require user ID and `husaynia.security_stamp`;
3. load the current `HusayniaIdentityUser` with `UserManager`;
4. reject absent/disabled users or ordinal security-stamp mismatch;
5. compare frozen cookie roles to current Identity roles;
6. reject an MFA cookie when current two-factor state is disabled;
7. sign out, replace the principal with anonymous, and update `HttpContext.User` on stale state;
8. only then build the actor, invoke policy/capability checks, construct audit, or reach transport,
   validation, and store code.

Denied stale sessions use the existing `IIdentityAuditFinalizer` once. Finalizer failure maps to
`forms_audit_unavailable`/503 and does not reach the store.

## Executed tests

- Forms integration namespace: 38 passed, 0 failed, 0 skipped.
- Identity cookie/security regression classes: 17 passed, 0 failed, 0 skipped.
- Domain/Application Forms: 10 passed, 0 failed, 0 skipped.
- Architecture/frozen contracts: 13 passed, 0 failed, 0 skipped.
- T19 retention/job Application and Integration regressions: 71 passed, 0 failed, 0 skipped.
- Strict Release Web build: 0 warnings, 0 errors.

The stale-session matrix executed eight Forms admin operations for each of:

- disabled user;
- revoked SiteAdministrator role;
- changed security stamp;
- deleted user;
- missing security-stamp claim;
- revoked current MFA state.

All 48 stale requests returned 401, produced exactly one redacted denied audit per correlation, and
left definition versions, submissions, and delivery jobs unchanged. A current valid session
completed definition read, submission read, definition publish, delivery retry, legal hold, legal
hold release, retention eligibility, and anonymization, each with exactly one allowed audit.

This artifact is implementation evidence, not the independent security verdict.
