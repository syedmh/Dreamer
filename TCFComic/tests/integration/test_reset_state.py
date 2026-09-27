from __future__ import annotations

import os
import signal
import sqlite3
import subprocess
import sys
import threading
from contextlib import closing
from dataclasses import replace
from pathlib import Path

import pytest
import yaml

from conftest import write_config, write_image
from test_named_prompts_reset import named_config
from test_rate_pacing import ManualTime, TimedProvider
from tcfcomic import cli, runtime
from tcfcomic.domain import AppError, ErrorCode
from tcfcomic.processor import Processor, RuntimeLock
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.runtime import DestinationSession
from tcfcomic.state import StateStore
from tcfcomic.state_inspection import preflight_state


def populate(config, logger, timer, *, outcome="success"):
    config = replace(config, provider=replace(config.provider, requests_per_minute=2),
                     retry=replace(config.retry, max_attempts=1))
    image = write_image(config.paths.source / "original.png")
    provider = TimedProvider(timer, duration=7, outcomes=[outcome])
    with Processor(config, logger, clock=timer.clock(), runner=InProcessAttemptRunner(provider)) as processor:
        processor.process_path(image)
    return config


@pytest.mark.parametrize("cooldown,elapsed,expected", [("success", 5, 30), (95, 10, 95), (0, 0, 30)])
def test_reset_waits_from_completion_before_archive_and_uses_monotonic_clock(
    tmp_path, quiet_logger, cooldown, elapsed, expected,
):
    _, config = write_config(tmp_path)
    timer = ManualTime()
    config = populate(config, quiet_logger, timer, outcome=cooldown)
    timer.advance(elapsed)
    db = config.paths.destination / ".tcfcomic" / "state.db"
    old = db.read_bytes()
    reports = []
    timer.on_wait = lambda event, delay: setattr(timer, "wall", timer.wall.replace(year=2040))
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), clock=timer.clock(), report=reports.append)
        assert (backup / "state.db").read_bytes() == old
        assert timer.elapsed == 7 + expected
        assert not db.exists()
        assert all("history has not been archived" in line for line in reports[:-1])
    with StateStoreContext(backup / "state.db") as db_copy:
        assert db_copy.execute("PRAGMA integrity_check").fetchone()[0] == "ok"


class StateStoreContext:
    """Read archived synthetic state without preparing/migrating it."""
    def __init__(self, path):
        self.path = path
    def __enter__(self):
        self.connection = sqlite3.connect(f"{self.path.as_uri()}?mode=ro", uri=True)
        return self.connection
    def __exit__(self, *args):
        self.connection.close()


def test_reset_cancel_keeps_archive_absent_and_releases_all_locks(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    timer = ManualTime()
    config = populate(config, quiet_logger, timer, outcome=120)
    old = (config.paths.destination / ".tcfcomic" / "state.db").read_bytes()
    timer.on_wait = lambda event, delay: event.set()
    with pytest.raises(AppError) as caught:
        with DestinationSession(config.paths.destination) as session:
            session.reset(config, threading.Event(), clock=timer.clock(), report=lambda _: None)
    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert (config.paths.destination / ".tcfcomic" / "state.db").read_bytes() == old
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))
    with DestinationSession(config.paths.destination) as session:
        session.preflight(named=False)


@pytest.mark.parametrize("kind", ["new", "old-inner", "outer"])
def test_reset_refuses_active_destination(tmp_path, quiet_logger, kind):
    _, config = write_config(tmp_path)
    if kind == "new":
        owner = Processor(config, quiet_logger)
    else:
        name = ".tcfcomic\\runtime.lock" if kind == "old-inner" else ".tcfcomic-coordination.lock"
        owner = RuntimeLock(config.paths.destination / name)
        owner.acquire()
    try:
        with pytest.raises(AppError, match="already using"):
            with DestinationSession(config.paths.destination) as session:
                session.reset(config, threading.Event())
        assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))
    finally:
        owner.close() if kind == "new" else owner.release()
    with DestinationSession(config.paths.destination) as session:
        session.lock_internal()


def test_old_binary_lock_in_another_process_is_respected(tmp_path):
    _, config = write_config(tmp_path)
    lock = config.paths.destination / ".tcfcomic" / "runtime.lock"
    code = (
        "import sys; from pathlib import Path; from tcfcomic.processor import RuntimeLock; "
        "lock=RuntimeLock(Path(sys.argv[1])); lock.acquire(); print('locked',flush=True); "
        "sys.stdin.readline(); lock.release()"
    )
    child = subprocess.Popen([sys.executable, "-c", code, str(lock)], stdin=subprocess.PIPE,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    try:
        assert child.stdout.readline().strip() == "locked"
        with pytest.raises(AppError):
            with DestinationSession(config.paths.destination) as session:
                session.reset(config, threading.Event())
        assert child.poll() is None
    finally:
        child.communicate("\n", timeout=10)
    assert child.returncode == 0
    with DestinationSession(config.paths.destination) as session:
        session.lock_internal()


@pytest.mark.parametrize("target", ["state.db", "runtime.lock", "state.db-wal", "outer"])
def test_reset_rejects_hardlinked_state_or_lock(tmp_path, quiet_logger, target):
    _, config = write_config(tmp_path)
    with Processor(config, quiet_logger):
        pass
    internal = config.paths.destination / ".tcfcomic"
    target_path = config.paths.destination / ".tcfcomic-coordination.lock" if target == "outer" else internal / target
    if not target_path.exists():
        target_path.write_bytes(b"unsafe")
    evidence = tmp_path / "unrelated-evidence"
    os.link(target_path, evidence)
    old = evidence.read_bytes()
    with pytest.raises(AppError):
        with DestinationSession(config.paths.destination) as session:
            session.reset(config, threading.Event())
    assert evidence.read_bytes() == old
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))


@pytest.mark.parametrize("kind", ["garbage", "unknown", "missing", "invalid-cooldown", "invalid-date", "naive-date"])
def test_unknown_state_never_silently_discards_history(tmp_path, quiet_logger, kind):
    _, config = write_config(tmp_path)
    timer = ManualTime()
    config = populate(config, quiet_logger, timer)
    internal = config.paths.destination / ".tcfcomic"
    db = internal / "state.db"
    if kind == "garbage":
        db.write_bytes(b"not sqlite")
    elif kind == "missing":
        db.unlink()
        (internal / "evidence.txt").write_text("history whose queue is missing")
    else:
        with closing(sqlite3.connect(db)) as connection, connection:
            if kind == "unknown":
                connection.execute("ALTER TABLE jobs ADD COLUMN unknown TEXT")
            elif kind == "invalid-cooldown":
                connection.execute("UPDATE attempts SET retry_after_seconds = 86401")
            elif kind == "invalid-date":
                connection.execute("UPDATE attempts SET finished_at = 'invalid'")
            else:
                connection.execute("UPDATE attempts SET started_at = '2030-01-01T00:00:00'")
    before = db.read_bytes() if db.exists() else None
    with pytest.raises(AppError):
        with DestinationSession(config.paths.destination) as session:
            session.reset(config, threading.Event(), clock=timer.clock())
    assert internal.is_dir()
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))
    assert (db.read_bytes() if db.exists() else None) == before


def test_reset_legacy_attempts_without_retry_column_and_empty_fresh_state(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    timer = ManualTime()
    config = populate(config, quiet_logger, timer)
    db = config.paths.destination / ".tcfcomic" / "state.db"
    with closing(sqlite3.connect(db)) as connection, connection:
        connection.execute("ALTER TABLE attempts DROP COLUMN retry_after_seconds")
    before = db.read_bytes()
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), clock=timer.clock(), report=lambda _: None)
        assert timer.elapsed == 37
        assert (backup / "state.db").read_bytes() == before
        fresh = replace(config, provider=replace(config.provider, prompts={"one": "first", "two": "second"}))
        with Processor(fresh, quiet_logger, runtime=session) as processor:
            assert processor.state.list_jobs() == []
            assert processor.state.named
    with StateStoreContext(backup / "state.db") as connection:
        assert "retry_after_seconds" not in {row[1] for row in connection.execute("PRAGMA table_info(attempts)")}


def test_reset_keeps_custom_quarantine_and_all_internal_evidence(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    custom = config.paths.destination / "custom-evidence"
    custom.mkdir()
    config = replace(config, paths=replace(config.paths, quarantine=custom))
    with Processor(config, quiet_logger):
        pass
    internal = config.paths.destination / ".tcfcomic"
    (internal / "logs").mkdir()
    (internal / "logs" / "old.log").write_bytes(b"log evidence")
    (internal / "staging" / "old.input").write_bytes(b"synthetic stage")
    (custom / "audit.json").write_bytes(b"custom evidence")
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), report=lambda _: None)
    assert (backup / "logs" / "old.log").read_bytes() == b"log evidence"
    assert (backup / "staging" / "old.input").read_bytes() == b"synthetic stage"
    assert (custom / "audit.json").read_bytes() == b"custom evidence"


@pytest.mark.parametrize("internal_exists", [True, False])
def test_empty_or_absent_state_reset_has_no_fresh_children(tmp_path, internal_exists):
    _, config = write_config(tmp_path)
    internal = config.paths.destination / ".tcfcomic"
    if not internal_exists:
        config.paths.quarantine.rmdir()
        internal.rmdir()
    reports = []
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), report=reports.append)
    assert bool(backup) == internal_exists
    assert not internal.exists()
    assert reports


def test_rename_failure_retains_history_and_next_reset_succeeds(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    with Processor(config, quiet_logger):
        pass
    internal = config.paths.destination / ".tcfcomic"
    before = (internal / "state.db").read_bytes()
    original = runtime.os.rename
    def denied(source, target):
        if source == internal:
            raise PermissionError("simulated Windows open handle")
        return original(source, target)
    with monkeypatch.context() as patch:
        patch.setattr(runtime.os, "rename", denied)
        with pytest.raises(AppError, match="could not be archived"):
            with DestinationSession(config.paths.destination) as session:
                session.reset(config, threading.Event())
    assert (internal / "state.db").read_bytes() == before
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), report=lambda _: None)
    assert (backup / "state.db").read_bytes() == before


def test_backup_collision_never_overwrites_or_leaves_reserved_child(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    with Processor(config, quiet_logger):
        pass
    timer = ManualTime()
    from types import SimpleNamespace
    values = iter(["a" * 32, "b" * 32])
    monkeypatch.setattr(runtime.uuid, "uuid4", lambda: SimpleNamespace(hex=next(values)))
    existing = config.paths.destination / ".tcfcomic-backup-20300101T000000-aaaaaaaaaaaa"
    existing.mkdir()
    (existing / "preserve").write_bytes(b"existing")
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), clock=timer.clock(), report=lambda _: None)
    assert backup.name.endswith("bbbbbbbbbbbb")
    assert (existing / "preserve").read_bytes() == b"existing"
    assert len(list(config.paths.destination.glob(".tcfcomic-backup-*"))) == 2


def test_named_legacy_preflight_before_browser_logging_and_state_writes(tmp_path, quiet_logger, monkeypatch, capsys):
    path, legacy = write_config(tmp_path)
    with Processor(legacy, quiet_logger):
        pass
    internal = legacy.paths.destination / ".tcfcomic"
    before = (internal / "state.db").read_bytes()
    data = yaml.safe_load(path.read_text())
    data["provider"].update(
        name="azure_openai", endpoint="https://offline.openai.azure.com",
        authentication="interactive", prompts={"one": "private prompt"},
    )
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    def forbidden(*args, **kwargs):
        pytest.fail("Mode mismatch must precede logging, authentication and Processor")
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    monkeypatch.setattr(cli, "configure_logging", forbidden)
    monkeypatch.setattr(cli, "Processor", forbidden)
    for command in (["watch"], ["process", "image.png"]):
        assert cli.main([*command, "--config", str(path)]) == 6
    assert "--reset-state" in capsys.readouterr().err
    assert not (internal / "logs").exists()
    assert (internal / "state.db").read_bytes() == before
    assert cli.main(["validate", "--config", str(path)]) == 0
    assert (internal / "state.db").read_bytes() == before


def test_cli_reset_finishes_before_logging_and_offline_watch(tmp_path, quiet_logger, monkeypatch, capsys):
    path, config = named_config(tmp_path)
    legacy = replace(config, provider=replace(config.provider, prompts=None, requests_per_minute=None))
    with Processor(legacy, quiet_logger):
        pass
    old = (config.paths.destination / ".tcfcomic" / "state.db").read_bytes()
    events = []
    original_logging = cli.configure_logging
    def logging_after_archive(*args):
        assert list(config.paths.destination.glob(".tcfcomic-backup-*/state.db"))
        events.append("logging")
        return original_logging(*args)
    class OfflineWatch(Processor):
        def watch(self, **kwargs):
            assert self.state.list_jobs() == []
            assert self.state.named
            events.append("watch")
    monkeypatch.setattr(cli, "configure_logging", logging_after_archive)
    monkeypatch.setattr(cli, "Processor", OfflineWatch)
    assert cli.main(["watch", "--config", str(path), "--reset-state"]) == 0
    assert events == ["logging", "watch"]
    assert capsys.readouterr().out == ""
    backups = list(config.paths.destination.glob(".tcfcomic-backup-*/state.db"))
    assert len(backups) == 1 and backups[0].read_bytes() == old
    # CLI owns and closes every log/DB/lock handle: immediate re-archive works on Windows.
    with DestinationSession(config.paths.destination) as session:
        assert session.reset(config, threading.Event(), report=lambda _: None)


def test_reset_cli_signal_restores_handlers_without_archive_or_browser(tmp_path, quiet_logger, monkeypatch):
    path, config = write_config(tmp_path)
    timer = ManualTime()
    config = populate(config, quiet_logger, timer, outcome=60)
    monkeypatch.setattr(cli, "load_config", lambda *args, **kwargs: config)
    original_handler = signal.getsignal(signal.SIGINT)
    def signal_wait(event, delay):
        signal.getsignal(signal.SIGINT)(signal.SIGINT, None)
        return event.is_set()
    timer.wait = signal_wait
    monkeypatch.setattr(runtime, "SchedulerClock", timer.clock)
    def forbidden(*args, **kwargs):
        pytest.fail("Cancelled reset must not start logging/authentication/watcher")
    monkeypatch.setattr(cli, "configure_logging", forbidden)
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    assert cli.main(["watch", "--config", str(path), "--reset-state"]) == 0
    assert signal.getsignal(signal.SIGINT) == original_handler
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))


def test_wal_layout_preflight_is_readonly_and_sees_uncheckpointed_schema(tmp_path):
    db = tmp_path / "state.db"
    store = StateStore(db, named=True)
    store.initialize()
    # Preserve crash-like DB/WAL bytes before closing the synthetic connection.
    main_bytes = db.read_bytes()
    wal = Path(str(db) + "-wal")
    wal_bytes = wal.read_bytes()
    store.close()
    db.write_bytes(main_bytes)
    wal.write_bytes(wal_bytes)
    before = {p.name: p.read_bytes() for p in tmp_path.iterdir()}
    with pytest.raises(AppError, match="reset-state"):
        preflight_state(db, named=False)
    preflight_state(db, named=True)
    assert {p.name: p.read_bytes() for p in tmp_path.iterdir()} == before
