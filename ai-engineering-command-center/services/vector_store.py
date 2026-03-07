"""
Vector store service — ChromaDB-backed semantic retrieval.

Wraps ChromaDB with a stable interface so agents never touch it directly.
All document chunks and their embeddings are persisted to data/chroma_db/.

Design notes:
- Uses ChromaDB's DefaultEmbeddingFunction (onnxruntime + all-MiniLM-L6-v2).
  No external API key required — runs fully locally.
- PersistentClient stores data on disk; the KB survives process restarts.
- n_results is clamped to collection.count() to prevent ChromaDB errors
  when fewer documents exist than requested.
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

_DEFAULT_DB_PATH   = Path(__file__).parent.parent / "data" / "chroma_db"
_DEFAULT_COLLECTION = "engineering_kb"


class VectorStoreService:
    """
    ChromaDB-backed vector store for the engineering knowledge base.

    Args:
        collection_name: Name of the ChromaDB collection.
        db_path:         Directory where ChromaDB persists data.
    """

    def __init__(
        self,
        collection_name: str = _DEFAULT_COLLECTION,
        db_path: str | Path = _DEFAULT_DB_PATH,
    ) -> None:
        self.collection_name = collection_name
        self._db_path = Path(db_path)
        self._db_path.mkdir(parents=True, exist_ok=True)

        # Lazy import so tests can mock this without installing chromadb
        import chromadb
        from chromadb.utils.embedding_functions import DefaultEmbeddingFunction

        self._client = chromadb.PersistentClient(path=str(self._db_path))
        self._ef = DefaultEmbeddingFunction()
        self._collection = self._client.get_or_create_collection(
            name=collection_name,
            embedding_function=self._ef,
        )
        logger.info(
            "VectorStoreService ready: collection=%r docs=%d path=%s",
            collection_name, self._collection.count(), self._db_path,
        )

    # ------------------------------------------------------------------
    # Write
    # ------------------------------------------------------------------

    def add_documents(
        self,
        documents: list[str],
        ids: list[str],
        metadatas: list[dict[str, Any]] | None = None,
    ) -> None:
        """
        Add or update document chunks in the collection.

        Duplicate IDs are upserted (ChromaDB behaviour).
        """
        if not documents:
            return
        self._collection.upsert(
            documents=documents,
            ids=ids,
            metadatas=metadatas or [{} for _ in documents],
        )
        logger.debug("Added %d document chunks", len(documents))

    def reset(self) -> None:
        """Delete and recreate the collection (wipes all data)."""
        self._client.delete_collection(self.collection_name)
        self._collection = self._client.get_or_create_collection(
            name=self.collection_name,
            embedding_function=self._ef,
        )
        logger.warning("VectorStoreService: collection %r reset", self.collection_name)

    # ------------------------------------------------------------------
    # Read
    # ------------------------------------------------------------------

    def query(self, text: str, n_results: int = 5) -> list[dict[str, Any]]:
        """
        Semantic similarity search.

        Returns a list of result dicts, each with keys:
            document (str), metadata (dict), distance (float)

        Distance is L2; lower = more similar.
        Returns [] if the collection is empty.
        """
        total = self._collection.count()
        if total == 0:
            return []

        safe_n = min(n_results, total)
        results = self._collection.query(
            query_texts=[text],
            n_results=safe_n,
            include=["documents", "metadatas", "distances"],
        )

        docs      = results.get("documents", [[]])[0]
        metadatas = results.get("metadatas",  [[]])[0]
        distances = results.get("distances",  [[]])[0]

        return [
            {"document": doc, "metadata": meta, "distance": dist}
            for doc, meta, dist in zip(docs, metadatas, distances)
        ]

    def count(self) -> int:
        """Return the number of chunks stored in the collection."""
        return self._collection.count()
