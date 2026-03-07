"""
Agent registry.

Maps workflow names to lists of agent instances.
Agents are registered at application startup (see core/container.py).
The orchestrator queries the registry at runtime.

Workflow name constants are defined here and imported throughout the system
to prevent typo-driven mismatches.
"""

from __future__ import annotations

from core.agent_interface import AgentBase


# ---------------------------------------------------------------------------
# Canonical workflow identifiers
# ---------------------------------------------------------------------------

WORKFLOW_PR_REVIEW    = "pr_review"
WORKFLOW_INCIDENT     = "incident"
WORKFLOW_BACKLOG      = "backlog"
WORKFLOW_TESTS        = "generate_tests"
WORKFLOW_SUMMARY      = "daily_summary"
WORKFLOW_KNOWLEDGE    = "knowledge"
WORKFLOW_DEBATE       = "debate"


# ---------------------------------------------------------------------------
# Registry
# ---------------------------------------------------------------------------

class AgentRegistry:
    """
    Stores and retrieves agent instances by workflow name.

    Agents are registered with :meth:`register` (one at a time) or
    :meth:`register_many` (bulk). The orchestrator calls
    :meth:`get_agents_for_workflow` at runtime.
    """

    def __init__(self) -> None:
        self._workflows: dict[str, list[AgentBase]] = {}

    # ------------------------------------------------------------------
    # Registration
    # ------------------------------------------------------------------

    def register(self, workflow: str, agent: AgentBase) -> None:
        """Add a single agent to a workflow group."""
        self._workflows.setdefault(workflow, []).append(agent)

    def register_many(self, workflow: str, agents: list[AgentBase]) -> None:
        """Add multiple agents to a workflow group in one call."""
        for agent in agents:
            self.register(workflow, agent)

    # ------------------------------------------------------------------
    # Lookup
    # ------------------------------------------------------------------

    def get_agents_for_workflow(self, workflow: str) -> list[AgentBase]:
        """Return all agents registered for the given workflow (may be empty)."""
        return list(self._workflows.get(workflow, []))

    def list_workflows(self) -> list[str]:
        """Return names of all registered workflows."""
        return list(self._workflows.keys())

    def agent_count(self, workflow: str) -> int:
        """Return the number of agents registered for a workflow."""
        return len(self._workflows.get(workflow, []))

    def summary(self) -> dict[str, list[str]]:
        """Return a mapping of workflow → list of agent names (for diagnostics)."""
        return {
            wf: [a.agent_name for a in agents]
            for wf, agents in self._workflows.items()
        }
