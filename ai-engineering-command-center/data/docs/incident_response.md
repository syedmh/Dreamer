# Incident Response Runbook

## Severity Levels

| Level | Definition                              | Response Time |
|-------|-----------------------------------------|---------------|
| SEV1  | Production down, revenue impact         | 15 minutes    |
| SEV2  | Degraded service, partial outage        | 30 minutes    |
| SEV3  | Non-critical issue, workaround exists   | 4 hours       |

## Response Steps

### SEV1 Response

1. Page on-call engineer via PagerDuty
2. Create incident Slack channel: `#inc-YYYYMMDD-description`
3. Assign incident commander (IC) and communications lead
4. Begin investigation — check dashboards, logs, recent deploys
5. Communicate status every 15 minutes to stakeholders
6. Mitigate (rollback, feature flag, circuit breaker)
7. Confirm resolution and monitor for 30 minutes
8. Write post-mortem within 48 hours

## Common Mitigation Patterns

### Database Connection Pool Exhausted
```bash
# Check current pool usage
psql -c "SELECT count(*) FROM pg_stat_activity;"

# Increase pool size (temporary)
kubectl set env deployment/payment-service DB_POOL_SIZE=50 -n prod

# Or enable pgBouncer connection pooling
kubectl apply -f k8s/pgbouncer.yaml
```

### Circuit Breaker Open
- Check upstream service health
- Verify the circuit breaker threshold configuration
- Once upstream is healthy, the breaker will move to HALF-OPEN automatically

### High Memory Usage
```bash
# Check pod memory
kubectl top pods -n prod

# Rolling restart (if memory leak suspected)
kubectl rollout restart deployment/<name> -n prod
```

## Post-Mortem Template

- **Incident Summary**: What happened?
- **Timeline**: Key events with timestamps
- **Root Cause**: What caused it?
- **Contributing Factors**: What allowed it to happen?
- **Impact**: Duration, affected users, revenue
- **Action Items**: What prevents recurrence?
