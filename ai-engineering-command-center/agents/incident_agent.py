"""
Incident Diagnosis Agent.

Analyzes log output to determine the root cause of an incident,
the sequence of events, affected services, and severity.

Expected context keys:
    log (str):       raw log text (read by LogService before passing here)
    log_path (str):  optional — path to log file (agent will read it if log absent)
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.log_service import LogService


class IncidentAgent(AgentBase):
    agent_name = "incident"

    def __init__(self, llm_service: LLMService, log_service: LogService) -> None:
        self.llm = llm_service
        self.log = log_service

    async def run(self, input: AgentInput) -> AgentOutput:
        log_text  = input.context.get("log", "").strip()
        log_path  = input.context.get("log_path", "")

        # Fallback: read from path if raw text not provided
        if not log_text and log_path:
            try:
                log_text = self.log.read_log(log_path)
            except Exception as exc:
                return self._error_output(f"Could not read log file: {exc}")

        if not log_text:
            return self._error_output(
                "Provide log text in context['log'] or a file path in context['log_path']"
            )

        system = load_prompt("incident")
        user = (
            "Analyze the following log excerpt and diagnose the incident. "
            "Return ONLY a JSON object matching the specified schema.\n\n"
            f"```\n{log_text}\n```"
        )

        try:
            data = await self.llm.complete_json(system, user)
            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
