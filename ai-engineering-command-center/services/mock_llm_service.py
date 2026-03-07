"""
Mock LLM service — no API key required.

Returns realistic pre-written responses for every agent in the system.
Activated automatically when ANTHROPIC_API_KEY is not set.

Agent detection works by matching keywords in the system prompt against
each agent's known ROLE description.
"""

from __future__ import annotations

import json
import random
from typing import Any


# ---------------------------------------------------------------------------
# Canned responses — realistic outputs for every agent
# ---------------------------------------------------------------------------

_RESPONSES: dict[str, dict[str, Any]] = {

    "code_review": {
        "agent": "code-review",
        "status": "success",
        "confidence": 0.88,
        "issues": [
            "SQL injection: string concatenation used in all DB queries (payment.py:13,19,28)",
            "N+1 query pattern in process_payment() — fetches all payments then iterates (payment.py:19)",
            "Hardcoded SECRET_KEY in PaymentService class body (payment.py:5)",
            "admin_delete() has no authorization check — any caller can delete payments",
            "get_all_payments() returns unbounded result set with no pagination",
        ],
        "recommendations": [
            "Replace string-concatenated SQL with parameterized queries throughout",
            "Batch status updates: single UPDATE … WHERE id IN (…) instead of per-row loop",
            "Move SECRET_KEY to environment variable; add pre-commit hook to block secrets",
            "Add @require_role('admin') guard to admin_delete()",
            "Add LIMIT/OFFSET pagination to get_all_payments()",
        ],
        "raw": {
            "severity": "high",
            "changed_files": ["services/payment.py"],
        },
    },

    "security": {
        "agent": "security",
        "status": "success",
        "confidence": 0.95,
        "issues": [
            "CWE-89 SQL Injection: all 4 queries use string concatenation (payment.py:13,19,22,28)",
            "CWE-798 Hardcoded credential: SECRET_KEY committed to source (payment.py:5)",
            "CWE-862 Missing authorization: admin_delete() performs DELETE with no role check",
            "CWE-284 Broken access control: get_all_payments() exposes all users' data",
        ],
        "recommendations": [
            "Rotate SECRET_KEY in production immediately — treat it as compromised",
            "Enforce parameterized queries via ORM; add SQLFluff rule to CI",
            "Add role-based access control decorator to all admin endpoints",
            "Scope payment queries to authenticated user ID at the query level",
        ],
        "raw": {
            "severity": "critical",
            "cwe_references": ["CWE-89", "CWE-798", "CWE-862", "CWE-284"],
        },
    },

    "architecture": {
        "agent": "architecture",
        "status": "success",
        "confidence": 0.82,
        "issues": [
            "PaymentService violates Single Responsibility: handles auth, data access, and business logic",
            "Direct db module calls couple business logic to infrastructure layer",
            "RefundService duplicates DB access patterns from PaymentService — no shared repository",
            "No service boundary between payment processing and refund logic",
        ],
        "recommendations": [
            "Extract PaymentRepository and RefundRepository as the data-access layer",
            "Inject repository via constructor to enable testing without a real DB",
            "Move authorization logic to a dedicated middleware or decorator layer",
            "Consider a single PaymentFacade entry point to enforce the service boundary",
        ],
        "raw": {
            "patterns_detected": ["God class", "Procedural data access", "Missing repository pattern"],
            "design_score": 4,
        },
    },

    "incident": {
        "agent": "incident",
        "status": "success",
        "confidence": 0.93,
        "issues": [
            "DB connection pool exhausted at 09:15 (100% utilization) triggering cascade",
            "Circuit breaker opened, rejecting all new payment requests for ~4 minutes",
            "Retry storm: failed transactions each spawned 3 retries, multiplying pool pressure",
        ],
        "recommendations": [
            "Increase DB connection pool size from default to 50 as immediate mitigation",
            "Add connection pool monitoring alert at 70% utilization (pre-exhaustion warning)",
            "Implement request queuing upstream of the pool to absorb traffic spikes",
            "Tune retry policy: use jitter + backoff to prevent retry storms",
        ],
        "raw": {
            "root_cause": "DB connection pool exhausted under load due to undersized pool and synchronous retry amplification",
            "affected_services": ["payment-service", "api-gateway"],
            "severity": "sev1",
        },
    },

    "sre": {
        "agent": "sre",
        "status": "success",
        "confidence": 0.87,
        "issues": [
            "No pre-exhaustion alert — team learned of pool exhaustion only after circuit breaker opened",
            "Retry policy lacks jitter, causing synchronized retry waves (thundering herd)",
            "Circuit breaker thresholds not tuned — opened too late after significant user impact",
        ],
        "recommendations": [
            "Add PagerDuty alert: DB pool utilization > 70% for > 2 minutes",
            "Add exponential backoff with ±20% jitter to all retry policies",
            "Implement pgBouncer connection pooling in transaction mode to multiply effective pool size",
            "Set circuit breaker to open at 30% failure rate over 5 requests (more sensitive)",
            "Add chaos engineering test: pool exhaustion scenario in staging monthly",
        ],
        "raw": {
            "immediate_actions": [
                "kubectl set env deployment/payment-service DB_POOL_SIZE=50 -n prod",
                "Enable pgBouncer: kubectl apply -f k8s/pgbouncer.yaml",
            ],
            "reliability_patterns": ["Circuit Breaker", "Bulkhead", "Exponential Backoff with Jitter"],
            "monitoring_gaps": ["DB pool utilization alert", "Retry rate per service alert"],
        },
    },

    "backlog": {
        "agent": "backlog",
        "status": "success",
        "confidence": 0.91,
        "issues": [
            "ENG-101 and ENG-105 are duplicates — both describe payment timeout from DB pool exhaustion",
            "ENG-103 (retry logic) has no acceptance criteria defined",
            "ENG-104 (Python upgrade) blocks ENG-103 but dependency not marked",
        ],
        "recommendations": [
            "Close ENG-105 as duplicate of ENG-101; add ENG-105 notes to ENG-101",
            "Reprioritize ENG-103 to high — retry logic directly mitigates the incident pattern",
            "Add acceptance criteria to ENG-103: exponential backoff, max 3 retries, jitter",
            "Mark ENG-104 as blocking ENG-103 in the backlog tool",
        ],
        "raw": {
            "duplicates": [["ENG-101", "ENG-105"]],
            "priority_order": ["ENG-101", "ENG-102", "ENG-103", "ENG-104"],
        },
    },

    "test_generation": {
        "agent": "test-generation",
        "status": "success",
        "confidence": 0.86,
        "issues": [
            "process_payment() has no test coverage for None user case",
            "admin_delete() has no authorization boundary test",
        ],
        "recommendations": [
            "Add integration test with real DB fixture for payment flow",
            "Use pytest-mock to isolate DB calls in unit tests",
        ],
        "raw": {
            "test_code": '''\
import pytest
from unittest.mock import MagicMock, patch, call
from services.payment import PaymentService, RefundService


@pytest.fixture
def mock_db():
    with patch("services.payment.db") as db:
        yield db


@pytest.fixture
def payment_service(mock_db):
    return PaymentService()


class TestPaymentService:
    def test_process_payment_returns_result(self, payment_service, mock_db):
        mock_db.query.return_value = {"id": 1, "name": "Alice"}
        mock_db.execute.return_value = {"id": 99}
        result = payment_service.process_payment(user_id=1, amount=50.00)
        assert result == {"id": 99}

    def test_process_payment_unknown_user_returns_none(self, payment_service, mock_db):
        mock_db.query.return_value = None
        result = payment_service.process_payment(user_id=999, amount=50.00)
        assert result is None
        mock_db.execute.assert_not_called()

    def test_process_payment_uses_parameterized_query(self, payment_service, mock_db):
        """Ensure SQL injection fix: query must not use string concatenation."""
        mock_db.query.return_value = {"id": 1}
        payment_service.process_payment(user_id="1 OR 1=1", amount=0)
        call_args = mock_db.query.call_args[0][0]
        assert "OR 1=1" not in call_args, "SQL injection payload found in query string"

    def test_admin_delete_requires_no_auth_currently(self, payment_service, mock_db):
        """Documents current behaviour — should fail after auth is added."""
        payment_service.admin_delete(payment_id=1)
        mock_db.execute.assert_called_once()


class TestRefundService:
    def test_issue_refund_returns_true_on_success(self, mock_db):
        mock_db.query.return_value = {"id": 1, "status": "completed"}
        svc = RefundService()
        assert svc.issue_refund(payment_id=1) is True

    def test_issue_refund_returns_false_when_not_found(self, mock_db):
        mock_db.query.return_value = None
        svc = RefundService()
        assert svc.issue_refund(payment_id=999) is False

    def test_issue_refund_does_not_update_on_missing_payment(self, mock_db):
        mock_db.query.return_value = None
        RefundService().issue_refund(payment_id=999)
        mock_db.execute.assert_not_called()
''',
            "functions_covered": [
                "PaymentService.process_payment",
                "PaymentService.admin_delete",
                "RefundService.issue_refund",
            ],
        },
    },

    "summary": {
        "agent": "summary",
        "status": "success",
        "confidence": 0.90,
        "issues": [
            "One open SEV1 incident (payment-service DB pool) still in mitigation phase",
        ],
        "recommendations": [
            "Complete pgBouncer rollout to production before end of day",
            "Schedule post-mortem within 48 hours of incident resolution",
            "Review retry policy settings across all services this sprint",
        ],
        "raw": {
            "summary_text": (
                "Strong engineering day: 5 PRs merged including critical security patches "
                "to the payment service. One SEV1 incident (DB connection pool exhaustion) "
                "was diagnosed and mitigated within 4 minutes via circuit breaker, with "
                "pgBouncer rollout in progress as the permanent fix. 3 deployments shipped "
                "to production with zero rollbacks. Backlog reduced by 3 tickets. "
                "Team velocity is healthy; primary risk is the open incident requiring "
                "post-mortem and retry-policy audit."
            ),
            "health_status": "yellow",
        },
    },

    "knowledge": {
        "agent": "knowledge",
        "status": "success",
        "confidence": 0.92,
        "issues": [],
        "recommendations": [
            "Check DLQ depth before replaying: redis-cli llen dlq:payments",
            "Use the replay script after fixing the root cause, not before",
        ],
        "raw": {
            "answer": (
                "The retry queue handles transient failures using exponential backoff. "
                "A job is eligible for retry if its error code is in RETRYABLE_ERRORS "
                "(db_pool_exhausted, upstream_timeout, rate_limited). "
                "Configuration: max_retries=3, base_delay_ms=500, max_delay_ms=30000. "
                "After max_retries the job moves to the dead-letter queue (DLQ). "
                "An alert fires when DLQ depth exceeds 100. "
                "To replay DLQ jobs: ./scripts/replay_dlq.sh payments"
            ),
            "sources_used": ["retry_queue.md"],
            "context_was_sufficient": True,
        },
    },

    "debate_propose": {
        "agent": "architecture-propose",
        "status": "success",
        "confidence": 0.85,
        "issues": [],
        "recommendations": ["Provision ElastiCache r6g.large in the same VPC as the API"],
        "raw": {
            "proposal": (
                "Add an AWS ElastiCache Redis cluster (r6g.large, Multi-AZ) as a read-through "
                "cache between the API gateway and Postgres. Cache TTL of 60 seconds for "
                "user profiles and payment status. Use cache-aside pattern: cache miss falls "
                "through to DB; successful reads populate the cache."
            ),
            "rationale": (
                "Postgres CPU is at 70% under current load. A cache hit rate of ~80% "
                "reduces DB queries by 4x, bringing CPU to ~18% and p99 latency from 450ms to ~50ms."
            ),
            "trade_offs": [
                "Cache invalidation complexity when payment status changes",
                "Stale reads within the 60s TTL window",
                "Additional operational component to monitor",
            ],
            "implementation_steps": [
                "Provision ElastiCache r6g.large with Multi-AZ and encryption-at-rest",
                "Implement cache-aside in PaymentService with tenant-namespaced keys",
                "Add cache metrics to Datadog dashboard",
                "Load test in staging to validate hit rate assumptions",
            ],
        },
    },

    "debate_critique": {
        "agent": "security-critique",
        "status": "success",
        "confidence": 0.88,
        "issues": [
            "Cache keys not namespaced by tenant — cross-tenant data leakage risk",
            "No mention of encryption in transit (TLS) for ElastiCache connections",
            "Stale cache during payment state transitions could show wrong status to users",
            "Cache poisoning: if an attacker can influence cache keys, they could serve forged data",
        ],
        "recommendations": [
            "Namespace all cache keys as tenant:{tenant_id}:payment:{id}",
            "Enable TLS (in-transit encryption) on ElastiCache endpoint",
            "Use write-through invalidation on payment status changes, not TTL-only",
            "Add Redis AUTH token and restrict security group to API subnets only",
        ],
        "raw": {
            "critique": (
                "The proposal lacks tenant-scoped key namespacing, creating a cross-tenant "
                "data leakage risk in a multi-tenant environment. TLS is not mentioned, "
                "leaving cache traffic potentially unencrypted within the VPC."
            ),
            "risk_level": "high",
            "blocking_issues": [
                "No cache key namespacing policy defined",
                "TLS in transit not specified",
            ],
            "required_mitigations": [
                "Tenant-scoped cache key schema",
                "ElastiCache TLS + AUTH token",
                "Write-through invalidation on status changes",
            ],
        },
    },

    "debate_cost": {
        "agent": "cost-evaluator",
        "status": "success",
        "confidence": 0.78,
        "issues": [
            "r6g.large at $0.166/hr = ~$120/month on-demand — reserved saves 35%",
            "Multi-AZ doubles instance cost to ~$240/month baseline",
            "Data transfer costs not accounted for (est. $20-40/month at current volume)",
        ],
        "recommendations": [
            "Use 1-year reserved instance for r6g.large: ~$156/month (35% saving)",
            "Start with r6g.medium ($78/month) and scale up after validating hit rate",
            "Set eviction policy to allkeys-lru to avoid unbounded memory growth",
        ],
        "raw": {
            "cost_summary": (
                "ElastiCache r6g.large Multi-AZ costs approximately $240-280/month "
                "on-demand. With a 1-year reservation, this drops to ~$160/month. "
                "ROI is strong if cache reduces DB instance size from db.r6g.xlarge to "
                "db.r6g.large, saving ~$180/month and netting positive within month 1."
            ),
            "monthly_estimate": "$160-280/month depending on reservation and traffic",
            "cost_at_scale": "$600-800/month at 10x load (scale-out to cluster mode)",
            "cost_risks": [
                "Cache miss storms during cold start after deployment",
                "Memory overrun if TTL not tuned correctly",
            ],
            "optimisations": [
                "1-year reserved instance: 35% discount",
                "Start with r6g.medium, promote after 2 weeks of metrics",
            ],
        },
    },

    "debate_rebuttal": {
        "agent": "architecture-rebuttal",
        "status": "success",
        "confidence": 0.87,
        "issues": [],
        "recommendations": [],
        "raw": {
            "rebuttal": (
                "Security critiques are valid and incorporated into the revised design. "
                "Cost concerns are acknowledged — recommending r6g.medium to start. "
                "The ROI case remains strong: DB savings offset cache cost within month 1."
            ),
            "concessions": [
                "Adding tenant:{tenant_id}: prefix to all cache keys",
                "Enabling TLS and Redis AUTH token in the provisioning template",
                "Switching from TTL-only to write-through invalidation on status changes",
                "Starting with r6g.medium instead of r6g.large",
            ],
            "defences": [
                "60s stale window is acceptable for read-heavy payment status queries; "
                "write-through invalidation handles the critical mutation path",
                "Multi-AZ is non-negotiable for a SEV1-risk component",
            ],
            "revised_proposal": (
                "ElastiCache Redis r6g.medium Multi-AZ with: tenant-namespaced keys "
                "(tenant:{id}:payment:{id}), TLS + AUTH token, write-through invalidation "
                "on payment mutations, TTL=60s for reads. Promote to r6g.large after "
                "2 weeks of production metrics."
            ),
        },
    },

    "debate_synthesize": {
        "agent": "debate-orchestrator",
        "status": "success",
        "confidence": 0.91,
        "issues": [
            "Penetration test of Redis endpoint not yet scheduled",
        ],
        "recommendations": [
            "Proceed with revised Redis design — ship within current sprint",
            "Schedule Redis pen test for next sprint before enabling in all regions",
            "Set a 30-day cost review checkpoint after production rollout",
        ],
        "raw": {
            "final_decision": "revise",
            "recommendation": (
                "PROCEED with the revised proposal. The architecture team has adequately "
                "addressed the security team's blocking concerns (key namespacing, TLS, "
                "write-through invalidation). Starting with r6g.medium is prudent given "
                "cost uncertainty. Three conditions must be met before GA: (1) tenant-scoped "
                "cache key schema implemented and reviewed, (2) TLS + AUTH token enabled "
                "in all environments, (3) write-through invalidation tested under load in staging."
            ),
            "conditions": [
                "Tenant-scoped cache key schema implemented and code-reviewed",
                "TLS + Redis AUTH token enabled in staging and production",
                "Write-through invalidation load-tested in staging",
            ],
            "dissenting_views": [
                "Security team requests a Redis-specific penetration test before multi-region rollout",
            ],
            "rationale": (
                "Benefits (4x DB load reduction, p99 improvement from 450ms to ~50ms) "
                "substantially outweigh costs and risks once the identified mitigations are applied."
            ),
        },
    },
}


# ---------------------------------------------------------------------------
# Keyword detector
# ---------------------------------------------------------------------------

_KEYWORD_MAP: list[tuple[list[str], str]] = [
    # Debate prompts — check before generic architecture/security/cost
    (["proposing a concrete", "design solution", "trade_offs"],      "debate_propose"),
    (["critically evaluating", "attack vectors", "blocking_issues"], "debate_critique"),
    (["operational cost", "monthly_estimate", "cost_at_scale"],      "debate_cost"),
    (["defending and refining", "concessions", "defences"],          "debate_rebuttal"),
    (["chief architect", "final_decision", "conditions"],            "debate_synthesize"),
    # Core agents
    (["code quality", "performance issues", "bad patterns"],         "code_review"),
    (["application security", "owasp", "cwe"],                       "security"),
    (["principal software architect", "coupling", "cohesion"],       "architecture"),
    (["site reliability", "root cause", "affected_services"],        "incident"),
    (["reliability improvements", "immediate_actions", "monitoring_gaps"], "sre"),
    (["backlog", "deduplicate", "priority_order"],                   "backlog"),
    (["test-driven", "pytest", "functions_covered"],                 "test_generation"),
    (["executive", "health_status", "merged_prs"],                   "summary"),
    (["knowledge assistant", "context chunks", "sources_used"],      "knowledge"),
]


def _detect_agent(system_prompt: str) -> str:
    """Return the agent key that best matches the system prompt."""
    lower = system_prompt.lower()
    for keywords, agent_key in _KEYWORD_MAP:
        if all(kw.lower() in lower for kw in keywords):
            return agent_key
    return "code_review"  # safe fallback


# ---------------------------------------------------------------------------
# Mock LLM Service
# ---------------------------------------------------------------------------

class MockLLMService:
    """
    Drop-in replacement for LLMService that returns realistic canned responses.

    No API key or network access required.
    """

    def __init__(self) -> None:
        pass

    async def complete(self, system: str, user: str) -> str:
        agent_key = _detect_agent(system)
        return json.dumps(_RESPONSES.get(agent_key, _RESPONSES["code_review"]))

    async def complete_json(self, system: str, user: str) -> dict:
        agent_key = _detect_agent(system)
        response = dict(_RESPONSES.get(agent_key, _RESPONSES["code_review"]))
        return response
