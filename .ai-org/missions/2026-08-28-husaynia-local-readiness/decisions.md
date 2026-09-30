# Local readiness decisions

## LR-01 — Add a Web-only synthetic adapter

The package-free local site lives under `Husaynia.Web\Features\LocalSite`. It changes no frozen
Application contract, package manifest, lock file, DbContext, migration, or provider adapter.

## LR-02 — Make local mode explicit and impossible outside Development

`Husaynia Local` opts in with `Husaynia__LocalDemo__Enabled=true`. The setting defaults false and
`Husaynia Integrated` preserves strict module/configuration startup. Local activation throws outside
Development.

## LR-03 — Prefer safe representative states over fake production integrations

Content and prayer values are labeled synthetic. Social/media provider states are represented
without embeds. Forms validate and acknowledge without retaining values or delivering. Donations
simulate category, amount, anonymity, success, processing, and cancellation without a gateway.

## LR-04 — Allow only ephemeral local administration

The local admin workflow is loopback-only, antiforgery-protected, in memory, visibly affects public
announcements/search, writes only redacted in-memory audit outcomes, and resets on restart.

## LR-05 — Separate compilation availability from security audit

NuGet audit must not be disabled and failed sources must not be ignored. During the external
outage, current Release output may be used for local runtime testing, but clean source compilation
remains blocked. No audit pass, build pass, package approval, or release claim is permitted.
