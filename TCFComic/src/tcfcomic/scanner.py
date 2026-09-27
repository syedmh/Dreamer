from __future__ import annotations

import hashlib
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from .domain import AppError, ErrorCode, ShutdownToken, SourceSnapshot, StableCandidate
from .path_safety import (
    is_reparse_or_link,
    reject_reparse_components,
)

SUPPORTED_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp"}


def is_supported_path(path: Path) -> bool:
    return path.suffix.lower() in SUPPORTED_EXTENSIONS


@dataclass
class _Observation:
    size: int
    mtime_ns: int
    unchanged_since: float
    admitted_signature: tuple[int, int] | None = None


class FolderScanner:
    def __init__(
        self, source: Path, stable_seconds: float, *,
        on_observation: Callable[[StableCandidate], None] | None = None,
    ) -> None:
        self.source = source
        self.stable_seconds = stable_seconds
        self._observations: dict[str, _Observation] = {}
        self._on_observation = on_observation

    def observe(self, now_monotonic: float | None = None) -> tuple[StableCandidate, ...]:
        now = time.monotonic() if now_monotonic is None else now_monotonic
        present: set[str] = set()
        stable: list[StableCandidate] = []
        try:
            entries_context = os.scandir(self.source)
        except OSError:
            return ()
        with entries_context as entries:
            for entry in entries:
                path = Path(entry.path)
                if not is_supported_path(path):
                    continue
                try:
                    if not entry.is_file(follow_symlinks=False) or is_reparse_or_link(path):
                        continue
                    info = entry.stat(follow_symlinks=False)
                except OSError:
                    continue
                key = os.path.normcase(str(Path(os.path.abspath(path))))
                present.add(key)
                previous = self._observations.get(key)
                signature = (info.st_size, info.st_mtime_ns)
                if previous is None or (previous.size, previous.mtime_ns) != signature:
                    self._observations[key] = _Observation(*signature, unchanged_since=now)
                    if self._on_observation is not None:
                        self._on_observation(StableCandidate(path, *signature))
                    continue
                if (
                    previous.admitted_signature != signature
                    and now - previous.unchanged_since >= self.stable_seconds
                ):
                    stable.append(StableCandidate(path, info.st_size, info.st_mtime_ns))
        for key in set(self._observations) - present:
            del self._observations[key]
        return tuple(stable)

    def acknowledge(self, candidate: StableCandidate) -> None:
        key = os.path.normcase(
            str(Path(os.path.abspath(candidate.path)))
        )
        observation = self._observations.get(key)
        if observation is not None and (
            observation.size,
            observation.mtime_ns,
        ) == (candidate.size, candidate.mtime_ns):
            observation.admitted_signature = (candidate.size, candidate.mtime_ns)


def wait_until_stable(
    path: Path,
    stable_seconds: float,
    poll_interval_seconds: float,
    shutdown: ShutdownToken,
) -> StableCandidate:
    if not is_supported_path(path):
        raise AppError(ErrorCode.UNSUPPORTED_FILE, "The input file type is not supported.")
    previous: tuple[int, int] | None = None
    unchanged_since = time.monotonic()
    while True:
        if shutdown.requested():
            raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted.")
        try:
            if not path.is_file() or is_reparse_or_link(path):
                raise AppError(ErrorCode.INVALID_IMAGE, "The input is not a regular local file.")
            info = path.stat()
        except OSError:
            raise AppError(ErrorCode.INVALID_IMAGE, "The input file cannot be read.") from None
        signature = (info.st_size, info.st_mtime_ns)
        now = time.monotonic()
        if signature != previous:
            previous = signature
            unchanged_since = now
        elif now - unchanged_since >= stable_seconds:
            return StableCandidate(path, info.st_size, info.st_mtime_ns)
        if stable_seconds == 0:
            return StableCandidate(path, info.st_size, info.st_mtime_ns)
        if shutdown.wait_for(min(poll_interval_seconds, max(0.01, stable_seconds))):
            raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted.")


def _raise_if_shutdown(shutdown: ShutdownToken) -> None:
    if shutdown.requested():
        raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted.")


def stage_candidate(
    candidate: StableCandidate,
    staged_path: Path,
    max_input_bytes: int,
    shutdown: ShutdownToken,
) -> SourceSnapshot:
    _raise_if_shutdown(shutdown)
    staged_path.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256()
    copied = 0
    try:
        _raise_if_shutdown(shutdown)
        reject_reparse_components(candidate.path)
        with candidate.path.open("rb") as source, staged_path.open("xb") as target:
            _raise_if_shutdown(shutdown)
            before = os.fstat(source.fileno())
            if (before.st_size, before.st_mtime_ns) != (candidate.size, candidate.mtime_ns):
                raise AppError(ErrorCode.SOURCE_CHANGED, "The source changed before staging.")
            while True:
                _raise_if_shutdown(shutdown)
                chunk = source.read(1024 * 1024)
                if not chunk:
                    break
                copied += len(chunk)
                if copied > max_input_bytes:
                    raise AppError(
                        ErrorCode.IMAGE_LIMIT_EXCEEDED,
                        "The input image exceeds the configured byte limit.",
                    )
                digest.update(chunk)
                target.write(chunk)
            _raise_if_shutdown(shutdown)
            target.flush()
            _raise_if_shutdown(shutdown)
            _raise_if_shutdown(shutdown)
            os.fsync(target.fileno())
            _raise_if_shutdown(shutdown)
            _raise_if_shutdown(shutdown)
            after = os.fstat(source.fileno())
        _raise_if_shutdown(shutdown)
        current = candidate.path.stat()
        _raise_if_shutdown(shutdown)
        if (
            before.st_size,
            before.st_mtime_ns,
            after.st_size,
            after.st_mtime_ns,
            current.st_size,
            current.st_mtime_ns,
        ) != (
            candidate.size,
            candidate.mtime_ns,
            candidate.size,
            candidate.mtime_ns,
            candidate.size,
            candidate.mtime_ns,
        ):
            raise AppError(ErrorCode.SOURCE_CHANGED, "The source changed during staging.")
    except AppError:
        staged_path.unlink(missing_ok=True)
        raise
    except (PermissionError, OSError):
        staged_path.unlink(missing_ok=True)
        raise AppError(
            ErrorCode.SOURCE_CHANGED,
            "The input file is temporarily unavailable for staging.",
        ) from None
    except Exception:
        staged_path.unlink(missing_ok=True)
        raise
    absolute_path = Path(os.path.abspath(candidate.path))
    return SourceSnapshot(
        path=absolute_path,
        normalized_path=os.path.normcase(str(absolute_path)),
        size=copied,
        mtime_ns=candidate.mtime_ns,
        sha256=digest.hexdigest(),
        staged_path=staged_path,
    )
