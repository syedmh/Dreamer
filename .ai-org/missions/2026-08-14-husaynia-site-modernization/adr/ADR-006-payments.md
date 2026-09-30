# ADR-006: Make verified Stripe webhooks the only payment completion authority
Date: 2026-08-15     Status: Accepted

## Context
The prior app completes donations from both a browser callback and webhook and increments campaign
totals separately. Duplicate/concurrent delivery must produce exactly one completion.

## Decision
The success page is read-only. A signature-verified webhook inserts a unique provider event and,
in one transaction, transitions the donation and inserts a unique campaign ledger entry. Category,
currency, amount rules, and baseline-proven one-time/subscription modes are data-driven.

## Consequences
Refresh/replay/concurrency are safe and payment rules are configurable. Completion display can lag
briefly until webhook processing succeeds.

## Alternatives considered
- Browser callback completion - rejected because browsers are untrusted and callbacks replay.
- Direct card collection - rejected because Stripe Checkout minimizes PCI scope.
- Hard-coded campaigns/mode - rejected because the frozen baseline is configurable.

