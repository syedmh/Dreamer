# ADR-008: Promote one immutable application artifact through isolated stages
Date: 2026-08-15     Status: Accepted

## Context
Development, Staging, and Production require isolated data/configuration, identical application
bits, approval gates, retry safety, and recoverable last-known-good releases.

## Decision
CI publishes a versioned package, migrations, manifests, SBOM, reports, and SHA-256. CD promotes the
same package checksum; environment configuration is injected at deployment. Migrations are explicit
expand-compatible pipeline steps. Retain prior packages and database restore points.

## Consequences
Promotion evidence is objective and rollback is possible without rebuilding. In-place production
rollback may take longer than slot swap, but avoids mandating a paid slot-capable SKU.

## Alternatives considered
- Rebuild per environment - rejected because artifact identity would be lost.
- Runtime startup migrations - rejected because failures would couple schema change to availability.
- Mandatory deployment slots/blue-green - rejected because they imply a cost capability not yet
  approved; they remain an optional enhancement.
