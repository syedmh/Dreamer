#!/usr/bin/env python3
"""
Document ingestion script.

Walks data/docs/, chunks every text/markdown/Python file, and upserts
all chunks into ChromaDB. Safe to re-run — duplicate IDs are upserted.

Usage:
    python scripts/ingest_docs.py
    python scripts/ingest_docs.py --docs-dir path/to/docs
    python scripts/ingest_docs.py --reset   # wipe KB before ingesting
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

# Ensure project root is on PYTHONPATH when run as a script
_ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(_ROOT))

from rich.console import Console
from rich.table import Table
from rich import box

from services.vector_store import VectorStoreService

console = Console()

_DOCS_DIR    = _ROOT / "data" / "docs"
_CHUNK_SIZE  = 500   # characters per chunk
_OVERLAP     = 50    # character overlap between consecutive chunks
_EXTENSIONS  = {".md", ".txt", ".py", ".rst", ".yaml", ".json"}


# ---------------------------------------------------------------------------
# Chunking
# ---------------------------------------------------------------------------

def chunk_text(text: str, size: int = _CHUNK_SIZE, overlap: int = _OVERLAP) -> list[str]:
    """
    Split text into overlapping chunks of `size` characters.

    Overlap ensures context is not lost at chunk boundaries.
    """
    if not text.strip():
        return []
    chunks: list[str] = []
    start = 0
    while start < len(text):
        end = start + size
        chunks.append(text[start:end])
        if end >= len(text):
            break
        start += size - overlap
    return chunks


# ---------------------------------------------------------------------------
# Ingestion
# ---------------------------------------------------------------------------

def ingest(
    docs_dir: Path = _DOCS_DIR,
    reset: bool = False,
    chunk_size: int = _CHUNK_SIZE,
    overlap: int = _OVERLAP,
) -> int:
    """
    Ingest all documents from docs_dir into ChromaDB.

    Returns:
        Total number of chunks ingested.
    """
    store = VectorStoreService()

    if reset:
        console.print("[yellow]Resetting knowledge base...[/yellow]")
        store.reset()

    if not docs_dir.exists():
        console.print(f"[red]Docs directory not found: {docs_dir}[/red]")
        return 0

    doc_files = [f for f in docs_dir.rglob("*") if f.is_file()
                 and f.suffix.lower() in _EXTENSIONS]

    if not doc_files:
        console.print(f"[yellow]No documents found in {docs_dir}[/yellow]")
        return 0

    table = Table(title="Document Ingestion", box=box.SIMPLE_HEAVY)
    table.add_column("File", style="cyan")
    table.add_column("Chunks", justify="right")
    table.add_column("Characters", justify="right")

    total_chunks = 0

    for doc_file in sorted(doc_files):
        try:
            text = doc_file.read_text(encoding="utf-8", errors="ignore").strip()
        except Exception as exc:
            console.print(f"[red]  Skipping {doc_file.name}: {exc}[/red]")
            continue

        if not text:
            continue

        chunks = chunk_text(text, size=chunk_size, overlap=overlap)
        ids = [f"{doc_file.stem}::chunk{i}" for i in range(len(chunks))]
        metadatas = [
            {
                "source": doc_file.name,
                "path":   str(doc_file.relative_to(_ROOT)),
                "chunk":  i,
                "total_chunks": len(chunks),
            }
            for i in range(len(chunks))
        ]

        store.add_documents(chunks, ids, metadatas)
        total_chunks += len(chunks)
        table.add_row(doc_file.name, str(len(chunks)), str(len(text)))

    console.print(table)
    console.print(
        f"\n[bold green]Ingestion complete.[/bold green] "
        f"[white]{total_chunks} chunks[/white] stored in knowledge base."
    )
    console.print(
        f"[dim]KB total: {store.count()} chunks[/dim]"
    )
    return total_chunks


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Ingest engineering docs into ChromaDB")
    parser.add_argument("--docs-dir", default=str(_DOCS_DIR),
                        help="Directory to ingest (default: data/docs/)")
    parser.add_argument("--reset", action="store_true",
                        help="Wipe the knowledge base before ingesting")
    parser.add_argument("--chunk-size", type=int, default=_CHUNK_SIZE)
    parser.add_argument("--overlap", type=int, default=_OVERLAP)
    args = parser.parse_args()

    ingest(
        docs_dir=Path(args.docs_dir),
        reset=args.reset,
        chunk_size=args.chunk_size,
        overlap=args.overlap,
    )
