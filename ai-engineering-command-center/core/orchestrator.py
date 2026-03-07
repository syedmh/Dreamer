"""
Core orchestrator.

Responsibilities:
  1. Resolve the correct agents for a named workflow via AgentRegistry
  2. Delegate concurrent execution to AgentRunner
  3. Aggregate per-agent outputs into a single OrchestratorResult

Design notes:
- OrchestratorResult is a plain Pydantic model — fully serialisable to JSON.
- Aggregation deduplicates issues and recommendations using insertion-order
  dict keys (Python 3.7+ guarantee), preserving the order agents produced them.
- Overall confidence = mean of successful-agent confidences.
- Status is "success" / "partial" / "error" based on how many agents succeeded.
"""

from __future__ import annotations

import time
import logging
from pydantic import BaseModel

from core.agent_interface import AgentInput, AgentOutput
from core.agent_runner import AgentRunner
from core.agent_registry import AgentRegistry

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Result model
# ---------------------------------------------------------------------------

class OrchestratorResult(BaseModel):
    """Aggregated output from a multi-agent workflow run."""

    workflow: str
    status: str                         # "success" | "partial" | "error"
    agent_count: int
    success_count: int
    error_count: int
    overall_confidence: float           # 0.0 – 1.0, mean of successful agents
    all_issues: list[str]               # deduplicated, insertion-ordered
    all_recommendations: list[str]      # deduplicated, insertion-ordered
    agent_outputs: list[AgentOutput]    # raw per-agent results
    duration_seconds: float

    def to_dict(self) -> dict:
        return self.model_dump()


# ---------------------------------------------------------------------------
# Orchestrator
# ---------------------------------------------------------------------------

class Orchestrator:
    """
    Coordinates multi-agent workflows.

    Usage::

        result = await orchestrator.execute("pr_review", context={"diff": "..."})
        print(result.status, result.all_issues)
    """

    def __init__(self, registry: AgentRegistry, runner: AgentRunner) -> None:
        self._registry = registry
        self._runner = runner

    async def execute(
        self,
        workflow: str,
        context: dict,
    ) -> OrchestratorResult:
        """
        Run all agents registered for `workflow` and return an aggregated result.

        Raises:
            ValueError: If no agents are registered for the requested workflow.
        """
        agents = self._registry.get_agents_for_workflow(workflow)
        if not agents:
            raise ValueError(
                f"No agents registered for workflow {workflow!r}. "
                f"Available workflows: {self._registry.list_workflows()}"
            )

        logger.info(
            "Orchestrator starting workflow=%r with %d agent(s)", workflow, len(agents)
        )

        inp = AgentInput(context=context)
        start = time.monotonic()
        outputs = await self._runner.run_all(agents, inp)
        elapsed = time.monotonic() - start

        result = self._aggregate(workflow, outputs, elapsed)

        logger.info(
            "Orchestrator finished workflow=%r status=%s confidence=%.2f in %.2fs",
            workflow, result.status, result.overall_confidence, elapsed,
        )
        return result

    # ------------------------------------------------------------------
    # Private aggregation
    # ------------------------------------------------------------------

    def _aggregate(
        self,
        workflow: str,
        outputs: list[AgentOutput],
        elapsed: float,
    ) -> OrchestratorResult:
        successful = [o for o in outputs if o.status == "success"]
        errored    = [o for o in outputs if o.status == "error"]

        # Derive overall status
        if not errored:
            status = "success"
        elif not successful:
            status = "error"
        else:
            status = "partial"

        # Deduplicate issues and recommendations in encounter order
        all_issues = list(
            dict.fromkeys(issue for o in outputs for issue in o.issues)
        )
        all_recommendations = list(
            dict.fromkeys(rec for o in outputs for rec in o.recommendations)
        )

        # Mean confidence of successful agents; 0.0 if all failed
        overall_confidence = (
            sum(o.confidence for o in successful) / len(successful)
            if successful else 0.0
        )

        return OrchestratorResult(
            workflow=workflow,
            status=status,
            agent_count=len(outputs),
            success_count=len(successful),
            error_count=len(errored),
            overall_confidence=round(overall_confidence, 3),
            all_issues=all_issues,
            all_recommendations=all_recommendations,
            agent_outputs=outputs,
            duration_seconds=round(elapsed, 3),
        )
