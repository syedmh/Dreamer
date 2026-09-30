# ADR-005: Isolate integrations behind ports and persisted snapshots
Date: 2026-08-15     Status: Accepted

## Context
Prayer, social, email, analytics, and payment dependencies must fail independently. Adding a queue
or cache service by default would increase nonprofit cost.

## Decision
Define provider ports in Application, adapters in Infrastructure, persisted prayer/social
snapshots, and a leased `BackgroundJob` table for retryable work. Public reads consume local state,
not live provider calls. Analytics/social browser scripts are consent-gated.

## Consequences
Outages do not cascade and tests can substitute providers. Jobs can be delayed when the app is
inactive, so freshness alerts and on-activation catch-up are required.

## Alternatives considered
- Live provider calls during page rendering - rejected for latency and outage propagation.
- Service Bus/Functions/Redis - rejected until measured delivery or scale needs justify them.
- Silent failure - rejected because explicit user states and operator telemetry are required.

