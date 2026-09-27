from __future__ import annotations

import math
import os
import sqlite3
import tempfile
import time
from contextlib import contextmanager
from datetime import datetime, timedelta
from pathlib import Path
from typing import Iterator

from .domain import AppError, ErrorCode, validate_retry_after
from .path_safety import private_file_stat, reject_reparse_components

_MAX_SNAPSHOT_BYTES = 512 * 1024 * 1024
_JOB_COLUMNS = {
    "job_id", "source_path", "source_path_key", "source_name", "size", "mtime_ns",
    "sha256", "status", "provider", "model", "prompt_hash", "staged_path",
    "temp_name", "output_name", "output_sha256", "next_attempt_at",
    "created_at", "updated_at", "error_code",
}
_ATTEMPT_COLUMNS = {
    "job_id", "attempt_no", "state", "started_at", "finished_at", "duration_ms",
    "error_code", "safe_message", "provider_request_id",
}


class _SnapshotChanged(AppError):
    def __init__(self) -> None:
        super().__init__(ErrorCode.STATE_FAILED, "Queue state changed during read-only inspection.")


def _failure() -> AppError:
    return AppError(ErrorCode.STATE_FAILED, "Existing queue state cannot be inspected safely; it was not reset.")


def _signature(info: os.stat_result) -> tuple[int, ...]:
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_nlink


@contextmanager
def queue_snapshot(path: Path) -> Iterator[sqlite3.Connection]:
    """Inspect a bounded private copy, including WAL, without SQLite touching the original."""
    reject_reparse_components(path)
    try:
        files = []
        total = 0
        for suffix in ("", "-wal", "-shm", "-journal"):
            source = Path(str(path) + suffix)
            if not os.path.lexists(source):
                if not suffix:
                    raise _failure()
                continue
            info = private_file_stat(source)
            total += info.st_size
            if total > _MAX_SNAPSHOT_BYTES or (suffix == "-journal" and info.st_size):
                raise _failure()
            files.append((source, info, suffix))
        with tempfile.TemporaryDirectory(prefix="tcfcomic-state-inspection-") as folder:
            copy = Path(folder) / "state.db"
            for source, before, suffix in files:
                # SQLite reconstructs the shared-memory index from the copied WAL.
                if suffix == "-shm":
                    continue
                with source.open("rb") as src, Path(str(copy) + suffix).open("xb") as dst:
                    if _signature(os.fstat(src.fileno())) != _signature(before):
                        raise _SnapshotChanged()
                    remaining = before.st_size
                    while remaining:
                        chunk = src.read(min(1024 * 1024, remaining))
                        if not chunk:
                            raise _SnapshotChanged()
                        dst.write(chunk)
                        remaining -= len(chunk)
                    if src.read(1) or _signature(os.fstat(src.fileno())) != _signature(before):
                        raise _SnapshotChanged()
            for source, before, suffix in files:
                try:
                    current = private_file_stat(source)
                except FileNotFoundError:
                    raise _SnapshotChanged() from None
                if suffix != "-shm" and _signature(current) != _signature(before):
                    raise _SnapshotChanged()
            connection = sqlite3.connect(copy, isolation_level=None)
            connection.row_factory = sqlite3.Row
            started = time.monotonic()
            connection.set_progress_handler(lambda: int(time.monotonic() - started > 10), 10000)
            try:
                if connection.execute("PRAGMA quick_check(1)").fetchone()[0] != "ok":
                    raise _failure()
                yield connection
            finally:
                connection.close()
    except (OSError, sqlite3.Error, ValueError, TypeError, OverflowError):
        raise _failure() from None


def inspect_layout(connection: sqlite3.Connection, *, allow_legacy_partial: bool = False) -> bool:
    jobs = {row["name"] for row in connection.execute("PRAGMA table_info(jobs)")}
    attempts = {row["name"] for row in connection.execute("PRAGMA table_info(attempts)")}
    named = "variant" in jobs
    if allow_legacy_partial and not jobs and (
        _ATTEMPT_COLUMNS <= attempts <= _ATTEMPT_COLUMNS | {"retry_after_seconds"}
    ):
        return False
    if jobs != (_JOB_COLUMNS | ({"variant"} if named else set())) or not (
        _ATTEMPT_COLUMNS <= attempts <= _ATTEMPT_COLUMNS | {"retry_after_seconds"}
    ):
        raise _failure()
    expected = ["source_path_key", "size", "mtime_ns", "sha256"] + (["variant"] if named else [])
    unique = []
    for index in connection.execute("SELECT name FROM pragma_index_list('jobs') WHERE \"unique\" = 1"):
        columns = [row["name"] for row in connection.execute("SELECT name FROM pragma_index_info(?)", (index["name"],))]
        unique.append(columns)
    if expected not in unique or (named and expected[:-1] in unique):
        raise _failure()
    return named


def preflight_state(path: Path, *, named: bool = False, allow_legacy_partial: bool = False) -> None:
    reject_reparse_components(path)
    if not os.path.lexists(path):
        if any(os.path.lexists(str(path) + suffix) for suffix in ("-wal", "-shm", "-journal")):
            raise _failure()
        return
    for attempt in range(50):
        try:
            with queue_snapshot(path) as connection:
                actual = inspect_layout(connection, allow_legacy_partial=allow_legacy_partial and not named)
                if named != actual:
                    raise AppError(
                        ErrorCode.STATE_FAILED,
                        "Queue mode does not match configuration. Use a new destination or watch --reset-state "
                        "to archive history explicitly; existing databases are never upgraded for named prompts.",
                    )
            return
        except _SnapshotChanged:
            if attempt == 49:
                raise
            time.sleep(0.01)


def reset_deadline(path: Path, requests_per_minute: int | None, now: datetime) -> datetime | None:
    with queue_snapshot(path) as connection:
        inspect_layout(connection)
        columns = {row["name"] for row in connection.execute("PRAGMA table_info(attempts)")}
        cooldown_column = "retry_after_seconds" if "retry_after_seconds" in columns else "NULL"
        deadline = None
        for count, row in enumerate(connection.execute(
            f"SELECT started_at, finished_at, {cooldown_column} AS cooldown FROM attempts"
        )):
            if count >= 1_000_000:
                raise _failure()
            start = datetime.fromisoformat(row["started_at"])
            finish = datetime.fromisoformat(row["finished_at"]) if row["finished_at"] else max(start, now)
            if start.tzinfo is None or finish.tzinfo is None or finish < start:
                raise _failure()
            cooldown = row["cooldown"]
            validate_retry_after(cooldown)
            spacing = 60 / requests_per_minute if requests_per_minute else 0
            delay = max(spacing, cooldown or 0)
            if delay:
                candidate = finish + timedelta(microseconds=math.ceil(delay * 1_000_000))
                deadline = max(deadline, candidate) if deadline else candidate
        return deadline
