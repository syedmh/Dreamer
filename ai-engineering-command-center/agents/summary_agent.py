"""
Executive Summary Agent.

Synthesizes daily engineering activity into a concise summary for
leadership and cross-functional stakeholders.

Expected context keys:
    merged_prs (int):       number of PRs merged today
    open_incidents (int):   number of active incidents
    backlog_changes (int):  tickets added or closed
    deploy_count (int):     number of deployments
    notes (str):            optional free-text notes from the team
"""

from __future__ import annotations

import json

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService

_REQUIRED_KEYS = {"merged_prs", "open_incidents", "backlog_changes", "deploy_count"}


class SummaryAgent(AgentBase):
    agent_name = "summary"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        ctx = input.context

        # Accept whatever subset of keys is present; fill defaults
        activity = {
            "merged_prs":      ctx.get("merged_prs", 0),
            "open_incidents":  ctx.get("open_incidents", 0),
            "backlog_changes": ctx.get("backlog_changes", 0),
            "deploy_count":    ctx.get("deploy_count", 0),
            "notes":           ctx.get("notes", ""),
        }

        system = load_prompt("summary")
        user = (
            "Generate an engineering daily summary from the following activity data. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```json\n{json.dumps(activity, indent=2)}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
