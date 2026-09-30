# ADR-007: Treat migration manifests and legacy URLs as executable contracts
Date: 2026-08-15     Status: Accepted

## Context
No authoritative WordPress export is currently guaranteed, but exact public URL/content/media/SEO
fidelity and later non-destructive reconciliation are required.

## Decision
Capture a timestamped machine-readable crawl/media manifest. Normalize crawl and authorized
WordPress sources into one import model, dry-run by default, identify source versions/checksums,
apply idempotently, and conflict instead of overwriting newer edits. Validate one-hop redirects and
stable media aliases as contract tests.

## Consequences
Crawl-first delivery can proceed and later evidence can reconcile safely. Hidden widget/plugin
configuration remains a documented residual risk until export evidence is available.

## Alternatives considered
- One-off database seed scripts - rejected because they are not replayable or reconcilable.
- Wait indefinitely for an export - rejected by frozen decision D-01.
- Preserve all WordPress technical endpoints - rejected because only public/indexed contracts matter.

