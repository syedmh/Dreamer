"""
Debate Synthesizer Agent.

Reads the full debate transcript and produces a final, binding
architecture decision that weighs all perspectives.

Used as Round 4 of Agent Debate Mode.

Expected context keys:
    topic (str):      the architecture decision being debated
    transcript (str): full debate transcript (all prior rounds)
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


class DebateSynthesizerAgent(AgentBase):
    agent_name = "debate-orchestrator"

    def __init__(self, llm_service: LLMService) -> None:
        self.llm = llm_service

    async def run(self, input: AgentInput) -> AgentOutput:
        topic      = input.context.get("topic", "").strip()
        transcript = input.context.get("transcript", "").strip()

        if not transcript:
            return self._error_output(
                "Provide the debate transcript in context['transcript']"
            )

        system = load_prompt("debate_synthesize")
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Full debate transcript:\n{transcript}\n\n"
            "Synthesize a final architecture decision. Return ONLY JSON."
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
