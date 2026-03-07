"""
Abstract base class for all agents in the AI Engineering Command Center.

Every agent must inherit AgentBase and implement `run`.
Services are injected at construction time — agents never import
or call external APIs directly.
"""

from __future__ import annotations

import json
import re
from abc import ABC, abstractmethod
from typing import Any

from pydantic import BaseModel


# ---------------------------------------------------------------------------
# Typed data contracts
# ---------------------------------------------------------------------------

class AgentInput(BaseModel):
    """Input envelope passed to every agent."""
    context: dict[str, Any] = {}


class AgentOutput(BaseModel):
    """Output envelope returned by every agent."""
    agent: str
    status: str = "success"          # "success" | "error"
    confidence: float = 0.0          # 0.0 – 1.0
    issues: list[str] = []
    recommendations: list[str] = []
    raw: dict[str, Any] = {}         # agent-specific payload


# ---------------------------------------------------------------------------
# Base class
# ---------------------------------------------------------------------------

class AgentBase(ABC):
    """
    Shared interface for all agents.

    Subclasses set `agent_name` and implement `run`.
    Services are injected via __init__ as positional/keyword arguments.
    """

    agent_name: str = "base"

    @abstractmethod
    async def run(self, input: AgentInput) -> AgentOutput:
        """Execute the agent's task and return structured output."""
        ...

    # ------------------------------------------------------------------
    # Helpers available to all subclasses
    # ------------------------------------------------------------------

    def _parse_llm_json(self, text: str) -> dict[str, Any]:
        """
        Extract and parse a JSON object from LLM response text.

        Handles:
        1. Raw JSON string
        2. JSON inside a ```json ... ``` code fence
        3. JSON embedded in surrounding prose
        """
        text = text.strip()

        try:
            return json.loads(text)
        except json.JSONDecodeError:
            pass

        fenced = re.search(r"```(?:json)?\s*(\{.*?\})\s*```", text, re.DOTALL)
        if fenced:
            return json.loads(fenced.group(1))

        embedded = re.search(r"\{.*\}", text, re.DOTALL)
        if embedded:
            return json.loads(embedded.group(0))

        raise ValueError(
            f"[{self.agent_name}] No JSON object in LLM response. "
            f"First 300 chars: {text[:300]}"
        )

    def _build_output(self, data: dict[str, Any]) -> AgentOutput:
        """
        Construct an AgentOutput from a parsed LLM JSON dict.

        Only AgentOutput field names are forwarded — any extra keys the
        LLM returns are silently dropped. The `agent` field is always
        overwritten with this agent's canonical name.
        """
        known = AgentOutput.model_fields.keys()
        filtered = {k: v for k, v in data.items() if k in known}
        filtered["agent"] = self.agent_name
        return AgentOutput(**filtered)

    def _error_output(self, message: str) -> AgentOutput:
        """Return a well-formed error AgentOutput."""
        return AgentOutput(
            agent=self.agent_name,
            status="error",
            issues=[message],
        )
