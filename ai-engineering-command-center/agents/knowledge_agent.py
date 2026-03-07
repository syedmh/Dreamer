"""
Knowledge Agent — Retrieval Augmented Generation.

Answers engineering questions by:
  1. Querying ChromaDB for the most semantically relevant doc chunks
  2. Passing those chunks + the question to the LLM for synthesis
  3. Returning a structured answer with source attribution

Expected context keys:
    question (str):   the engineering question to answer
    n_results (int):  optional — how many chunks to retrieve (default 5)
"""

from __future__ import annotations

from core.agent_interface import AgentBase, AgentInput, AgentOutput
from core.prompt_loader import load_prompt
from services.llm_service import LLMService
from services.vector_store import VectorStoreService


class KnowledgeAgent(AgentBase):
    agent_name = "knowledge"

    def __init__(
        self,
        llm_service: LLMService,
        vector_store: VectorStoreService,
        n_results: int = 5,
    ) -> None:
        self.llm    = llm_service
        self.store  = vector_store
        self.n_results = n_results

    async def run(self, input: AgentInput) -> AgentOutput:
        question  = input.context.get("question", "").strip()
        n_results = input.context.get("n_results", self.n_results)

        if not question:
            return self._error_output("No question provided in context['question']")

        # Demo mode: if using MockLLMService, skip vector search and answer directly
        from services.mock_llm_service import MockLLMService
        if isinstance(self.llm, MockLLMService):
            system = load_prompt("knowledge")
            user = f"Question: {question}\n\nReturn ONLY JSON."
            try:
                data = await self.llm.complete_json(system, user)
                data.setdefault("raw", {})
                data["raw"].setdefault("sources_used", ["retry_queue.md (demo)"])
                data["raw"]["chunk_count"] = 3
                return self._build_output(data)
            except Exception as exc:
                return self._error_output(str(exc))

        # Guard: KB must be populated before queries can succeed
        if self.store.count() == 0:
            return self._error_output(
                "Knowledge base is empty. "
                "Run: python scripts/ingest_docs.py"
            )

        # Step 1: retrieve relevant chunks
        chunks = self.store.query(question, n_results=n_results)
        if not chunks:
            return self._error_output(
                "No relevant documents found for this question."
            )

        # Step 2: format retrieved context for the LLM
        context_blocks = []
        for i, chunk in enumerate(chunks, 1):
            source = chunk["metadata"].get("source", "unknown")
            context_blocks.append(f"[Chunk {i} | Source: {source}]\n{chunk['document']}")
        context_text = "\n\n---\n\n".join(context_blocks)

        system = load_prompt("knowledge")
        user = (
            f"Question: {question}\n\n"
            f"Retrieved context ({len(chunks)} chunks):\n\n"
            f"{context_text}\n\n"
            "Answer using ONLY the context above. Return ONLY JSON."
        )

        try:
            data = await self.llm.complete_json(system, user)

            # Inject retrieval metadata into raw payload
            data.setdefault("raw", {})
            data["raw"]["sources_used"] = list(
                dict.fromkeys(c["metadata"].get("source", "unknown") for c in chunks)
            )
            data["raw"]["chunk_count"] = len(chunks)
            data["raw"].setdefault("context_was_sufficient", True)

            return self._build_output(data)
        except Exception as exc:
            return self._error_output(str(exc))
