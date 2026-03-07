"""
Async agent runner.

Executes a list of agents concurrently using asyncio.gather.
Each agent is wrapped in a timeout and an error guard so that
a single slow or failing agent never blocks or crashes the run.

Rich is used to render a live progress panel during execution.
"""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Sequence

from rich.console import Console
from rich.live import Live
from rich.table import Table

from core.agent_interface import AgentBase, AgentInput, AgentOutput

logger = logging.getLogger(__name__)

_DEFAULT_TIMEOUT = 120.0   # seconds per agent


class AgentRunner:
    """
    Runs agents concurrently and aggregates their outputs.

    Args:
        timeout: Per-agent timeout in seconds. Agents that exceed this
                 limit receive an error AgentOutput instead of crashing.
        show_progress: When True, render a Rich live progress table.
    """

    def __init__(
        self,
        timeout: float = _DEFAULT_TIMEOUT,
        show_progress: bool = True,
    ) -> None:
        self.timeout = timeout
        self.show_progress = show_progress
        self._console = Console()

    async def run_all(
        self,
        agents: Sequence[AgentBase],
        input: AgentInput,
    ) -> list[AgentOutput]:
        """
        Execute all agents concurrently and return their outputs.

        Results are returned in the same order as `agents`.
        Failed or timed-out agents produce an error AgentOutput.
        """
        if not agents:
            return []

        start = time.monotonic()

        if self.show_progress:
            results = await self._run_with_progress(agents, input)
        else:
            tasks = [self._safe_run(agent, input) for agent in agents]
            results = list(await asyncio.gather(*tasks))

        elapsed = time.monotonic() - start
        success = sum(1 for r in results if r.status == "success")
        logger.info(
            "AgentRunner completed %d/%d agents in %.2fs",
            success, len(agents), elapsed,
        )
        return results

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    async def _run_with_progress(
        self,
        agents: Sequence[AgentBase],
        input: AgentInput,
    ) -> list[AgentOutput]:
        """Run agents and show a live Rich table while they execute."""
        state: dict[str, str] = {a.agent_name: "pending" for a in agents}

        def build_table() -> Table:
            table = Table(title="Agent Execution", show_header=True)
            table.add_column("Agent", style="cyan")
            table.add_column("Status")
            for name, status in state.items():
                colour = {"pending": "white", "running": "yellow",
                          "success": "green", "error": "red"}.get(status, "white")
                table.add_row(name, f"[{colour}]{status}[/{colour}]")
            return table

        results: list[AgentOutput | None] = [None] * len(agents)

        async def tracked(index: int, agent: AgentBase) -> None:
            state[agent.agent_name] = "running"
            result = await self._safe_run(agent, input)
            results[index] = result
            state[agent.agent_name] = result.status

        with Live(build_table(), console=self._console, refresh_per_second=4) as live:
            tasks = [tracked(i, a) for i, a in enumerate(agents)]
            # Refresh display as tasks complete
            async def refresh_loop() -> None:
                while any(s in ("pending", "running") for s in state.values()):
                    live.update(build_table())
                    await asyncio.sleep(0.25)
                live.update(build_table())

            await asyncio.gather(*tasks, refresh_loop())

        return [r for r in results if r is not None]

    async def _safe_run(self, agent: AgentBase, input: AgentInput) -> AgentOutput:
        """Run a single agent with timeout and exception isolation."""
        try:
            return await asyncio.wait_for(agent.run(input), timeout=self.timeout)
        except asyncio.TimeoutError:
            msg = f"Agent timed out after {self.timeout}s"
            logger.error("[%s] %s", agent.agent_name, msg)
            return AgentOutput(agent=agent.agent_name, status="error", issues=[msg])
        except Exception as exc:
            logger.error("[%s] Unhandled exception: %s", agent.agent_name, exc, exc_info=True)
            return AgentOutput(
                agent=agent.agent_name,
                status="error",
                issues=[str(exc)],
            )
