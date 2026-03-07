"""
Phase 4 tests — agent contract validation without LLM calls.

Strategy: inject a mock LLMService that returns canned JSON,
then assert each agent correctly builds its AgentOutput.
"""

from __future__ import annotations

import asyncio
import json
import pytest

from core.agent_interface import AgentInput, AgentOutput
from services.llm_service import LLMService
from services.github_service import GitHubService
from services.jira_service import JiraService
from services.log_service import LogService


# ---------------------------------------------------------------------------
# Mock LLM service
# ---------------------------------------------------------------------------

class MockLLM:
    """Returns a preset JSON string for any complete_json call."""

    def __init__(self, response: dict):
        self._response = response

    async def complete_json(self, system: str, user: str) -> dict:
        return dict(self._response)

    async def complete(self, system: str, user: str) -> str:
        return json.dumps(self._response)


def good_response(agent_name: str, **extra) -> dict:
    return {
        "agent": agent_name,
        "status": "success",
        "confidence": 0.85,
        "issues": ["issue 1"],
        "recommendations": ["rec 1"],
        "raw": extra,
    }


SAMPLE_DIFF = """\
diff --git a/services/payment.py b/services/payment.py
--- a/services/payment.py
+++ b/services/payment.py
@@ -10,6 +10,10 @@ class PaymentService:
+    def process(self, amount):
+        conn = db.connect()
+        conn.execute("SELECT * FROM users WHERE id=?", (user_id,))
+        return conn.fetchone()
"""

SAMPLE_LOG = """\
2026-03-05T09:14:39Z ERROR payment-service  Connection timeout
2026-03-05T09:15:01Z CRITICAL payment-service  Circuit breaker OPEN
"""

SAMPLE_TICKETS = [
    {"id": "ENG-101", "title": "Payment timeout", "description": "DB pool exhausted",
     "priority": "high", "status": "open"},
]


def run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# CodeReviewAgent
# ---------------------------------------------------------------------------

class TestCodeReviewAgent:
    def _agent(self, response=None):
        from agents.code_review_agent import CodeReviewAgent
        llm = MockLLM(response or good_response("code-review"))
        return CodeReviewAgent(llm_service=llm, github_service=GitHubService())

    def test_success(self):
        agent = self._agent()
        out = run(agent.run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert out.status == "success"
        assert out.agent == "code-review"
        assert out.confidence == pytest.approx(0.85)

    def test_no_diff_returns_error(self):
        agent = self._agent()
        out = run(agent.run(AgentInput(context={})))
        assert out.status == "error"
        assert "diff" in out.issues[0]

    def test_changed_files_in_raw(self):
        agent = self._agent()
        out = run(agent.run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert "changed_files" in out.raw

    def test_agent_name_always_correct(self):
        agent = self._agent(good_response("wrong-name"))
        out = run(agent.run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert out.agent == "code-review"


# ---------------------------------------------------------------------------
# SecurityAgent
# ---------------------------------------------------------------------------

class TestSecurityAgent:
    def _agent(self, response=None):
        from agents.security_agent import SecurityAgent
        llm = MockLLM(response or good_response("security"))
        return SecurityAgent(llm_service=llm, github_service=GitHubService())

    def test_success(self):
        out = run(self._agent().run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert out.status == "success"
        assert out.agent == "security"

    def test_no_diff_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# ArchitectureAgent
# ---------------------------------------------------------------------------

class TestArchitectureAgent:
    def _agent(self, response=None):
        from agents.architecture_agent import ArchitectureAgent
        llm = MockLLM(response or good_response("architecture"))
        return ArchitectureAgent(llm_service=llm)

    def test_with_diff(self):
        out = run(self._agent().run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert out.status == "success"

    def test_with_description(self):
        out = run(self._agent().run(AgentInput(context={"description": "microservice design"})))
        assert out.status == "success"

    def test_no_input_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# IncidentAgent
# ---------------------------------------------------------------------------

class TestIncidentAgent:
    def _agent(self, response=None):
        from agents.incident_agent import IncidentAgent
        llm = MockLLM(response or good_response("incident"))
        return IncidentAgent(llm_service=llm, log_service=LogService())

    def test_with_log_text(self):
        out = run(self._agent().run(AgentInput(context={"log": SAMPLE_LOG})))
        assert out.status == "success"
        assert out.agent == "incident"

    def test_no_log_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"

    def test_with_log_path(self, tmp_path):
        log_file = tmp_path / "test.log"
        log_file.write_text(SAMPLE_LOG)
        out = run(self._agent().run(AgentInput(context={"log_path": str(log_file)})))
        assert out.status == "success"

    def test_bad_log_path_returns_error(self):
        out = run(self._agent().run(AgentInput(context={"log_path": "/nonexistent/path.log"})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# SREAgent
# ---------------------------------------------------------------------------

class TestSREAgent:
    def _agent(self, response=None):
        from agents.sre_agent import SREAgent
        llm = MockLLM(response or good_response("sre"))
        return SREAgent(llm_service=llm, log_service=LogService())

    def test_with_log(self):
        out = run(self._agent().run(AgentInput(context={"log": SAMPLE_LOG})))
        assert out.status == "success"
        assert out.agent == "sre"

    def test_with_incident_analysis(self):
        ctx = {"log": SAMPLE_LOG, "incident_analysis": "DB pool exhausted at 09:15"}
        out = run(self._agent().run(AgentInput(context=ctx)))
        assert out.status == "success"

    def test_no_log_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# BacklogAgent
# ---------------------------------------------------------------------------

class TestBacklogAgent:
    def _agent(self, response=None):
        from agents.backlog_agent import BacklogAgent
        llm = MockLLM(response or good_response("backlog"))
        return BacklogAgent(llm_service=llm, jira_service=JiraService())

    def test_with_tickets(self):
        out = run(self._agent().run(AgentInput(context={"tickets": SAMPLE_TICKETS})))
        assert out.status == "success"
        assert out.agent == "backlog"

    def test_no_tickets_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"

    def test_with_tickets_path(self, tmp_path):
        tickets_file = tmp_path / "tickets.json"
        tickets_file.write_text(json.dumps(SAMPLE_TICKETS))
        out = run(self._agent().run(AgentInput(context={"tickets_path": str(tickets_file)})))
        assert out.status == "success"


# ---------------------------------------------------------------------------
# TestGenerationAgent
# ---------------------------------------------------------------------------

class TestTestGenerationAgent:
    def _agent(self, response=None):
        from agents.test_generation_agent import TestGenerationAgent
        llm = MockLLM(response or good_response("test-generation"))
        return TestGenerationAgent(llm_service=llm)

    def test_success(self):
        out = run(self._agent().run(AgentInput(context={"diff": SAMPLE_DIFF})))
        assert out.status == "success"
        assert out.agent == "test-generation"

    def test_no_diff_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"


# ---------------------------------------------------------------------------
# SummaryAgent
# ---------------------------------------------------------------------------

class TestSummaryAgent:
    def _agent(self, response=None):
        from agents.summary_agent import SummaryAgent
        llm = MockLLM(response or good_response("summary"))
        return SummaryAgent(llm_service=llm)

    def test_success_with_full_context(self):
        ctx = {"merged_prs": 5, "open_incidents": 1, "backlog_changes": 3, "deploy_count": 2}
        out = run(self._agent().run(AgentInput(context=ctx)))
        assert out.status == "success"
        assert out.agent == "summary"

    def test_success_with_empty_context(self):
        # Should default all fields to 0 and still call LLM
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "success"


# ---------------------------------------------------------------------------
# Container wiring
# ---------------------------------------------------------------------------

class TestContainer:
    def test_build_orchestrator_registers_workflows(self):
        """Smoke test: container builds without error and registers known workflows."""
        from core.container import build_orchestrator
        from core.agent_registry import (
            WORKFLOW_PR_REVIEW, WORKFLOW_INCIDENT,
            WORKFLOW_BACKLOG, WORKFLOW_TESTS, WORKFLOW_SUMMARY,
        )
        orchestrator, services = build_orchestrator(show_progress=False)
        reg = orchestrator._registry
        assert reg.agent_count(WORKFLOW_PR_REVIEW) == 4
        assert reg.agent_count(WORKFLOW_INCIDENT) == 2
        assert reg.agent_count(WORKFLOW_BACKLOG) == 1
        assert reg.agent_count(WORKFLOW_TESTS) == 1
        assert reg.agent_count(WORKFLOW_SUMMARY) == 1
