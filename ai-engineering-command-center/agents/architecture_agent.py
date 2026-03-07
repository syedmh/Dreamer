"""
Architecture Agent.

Evaluates code changes or design descriptions for architectural concerns:
coupling, cohesion, SOLID violations, scalability, and design patterns.

Expected context keys:
    diff (str):         unified diff (used if description absent)
    description (str):  free-text design description (optional)
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


class ArchitectureAgent(AgentBase):
    agent_name = "architecture"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        diff        = input.context.get("diff", "").strip()
        description = input.context.get("description", "").strip()

        if not diff and not description:
            return self._error_output(
                "Provide at least one of context['diff'] or context['description']"
            )

        material = description if description else diff
        label    = "design description" if description else "git diff"

        system = load_prompt("architecture")
        user = (
            f"Evaluate the following {label} for architectural concerns. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"{material}"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
