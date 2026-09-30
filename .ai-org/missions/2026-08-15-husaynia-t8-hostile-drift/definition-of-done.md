# Definition of Done

- [ ] Hostile same-name non-unique index and permissive check constraint fail closed.
- [ ] Failure occurs before corrective migration-history insertion.
- [ ] Correct existing objects pass idempotently after schema/table/definition verification.
- [ ] PUBLIC effective privileges are denied before and after corrective Down.
- [ ] EF upgrade and downgrade reject missing, multi-schema, and mismatched Search Path.
- [ ] Owner script enforces one explicit target schema and matching search path.
- [ ] Initial migration source and designer hashes remain unchanged.
- [ ] Manifest pins only the approved initial immutability inputs and modified corrective artifacts.
- [ ] Real PostgreSQL 18.6 persistence suite has zero skips and covers hostile drift and PUBLIC grants.
- [ ] Fresh, upgrade, down, and re-up migration cycles pass.
- [ ] Full .NET build/tests/format, mobile, and Compose pass.
- [ ] Independent tests, security review, code review, and judge approve.
- [ ] Provenance limitation is recorded as residual process risk.
