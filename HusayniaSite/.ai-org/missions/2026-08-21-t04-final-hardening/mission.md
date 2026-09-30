# T04 Final Hardening

Objective: Close the four remaining T04 security findings without migrations, runtime DDL, project/package edits, or changes outside Identity production/tests.

Classification: security-sensitive large bugfix/hardening.

Frozen inputs: T04, ADR-004, R-19..24, and the CTO requirements in the initiating directive.

Scope:
- Bootstrap seal database immutability.
- Cancellation-safe exactly-once privileged auditing.
- Complete sanitized MFA auditing.
- DB-backed distributed anonymous throttling.
- Causal Identity tests and exact T18 schema/SQL handoff.

Preservation note: an unrelated active Social mission existed before this mission; its artifacts remain untouched.
