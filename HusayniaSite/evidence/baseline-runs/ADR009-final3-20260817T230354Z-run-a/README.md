# Husaynia.org incomplete capture diagnostics

Capture ID: `husaynia-20260817T230407Z`  
Started UTC: `2026-08-17T23:04:07.9739074+00:00`  
Failed UTC: `2026-08-17T23:27:25.2697018+00:00`  
Source: `https://www.husaynia.org/`  
Safety mode: `--no-submit`  
Status: `failed`  
Failure stage: `route-capture`  
Failure reason: `capture-error:socketexception`

This directory is retained diagnostic evidence only. The capture did not complete,
has no determinism comparison or baseline verification seal, cannot satisfy the
validator, and must never be promoted. Start any retry in a new empty output
directory so this evidence remains intact.

## Truthful partial counts

Discovered routes `159`; retained route observations
`0`; successful/redirect responses
`0`; metadata `0`; assets
`0` (`0` retained); forms
`0`; religious fixtures `0`; dynamic
regions `0`; screenshots captured
`0/36`; screenshots
quality-pass `0/36`;
residual risks `3`. Duration
`1397.296` seconds; network requests
`227`; cache hits `6`.

`screenshots.json` always contains the complete 36-key matrix. Rows completed before
failure are retained unchanged; remaining rows are explicit failures with the stable
failure reason. `screenshot-network-decisions.json` contains every decision returned
by a completed screenshot attempt. Browser provenance and policy are retained when a
browser session existed; otherwise the tooling-failure provenance/policy records are
intentionally invalid for baseline approval. Exception messages, ambient environment
values, paths, credentials, and secrets are not written. `checksums.sha256` was
generated last and covers every retained file except itself.