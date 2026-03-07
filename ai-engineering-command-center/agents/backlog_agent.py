"""
Backlog Agent.

Deduplicates and prioritizes engineering tickets.
Flags missing acceptance criteria and recommends split/merge actions.

Expected context keys:
    tickets (list[dict]):   list of ticket dicts (id, title, description, priority, status)
    tickets_path (str):     optional fallback — path to a JSON tickets file
"""

from __future__ import annotations

import json

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.jira_service import JiraService


class BacklogAgent(AgentBase):
    agent_name = "backlog"

    def __init__(self, llm_service: LLMService, jira_service: JiraService) -> None:
        self.llm  = llm_service
        self.jira = jira_service

    async def run(self, input: AgentInput) -> AgentOutput:
        tickets      = input.context.get("tickets")
        tickets_path = input.context.get("tickets_path", "")

        if not tickets and tickets_path:
            try:
                tickets = self.jira.load_tickets(tickets_path)
            except Exception as exc:
                return self._error_output(f"Could not load tickets file: {exc}")

        if not tickets:
            return self._error_output(
                "Provide tickets in context['tickets'] or a path in context['tickets_path']"
            )

        tickets_json = json.dumps(tickets, indent=2)

        system = load_prompt("backlog")
        user = (
            "Analyze the following engineering tickets. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```json\n{tickets_json}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
