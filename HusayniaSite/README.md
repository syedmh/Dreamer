# Husaynia Site

.NET 10 modular-monolith implementation of the Husaynia website. The repository now includes a
development-only, synthetic site that is ready for local UI and workflow testing while production
content, database migrations, credentials, and external integrations remain gated.

## Open and run in Visual Studio

1. Install Visual Studio 2026 with the **ASP.NET and web development** workload and .NET SDK
   `10.0.400` (pinned by `global.json`).
2. Open `HusayniaSite.sln`.
3. Set `Husaynia.Web` as the startup project.
4. Select the **Husaynia Local** launch profile and run.
5. Browse to `http://127.0.0.1:5086`.

The **Husaynia Local** launch profile explicitly enables `Husaynia:LocalDemo:Enabled`. Local mode
refuses to start outside Development. The separate **Husaynia Integrated** profile leaves local mode
off and exercises the established strict integrated configuration path.

## Command-line local run

With the already-built Release output:

```powershell
dotnet run --project .\src\Husaynia.Web\Husaynia.Web.csproj -c Release --no-build --no-restore --launch-profile "Husaynia Local"
```

Do not disable NuGet audit or ignore failed sources. If Release output is absent, restore/build
remains blocked until the NuGet TLS service-index failure is resolved.

## What can be tested locally

- Responsive crimson/white site shell, desktop/mobile navigation, footer, and local assets.
- Overview, Build Husaynia, legal, Duas, Arabic/RTL, and representative content pages.
- Programs, event details, Islamic calendar, `/events` redirect, sitemap, robots, and iCal.
- Deterministic synthetic prayer state with provider isolation and clear non-authoritative labels.
- Persisted-style social snapshot, photos, video/audio unavailable states, and no third-party embeds.
- Published-content search, empty/zero-result behavior, bounded input, and output encoding.
- Contact and pledge validation, honeypot, antiforgery, and in-memory acknowledgement.
- Donation categories, amount/anonymity inputs, and success/cancelled/processing simulations.
- Loopback-only, antiforgery-protected in-memory publish/unpublish and audit display at `/admin`.
- `/health/live` and `/health/ready` local-mode diagnostics.

Local mode registers no database, payment gateway, email sender, Blob client, social provider,
analytics, production secrets, or production network dependency. State is synthetic and disappears
when the process stops.

## Tests

```powershell
dotnet test .\tests\Husaynia.IntegrationTests\Husaynia.IntegrationTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~LocalReadiness"
```

Run the broader cached-dependency validation with:

```powershell
dotnet test .\HusayniaSite.sln -c Release --no-build --no-restore
```

Some SQL-backed suites require Windows LocalDB. No local-readiness test calls a live provider,
submits a real form, or creates a payment.

## Fail-closed dependency audit

The security gate remains separate and fail-closed:

```powershell
dotnet list .\HusayniaSite.sln package --vulnerable --include-transitive
```

As of 2026-08-28, `api.nuget.org` resolves and accepts TCP 443 but the TLS handshake fails in this
environment, producing unsuppressed `NU1900` errors. Consequently package-dependent T05/T06/T08/T12
work, clean source builds, and release audit approval remain blocked. No audit bypass is an
acceptable substitute.

See `docs\development.md` for boundaries and repository structure. Authoritative mission contracts
and ADRs are under `..\.ai-org\missions\2026-08-14-husaynia-site-modernization`.
