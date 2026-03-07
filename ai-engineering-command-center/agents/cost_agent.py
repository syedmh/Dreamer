"""
Cost Agent.

Evaluates the operational cost implications of an architecture proposal.
Used as a debate participant in Agent Debate Mode.

Expected context keys:
    topic (str):    the architecture decision being evaluated
    proposal (str): the proposed design to cost-evaluate
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


class CostAgent(AgentBase):
    agent_name = "cost"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        topic    = input.context.get("topic", "").strip()
        proposal = input.context.get("proposal", "").strip()

        if not topic and not proposal:
            return self._error_output(
                "Provide 'topic' and/or 'proposal' in context"
            )

        system = load_prompt("debate_cost")
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Proposed design:\n{proposal}\n\n"
            "Evaluate the operational cost implications. Return ONLY JSON."
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
