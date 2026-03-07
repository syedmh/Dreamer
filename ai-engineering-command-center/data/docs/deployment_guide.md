# Deployment Guide

## Overview

Services are deployed via GitHub Actions to Kubernetes (EKS).
Every merge to `main` triggers the CD pipeline.

## Deployment Pipeline

1. **Build** — Docker image built and pushed to ECR
2. **Scan** — Trivy security scan on the image
3. **Staging deploy** — Helm upgrade to `staging` namespace
4. **Smoke tests** — Automated health check suite runs
5. **Production deploy** — Manual approval gate, then Helm upgrade to `prod`

## Rollback Procedure

If a deployment causes issues:

```bash
# Rollback to previous Helm revision
helm rollback <release-name> -n prod

# Verify rollback
kubectl rollout status deployment/<name> -n prod
```

## Environment Variables

All secrets are stored in AWS Secrets Manager and injected at runtime via
the External Secrets Operator. Never commit secrets to the repository.

Key environment variables:

| Variable              | Description                    |
|-----------------------|-------------------------------|
| DATABASE_URL          | Primary Postgres connection    |
| REDIS_URL             | Redis connection string        |
| PAYMENT_API_KEY       | Third-party payment gateway    |
| SENTRY_DSN            | Error tracking endpoint        |

## Health Checks

Each service exposes:
- `GET /health/live`  — liveness probe (returns 200 if process is alive)
- `GET /health/ready` — readiness probe (returns 200 if DB connection is OK)

## Deployment Frequency

Target: multiple deploys per day. Each deploy must be:
- Small (< 400 lines changed)
- Tested (> 80% coverage on changed code)
- Reviewed (at least one approving review)
