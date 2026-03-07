"""
SRE Agent.

Recommends immediate mitigations, short-term fixes, and long-term
reliability improvements based on incident log data.

Expected context keys:
    log (str):               raw log text
    log_path (str):          optional fallback path
    incident_analysis (str): optional — output from IncidentAgent for richer context
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.log_service import LogService


class SREAgent(AgentBase):
    agent_name = "sre"

    def __init__(self, llm_service: LLMService, log_service: LogService) -> None:
        self.llm = llm_service
        self.log = log_service

    async def run(self, input: AgentInput) -> AgentOutput:
        log_text  = input.context.get("log", "").strip()
        log_path  = input.context.get("log_path", "")
        incident  = input.context.get("incident_analysis", "")

        if not log_text and log_path:
            try:
                log_text = self.log.read_log(log_path)
            except Exception as exc:
                return self._error_output(f"Could not read log file: {exc}")

        if not log_text:
            return self._error_output(
                "Provide log text in context['log'] or a file path in context['log_path']"
            )

        incident_section = (
            f"\n\nIncident diagnosis from prior analysis:\n{incident}"
            if incident else ""
        )

        system = load_prompt("sre")
        user = (
            "Recommend reliability improvements for the following incident. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"Log excerpt:\n```\n{log_text}\n```"
            f"{incident_section}"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
