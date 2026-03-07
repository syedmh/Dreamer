"""
GitHub / Git service.

Reads diffs and repo metadata using GitPython.
Agents receive pre-parsed data from this service.

Placeholder — full implementation in Phase 4.
"""

from __future__ import annotations
from pathlib import Path


class GitHubService:
    """Provides git diff and commit metadata to agents."""

    def read_diff(self, path: str | Path) -> str:
        """Read a unified diff file and return its contents."""
        return Path(path).read_text(encoding="utf-8")

    def get_changed_files(self, diff: str) -> list[str]:
        """Parse a diff string and return the list of changed file paths."""
        files: list[str] = []
        for line in diff.splitlines():
            if line.startswith("+++ b/"):
                files.append(line[6:])
        return files
