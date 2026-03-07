"""
Jira / ticket service.

Reads and parses ticket JSON files for the Backlog Agent.

Placeholder — full implementation in Phase 4.
"""

from __future__ import annotations
import json
from pathlib import Path
from typing import Any


class JiraService:
    """Loads ticket data from local JSON files."""

    def load_tickets(self, path: str | Path) -> list[dict[str, Any]]:
        """Load a JSON array of tickets from disk."""
        return json.loads(Path(path).read_text(encoding="utf-8"))
