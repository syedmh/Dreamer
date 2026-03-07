# Retry Queue — Engineering Reference

## Overview

The retry queue handles transient failures in downstream service calls.
Jobs that fail with a retryable error code are placed onto a Redis-backed queue
with exponential backoff.

## Configuration

| Parameter        | Default | Description                              |
|------------------|---------|------------------------------------------|
| max_retries      | 3       | Maximum number of retry attempts         |
| base_delay_ms    | 500     | Initial backoff delay in milliseconds    |
| max_delay_ms     | 30000   | Cap on backoff delay                     |
| dead_letter_ttl  | 7d      | How long dead-letter entries are kept    |

## Retry Eligibility

A job is eligible for retry if the error code is in the `RETRYABLE_ERRORS` set:
- `db_pool_exhausted`
- `upstream_timeout`
- `rate_limited`

Non-retryable errors (e.g. `validation_error`, `not_found`) are rejected immediately.

## Dead Letter Queue

After `max_retries` the job moves to the dead-letter queue (DLQ).
An alerting rule fires a PagerDuty notification when DLQ depth > 100.

## Runbook

1. Check DLQ depth: `redis-cli llen dlq:payments`
2. Inspect failed jobs: `redis-cli lrange dlq:payments 0 9`
3. Replay jobs after fix: `./scripts/replay_dlq.sh payments`
