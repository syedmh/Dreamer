# Definition of Done

- API authorization fails closed by default; anonymous access is explicit only.
- Deterministic endpoint-discovered OpenAPI matches a checked-in non-self-updating snapshot.
- Enums serialize as camel-case strings and undefined numeric values are rejected.
- Safe RFC 9457 responses cover malformed and unexpected exceptions without text leakage.
- Seven missing independent probes are committed as API contract tests.
- Build, full tests, warnings-as-errors, format, mobile, and Compose pass where available.
- Independent tests, security review, code review, and final judgment pass.
- No T4, database, deployment, commit, push, reset, or history changes.
