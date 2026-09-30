# T07 Prayer red-first evidence

Date: 2026-08-28  
Scope: T07-owned Prayer paths only.

## Asset precondition

The first two `--no-restore` attempts could not reach compilation because existing assets contained
NU1900 as an error:

```powershell
dotnet test .\tests\Husaynia.Domain.Tests\Husaynia.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer."
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~.Prayer."
```

Both exited 1: vulnerability data for `https://api.nuget.org/v3/index.json` was unavailable. This was
not counted as a valid red test. Existing locked assets were refreshed once, without changing a
manifest or lock file:

```powershell
dotnet restore .\HusayniaSite.sln --locked-mode -p:NuGetAudit=false --ignore-failed-sources
```

Result: 12 projects restored from the locked/current package graph.

## Valid red executions

After the asset refresh, the same Domain and Application commands exited 1 for the intended missing
Prayer behavior:

- Domain: `Husaynia.Domain.Prayer` did not exist.
- Application: `Husaynia.Application.Prayer`, its stores, services, commands, receipts, and job
  payload/handler did not exist.

Two later gap tests were also executed red before their remediation:

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~OverrideForInactiveProfile|FullyQualifiedName~MutationRequiresAntiforgery"
```

Result: 0 passed, 2 failed:

1. inactive-profile override was incorrectly accepted;
2. antiforgery failure omitted the response correlation identifier.

The store now rejects the inactive profile atomically with one audit, and Web antiforgery failures
return the correlation identifier. The same command then passed 2/2.

## Final green

- Domain Prayer: 7 passed, 0 failed, 0 skipped.
- Application Prayer: 15 passed, 0 failed, 0 skipped.
- Integration/Web Prayer: 15 passed, 0 failed, 0 skipped.

## Independent-review rework red/green

The five review findings received causal tests before remediation.

Red commands and outcomes:

```powershell
dotnet test .\tests\Husaynia.Domain.Tests\Husaynia.Domain.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~OffsetKeyCase" --nologo
```

Result: 0 passed, 1 failed. Mixed-case offset keys produced different canonical JSON/hashes.

```powershell
dotnet test .\tests\Husaynia.Application.Tests\Husaynia.Application.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ActivationPersistsPointer|FullyQualifiedName~ActivationEnqueueFailure|FullyQualifiedName~FuturePersistedGeneration" --nologo
```

Result: 0 passed, 3 failed:

1. enqueue occurred before activation;
2. enqueue failure returned failure after no committed activation contract;
3. a future persisted generation timestamp was labeled fresh.

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~FarFutureProviderTimestamp|FullyQualifiedName~NearFutureProviderTimestamp|FullyQualifiedName~CreateProfileDoesNotQueryAfterCommitted" --nologo
```

Result: 0 passed, 3 failed:

1. far-future provider time was accepted;
2. near-future provider time was not clamped;
3. a synthetic post-insert projection query failure escaped after profile+audit commit.

After remediation, the same commands passed 1/1, 3/3, and 3/3. An additional race test simulates
an immediate handler state mutation and proves activation commits before enqueue.

## Final schema rework red/green

The schema findings were reproduced before changing `PrayerPersistence.cs`:

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~PrayerConfigurationAndModelTests" --nologo
```

Red result: 2 passed, 2 failed. The design-time model reported nullable Prayer rowversions, generated
SQL contained zero `rowversion NOT NULL` declarations, and the refresh-intent index was not explicitly
present under its required name.

Green result after Prayer-owned configuration changes: 4 passed, 0 failed, 0 skipped. The model and
generated SQL now prove three non-null rowversions and explicit
`IX_PrayerIntegrationStates_RefreshIntentProfileHash`.
