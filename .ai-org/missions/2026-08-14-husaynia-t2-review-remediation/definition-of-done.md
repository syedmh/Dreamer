# Definition of Done

- [ ] Atomic idempotency create/complete/fail contracts are explicit, strongly typed, cancellation-aware, and expose expected-state/concurrency conflicts.
- [ ] Duplicate, race, request-mismatch, and invalid terminal-transition tests execute.
- [ ] No storage adapter or T3 API behavior is implemented.
- [ ] Strong-ID public persistence/serialization access rejects default values; UUIDv7 and canonical parsing remain intact.
- [ ] Application dependency guard evaluates MSBuild-resolved package, framework, and project references, including imported items.
- [ ] Mutation probes prove each dependency category is detected.
- [ ] `dotnet restore`, warnings-as-errors build, full tests, format verification, Docker Compose config, and mobile lint/typecheck/tests pass.
- [ ] Independent test engineer and code reviewer pass; Engineering Judge approves.
- [ ] Exact counts and T3 readiness are reported.

