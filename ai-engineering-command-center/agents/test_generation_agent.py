"""
Test Generation Agent.

Generates pytest unit tests for new or modified code in a git diff.
Covers happy paths, edge cases, and error conditions.

Expected context keys:
    diff (str): unified diff text from a pull request
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


class TestGenerationAgent(AgentBase):
    agent_name = "test-generation"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        diff = input.context.get("diff", "").strip()
        if not diff:
            return self._error_output("No diff provided in context['diff']")

        system = load_prompt("test_generation")
        user = (
            "Generate pytest unit tests for the code changes in this diff. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```diff\n{diff}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
