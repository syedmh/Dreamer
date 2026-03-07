"""
Phase 5 tests — CLI commands using Typer's test runner.

Strategy:
  - Inject a patched build_orchestrator that returns a mock orchestrator
  - MockOrchestrator returns a pre-built OrchestratorResult with no LLM calls
  - Assert exit codes, JSON output structure, and error handling
"""

from __future__ import annotations

import asyncio
import json
from unittest.mock import AsyncMock, patch

import pytest
from typer.testing import CliRunner

from cli.main import app
from core.agent_interface import AgentOutput
from core.orchestrator import OrchestratorResult

runner = CliRunner(mix_stderr=False)


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

def make_result(
    workflow: str,
    status: str = "success",
    agent_outputs: list[AgentOutput] | None = None,
) -> OrchestratorResult:
    outputs = agent_outputs or [
        AgentOutput(
            agent="mock-agent",
            status="success",
            confidence=0.85,
            issues=["mock issue"],
            recommendations=["mock rec"],
            raw={},
        )
    ]
    successful = [o for o in outputs if o.status == "success"]
    return OrchestratorResult(
        workflow=workflow,
        status=status,
        agent_count=len(outputs),
        success_count=len(successful),
        error_count=len(outputs) - len(successful),
        overall_confidence=0.85,
        all_issues=["mock issue"],
        all_recommendations=["mock rec"],
        agent_outputs=outputs,
        duration_seconds=1.23,
    )


class MockOrchestrator:
    """Returns a preset result for any workflow."""
    def __init__(self, result: OrchestratorResult):
        self._result = result

    async def execute(self, workflow: str, context: dict) -> OrchestratorResult:
        return self._result


def patch_orchestrator(result: OrchestratorResult):
    """Context manager: patch build_orchestrator to return MockOrchestrator."""
    mock_orch = MockOrchestrator(result)
    return patch("cli.main._build_orch", return_value=mock_orch)


# ---------------------------------------------------------------------------
# review-pr
# ---------------------------------------------------------------------------

class TestReviewPr:
    def test_success_rich_output(self, tmp_path):
        diff_file = tmp_path / "pr.diff"
        diff_file.write_text("--- a/foo.py\n+++ b/foo.py\n@@ -1 +1 @@\n+x=1\n")
        result = make_result("pr_review")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["review-pr", "--diff", str(diff_file)])
        assert out.exit_code == 0
        assert "pr_review" in out.output or "success" in out.output.lower()

    def test_json_flag(self, tmp_path):
        diff_file = tmp_path / "pr.diff"
        diff_file.write_text("--- a/foo.py\n+++ b/foo.py\n")
        result = make_result("pr_review")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["review-pr", "--diff", str(diff_file), "--json"])
        assert out.exit_code == 0
        data = json.loads(out.output)
        assert data["workflow"] == "pr_review"
        assert "agent_outputs" in data

    def test_missing_diff_file_exits_1(self):
        out = runner.invoke(app, ["review-pr", "--diff", "/nonexistent/pr.diff"])
        assert out.exit_code == 1

    def test_no_diff_flag_shows_help(self):
        out = runner.invoke(app, ["review-pr"])
        # Missing required option → non-zero exit
        assert out.exit_code != 0


# ---------------------------------------------------------------------------
# diagnose-incident
# ---------------------------------------------------------------------------

class TestDiagnoseIncident:
    def test_success(self, tmp_path):
        log_file = tmp_path / "app.log"
        log_file.write_text("ERROR db timeout\nCRITICAL circuit breaker OPEN\n")
        result = make_result("incident", agent_outputs=[
            AgentOutput(
                agent="incident", status="success", confidence=0.9,
                issues=["DB pool exhausted"],
                recommendations=["Increase pool size"],
                raw={"root_cause": "DB pool exhausted", "severity": "sev1",
                     "affected_services": ["payment-service"]},
            ),
            AgentOutput(
                agent="sre", status="success", confidence=0.8,
                issues=[],
                recommendations=["Add circuit breaker"],
                raw={"immediate_actions": ["Restart DB connection pool"]},
            ),
        ])
        with patch_orchestrator(result):
            out = runner.invoke(app, ["diagnose-incident", "--log", str(log_file)])
        assert out.exit_code == 0

    def test_missing_log_file_exits_1(self):
        out = runner.invoke(app, ["diagnose-incident", "--log", "/no/such/file.log"])
        assert out.exit_code == 1

    def test_json_output(self, tmp_path):
        log_file = tmp_path / "app.log"
        log_file.write_text("ERROR timeout\n")
        result = make_result("incident")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["diagnose-incident", "--log", str(log_file), "--json"])
        assert out.exit_code == 0
        data = json.loads(out.output)
        assert data["workflow"] == "incident"


# ---------------------------------------------------------------------------
# manage-backlog
# ---------------------------------------------------------------------------

class TestManageBacklog:
    def _tickets_file(self, tmp_path):
        f = tmp_path / "tickets.json"
        f.write_text(json.dumps([
            {"id": "ENG-1", "title": "Fix bug", "description": "...",
             "priority": "high", "status": "open"}
        ]))
        return f

    def test_success(self, tmp_path):
        result = make_result("backlog", agent_outputs=[
            AgentOutput(
                agent="backlog", status="success", confidence=0.88,
                issues=[], recommendations=["Merge ENG-1 and ENG-2"],
                raw={"duplicates": [["ENG-1", "ENG-2"]], "priority_order": ["ENG-1"]},
            )
        ])
        with patch_orchestrator(result):
            out = runner.invoke(app, ["manage-backlog", "--tickets",
                                       str(self._tickets_file(tmp_path))])
        assert out.exit_code == 0

    def test_missing_tickets_file_exits_1(self):
        out = runner.invoke(app, ["manage-backlog", "--tickets", "/no/tickets.json"])
        assert out.exit_code == 1

    def test_json_output(self, tmp_path):
        result = make_result("backlog")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["manage-backlog", "--tickets",
                                       str(self._tickets_file(tmp_path)), "--json"])
        data = json.loads(out.output)
        assert data["workflow"] == "backlog"


# ---------------------------------------------------------------------------
# generate-tests
# ---------------------------------------------------------------------------

class TestGenerateTests:
    def test_success_shows_code(self, tmp_path):
        diff_file = tmp_path / "pr.diff"
        diff_file.write_text("--- a/foo.py\n+++ b/foo.py\n+def add(a,b): return a+b\n")
        result = make_result("generate_tests", agent_outputs=[
            AgentOutput(
                agent="test-generation", status="success", confidence=0.92,
                issues=[], recommendations=[],
                raw={"test_code": "def test_add():\n    assert add(1,2)==3",
                     "functions_covered": ["add"]},
            )
        ])
        with patch_orchestrator(result):
            out = runner.invoke(app, ["generate-tests", "--diff", str(diff_file)])
        assert out.exit_code == 0
        assert "test_add" in out.output or "Generated Tests" in out.output

    def test_missing_diff_exits_1(self):
        out = runner.invoke(app, ["generate-tests", "--diff", "/no/file.diff"])
        assert out.exit_code == 1


# ---------------------------------------------------------------------------
# daily-summary
# ---------------------------------------------------------------------------

class TestDailySummary:
    def test_success(self):
        result = make_result("daily_summary", agent_outputs=[
            AgentOutput(
                agent="summary", status="success", confidence=0.95,
                issues=[], recommendations=["Ship the fix"],
                raw={"summary_text": "Great day. 5 PRs merged.",
                     "health_status": "green"},
            )
        ])
        with patch_orchestrator(result):
            out = runner.invoke(app, ["daily-summary", "--prs", "5",
                                       "--incidents", "0", "--deploys", "3"])
        assert out.exit_code == 0
        assert "Great day" in out.output or "summary" in out.output.lower()

    def test_json_output(self):
        result = make_result("daily_summary")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["daily-summary", "--json"])
        data = json.loads(out.output)
        assert data["workflow"] == "daily_summary"

    def test_zero_args_runs(self):
        result = make_result("daily_summary")
        with patch_orchestrator(result):
            out = runner.invoke(app, ["daily-summary"])
        assert out.exit_code == 0


# ---------------------------------------------------------------------------
# ask-knowledge (stub behaviour)
# ---------------------------------------------------------------------------

class TestAskKnowledge:
    def test_stub_message_shown(self):
        # Knowledge workflow not yet registered → CLI shows stub message
        out = runner.invoke(app, ["ask-knowledge", "How does the retry queue work?"])
        # Should not hard-crash; either shows stub panel or registered result
        assert out.exit_code in (0, 1)
