# T11 Forms test results

Date: 2026-08-28  
Environment: Windows NT, .NET 10.0, SQL Server LocalDB  
Restore: not performed; every command used `--no-restore`  
Live activity: zero provider/email/network calls, zero live forms, zero migrations

## Final results

| Command | Result |
|---|---|
| `dotnet build .\src\Husaynia.Web\Husaynia.Web.csproj -c Release --no-restore -warnaserror` | BLOCKED before compilation by cached `NU1900`: NuGet vulnerability service index unavailable |
| `dotnet test .\tests\Husaynia.Domain.Tests\Husaynia.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Husaynia.Domain.Tests.Forms"` | PASS; 3 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~Husaynia.Application.Tests.Forms"` | PASS; 7 passed, 0 failed, 0 skipped |
| Current manually compiled Integration assembly, `dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~Husaynia.IntegrationTests.Forms"` | PASS; 50 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.ArchitectureTests\Husaynia.ArchitectureTests.csproj -c Release --no-restore` | PASS; 10 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~FrozenContractTests"` | PASS; 3 passed, 0 failed, 0 skipped |

Focused Forms/architecture/contracts total reported by these commands: 73 passed, 0 failed,
0 skipped.

Security rework regression command:

`dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~IdentitySecurityAndAuditTests|FullyQualifiedName~IdentityCompatibilityRegressionTests|FullyQualifiedName~IdentityMfaAndLockoutTests"`

Result: PASS; 17 passed, 0 failed, 0 skipped.

Relevant T19 commands:

- Application retention/job processor tests: 32 passed, 0 failed, 0 skipped.
- Integration retention/job store/dependency-scope tests: 39 passed, 0 failed, 0 skipped.

The final independent confirmation executed the complete Forms/T19 regression selection and
reported **468 passed, 0 failed, 0 skipped**. This included real SQL lock-contention coverage proving
that an expired/taken-over worker performs zero mutation, a hold committed while a worker waits
blocks anonymization, and a current permit mutates exactly once.

Because another restore wrote `NU1900` as an Error into shared `obj/project.assets.json`, every
required official `--no-restore` build/test invocation now stops before compilation. No restore,
asset-cache edit, audit suppression, package, project, or lock change was performed. As a bounded
diagnostic, current Infrastructure, Web, and Integration sources were compiled from the already
cached references with compiler warnings treated as errors (excluding only compiler reference
unification diagnostics), then the resulting isolated assembly was executed with `dotnet vstest`.
This does not replace or self-certify the blocked official strict/analyzer gate.

## Concurrency repetition

After the concurrency fixes, the duplicate-submission and SQL rate-limit race tests ran together
four consecutive times: 8 passed, 0 failed. The duplicate race also ran five consecutive times:
5 passed, 0 failed.

## Coverage represented

Versioned fields and all field kinds; contact/pledge synthetic fixtures; accessible errors;
authorization-before-validation/store; antiforgery; honeypot; strict/bounded JSON; keyed
fingerprints; fail-closed distributed rate limiting; atomic submission/value/T19 job/audit;
duplicate races/conflicts; pickup/disabled delivery; retry/dead-letter/cancellation; retention/legal
hold; configuration validation; module/model discovery; admin role/MFA/exactly-one redacted audit.

The security rework adds 48 stale-session requests across eight Forms admin operations for disabled,
revoked-role, changed-stamp, deleted-user, missing-stamp, and revoked-MFA sessions; all were denied
before validation/store. Eight current-session operations succeeded with one audit each. Audit
finalizer failure returned 503 after one finalization attempt and no store access.

Independent test verdict: **APPROVED**.
