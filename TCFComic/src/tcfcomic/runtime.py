from __future__ import annotations

import math
import os
import threading
import uuid
from pathlib import Path
from typing import Callable

from .config import AppConfig
from .domain import AppError, ErrorCode
from .path_safety import contained_leaf, private_file_stat, reject_reparse_components
from .scheduler import SchedulerClock
from .state_inspection import preflight_state, reset_deadline


class RuntimeLock:
    def __init__(self, path: Path) -> None:
        self.path = path
        self._stream = None

    def acquire(self) -> None:
        if self._stream is not None:
            raise AppError(ErrorCode.STATE_FAILED, "The runtime lock is already owned.")
        reject_reparse_components(self.path)
        if os.path.lexists(self.path):
            private_file_stat(self.path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        try:
            fd = os.open(self.path, os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o600)
            self._stream = os.fdopen(fd, "r+b")
            info = private_file_stat(self.path)
            opened = os.fstat(self._stream.fileno())
            if (info.st_dev, info.st_ino, info.st_nlink) != (opened.st_dev, opened.st_ino, 1):
                raise AppError(ErrorCode.STATE_FAILED, "The runtime lock path changed during acquisition.")
            if os.name == "nt":
                import msvcrt
                msvcrt.locking(self._stream.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
            if not opened.st_size:
                self._stream.write(b"\0")
                self._stream.flush()
        except BaseException as exc:
            if self._stream is not None:
                self._stream.close()
            self._stream = None
            if isinstance(exc, OSError):
                raise AppError(ErrorCode.STATE_FAILED, "Another TCFComic process is already using this destination, or its lock is unavailable.") from None
            raise

    def release(self) -> None:
        if self._stream is None:
            return
        try:
            if os.name == "nt":
                import msvcrt
                self._stream.seek(0)
                msvcrt.locking(self._stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                import fcntl
                fcntl.flock(self._stream.fileno(), fcntl.LOCK_UN)
        finally:
            self._stream.close()
            self._stream = None


class DestinationSession:
    """Outer coordination survives archival; inner lock also excludes older binaries."""

    def __init__(self, destination: Path) -> None:
        self.destination = Path(os.path.abspath(destination))
        self.internal = self.destination / ".tcfcomic"
        self.outer = RuntimeLock(contained_leaf(self.destination, ".tcfcomic-coordination.lock"))
        self.inner: RuntimeLock | None = None
        self.active = False

    def __enter__(self) -> DestinationSession:
        self.outer.acquire()
        self.active = True
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        try:
            self.release_inner()
        finally:
            self.active = False
            self.outer.release()

    def release_inner(self) -> None:
        if self.inner is not None:
            self.inner.release()
            self.inner = None

    def lock_internal(self) -> None:
        if not self.active:
            raise AppError(ErrorCode.STATE_FAILED, "Destination coordination is not held.")
        if self.inner is None:
            lock = RuntimeLock(contained_leaf(self.internal, "runtime.lock"))
            lock.acquire()
            self.inner = lock

    def preflight(self, *, named: bool) -> None:
        # Do not create a new internal directory just to inspect a fresh destination.
        if os.path.lexists(self.internal):
            self.lock_internal()
        preflight_state(self.internal / "state.db", named=named)

    def reset(
        self, config: AppConfig, event: threading.Event, *,
        clock: SchedulerClock | None = None,
        report: Callable[[str], None] = print,
    ) -> Path | None:
        if not self.active or self.destination != Path(os.path.abspath(config.paths.destination)):
            raise AppError(ErrorCode.STATE_FAILED, "Reset requires destination coordination.")
        clock = clock or SchedulerClock()
        if event.is_set():
            raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Reset cancelled; history was not archived.")
        reject_reparse_components(self.internal)
        if not os.path.lexists(self.internal):
            report("Reset: no existing internal state to archive.")
            return None
        self.lock_internal()
        db = contained_leaf(self.internal, "state.db")
        if db.exists():
            deadline = reset_deadline(db, config.provider.requests_per_minute, clock.now())
        else:
            # An empty, never-started internal tree is harmless. Evidence without
            # its queue cannot establish that a paid request has finished cooling down.
            for root, dirs, files in os.walk(self.internal, followlinks=False):
                for name in dirs:
                    reject_reparse_components(Path(root) / name)
                for name in files:
                    if Path(root) != self.internal or name != "runtime.lock":
                        raise AppError(ErrorCode.STATE_FAILED, "Internal state has evidence but no queue database; reset refused.")
            deadline = None
        while deadline is not None:
            remaining = (deadline - clock.now()).total_seconds()
            if remaining <= 0:
                break
            report(f"Reset waiting: provider cooldown {math.ceil(remaining)}s; history has not been archived.")
            if clock.wait(event, min(remaining, config.watch.heartbeat_seconds)):
                raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Reset cancelled; history was not archived.")
        if event.is_set():
            raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Reset cancelled; history was not archived.")
        # No owned SQLite, log or inner-lock handles may survive the Windows rename.
        self.release_inner()
        reject_reparse_components(self.internal)
        for _ in range(100):
            name = f".tcfcomic-backup-{clock.now():%Y%m%dT%H%M%S}-{uuid.uuid4().hex[:12]}"
            backup = contained_leaf(self.destination, name)
            if os.path.lexists(backup):
                continue
            try:
                os.rename(self.internal, backup)
            except FileExistsError:
                continue
            except OSError:
                raise AppError(ErrorCode.STATE_FAILED, "Internal state could not be archived; no new queue was created.") from None
            report(f"Reset archived internal history to {backup.name}. Incoming images will be reprocessed with additional billable requests.")
            return backup
        raise AppError(ErrorCode.STATE_FAILED, "A unique state backup name could not be allocated.")
