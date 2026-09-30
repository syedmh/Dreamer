# Local readiness validation results

Date: 2026-08-28

## Prohibited restore evidence

The implementation fallback ran a restore with audit disabled and failed sources ignored. The
exact command is intentionally not retained as an approved workflow. It is not accepted validation
evidence and must not be repeated.

## Strict build

```powershell
dotnet build .\HusayniaSite.sln -c Release --no-restore -warnaserror
```

The implementation fallback reported exit 0 after the prohibited restore. Independent current
verification without suppression returned exit 1 before compilation with 11 unsuppressed NU1900
errors. The strict build is **BLOCKED_EXTERNAL**, not passed.

## Local readiness suite

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~LocalReadiness"
```

Result: exit 0; 10 passed, 0 failed, 0 skipped.

Coverage includes representative routes, navigation/security headers, search encoding, form
antiforgery and receipt, donation category/outcome preservation, public-visible admin publication,
redacted audit display, video unavailable state, iCal/sitemap/robots/health, redirect behavior, and
the Development-only startup guard.

## Full solution regression

```powershell
dotnet test .\HusayniaSite.sln -c Release --no-build --no-restore --logger "console;verbosity=minimal"
```

Result: exit 0; 576 passed, 0 failed, 0 skipped:

- Domain: 11
- Application: 169
- Integration: 363
- Contract: 21
- Architecture: 10
- E2E scaffold: 1
- System validation scaffold: 1

## Runtime smoke

```powershell
dotnet run --project .\src\Husaynia.Web\Husaynia.Web.csproj -c Release --no-build --launch-profile "Husaynia Local"
```

`curl` returned HTTP 200 for 19 representative endpoints: home, overview, prayer, programs,
calendar, event detail, announcements, photos, videos, Duas, category donation, contact, pledge,
admin, iCal, sitemap, robots, and live/ready health. `/events` returned one HTTP 301 to
`/islamic-calendar`.

Independent current verification confirmed 17 representative endpoints returned HTTP 200 and the
server listened on `http://127.0.0.1:5086`.

## Fail-closed external dependency audit

```powershell
dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive
```

Result: exit 1; 11 unsuppressed NU1900 errors because `https://api.nuget.org/v3/index.json` could
not be loaded. Audit status is BLOCKED, not passed. PX-01 and T05/T06/T08/T12 remain blocked.
