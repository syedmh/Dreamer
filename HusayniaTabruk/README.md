# Tabruk

Tabruk is an iPhone-first Expo application backed by a .NET 10 modular monolith and PostgreSQL. This foundation contains project and quality shells only; signup and other business behavior are intentionally not implemented.

## Prerequisites

- .NET SDK 10.0.400 (pinned by `global.json`)
- Node.js 24 LTS and npm 11
- Docker CLI with Docker Compose

## Repository boundaries

- `src/HusayniaTabruk.Domain` is framework-independent.
- `src/HusayniaTabruk.Application` references only Domain.
- `src/HusayniaTabruk.Infrastructure` implements Application ports.
- `src/HusayniaTabruk.Api` composes Application and Infrastructure.
- `apps/mobile` is the Expo Router client shell.

## Foundation validation

Run from this directory:

```powershell
docker compose config
dotnet restore .\HusayniaTabruk.sln
dotnet build .\HusayniaTabruk.sln --no-restore -warnaserror
dotnet test .\HusayniaTabruk.sln --no-build
npm ci --prefix .\apps\mobile
npm run lint --prefix .\apps\mobile
npm run typecheck --prefix .\apps\mobile
npm test --prefix .\apps\mobile -- --runInBand
```

Start the local PostgreSQL 18.6 service with:

```powershell
docker compose up -d postgres
```

The checked-in database credentials are development-only defaults and must not be used outside local development.

## Authentication operational boundary

Authentication rate-limit buckets are process-local and are not coordinated across API replicas.
Deployments requiring a global quota must replace the in-memory limiter with a shared store while
preserving the stable, non-secret refresh-family fingerprint contract.
