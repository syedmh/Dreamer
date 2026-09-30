# Development guide

## Repository map

- `src\Husaynia.Domain`: entities and invariants without infrastructure dependencies.
- `src\Husaynia.Application`: use cases, frozen ports, authorization, jobs, and DTOs.
- `src\Husaynia.Infrastructure`: EF Core, Identity, operations, prayer, forms, and social adapters.
- `src\Husaynia.Web`: composition root, admin endpoints, and the synthetic local site.
- `tests`: domain, application, integration, contract, architecture, E2E, and system-validation tests.
- `contracts`: route and migration schemas.
- `eng`, `pipelines`, `infra`: validation, release, promotion, and Azure definitions.

## Local synthetic mode

Local synthetic mode is opt-in through the **Husaynia Local** launch profile or the
`Husaynia__LocalDemo__Enabled=true` environment variable. `appsettings.Development.json` keeps it
off by default. `Program.cs` selects one of two mutually exclusive composition paths:

1. **Local synthetic:** only framework services and `Features\LocalSite` are registered.
2. **Integrated application:** the established deterministic Application/Infrastructure/Web module
   discovery and strict configuration validation remain unchanged.

The local path is intentionally in Web because it is a presentation/test adapter. It does not
change frozen Application contracts or pretend to implement package-, migration-, or export-gated
production modules. `LocalSiteOptions.EnsureEnvironment` prevents accidental activation outside
Development. The **Husaynia Integrated** launch profile keeps the synthetic adapter disabled so
developers can exercise strict integrated composition when they have the required configuration.

### Safety properties

- Loopback launch URL; the in-memory admin endpoint additionally rejects non-loopback requests.
- All state-changing forms require ASP.NET Core antiforgery tokens.
- Form values are validated but not retained; only a synthetic receipt counter and redacted outcome
  are stored in memory.
- Donation simulation accepts no card/payment credentials and performs no gateway call.
- CSP allows only same-origin scripts/styles/assets; analytics and external embeds are absent.
- Search HTML-encodes visitor input and searches only an immutable synthetic public fixture.
- Robots and page metadata are `noindex`/disallow-all to avoid publishing the fixture.
- Local readiness is degraded by design and reports disabled database/external dependencies.

## Editing boundaries

- Do not add or change packages while PX-01 is blocked.
- Do not infer authoritative prayer values, donation modes, form destinations, legal claims, or
  religious text from incomplete live/widget evidence.
- Keep provider SDK types in Infrastructure and keep external I/O outside SQL transactions.
- Preserve endpoint and Application authorization plus append-only audit for integrated admin work.
- Do not add secrets, live endpoints, real payment tests, or production form destinations.
- T18 alone owns generated migrations and the EF model snapshot.

## Validation levels

### Local functionality

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~LocalReadiness"
```

These tests cover representative routes, secure headers, search encoding, forms, donation states,
local admin transitions, iCal/sitemap/robots, redirects, health, and the Development-only guard.

### Strict source build

```powershell
dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror
```

This command currently fails before compilation with 11 unsuppressed `NU1900` errors because the
NuGet vulnerability service TLS handshake is unavailable. Do not disable audit or ignore failed
sources to make it pass.

### External dependency security gate

```powershell
dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive
```

The external audit command must exit successfully before package selection or release claims.
Audit suppression and failed-source bypasses are prohibited. Record the audit and source build as
blocked until metadata access recovers.

## Production path

The integrated application still requires validated database, Identity anonymous-rate-limit,
environment, and feature configuration. Local synthetic mode is not a deployment configuration,
not a migration substitute, and not a production-readiness claim.
