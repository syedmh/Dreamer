# Husaynia.org live baseline

Capture ID: `husaynia-20260816T033708Z`  
Started UTC: `2026-08-16T03:37:08.1295651+00:00`  
Completed UTC: `2026-08-16T03:47:25.0783795+00:00`  
Source: `https://www.husaynia.org/`  
Safety mode: `--no-submit` (no form, login, donation, or payment action is executed)

## Reproduce

From `HusayniaSite`:

```powershell
dotnet restore tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj
dotnet build tools/Husaynia.BaselineCapture/Husaynia.BaselineCapture.csproj --no-restore
dotnet run --project tools/Husaynia.BaselineCapture -- capture --no-submit
dotnet test tests/Husaynia.BaselineCapture.Tests
```

The crawler uses at most 2 concurrent safe public GET requests, honors `robots.txt`, performs at most two attempts per request, uses a 30-second request timeout and 35-minute whole-capture deadline, caps retained assets at 26214400 bytes each, and applies a 300 ms per-request delay. An in-run deterministic URL cache prevents redundant downloads. Re-running removes and replaces generated bulk directories beneath this directory.

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

Routes `180`; successful/redirect responses `180`; metadata `173`; assets `615` (`615` retained); forms `347`; religious fixtures `7`; dynamic regions `1074`; screenshots `36/36`; residual risks `3`. Duration `616.9` seconds; network requests `797`; cache hits `6`.

## Stability and rights

Screenshots use the exact Chromium revision matched to the pinned Microsoft.Playwright package. Each capture opens the live HTTPS page in a fresh context with exact viewport and screen dimensions, DPR 1, service workers blocked, downloads disabled, and context-wide default-deny routing. Same-origin GET/HEAD resources and explicitly allowlisted static font/CDN resources are retained; scripts, form submission, refresh navigation, WebSockets, analytics, payment hosts, trackers, and third-party dynamic widgets are blocked. No local HTML, fixture markup, masking, DOM rewrite, layout CSS, or viewport rescaling is used. Layout readiness waits for fonts, visible images, zero allowed in-flight requests, and stable geometry; natural overflow is recorded without forced max-width rules. Build first, then install the package-matched browser explicitly with `pwsh tools/Husaynia.BaselineCapture/bin/Release/net10.0/playwright.ps1 install --no-shell chromium`. Retained public assets are fidelity/migration evidence only; later publication requires ownership/license confirmation. Private/export-only records, form payloads, credentials, and payment data are not captured.