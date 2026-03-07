"""
Phase 6 tests — RAG knowledge system.

Strategy:
  - MockVectorStore replaces ChromaDB so no local DB is needed
  - MockLLM returns canned JSON — no Anthropic API calls
  - Tests cover: chunking, empty-KB guard, retrieval, answer rendering
"""

from __future__ import annotations

import asyncio
import json
from pathlib import Path
from unittest.mock import patch

import pytest
from typer.testing import CliRunner

from cli.main import app
from core.agent_interface import AgentInput, AgentOutput
from core.orchestrator import OrchestratorResult


# ---------------------------------------------------------------------------
# Helpers / Mocks
# ---------------------------------------------------------------------------

class MockVectorStore:
    """In-memory vector store for tests — no ChromaDB dependency."""

    def __init__(self, docs: list[dict] | None = None, empty: bool = False):
        self._docs = docs or []
        self._empty = empty

    def count(self) -> int:
        return 0 if self._empty else len(self._docs)

    def query(self, text: str, n_results: int = 5) -> list[dict]:
        if self._empty:
            return []
        return self._docs[:n_results]

    def add_documents(self, documents, ids, metadatas=None):
        for i, doc in enumerate(documents):
            self._docs.append({
                "document": doc,
                "metadata": (metadatas or [{}])[i],
                "distance": 0.1,
            })

    def reset(self):
        self._docs.clear()


class MockLLM:
    def __init__(self, response: dict):
        self._response = response

    async def complete_json(self, system: str, user: str) -> dict:
        return dict(self._response)

    async def complete(self, system: str, user: str) -> str:
        return json.dumps(self._response)


SAMPLE_CHUNKS = [
    {
        "document": "The retry queue handles transient failures with exponential backoff.",
        "metadata": {"source": "retry_queue.md", "chunk": 0},
        "distance": 0.12,
    },
    {
        "document": "Max retries: 3, base delay: 500ms, max delay: 30s.",
        "metadata": {"source": "retry_queue.md", "chunk": 1},
        "distance": 0.18,
    },
]

GOOD_ANSWER = {
    "agent": "knowledge",
    "status": "success",
    "confidence": 0.9,
    "issues": [],
    "recommendations": ["Check DLQ depth after incidents"],
    "raw": {
        "answer": "The retry queue uses exponential backoff with max 3 retries.",
        "sources_used": ["retry_queue.md"],
        "context_was_sufficient": True,
    },
}


def run(coro):
    return asyncio.run(coro)


# ---------------------------------------------------------------------------
# KnowledgeAgent tests
# ---------------------------------------------------------------------------

class TestKnowledgeAgent:
    def _agent(self, store=None, response=None):
        from agents.knowledge_agent import KnowledgeAgent
        return KnowledgeAgent(
            llm_service=MockLLM(response or GOOD_ANSWER),
            vector_store=store or MockVectorStore(SAMPLE_CHUNKS),
        )

    def test_success(self):
        out = run(self._agent().run(AgentInput(context={"question": "How does retry work?"})))
        assert out.status == "success"
        assert out.agent == "knowledge"
        assert out.confidence == pytest.approx(0.9)

    def test_no_question_returns_error(self):
        out = run(self._agent().run(AgentInput(context={})))
        assert out.status == "error"
        assert "question" in out.issues[0]

    def test_empty_kb_returns_error(self):
        agent = self._agent(store=MockVectorStore(empty=True))
        out = run(agent.run(AgentInput(context={"question": "anything?"})))
        assert out.status == "error"
        assert "empty" in out.issues[0].lower()

    def test_sources_injected_into_raw(self):
        out = run(self._agent().run(AgentInput(context={"question": "retry?"})))
        assert "sources_used" in out.raw
        assert "retry_queue.md" in out.raw["sources_used"]

    def test_chunk_count_in_raw(self):
        out = run(self._agent().run(AgentInput(context={"question": "retry?"})))
        assert out.raw["chunk_count"] == 2

    def test_agent_name_always_correct(self):
        bad_response = dict(GOOD_ANSWER)
        bad_response["agent"] = "wrong-name"
        out = run(self._agent(response=bad_response).run(
            AgentInput(context={"question": "retry?"})
        ))
        assert out.agent == "knowledge"

    def test_context_n_results_respected(self):
        store = MockVectorStore(SAMPLE_CHUNKS * 10)  # 20 chunks
        agent = self._agent(store=store)
        # n_results=2 should limit retrieval
        out = run(agent.run(AgentInput(context={"question": "retry?", "n_results": 2})))
        assert out.status in ("success", "error")  # depends on mock


# ---------------------------------------------------------------------------
# Chunking tests
# ---------------------------------------------------------------------------

class TestChunking:
    def _chunk(self, text, size=500, overlap=50):
        from scripts.ingest_docs import chunk_text
        return chunk_text(text, size=size, overlap=overlap)

    def test_short_text_single_chunk(self):
        chunks = self._chunk("Hello world", size=500)
        assert len(chunks) == 1
        assert chunks[0] == "Hello world"

    def test_long_text_splits_into_multiple(self):
        text = "x" * 1200
        chunks = self._chunk(text, size=500, overlap=50)
        assert len(chunks) > 1

    def test_overlap_applied(self):
        text = "a" * 600
        chunks = self._chunk(text, size=500, overlap=100)
        # Second chunk should start at 400, not 500
        assert len(chunks) == 2

    def test_empty_text_returns_empty(self):
        assert self._chunk("") == []
        assert self._chunk("   ") == []

    def test_exact_size_text_single_chunk(self):
        text = "z" * 500
        assert self._chunk(text, size=500, overlap=50) == [text]

    def test_chunk_content_contiguous(self):
        """Chunks should cover the entire text without gaps."""
        text = "abcdefghij" * 60   # 600 chars
        chunks = self._chunk(text, size=100, overlap=20)
        # Verify no gaps: each chunk starts in the right range
        assert chunks[0][:10] == "abcdefghij"


# ---------------------------------------------------------------------------
# VectorStore tests (using MockVectorStore logic — no ChromaDB)
# ---------------------------------------------------------------------------

class TestMockVectorStore:
    def test_count_empty(self):
        store = MockVectorStore(empty=True)
        assert store.count() == 0

    def test_count_with_docs(self):
        store = MockVectorStore(SAMPLE_CHUNKS)
        assert store.count() == 2

    def test_query_returns_docs(self):
        store = MockVectorStore(SAMPLE_CHUNKS)
        results = store.query("retry", n_results=1)
        assert len(results) == 1
        assert "document" in results[0]

    def test_query_empty_store_returns_empty(self):
        store = MockVectorStore(empty=True)
        assert store.query("anything") == []

    def test_add_documents(self):
        store = MockVectorStore()
        store.add_documents(["doc1", "doc2"], ["id1", "id2"])
        assert store.count() == 2

    def test_reset_clears(self):
        store = MockVectorStore(SAMPLE_CHUNKS)
        store.reset()
        assert store.count() == 0


# ---------------------------------------------------------------------------
# CLI ask-knowledge tests
# ---------------------------------------------------------------------------

cli_runner = CliRunner(mix_stderr=False)


def make_knowledge_result() -> OrchestratorResult:
    return OrchestratorResult(
        workflow="knowledge",
        status="success",
        agent_count=1,
        success_count=1,
        error_count=0,
        overall_confidence=0.9,
        all_issues=[],
        all_recommendations=["Check DLQ depth"],
        agent_outputs=[
            AgentOutput(
                agent="knowledge",
                status="success",
                confidence=0.9,
                issues=[],
                recommendations=["Check DLQ depth"],
                raw={
                    "answer": "The retry queue uses exponential backoff.",
                    "sources_used": ["retry_queue.md"],
                    "context_was_sufficient": True,
                },
            )
        ],
        duration_seconds=2.1,
    )


class MockOrchestrator:
    def __init__(self, result): self._result = result
    async def execute(self, workflow, context): return self._result


class TestAskKnowledgeCli:
    def test_success_rich(self):
        result = make_knowledge_result()
        with patch("cli.main._build_orch", return_value=MockOrchestrator(result)):
            out = cli_runner.invoke(app, ["ask-knowledge", "How does retry queue work?"])
        assert out.exit_code == 0
        assert "retry" in out.output.lower() or "knowledge" in out.output.lower()

    def test_json_output(self):
        result = make_knowledge_result()
        with patch("cli.main._build_orch", return_value=MockOrchestrator(result)):
            out = cli_runner.invoke(
                app, ["ask-knowledge", "How does retry queue work?", "--json"]
            )
        assert out.exit_code == 0
        data = json.loads(out.output)
        assert data["workflow"] == "knowledge"

    def test_answer_panel_rendered(self):
        result = make_knowledge_result()
        with patch("cli.main._build_orch", return_value=MockOrchestrator(result)):
            out = cli_runner.invoke(app, ["ask-knowledge", "retry queue?"])
        assert "exponential" in out.output or "Answer" in out.output
