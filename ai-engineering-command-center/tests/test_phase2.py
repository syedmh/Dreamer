"""
Phase 2 smoke tests — no LLM calls required.

Validates that:
- AgentBase can be subclassed and enforces the run() contract
- AgentRunner executes agents concurrently and isolates failures
- AgentRunner respects timeout
- LLMService._extract_json handles all three response formats
- PromptLoader loads and substitutes templates
"""

import asyncio
import pytest
from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.agent_runner import AgentRunner
from core.prompt_loader import load_prompt
from services.llm_service import LLMService


# ---------------------------------------------------------------------------
# Fixtures: concrete agent implementations for testing
# ---------------------------------------------------------------------------

class EchoAgent(AgentBase):
    agent_name = "echo"
    async def run(self, input: AgentInput) -> AgentOutput:
        return AgentOutput(agent=self.agent_name, status="success", confidence=1.0)


class FailingAgent(AgentBase):
    agent_name = "failing"
    async def run(self, input: AgentInput) -> AgentOutput:
        raise RuntimeError("intentional failure")


class SlowAgent(AgentBase):
    agent_name = "slow"
    async def run(self, input: AgentInput) -> AgentOutput:
        await asyncio.sleep(10)
        return AgentOutput(agent=self.agent_name, status="success")


# ---------------------------------------------------------------------------
# AgentBase tests
# ---------------------------------------------------------------------------

def test_agent_base_is_abstract():
    """AgentBase cannot be instantiated directly."""
    with pytest.raises(TypeError):
        AgentBase()  # type: ignore


def test_parse_llm_json_raw():
    agent = EchoAgent()
    result = agent._parse_llm_json('{"agent": "test", "confidence": 0.9}')
    assert result["confidence"] == 0.9


def test_parse_llm_json_fenced():
    agent = EchoAgent()
    text = '```json\n{"agent": "test", "status": "success"}\n```'
    result = agent._parse_llm_json(text)
    assert result["status"] == "success"


def test_parse_llm_json_embedded():
    agent = EchoAgent()
    text = 'Here is the output: {"agent": "test", "issues": []} as requested.'
    result = agent._parse_llm_json(text)
    assert result["issues"] == []


def test_parse_llm_json_raises_on_no_json():
    agent = EchoAgent()
    with pytest.raises(ValueError):
        agent._parse_llm_json("no json here at all")


def test_build_output_injects_agent_name():
    agent = EchoAgent()
    out = agent._build_output({"agent": "wrong", "confidence": 0.5})
    assert out.agent == "echo"


# ---------------------------------------------------------------------------
# AgentRunner tests
# ---------------------------------------------------------------------------

def test_runner_returns_all_results():
    runner = AgentRunner(show_progress=False)
    agents = [EchoAgent(), EchoAgent()]
    results = asyncio.run(runner.run_all(agents, AgentInput()))
    assert len(results) == 2
    assert all(r.status == "success" for r in results)


def test_runner_isolates_failure():
    runner = AgentRunner(show_progress=False)
    agents = [EchoAgent(), FailingAgent(), EchoAgent()]
    results = asyncio.run(runner.run_all(agents, AgentInput()))
    statuses = [r.status for r in results]
    assert statuses.count("success") == 2
    assert statuses.count("error") == 1


def test_runner_handles_empty_list():
    runner = AgentRunner(show_progress=False)
    results = asyncio.run(runner.run_all([], AgentInput()))
    assert results == []


def test_runner_timeout():
    runner = AgentRunner(timeout=0.1, show_progress=False)
    results = asyncio.run(runner.run_all([SlowAgent()], AgentInput()))
    assert results[0].status == "error"
    assert "timed out" in results[0].issues[0]


def test_runner_concurrent_execution():
    """Two slow agents should finish in roughly the time of one, not both."""
    import time

    class FastSleepAgent(AgentBase):
        agent_name = "fast-sleep"
        async def run(self, input: AgentInput) -> AgentOutput:
            await asyncio.sleep(0.2)
            return AgentOutput(agent=self.agent_name, status="success")

    runner = AgentRunner(show_progress=False)
    start = time.monotonic()
    results = asyncio.run(runner.run_all([FastSleepAgent(), FastSleepAgent()], AgentInput()))
    elapsed = time.monotonic() - start
    assert elapsed < 0.5, f"Expected concurrent execution but took {elapsed:.2f}s"
    assert all(r.status == "success" for r in results)


# ---------------------------------------------------------------------------
# LLMService JSON extraction tests (no API call)
# ---------------------------------------------------------------------------

def test_llm_service_extract_json_raw():
    svc = LLMService.__new__(LLMService)
    result = svc._extract_json('{"key": "value"}')
    assert result["key"] == "value"


def test_llm_service_extract_json_fenced():
    svc = LLMService.__new__(LLMService)
    result = svc._extract_json("```json\n{\"x\": 1}\n```")
    assert result["x"] == 1


def test_llm_service_extract_json_raises():
    svc = LLMService.__new__(LLMService)
    with pytest.raises(ValueError):
        svc._extract_json("no json")


# ---------------------------------------------------------------------------
# PromptLoader tests
# ---------------------------------------------------------------------------

def test_load_prompt_returns_text():
    text = load_prompt("code_review")
    assert "ROLE" in text
    assert "OUTPUT FORMAT" in text


def test_load_prompt_missing_raises():
    with pytest.raises(FileNotFoundError):
        load_prompt("nonexistent_agent")
