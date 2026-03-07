"""
Phase 7 tests — Agent Debate Mode.

Strategy:
  - MockLLM returns round-appropriate canned JSON so no API calls are made
  - Tests cover: full happy path, error isolation, result model, CLI rendering
"""

from __future__ import annotations

import asyncio
import json
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from cli.main import app
from core.debate_engine import DebateEngine, DebateResult, DebateRound


# ---------------------------------------------------------------------------
# Mock LLM — returns role-appropriate responses
# ---------------------------------------------------------------------------

PROPOSAL_RESPONSE = {
    "agent": "architecture-propose",
    "status": "success",
    "confidence": 0.85,
    "issues": [],
    "recommendations": ["Use ElastiCache r6g.large"],
    "raw": {
        "proposal": "Add a Redis caching layer between API gateway and Postgres.",
        "rationale": "Reduce DB load and lower p99 latency from 450ms to ~50ms.",
        "trade_offs": ["Cache invalidation complexity", "Additional ops overhead"],
        "implementation_steps": ["Provision ElastiCache", "Update connection pool", "Add TTL policy"],
    },
}

CRITIQUE_RESPONSE = {
    "agent": "security-critique",
    "status": "success",
    "confidence": 0.80,
    "issues": ["Cache poisoning risk if keys not namespaced"],
    "recommendations": ["Namespace cache keys by tenant", "Enable TLS on ElastiCache"],
    "raw": {
        "critique": "Proposal lacks cache key namespacing, risking cross-tenant data leakage.",
        "risk_level": "high",
        "blocking_issues": ["No key namespacing policy defined"],
        "required_mitigations": ["Tenant-scoped cache keys", "Encryption in transit"],
    },
}

COST_RESPONSE = {
    "agent": "cost-evaluator",
    "status": "success",
    "confidence": 0.75,
    "issues": [],
    "recommendations": ["Start with r6g.medium and scale up"],
    "raw": {
        "cost_summary": "ElastiCache r6g.large costs ~$180/month at baseline.",
        "monthly_estimate": "$180-360/month depending on traffic",
        "cost_at_scale": "$800+/month at 10x load",
        "cost_risks": ["Unexpected cache miss storms increase DB cost"],
        "optimisations": ["Use reserved instances for 35% discount"],
    },
}

REBUTTAL_RESPONSE = {
    "agent": "architecture-rebuttal",
    "status": "success",
    "confidence": 0.88,
    "issues": [],
    "recommendations": [],
    "raw": {
        "rebuttal": "Agreed on key namespacing — adding tenant prefix. Cost is justified by latency SLO.",
        "concessions": ["Will add tenant-scoped cache key policy"],
        "defences": ["$180/month is well within budget given 90% latency improvement"],
        "revised_proposal": "Redis cache with tenant-namespaced keys and TLS enabled.",
    },
}

SYNTHESIS_RESPONSE = {
    "agent": "debate-orchestrator",
    "status": "success",
    "confidence": 0.90,
    "issues": [],
    "recommendations": ["Proceed with tenant-namespaced Redis cache"],
    "raw": {
        "final_decision": "revise",
        "recommendation": (
            "Proceed with Redis caching subject to: tenant-scoped key namespacing, "
            "TLS in transit, and a cost review after 30 days."
        ),
        "conditions": ["Namespace keys", "Enable TLS", "30-day cost review"],
        "dissenting_views": ["Security team requests penetration test before GA"],
        "rationale": "Benefits outweigh risks once mitigations are applied.",
    },
}

# Cycle through responses in order of LLM calls
_RESPONSES = [
    PROPOSAL_RESPONSE,
    CRITIQUE_RESPONSE,
    COST_RESPONSE,
    REBUTTAL_RESPONSE,
    SYNTHESIS_RESPONSE,
]


class CycleMockLLM:
    """Returns responses in round order."""
    def __init__(self):
        self._index = 0

    async def complete_json(self, system: str, user: str) -> dict:
        response = _RESPONSES[self._index % len(_RESPONSES)]
        self._index += 1
        return dict(response)

    async def complete(self, system: str, user: str) -> str:
        return json.dumps(await self.complete_json(system, user))


def run(coro):
    return asyncio.run(coro)


def make_engine(show_progress=False) -> DebateEngine:
    return DebateEngine(llm_service=CycleMockLLM(), show_progress=show_progress)


# ---------------------------------------------------------------------------
# DebateEngine tests
# ---------------------------------------------------------------------------

class TestDebateEngine:
    def test_complete_run_returns_result(self):
        engine = make_engine()
        result = run(engine.run("Should we add Redis cache?"))
        assert isinstance(result, DebateResult)
        assert result.status == "complete"

    def test_four_rounds_produced(self):
        result = run(make_engine().run("Redis cache?"))
        assert len(result.rounds) == 5  # 1 proposal + 2 critiques + 1 rebuttal + 1 synthesis

    def test_rounds_have_correct_participants(self):
        result = run(make_engine().run("Redis cache?"))
        participants = [r.participant for r in result.rounds]
        assert "architecture" in participants
        assert "security" in participants
        assert "cost" in participants
        assert "orchestrator" in participants

    def test_rounds_have_correct_roles(self):
        result = run(make_engine().run("Redis cache?"))
        roles = {r.role for r in result.rounds}
        assert roles == {"proposal", "critique", "cost_evaluation", "rebuttal", "synthesis"}

    def test_final_decision_populated(self):
        result = run(make_engine().run("Redis cache?"))
        assert result.final_decision in ("proceed", "reject", "revise")

    def test_recommendation_populated(self):
        result = run(make_engine().run("Redis cache?"))
        assert len(result.recommendation) > 0

    def test_confidence_in_range(self):
        result = run(make_engine().run("Redis cache?"))
        assert 0.0 <= result.overall_confidence <= 1.0

    def test_dissenting_views_populated(self):
        result = run(make_engine().run("Redis cache?"))
        assert len(result.dissenting_views) >= 1

    def test_duration_positive(self):
        result = run(make_engine().run("Redis cache?"))
        assert result.duration_seconds >= 0

    def test_topic_preserved(self):
        topic = "Migrate from REST to GraphQL"
        result = run(make_engine().run(topic))
        assert result.topic == topic

    def test_context_accepted(self):
        result = run(make_engine().run("Redis cache?", context="p99=450ms"))
        assert result.status == "complete"

    def test_result_serialisable(self):
        result = run(make_engine().run("Redis cache?"))
        d = result.to_dict()
        assert isinstance(d, dict)
        assert "rounds" in d
        assert "final_decision" in d

    def test_round_summaries_not_empty(self):
        result = run(make_engine().run("Redis cache?"))
        for r in result.rounds:
            assert r.summary, f"Round {r.round_number} ({r.role}) has empty summary"

    def test_error_on_llm_failure(self):
        class BrokenLLM:
            async def complete_json(self, *a, **kw):
                raise RuntimeError("connection refused")

        engine = DebateEngine(llm_service=BrokenLLM(), show_progress=False)
        result = run(engine.run("anything"))
        assert result.status == "error"


# ---------------------------------------------------------------------------
# DebateResult model tests
# ---------------------------------------------------------------------------

class TestDebateResult:
    def test_model_fields(self):
        fields = DebateResult.model_fields.keys()
        required = {
            "topic", "status", "rounds", "final_decision",
            "recommendation", "overall_confidence", "dissenting_views",
            "duration_seconds",
        }
        assert required.issubset(fields)

    def test_round_model_fields(self):
        fields = DebateRound.model_fields.keys()
        required = {"round_number", "participant", "role", "summary", "confidence"}
        assert required.issubset(fields)


# ---------------------------------------------------------------------------
# CostAgent standalone tests
# ---------------------------------------------------------------------------

class TestCostAgent:
    def _agent(self, response=None):
        from agents.cost_agent import CostAgent

        class FixedLLM:
            def __init__(self, resp): self._resp = resp
            async def complete_json(self, *a, **kw): return dict(self._resp)

        return CostAgent(llm_service=FixedLLM(response or {
            "agent": "cost",
            "status": "success",
            "confidence": 0.75,
            "issues": [],
            "recommendations": ["Use reserved instances"],
            "raw": {"cost_summary": "~$200/month"},
        }))

    def test_success(self):
        from core.agent_interface import AgentInput
        out = run(self._agent().run(AgentInput(context={
            "topic": "Redis cache",
            "proposal": "Add ElastiCache r6g.large",
        })))
        assert out.status == "success"
        assert out.agent == "cost"

    def test_no_input_returns_error(self):
        from core.agent_interface import AgentInput
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# CLI debate command tests
# ---------------------------------------------------------------------------

cli_runner = CliRunner(mix_stderr=False)


def make_debate_result() -> DebateResult:
    rounds = [
        DebateRound(round_number=1, participant="architecture", role="proposal",
                    summary="Add Redis cache.", confidence=0.85),
        DebateRound(round_number=2, participant="security", role="critique",
                    summary="Cache poisoning risk.", confidence=0.80),
        DebateRound(round_number=2, participant="cost", role="cost_evaluation",
                    summary="~$180/month.", confidence=0.75),
        DebateRound(round_number=3, participant="architecture", role="rebuttal",
                    summary="Will add key namespacing.", confidence=0.88),
        DebateRound(round_number=4, participant="orchestrator", role="synthesis",
                    summary="Proceed with revisions.", confidence=0.90),
    ]
    return DebateResult(
        topic="Should we add Redis cache?",
        status="complete",
        rounds=rounds,
        final_decision="revise",
        recommendation="Proceed with tenant-namespaced Redis cache.",
        overall_confidence=0.90,
        dissenting_views=["Security team wants pen test"],
        duration_seconds=18.3,
    )


class MockDebateEngine:
    def __init__(self, result): self._result = result
    async def run(self, topic, context=""): return self._result


class TestDebateCli:
    def test_success_rich(self):
        result = make_debate_result()
        with patch("cli.main.DebateEngine", return_value=MockDebateEngine(result)):
            out = cli_runner.invoke(app, ["debate", "Should we add Redis cache?"])
        assert out.exit_code == 0
        assert "REVISE" in out.output or "debate" in out.output.lower()

    def test_json_output(self):
        result = make_debate_result()
        with patch("cli.main.DebateEngine", return_value=MockDebateEngine(result)):
            out = cli_runner.invoke(
                app, ["debate", "Should we add Redis cache?", "--json"]
            )
        assert out.exit_code == 0
        data = json.loads(out.output)
        assert data["topic"] == "Should we add Redis cache?"
        assert data["final_decision"] == "revise"
        assert len(data["rounds"]) == 5

    def test_with_context_flag(self):
        result = make_debate_result()
        with patch("cli.main.DebateEngine", return_value=MockDebateEngine(result)):
            out = cli_runner.invoke(
                app,
                ["debate", "GraphQL migration?", "--context", "50 REST endpoints today"],
            )
        assert out.exit_code == 0

    def test_rounds_shown_in_output(self):
        result = make_debate_result()
        with patch("cli.main.DebateEngine", return_value=MockDebateEngine(result)):
            out = cli_runner.invoke(app, ["debate", "Redis cache?"])
        assert "architecture" in out.output.lower() or "security" in out.output.lower()

    def test_final_decision_shown(self):
        result = make_debate_result()
        with patch("cli.main.DebateEngine", return_value=MockDebateEngine(result)):
            out = cli_runner.invoke(app, ["debate", "Redis cache?"])
        assert "REVISE" in out.output or "revise" in out.output.lower()
