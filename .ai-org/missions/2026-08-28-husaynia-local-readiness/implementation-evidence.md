# Implementation evidence

## Production source

- `src\Husaynia.Web\Program.cs`
- `src\Husaynia.Web\Features\LocalSite\LocalSiteModule.cs`
- `src\Husaynia.Web\Features\LocalSite\LocalSiteData.cs`
- `src\Husaynia.Web\Features\LocalSite\LocalSiteRenderer.cs`
- `src\Husaynia.Web\Features\LocalSite\LocalSiteEndpoints.cs`
- `src\Husaynia.Web\appsettings.Development.json`
- `src\Husaynia.Web\Properties\launchSettings.json`
- `src\Husaynia.Web\wwwroot\css\site.css`
- `src\Husaynia.Web\wwwroot\js\site.js`
- `src\Husaynia.Web\wwwroot\images\community-hall.svg`
- `src\Husaynia.Web\wwwroot\images\project.svg`

## Tests and documentation

- `tests\Husaynia.IntegrationTests\LocalReadiness\LocalSiteTests.cs`
- `README.md`
- `docs\development.md`

## Key behavior

- Local and integrated startup are mutually exclusive; local is explicit, defaults off, and is
  refused outside Development.
- Public pages use trusted synthetic fixtures and HTML-encode search/query values.
- CSP and secure response headers allow only same-origin local assets.
- Contact/pledge use antiforgery, honeypot, bounded validation, no delivery, and no value retention.
- Donation simulation validates/preserves category and amount and never accepts card data.
- Admin publication affects public announcements/search, requires loopback and antiforgery, and
  retains only ephemeral redacted audit outcomes.
- `/events` performs one permanent redirect; sitemap, robots, iCal, and health states are available.

## Final source hashes

```text
caf200df3d4c884a499e319418ec1f922fd517286fef4f8b0dea377738d5c0e2  LocalSiteData.cs
fadf171b2c3084b0e873bd8b0b5dcee47146346eac109ba9f7a0449eff6ee7b7  LocalSiteEndpoints.cs
d75042a0dde11d1da682e5cddb96fa55fe5e39727706121ffc6f0ed2983c90dc  LocalSiteModule.cs
3106c729104c7810943f5b40d18970fecefe588b4030f6fff1002aa6cb7d89c9  LocalSiteRenderer.cs
d706c0f54e64e6d88ef00b3b312192801df2fe61e2f37bfe6f1be406b22870ca  LocalSiteTests.cs
```
