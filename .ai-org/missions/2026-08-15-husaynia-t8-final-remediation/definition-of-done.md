# Definition of Done

- [ ] Original exceptions survive rollback/disposal cleanup failures; cancellation regression is executed.
- [ ] Corrective Down uses a centralized safe privilege classification and never grants migration-history CRUD.
- [ ] Corrective migration uses transactional boundaries valid for concurrent indexes and staged validated constraints, including idempotent upgrades.
- [ ] Classification covers each EF-mapped table exactly, with executable tests.
- [ ] Immutable initial migration is unchanged; manifest verifies both migrations, designers, and snapshot.
- [ ] Safety documentation describes owner/runtime roles and up/down procedure.
- [ ] Real isolated PostgreSQL 18.6 fresh up/down/up and initial-to-corrective up/down/re-up pass.
- [ ] Full .NET build/tests/format, mobile lint/typecheck/tests, and Compose config pass with zero skipped persistence tests.
- [ ] Independent test, security, code review, and final judgment gates pass.
