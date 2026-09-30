# T11 Forms red-first evidence

Date: 2026-08-28  
Owner: T11 implementation owner  
Verdict: implementation evidence only; not an independent gate

Tests were added before the Forms implementation:

- `tests/Husaynia.Domain.Tests/Forms/FormDomainTests.cs`
- `tests/Husaynia.Application.Tests/Forms/FormSubmissionValidationTests.cs`
- `tests/Husaynia.IntegrationTests/Forms/FormsConfigurationValidatorTests.cs`

Initial commands used `-c Release --no-restore --filter FullyQualifiedName~Forms`.

Observed red results:

- Domain: compile failed with `CS0234`, `Husaynia.Domain.Forms` absent.
- Application: compile failed with `CS0234/CS0246`, Forms contracts and validation absent.
- Integration: compile failed with `CS0234`, `Husaynia.Infrastructure.Forms` absent.

Subsequent red/green iterations caught and fixed:

- duplicate-fingerprint concurrent deadlock/zombie-transaction handling;
- fixed-window rate-limit absent-row deadlock handling;
- retry/dead-letter attempt-count behavior;
- exact model metadata nullability in schema assertions.

Final green commands and counts are in `test-results.md`.
