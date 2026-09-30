# Husaynia.org live baseline

Capture ID: `husaynia-20260816T063032Z`  
Started UTC: `2026-08-16T06:30:32.9950704+00:00`  
Completed UTC: `2026-08-16T06:41:33.6565439+00:00`  
Source: `https://www.husaynia.org/`  
Safety mode: `--no-submit` (no form, login, donation, or payment action is executed)

## Reproduce

From `HusayniaSite`:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --locked-mode
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj -c Release --no-restore
pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- capture --no-submit --output evidence/staging/<run-id>
$env:HUSAYNIA_BASELINE_EVIDENCE = (Resolve-Path evidence/staging/<run-id>)
dotnet test tests/Husaynia.BaselineCapture.Tests -c Release --no-restore
dotnet run --project tools/Husaynia.BaselineCapture -c Release -- promote --approved --from evidence/staging/<run-id> --to evidence/baseline
```

Capture output is staged and is not promoted over the approved baseline without independent approval and the explicit `promote --approved` command. Capture refuses a non-empty output directory. The crawler uses at most 6 concurrent safe public GET requests, honors `robots.txt`, performs at most two attempts per request, uses a 30-second request timeout and 25-minute whole-capture deadline, caps retained assets at 26214400 bytes each, and applies a 100 ms per-request delay. An in-run deterministic URL cache prevents redundant downloads.

## Inventories

- `route-manifest.json`: frozen C1-compatible URL contract instance.
- `http-inventory.json`: status, redirect hops, headers, retained payload, and checksum.
- `metadata-inventory.json`: title, canonical, robots, Open Graph, Twitter, JSON-LD, language/direction, and H1.
- `media-inventory.json`: discovered same-origin public assets and checksums.
- `navigation-inventory.json`: observed header/nav/footer labels, order, hierarchy, and destinations.
- `forms-widgets.json`: markup-only form fields and normalized dynamic-region observations.
- `religious-content.json`: ordered Arabic/transliteration/translation/audio fixtures and checksums.
- `screenshots.json`: validated representative template captures at all six AC-03 viewports.
- `screenshot-network-policy.json`: executable allow/block rules for GET-only visual rendering.
- `screenshot-network-decisions.json`: redacted per-request, terminal response/failure, and blocked-capability decisions.
- `screenshot-capture-provenance.json`: pinned Playwright/Chromium/runtime provenance and browser install command.
- `migration-import-manifest.json`: C5-shaped dry-run candidate evidence with an explicit rights profile.
- `residual-risks.json`: export/widget/form/payment unknowns and required follow-up.
- `checksums.sha256`: integrity hashes for every retained file except the checksum file itself.

## Counts

Routes `180`; successful/redirect responses `180`; metadata `173`; assets `619` (`617` retained); forms `348`; religious fixtures `7`; dynamic regions `1070`; screenshots captured `0/36`; screenshots quality-pass `0/36`; residual risks `3`. Duration `660.7` seconds; network requests `825`; cache hits `6`.

## Stability and rights

Screenshots use Microsoft.Playwright 1.62.0 with package-matched Chromium 151.0.7922.34 (revision 1234). The executable is SHA-256 verified before launch, ChromiumSandbox is true, and the browser receives only an allowlisted OS/runtime/temp environment while running as a non-administrative account from an empty OS temporary directory. The browser cache is read/execute, the repository is not exposed as a browser working directory, and only the run staging directory is written by .NET. Each bounded attempt opens a fresh context and page with exact viewport and screen dimensions, DPR 1, service workers blocked, downloads disabled, no cookies/storage state/permissions, and context-wide default-deny routing. DNS answers are public-only, fixed to https://www.husaynia.org:443 plus the approved static hosts, checked for changes before each attempt, and pinned for crawl and Chromium traffic. Same-origin GET/HEAD documents/resources and explicitly allowlisted static font/CDN resources are retained; non-GET/HEAD requests, form/payment endpoints, refresh navigation, WebSockets, popups, downloads, analytics, payment hosts, trackers, and non-allowlisted third-party widgets are blocked and terminally logged. No local HTML, fixture markup, masking, DOM rewrite, layout CSS, or viewport rescaling is used. Layout readiness waits for fonts, visible images, zero allowed in-flight requests, stable geometry, and a terminal request barrier; genuine live-page overflow is recorded as captured evidence but fails quality and promotion. Retained public assets are fidelity/migration evidence only; later publication requires ownership/license confirmation. Private/export-only records, form payloads, credentials, and payment data are not captured.