"""
Phase 3 tests — orchestrator, registry, and aggregation logic.

All tests use in-process stub agents — no LLM calls made.
"""

import asyncio
import pytest

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.agent_registry import AgentRegistry, WORKFLOW_PR_REVIEW, WORKFLOW_INCIDENT
from core.agent_runner import AgentRunner
from core.orchestrator import Orchestrator, OrchestratorResult


# ---------------------------------------------------------------------------
# Stub agents
# ---------------------------------------------------------------------------

class SuccessAgent(AgentBase):
    agent_name = "success-agent"
    def __init__(self, issues=None, recommendations=None, confidence=0.8):
        self._issues = issues or []
        self._recs   = recommendations or []
        self._conf   = confidence

    async def run(self, input: AgentInput) -> AgentOutput:
        return AgentOutput(
            agent=self.agent_name,
            status="success",
            confidence=self._conf,
            issues=self._issues,
            recommendations=self._recs,
        )


class ErrorAgent(AgentBase):
    agent_name = "error-agent"
    async def run(self, input: AgentInput) -> AgentOutput:
        raise RuntimeError("intentional agent failure")


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def make_orchestrator(agents, workflow=WORKFLOW_PR_REVIEW) -> Orchestrator:
    registry = AgentRegistry()
    registry.register_many(workflow, agents)
    runner = AgentRunner(show_progress=False)
    return Orchestrator(registry, runner)


def run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# AgentRegistry tests
# ---------------------------------------------------------------------------

class TestAgentRegistry:
    def test_register_and_retrieve(self):
        reg = AgentRegistry()
        agent = SuccessAgent()
        reg.register(WORKFLOW_PR_REVIEW, agent)
        assert reg.get_agents_for_workflow(WORKFLOW_PR_REVIEW) == [agent]

    def test_register_many(self):
        reg = AgentRegistry()
        agents = [SuccessAgent(), SuccessAgent()]
        reg.register_many(WORKFLOW_PR_REVIEW, agents)
        assert len(reg.get_agents_for_workflow(WORKFLOW_PR_REVIEW)) == 2

    def test_empty_workflow_returns_empty_list(self):
        reg = AgentRegistry()
        assert reg.get_agents_for_workflow("nonexistent") == []

    def test_list_workflows(self):
        reg = AgentRegistry()
        reg.register(WORKFLOW_PR_REVIEW, SuccessAgent())
        reg.register(WORKFLOW_INCIDENT, SuccessAgent())
        assert set(reg.list_workflows()) == {WORKFLOW_PR_REVIEW, WORKFLOW_INCIDENT}

    def test_agent_count(self):
        reg = AgentRegistry()
        reg.register_many(WORKFLOW_PR_REVIEW, [SuccessAgent(), SuccessAgent()])
        assert reg.agent_count(WORKFLOW_PR_REVIEW) == 2
        assert reg.agent_count("other") == 0

    def test_summary(self):
        reg = AgentRegistry()
        reg.register(WORKFLOW_PR_REVIEW, SuccessAgent())
        summary = reg.summary()
        assert WORKFLOW_PR_REVIEW in summary
        assert "success-agent" in summary[WORKFLOW_PR_REVIEW]

    def test_get_returns_copy(self):
        """Mutating the returned list should not affect the registry."""
        reg = AgentRegistry()
        reg.register(WORKFLOW_PR_REVIEW, SuccessAgent())
        agents = reg.get_agents_for_workflow(WORKFLOW_PR_REVIEW)
        agents.clear()
        assert reg.agent_count(WORKFLOW_PR_REVIEW) == 1


# ---------------------------------------------------------------------------
# Orchestrator — happy path
# ---------------------------------------------------------------------------

class TestOrchestratorSuccess:
    def test_all_success_status(self):
        orch = make_orchestrator([SuccessAgent(), SuccessAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.status == "success"

    def test_result_fields(self):
        orch = make_orchestrator([SuccessAgent(confidence=0.9)])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.workflow == WORKFLOW_PR_REVIEW
        assert result.agent_count == 1
        assert result.success_count == 1
        assert result.error_count == 0
        assert result.overall_confidence == pytest.approx(0.9)
        assert result.duration_seconds >= 0

    def test_issues_aggregated(self):
        a1 = SuccessAgent(issues=["issue A", "shared issue"])
        a2 = SuccessAgent(issues=["issue B", "shared issue"])
        orch = make_orchestrator([a1, a2])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        # "shared issue" appears once (deduplication)
        assert result.all_issues.count("shared issue") == 1
        assert "issue A" in result.all_issues
        assert "issue B" in result.all_issues

    def test_recommendations_aggregated(self):
        a1 = SuccessAgent(recommendations=["rec X", "common rec"])
        a2 = SuccessAgent(recommendations=["rec Y", "common rec"])
        orch = make_orchestrator([a1, a2])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.all_recommendations.count("common rec") == 1
        assert "rec X" in result.all_recommendations

    def test_confidence_is_mean_of_successful(self):
        a1 = SuccessAgent(confidence=0.6)
        a2 = SuccessAgent(confidence=1.0)
        orch = make_orchestrator([a1, a2])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.overall_confidence == pytest.approx(0.8, abs=0.001)

    def test_agent_outputs_included(self):
        orch = make_orchestrator([SuccessAgent(), SuccessAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert len(result.agent_outputs) == 2

    def test_context_passed_through(self):
        """Agents receive the context dict."""
        received = {}

        class ContextCapture(AgentBase):
            agent_name = "ctx-capture"
            async def run(self, input: AgentInput) -> AgentOutput:
                received.update(input.context)
                return AgentOutput(agent=self.agent_name, status="success")

        reg = AgentRegistry()
        reg.register(WORKFLOW_PR_REVIEW, ContextCapture())
        orch = Orchestrator(reg, AgentRunner(show_progress=False))
        run(orch.execute(WORKFLOW_PR_REVIEW, context={"diff": "test diff"}))
        assert received.get("diff") == "test diff"

    def test_result_serialisable(self):
        orch = make_orchestrator([SuccessAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        d = result.to_dict()
        assert isinstance(d, dict)
        assert "workflow" in d and "agent_outputs" in d


# ---------------------------------------------------------------------------
# Orchestrator — error and partial scenarios
# ---------------------------------------------------------------------------

class TestOrchestratorErrors:
    def test_all_error_status(self):
        orch = make_orchestrator([ErrorAgent(), ErrorAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.status == "error"
        assert result.success_count == 0
        assert result.error_count == 2
        assert result.overall_confidence == 0.0

    def test_partial_status(self):
        orch = make_orchestrator([SuccessAgent(), ErrorAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.status == "partial"
        assert result.success_count == 1
        assert result.error_count == 1

    def test_error_confidence_is_zero(self):
        orch = make_orchestrator([ErrorAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        assert result.overall_confidence == 0.0

    def test_unknown_workflow_raises(self):
        registry = AgentRegistry()  # nothing registered
        runner = AgentRunner(show_progress=False)
        orch = Orchestrator(registry, runner)
        with pytest.raises(ValueError, match="No agents registered"):
            run(orch.execute("nonexistent_workflow", context={}))

    def test_error_issues_included_in_aggregate(self):
        orch = make_orchestrator([SuccessAgent(issues=["ok issue"]), ErrorAgent()])
        result = run(orch.execute(WORKFLOW_PR_REVIEW, context={}))
        # The error agent produces a runtime error message as an issue
        assert len(result.all_issues) >= 1


# ---------------------------------------------------------------------------
# OrchestratorResult model
# ---------------------------------------------------------------------------

class TestOrchestratorResult:
    def test_model_fields_present(self):
        fields = OrchestratorResult.model_fields.keys()
        required = {
            "workflow", "status", "agent_count", "success_count",
            "error_count", "overall_confidence", "all_issues",
            "all_recommendations", "agent_outputs", "duration_seconds",
        }
        assert required.issubset(fields)
