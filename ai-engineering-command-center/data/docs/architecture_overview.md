# System Architecture Overview

## Services

The platform is composed of the following microservices:

| Service           | Language | Responsibility                        |
|-------------------|----------|---------------------------------------|
| api-gateway       | Go       | Rate limiting, auth, routing          |
| payment-service   | Python   | Payment processing, retries           |
| user-service      | Python   | User profiles, auth tokens            |
| notification-svc  | Node.js  | Email, SMS, push notifications        |
| analytics-service | Python   | Event processing, dashboards          |

## Data Flow

```
Client → API Gateway → payment-service → Postgres (primary)
                    ↘ Redis (session cache)
                    ↘ notification-svc (async via SQS)
```

## Key Design Decisions

### Event-Driven Communication
Services communicate asynchronously via SQS where latency tolerance allows.
Synchronous REST calls are only used for user-facing critical paths.

### Database per Service
Each service owns its own database schema. Cross-service data access is
performed via service APIs, never direct DB queries.

### Circuit Breaker Pattern
All inter-service HTTP calls use a circuit breaker (Hystrix-compatible).
Default thresholds: 50% failure rate over 10 requests opens the breaker.

### Retry Strategy
Retryable errors use exponential backoff:
- Base delay: 500ms
- Max delay: 30s
- Max attempts: 3
- Jitter: ±10% to prevent thundering herd

## Infrastructure

- **Compute**: AWS EKS (Kubernetes 1.28)
- **Database**: AWS RDS Postgres 15 (Multi-AZ)
- **Cache**: AWS ElastiCache Redis 7
- **Queue**: AWS SQS (standard queues)
- **Observability**: Datadog APM + logs, PagerDuty alerts
