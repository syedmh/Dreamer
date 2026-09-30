# T10 Code Review

Status: **CHANGES_REQUIRED**

- Prior High invitation/governance CAS defect is resolved.
- Remaining Medium: expired invitations cannot be reissued because the conflict query ignores expiry.
- Remaining Medium: OpenAPI lacks admin response `ETag` metadata and marks required body fields optional.

Evidence: independent review plus focused build/application/contract/PostgreSQL tests.
