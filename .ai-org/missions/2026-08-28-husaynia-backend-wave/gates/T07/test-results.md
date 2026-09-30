# T07 Prayer developer validation

Date: 2026-08-28  
Environment: Windows_NT, .NET SDK 10.0.400, `(localdb)\MSSQLLocalDB`.  
Status: developer-executed validation passed; independent test gate remains pending.

| Command | Result |
|---|---|
| `dotnet test .\tests\Husaynia.Domain.Tests\Husaynia.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer." --nologo` | 7 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer." --nologo` | 15 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer." --nologo` | 16 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~PrayerConfigurationAndModelTests" --nologo` | 4 passed, 0 failed, 0 skipped; subset of Integration total |
| `dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~PrayerRefreshJobTests" --nologo` | 6 passed, 0 failed, 0 skipped; subset of Application total |
| `dotnet test .\tests\Husaynia.ContractTests\Husaynia.ContractTests.csproj -c Release --no-restore --nologo` | 21 passed, 0 failed, 0 skipped |
| `dotnet test .\tests\Husaynia.ArchitectureTests\Husaynia.ArchitectureTests.csproj -c Release --no-restore --nologo` | 10 passed, 0 failed, 0 skipped |
| `dotnet build .\src\Husaynia.Web\Husaynia.Web.csproj -c Release --no-restore -warnaserror --nologo` | succeeded, 0 warnings, 0 errors |
| `dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror --nologo` | succeeded, 0 warnings, 0 errors |

Distinct executed tests represented above: 69 passed, 0 failed, 0 skipped.

All Prayer provider tests use synthetic objects or an in-memory `HttpMessageHandler`. No live
provider, credential, secret, payment, form destination, migration, or production resource was
used. LocalDB databases were uniquely named `HusayniaT07_*` and guarded before deletion.

One parallel architecture-test attempt transiently failed because Defender held an intermediate
assembly. The clean-process rerun passed 10/10 and the subsequent strict solution build passed.
