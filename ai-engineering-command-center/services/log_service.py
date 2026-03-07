"""
Log service.

Reads and pre-processes log files for the Incident Diagnosis Agent.

Placeholder — full implementation in Phase 4.
"""

from __future__ import annotations
from pathlib import Path


class LogService:
    """Reads log files and returns their content as a string."""

    def read_log(self, path: str | Path, tail: int = 500) -> str:
        """
        Read the last `tail` lines of a log file.

        Args:
            path: Path to the log file.
            tail: Number of lines to return from the end.
        """
        lines = Path(path).read_text(encoding="utf-8").splitlines()
        return "\n".join(lines[-tail:])
