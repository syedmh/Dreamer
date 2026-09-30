# ADR-001: Use a .NET 10 server-rendered modular monolith
Date: 2026-08-15     Status: Accepted

## Context
The target is empty, Visual Studio is mandatory, fidelity/SEO dominate, and required throughput is
modest. The prior .NET 8 three-project solution is structurally reusable but incomplete.

## Decision
Use .NET 10 LTS, Visual Studio 2026, ASP.NET Core MVC/Razor Pages, progressive enhancement, and
Domain/Application/Infrastructure/Web projects in one deployable modular monolith.

## Consequences
One solution and deployment remain inexpensive and understandable. Module scaling is coupled and
boundaries require architecture tests.

## Alternatives considered
- SPA plus API - rejected for extra rendering, accessibility, SEO, and visual-parity complexity.
- Microservices - rejected because no independent scaling or ownership need justifies them.
- Continue directly in prior source - rejected because the mission requires a new target and the
  prior security/data/test gaps require controlled reuse rather than inheritance.

