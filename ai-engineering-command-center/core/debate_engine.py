"""
Agent Debate Engine.

Orchestrates a 4-round structured debate between AI agents to evaluate
an architecture decision. Unlike the parallel AgentRunner, each round
is sequential and informed by prior rounds.

Debate structure:
    Round 1  Architecture Agent proposes a design solution
    Round 2  Security Agent + Cost Agent critique in parallel
    Round 3  Architecture Agent rebuts critiques and refines the proposal
    Round 4  Orchestrator Agent synthesizes all positions → final decision

Usage::

    engine = DebateEngine(llm_service)
    result = await engine.run(
        topic="Should we add a Redis caching layer?",
        context="Current p99 API latency is 450ms. Postgres primary is at 70% CPU."
    )
"""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Any

from pydantic import BaseModel
from rich.console import Console

from core.prompt_loader import load_prompt
from services.llm_service import LLMService

logger = logging.getLogger(__name__)


# ---------------------------------------------------------------------------
# Result models
# ---------------------------------------------------------------------------

class DebateRound(BaseModel):
    """A single participant's contribution to the debate."""
    round_number: int
    participant: str         # "architecture" | "security" | "cost" | "orchestrator"
    role: str                # "proposal" | "critique" | "cost_evaluation" | "rebuttal" | "synthesis"
    summary: str             # key position text extracted from LLM response
    confidence: float = 0.0
    raw: dict[str, Any] = {}


class DebateResult(BaseModel):
    """Final output of a complete debate run."""
    topic: str
    status: str              # "complete" | "error"
    rounds: list[DebateRound]
    final_decision: str      # "proceed" | "reject" | "revise"
    recommendation: str      # full recommendation text
    overall_confidence: float
    dissenting_views: list[str] = []
    duration_seconds: float

    def to_dict(self) -> dict:
        return self.model_dump()


# ---------------------------------------------------------------------------
# Debate Engine
# ---------------------------------------------------------------------------

class DebateEngine:
    """
    Runs a 4-round architecture debate using the LLM service.

    The engine calls the LLM directly (it is a coordinator, not an agent)
    but always routes through the injected LLMService — never the SDK.
    """

    def __init__(
        self,
        llm_service: LLMService,
        show_progress: bool = True,
    ) -> None:
        self.llm = llm_service
        self.show_progress = show_progress
        self._console = Console()

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    async def run(self, topic: str, context: str = "") -> DebateResult:
        """
        Execute the full 4-round debate and return the result.

        Args:
            topic:   The architecture question being debated.
            context: Optional background information (metrics, constraints).
        """
        start = time.monotonic()
        rounds: list[DebateRound] = []

        self._status("[bold cyan]Agent Debate Mode[/bold cyan] starting…")
        self._status(f"Topic: [white]{topic}[/white]")

        try:
            # Round 1 — Architecture proposes
            self._status("\n[1/4] Architecture Agent — proposing design…")
            proposal = await self._run_proposal(topic, context)
            rounds.append(proposal)
            self._status(f"      [dim]{proposal.summary[:120]}…[/dim]")

            # Round 2 — Security + Cost critique in parallel
            self._status("[2/4] Security + Cost Agents — critiquing in parallel…")
            security_critique, cost_evaluation = await asyncio.gather(
                self._run_security_critique(topic, proposal),
                self._run_cost_evaluation(topic, proposal),
            )
            rounds.extend([security_critique, cost_evaluation])
            self._status(f"      Security: [dim]{security_critique.summary[:100]}…[/dim]")
            self._status(f"      Cost:     [dim]{cost_evaluation.summary[:100]}…[/dim]")

            # Round 3 — Architecture rebuts
            self._status("[3/4] Architecture Agent — rebutting critiques…")
            rebuttal = await self._run_rebuttal(topic, proposal, security_critique, cost_evaluation)
            rounds.append(rebuttal)
            self._status(f"      [dim]{rebuttal.summary[:120]}…[/dim]")

            # Round 4 — Synthesis
            self._status("[4/4] Orchestrator Agent — synthesizing final decision…")
            synthesis = await self._run_synthesis(topic, rounds)
            rounds.append(synthesis)

        except Exception as exc:
            logger.error("Debate failed: %s", exc, exc_info=True)
            elapsed = time.monotonic() - start
            return DebateResult(
                topic=topic,
                status="error",
                rounds=rounds,
                final_decision="error",
                recommendation=str(exc),
                overall_confidence=0.0,
                duration_seconds=round(elapsed, 3),
            )

        elapsed = time.monotonic() - start
        return self._build_result(topic, rounds, synthesis, elapsed)

    # ------------------------------------------------------------------
    # Round helpers
    # ------------------------------------------------------------------

    async def _run_proposal(self, topic: str, context: str) -> DebateRound:
        system = load_prompt("debate_propose")
        user_parts = [f"Architecture topic: {topic}"]
        if context:
            user_parts.append(f"Background context:\n{context}")
        user_parts.append("Propose your design solution. Return ONLY JSON.")

        data = await self._safe_complete(system, "\n\n".join(user_parts))
        return DebateRound(
            round_number=1,
            participant="architecture",
            role="proposal",
            summary=data.get("raw", {}).get("proposal", data.get("raw", {}).get("summary", "")),
            confidence=data.get("confidence", 0.0),
            raw=data,
        )

    async def _run_security_critique(
        self, topic: str, proposal: DebateRound
    ) -> DebateRound:
        system = load_prompt("debate_critique")
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Proposed design:\n{proposal.summary}\n\n"
            "Critique this proposal from a security standpoint. Return ONLY JSON."
        )
        data = await self._safe_complete(system, user)
        return DebateRound(
            round_number=2,
            participant="security",
            role="critique",
            summary=data.get("raw", {}).get("critique", data.get("raw", {}).get("summary", "")),
            confidence=data.get("confidence", 0.0),
            raw=data,
        )

    async def _run_cost_evaluation(
        self, topic: str, proposal: DebateRound
    ) -> DebateRound:
        system = load_prompt("debate_cost")
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Proposed design:\n{proposal.summary}\n\n"
            "Evaluate operational cost. Return ONLY JSON."
        )
        data = await self._safe_complete(system, user)
        return DebateRound(
            round_number=2,
            participant="cost",
            role="cost_evaluation",
            summary=data.get("raw", {}).get("cost_summary", data.get("raw", {}).get("summary", "")),
            confidence=data.get("confidence", 0.0),
            raw=data,
        )

    async def _run_rebuttal(
        self,
        topic: str,
        proposal: DebateRound,
        security_critique: DebateRound,
        cost_evaluation: DebateRound,
    ) -> DebateRound:
        system = load_prompt("debate_rebuttal")
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Your original proposal:\n{proposal.summary}\n\n"
            f"Security critique:\n{security_critique.summary}\n\n"
            f"Cost evaluation:\n{cost_evaluation.summary}\n\n"
            "Rebut the critiques and refine your proposal. Return ONLY JSON."
        )
        data = await self._safe_complete(system, user)
        return DebateRound(
            round_number=3,
            participant="architecture",
            role="rebuttal",
            summary=data.get("raw", {}).get("rebuttal", data.get("raw", {}).get("summary", "")),
            confidence=data.get("confidence", 0.0),
            raw=data,
        )

    async def _run_synthesis(
        self, topic: str, rounds: list[DebateRound]
    ) -> DebateRound:
        system = load_prompt("debate_synthesize")

        transcript = "\n\n".join(
            f"[Round {r.round_number} | {r.participant.upper()} | {r.role}]\n{r.summary}"
            for r in rounds
        )
        user = (
            f"Architecture topic: {topic}\n\n"
            f"Full debate transcript:\n{transcript}\n\n"
            "Synthesize a final decision. Return ONLY JSON."
        )
        data = await self._safe_complete(system, user)
        return DebateRound(
            round_number=4,
            participant="orchestrator",
            role="synthesis",
            summary=data.get("raw", {}).get("recommendation", data.get("raw", {}).get("summary", "")),
            confidence=data.get("confidence", 0.0),
            raw=data,
        )

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    async def _safe_complete(self, system: str, user: str) -> dict[str, Any]:
        """Call LLM and parse JSON; return error dict on failure."""
        try:
            return await self.llm.complete_json(system, user)
        except Exception as exc:
            logger.error("LLM call failed in debate: %s", exc)
            return {
                "agent": "unknown",
                "status": "error",
                "confidence": 0.0,
                "issues": [str(exc)],
                "recommendations": [],
                "raw": {"summary": f"Error: {exc}"},
            }

    def _build_result(
        self,
        topic: str,
        rounds: list[DebateRound],
        synthesis: DebateRound,
        elapsed: float,
    ) -> DebateResult:
        synth_raw = synthesis.raw.get("raw", {})
        return DebateResult(
            topic=topic,
            status="complete",
            rounds=rounds,
            final_decision=synth_raw.get("final_decision", "revise"),
            recommendation=synth_raw.get("recommendation", synthesis.summary),
            overall_confidence=round(synthesis.confidence, 3),
            dissenting_views=synth_raw.get("dissenting_views", []),
            duration_seconds=round(elapsed, 3),
        )

    def _status(self, message: str) -> None:
        if self.show_progress:
            self._console.print(message)
