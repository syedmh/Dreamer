# T16 Final Verification Evidence

## Red/green

- Initial command:
  `dotnet test tests/HusayniaTabruk.Application.Tests/HusayniaTabruk.Application.Tests.csproj --filter FullyQualifiedName~T16CancellationContractTests --no-restore`
- Red: 1 failed because the T16 command types did not exist.
- Green: 1 passed after identifier-only contracts were added.

## Final commands

- `dotnet format HusayniaTabruk.sln --no-restore --verify-no-changes` — exit 0.
- `dotnet build HusayniaTabruk.sln --no-restore -warnaserror` — succeeded, 0 warnings/errors.
- Full .NET with PostgreSQL 18.6 and psql path — 977 passed, 0 failed, 0 skipped:
  - Domain 395
  - Application 155
  - API contract 74
  - Integration 353
- Focused T16 PostgreSQL — 7 passed, 0 failed, 0 skipped.
- Mobile:
  - lint passed
  - typecheck passed
  - 121 tests passed
  - generated-client drift check passed

PostgreSQL version:
`PostgreSQL 18.6 on x86_64-windows, compiled by msvc-19.44.35228, 64-bit`.
