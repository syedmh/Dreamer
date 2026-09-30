# ADR-002: Use a minimal isolated Azure PaaS baseline
Date: 2026-08-15     Status: Accepted

## Context
Three isolated stages, managed secrets, persistence, media, observability, recovery, and nonprofit
cost control are required; final SKU selection is prohibited.

## Decision
Per environment, deploy App Service, Azure SQL Database, StorageV2, Key Vault, Application
Insights/Log Analytics, and alerts using Bicep. Use managed identity and no mandatory CDN, Redis,
queue, AI Search, slot, or separate CMS.

## Consequences
The topology is small, managed, and repeatable. Media initially traverses the web application and
in-process jobs depend on application activation; both are measured before adding services.

## Alternatives considered
- AKS/containers - rejected for disproportionate operational cost.
- Azure Static Web Apps plus APIs - rejected because the server-rendered/admin/application model
  would be split without benefit.
- Front Door/CDN/Redis/Service Bus by default - rejected as premature paid infrastructure.

