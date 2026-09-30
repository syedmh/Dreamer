# ADR-003: Store immutable content revisions with explicit publication
Date: 2026-08-15     Status: Accepted

## Context
Editors require preview, publish, unpublish, rollback, Unicode religious text integrity, SEO
control, and non-destructive migration reconciliation.

## Decision
Store stable content identities and immutable revisions. Draft and published revision pointers are
updated atomically; rollback republishes a previous revision as a new revision. Use typed event,
media, prayer, donation, and form tables rather than forcing all behavior into generic content.

## Consequences
History, preview, rollback, checksums, and import conflict detection are reliable. Storage grows
with edits and publish operations require optimistic concurrency.

## Alternatives considered
- Edit published rows in place - rejected because rollback/audit/import safety becomes unreliable.
- External/headless CMS - rejected for cost, second authorization boundary, and URL/model impedance.
- Everything as schema-free JSON - rejected because high-risk operational data needs constraints.

