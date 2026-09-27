from __future__ import annotations

import hashlib
import logging
import os
import sqlite3
import subprocess
import sys
import threading
from contextlib import closing
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
import yaml
from PIL import Image

from conftest import write_image, write_mpo
from test_authentication_heartbeat import AuthenticatedRunner, PollingConnection, simulated_broker
from test_named_prompts_reset import admit_variant, named_config
from test_rate_pacing import ManualTime, TimedProvider
from test_reset_state import populate
from tcfcomic import cli, runtime
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode, JobStatus, ProviderResult
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.runtime import DestinationSession
from tcfcomic.scheduler import SchedulerClock


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def rows(db, query):
    with closing(sqlite3.connect(f"{db.as_uri()}?mode=ro", uri=True)) as connection:
        return connection.execute(query).fetchall()


@pytest.fixture
def offline_cli(tmp_path):
    guard = tmp_path / "offline-python"
    guard.mkdir()
    (guard / "sitecustomize.py").write_text(
        "import socket, webbrowser\n"
        "def forbidden(*args, **kwargs):\n"
        "    raise AssertionError('network/browser forbidden in synthetic CLI test')\n"
        "socket.socket.connect = forbidden\n"
        "socket.socket.connect_ex = forbidden\n"
        "socket.getaddrinfo = forbidden\n"
        "webbrowser.open = forbidden\n",
        encoding="utf-8",
    )
    env = os.environ.copy()
    env["PYTHONPATH"] = os.pathsep.join((str(guard), str(Path(__file__).parents[2] / "src")))

    def run(*args, timeout=75):
        return subprocess.run(
            [sys.executable, "-m", "tcfcomic", *map(str, args)],
            cwd=tmp_path, env=env, capture_output=True, text=True,
            encoding="utf-8", timeout=timeout, check=False,
        )

    return run


def test_real_cli_named_outputs_restart_source_and_completion_rate(tmp_path, offline_cli):
    path, config = named_config(tmp_path)
    image = write_image(config.paths.source / "original.png")
    original_hash = digest(image)
    config_bytes = path.read_bytes()
    first = offline_cli("process", "--config", path, image)
    assert first.returncode == 0, first.stderr
    outputs = [Path(line) for line in first.stdout.splitlines()]
    assert len(outputs) == len(set(outputs)) == 2
    for output, variant in zip(outputs, config.provider.variants):
        assert output.parent == config.paths.destination and variant in output.name
        with Image.open(output) as rendered:
            assert rendered.format == "PNG"
            assert rendered.getpixel((0, 0)) == tuple(bytes.fromhex(original_hash[:6]))
    saved = {output: digest(output) for output in outputs}
    db = config.paths.destination / ".tcfcomic" / "state.db"
    jobs = rows(db, "SELECT job_id, variant, sha256, status, prompt_hash FROM jobs ORDER BY rowid")
    assert len({job[0] for job in jobs}) == 2
    assert [job[1] for job in jobs] == list(config.provider.variants)
    assert all(job[2:4] == (original_hash, "SUCCEEDED") for job in jobs)
    assert [job[4] for job in jobs] == [
        hashlib.sha256(prompt.encode()).hexdigest() for prompt in config.provider.prompts.values()
    ]
    attempts = rows(db, "SELECT started_at, finished_at FROM attempts ORDER BY started_at")
    assert len(attempts) == 2
    assert (datetime.fromisoformat(attempts[1][0]) -
            datetime.fromisoformat(attempts[0][1])).total_seconds() >= 30
    restarted = offline_cli("process", "--config", path, image)
    assert restarted.returncode == 0, restarted.stderr
    assert restarted.stdout == first.stdout
    assert rows(db, "SELECT job_id, variant, sha256, status, prompt_hash FROM jobs ORDER BY rowid") == jobs
    assert rows(db, "SELECT started_at, finished_at FROM attempts ORDER BY started_at") == attempts
    assert set(config.paths.destination.glob("*.png")) == set(outputs)
    assert {output: digest(output) for output in outputs} == saved
    assert digest(image) == original_hash and path.read_bytes() == config_bytes
    for prompt in config.provider.prompts.values():
        assert prompt not in first.stdout + first.stderr + restarted.stdout + restarted.stderr


def test_cli_reset_archives_named_history_then_two_fresh_outputs(
    tmp_path, quiet_logger, monkeypatch, capsys,
):
    path, config = named_config(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["watch"]["poll_interval_seconds"] = 10
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    config = load_config(path)
    image = write_image(config.paths.source / "original.png")
    timer = ManualTime()
    provider = TimedProvider(timer, duration=7)
    with Processor(config, quiet_logger, clock=timer.clock(),
                   runner=InProcessAttemptRunner(provider)) as processor:
        original_results = processor.process_variants(image)
    old_outputs = {result.output_path: digest(result.output_path) for result in original_results}
    original_hash = digest(image)
    db = config.paths.destination / ".tcfcomic" / "state.db"
    old_db = db.read_bytes()
    original_ids = {result.job_id for result in original_results}

    class BoundedProcessor(Processor):
        def __init__(self, cfg, logger, **kwargs):
            super().__init__(cfg, logger, clock=timer.clock(),
                             runner=InProcessAttemptRunner(provider), **kwargs)

        def watch(self, **kwargs):
            return super().watch(max_cycles=8, **kwargs)

    monkeypatch.setattr(cli, "Processor", BoundedProcessor)
    monkeypatch.setattr(runtime, "SchedulerClock", timer.clock)
    assert cli.main(["watch", "--config", str(path), "--reset-state"]) == 0
    assert capsys.readouterr().out == ""
    backup, = config.paths.destination.glob(".tcfcomic-backup-*")
    assert (backup / "state.db").read_bytes() == old_db
    assert digest(image) == original_hash
    assert {output: digest(output) for output in old_outputs} == old_outputs
    fresh = rows(db, "SELECT job_id, variant, status, output_name FROM jobs ORDER BY rowid")
    assert len(fresh) == 2 and {job[0] for job in fresh}.isdisjoint(original_ids)
    assert [job[1] for job in fresh] == list(config.provider.variants)
    assert all(job[2] == "SUCCEEDED" for job in fresh)
    assert len(rows(db, "SELECT * FROM attempts")) == 2
    assert len(list(config.paths.destination.glob("*.png"))) == 4
    for _, variant, _, name in fresh:
        output = config.paths.destination / name
        assert variant in name and output not in old_outputs
        with Image.open(output) as rendered:
            rendered.verify()
    assert len(provider.starts) == 4
    assert all(start - finish >= 30 for start, finish in zip(provider.starts[1:], provider.finishes))


@pytest.mark.parametrize("lock_kind", ["outer", "legacy-inner"])
def test_real_cli_reset_refuses_other_process_without_archival(
    tmp_path, quiet_logger, offline_cli, lock_kind,
):
    path, config = named_config(tmp_path)
    with Processor(config, quiet_logger):
        pass
    db = config.paths.destination / ".tcfcomic" / "state.db"
    before = digest(db)
    lock = (config.paths.destination / ".tcfcomic-coordination.lock" if lock_kind == "outer"
            else db.parent / "runtime.lock")
    code = (
        "import sys; from pathlib import Path; from tcfcomic.runtime import RuntimeLock; "
        "lock=RuntimeLock(Path(sys.argv[1])); lock.acquire(); print('locked', flush=True); "
        "sys.stdin.readline(); lock.release()"
    )
    child = subprocess.Popen(
        [sys.executable, "-c", code, str(lock)], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    try:
        assert child.stdout.readline().strip() == "locked"
        refused = offline_cli("watch", "--config", path, "--reset-state", timeout=10)
        assert refused.returncode == 6 and refused.stdout == "", refused.stderr
        assert "already using this destination" in refused.stderr
        assert child.poll() is None
        assert digest(db) == before
        assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))
    finally:
        child.communicate("\n", timeout=10)
    assert child.returncode == 0
    with DestinationSession(config.paths.destination) as session:
        session.preflight(named=True)


@pytest.mark.parametrize("old_named", [False, True])
def test_real_cli_mode_mismatch_leaves_database_bytes_and_schema_unchanged(
    tmp_path, quiet_logger, offline_cli, old_named,
):
    path, config = named_config(tmp_path)
    legacy = replace(config, provider=replace(config.provider, prompts=None))
    with Processor(config if old_named else legacy, quiet_logger):
        pass
    db = config.paths.destination / ".tcfcomic" / "state.db"
    before = digest(db)
    schema = rows(db, "SELECT sql FROM sqlite_master ORDER BY name")
    if old_named:
        data = yaml.safe_load(path.read_text())
        del data["provider"]["prompts"]
        path.write_text(yaml.safe_dump(data), encoding="utf-8")
    config_bytes = path.read_bytes()
    source = write_image(config.paths.source / "original.png")
    result = offline_cli("process", "--config", path, source)
    assert result.returncode == 6 and result.stdout == "", result.stderr
    assert "--reset-state" in result.stderr
    assert digest(db) == before
    assert rows(db, "SELECT sql FROM sqlite_master ORDER BY name") == schema
    assert path.read_bytes() == config_bytes
    assert not list(config.paths.destination.glob("*.png"))
    assert not list(config.paths.destination.glob(".tcfcomic-backup-*"))


@pytest.mark.parametrize("cancel", [False, True])
def test_prior_429_survives_mode_change_and_disabled_current_rate_cap(
    tmp_path, quiet_logger, cancel,
):
    _, named = named_config(tmp_path)
    legacy = replace(named, provider=replace(named.provider, prompts=None))
    timer = ManualTime()
    populate(legacy, quiet_logger, timer, outcome=95)
    changed = replace(named, provider=replace(named.provider, requests_per_minute=None))
    db = named.paths.destination / ".tcfcomic" / "state.db"
    old = db.read_bytes()
    event = threading.Event()

    def waiting(event, delay):
        assert db.read_bytes() == old
        assert not list(named.paths.destination.glob(".tcfcomic-backup-*"))
        if cancel:
            event.set()

    timer.on_wait = waiting
    if cancel:
        with pytest.raises(AppError) as caught:
            with DestinationSession(named.paths.destination) as session:
                session.reset(changed, event, clock=timer.clock(), report=lambda _: None)
        assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
        assert timer.elapsed == 7 and db.read_bytes() == old
        assert not list(named.paths.destination.glob(".tcfcomic-backup-*"))
    else:
        with DestinationSession(named.paths.destination) as session:
            backup = session.reset(changed, event, clock=timer.clock(), report=lambda _: None)
        assert timer.elapsed == 102
        assert (backup / "state.db").read_bytes() == old and not db.exists()


def test_partial_cli_explicit_retry_uses_current_slot_prompt_and_never_replays_success(
    tmp_path, quiet_logger, monkeypatch, capsys,
):
    path, config = named_config(tmp_path)
    image = write_mpo(config.paths.source / "original.JPG")
    source_hash = digest(image)
    timer = ManualTime()
    records = []
    colors = {"tcf-school": (210, 20, 30), "pakistani-80s": (20, 30, 210)}

    class IdentityProvider:
        def transform(self, request, output):
            records.append((request.variant, request.prompt, request.job_id,
                            request.source.staged_path, digest(request.source.staged_path)))
            with Image.new("RGB", (8, 8), colors[request.variant]) as rendered:
                rendered.save(output, format="PNG")
            return ProviderResult("offline-identity", output.tell(), "image/png")

    provider = IdentityProvider()
    with Processor(config, quiet_logger, clock=timer.clock(),
                   runner=InProcessAttemptRunner(provider)) as processor:
        first = admit_variant(processor, image, "tcf-school")
        with monkeypatch.context() as patch:
            def reject(*args, **kwargs):
                raise AppError(ErrorCode.INVALID_IMAGE, "Historical synthetic input rejection.")
            patch.setattr("tcfcomic.processor.validate_input", reject)
            second = admit_variant(processor, image, "pakistani-80s")
        assert first.status == JobStatus.READY and second.status == JobStatus.FAILED
        assert processor.state.attempt_count(second.job_id) == 0

    class OfflineProcessor(Processor):
        def __init__(self, cfg, logger, **kwargs):
            super().__init__(cfg, logger, clock=timer.clock(),
                             runner=InProcessAttemptRunner(provider), **kwargs)

    monkeypatch.setattr(cli, "Processor", OfflineProcessor)
    # Same-identity MPO repair is intentionally automatic; changed settings require the flag.
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"]["pakistani-80s"] = "Intermediate second-slot settings."
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    args = ["process", "--config", str(path), str(image)]
    assert cli.main(args) == 4
    first_lines = capsys.readouterr().out.splitlines()
    assert len(first_lines) == 1 and "tcf-school" in Path(first_lines[0]).name
    successful = Path(first_lines[0])
    saved = digest(successful)
    changed = {"tcf-school": "Changed completed slot must not run.\n\n",
               "pakistani-80s": "Current second slot.\n\nKeep its own paragraphs.\n"}
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = changed
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    assert cli.main(args) == 4
    assert capsys.readouterr().out.splitlines() == first_lines
    assert len(records) == 1
    assert cli.main([*args, "--retry-input-rejection"]) == 0
    captured = capsys.readouterr()
    outputs = [Path(line) for line in captured.out.splitlines()]
    assert len(outputs) == 2 and outputs[0] == successful
    assert [(record[0], record[1], record[2]) for record in records] == [
        ("tcf-school", config.provider.prompts["tcf-school"], first.job_id),
        ("pakistani-80s", changed["pakistani-80s"], second.job_id),
    ]
    assert len({record[3] for record in records}) == 2
    assert all(record[4] == source_hash for record in records)
    for output, variant in zip(outputs, config.provider.variants):
        assert variant in output.name
        with Image.open(output) as rendered:
            assert rendered.getpixel((0, 0)) == colors[variant]
    assert digest(successful) == saved and digest(image) == source_hash
    assert cli.main([*args, "--retry-input-rejection"]) == 0
    assert capsys.readouterr().out.splitlines() == list(map(str, outputs))
    assert len(records) == 2 and len(list(config.paths.destination.glob("*.png"))) == 2
    for prompt in (*config.provider.prompts.values(), *changed.values()):
        assert prompt not in captured.out + captured.err


def test_pending_variants_own_distinct_original_stages(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    image = write_image(config.paths.source / "original.png")
    timer = ManualTime()
    provider = FakeProvider()
    with Processor(config, quiet_logger, clock=timer.clock(),
                   runner=InProcessAttemptRunner(provider)) as processor:
        admitted = [admit_variant(processor, image, variant) for variant in config.provider.variants]
        jobs = [processor.state.get_job(result.job_id) for result in admitted]
        assert len({job.staged_path for job in jobs}) == 2
        assert all(digest(Path(job.staged_path)) == digest(image) for job in jobs)
        first = processor._run_until_terminal(jobs[0].job_id, threading.Event())
        assert first.status == JobStatus.SUCCEEDED
        assert not Path(jobs[0].staged_path).exists()
        assert digest(Path(jobs[1].staged_path)) == digest(image)
        assert processor.state.attempt_count(jobs[1].job_id) == 0
        second = processor._run_until_terminal(jobs[1].job_id, threading.Event())
        assert second.status == JobStatus.SUCCEEDED
        assert [request.prompt for request in provider.requests] == list(config.provider.prompts.values())


@pytest.mark.parametrize("outcome", ["success", "cancel", "timeout"])
def test_named_authentication_heartbeat_keeps_both_slots_unclaimed_until_token(
    tmp_path, quiet_logger, monkeypatch, outcome,
):
    _, config = named_config(tmp_path)
    config = replace(config, provider=replace(
        config.provider, name="azure_openai", endpoint="https://offline.openai.azure.com",
        authentication="interactive",
    ))
    connection = PollingConnection(45.05 if outcome == "success" else 100)
    clock = SchedulerClock(
        utc=lambda: datetime(2030, 1, 1, tzinfo=UTC) + timedelta(seconds=connection.elapsed),
        monotonic=lambda: connection.elapsed,
    )
    event = threading.Event()
    provider = FakeProvider()
    beats = []
    callbacks = []

    class Heartbeats(logging.Handler):
        def emit(self, record):
            if getattr(record, "event", None) == "watch_heartbeat":
                beats.append(record)

    def previous_callback():
        callbacks.append(connection.elapsed)
        if outcome == "cancel" and connection.elapsed >= 30:
            event.set()

    handler = Heartbeats()
    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(handler)
    try:
        with simulated_broker(config, connection, monkeypatch) as session:
            session.on_wait = previous_callback
            with Processor(config, quiet_logger, clock=clock, auth_session=session,
                           runner=AuthenticatedRunner(provider)) as processor:
                image = write_image(config.paths.source / "original.png")
                admitted = [admit_variant(processor, image, variant)
                            for variant in config.provider.variants]
                before = [processor.state.get_job(result.job_id) for result in admitted]

                def unclaimed():
                    assert [processor.state.get_job(job.job_id) for job in before] == before
                    assert all(processor.state.attempt_count(job.job_id) == 0 for job in before)
                    assert provider.requests == []

                connection.check = unclaimed
                if outcome == "success":
                    assert processor._dispatch_one(event, before[0].job_id)
                    assert processor.state.get_job(before[0].job_id).status == JobStatus.SUCCEEDED
                    assert processor.state.attempt_count(before[0].job_id) == 1
                    assert processor.state.get_job(before[1].job_id) == before[1]
                    assert processor.state.attempt_count(before[1].job_id) == 0
                    assert len(provider.requests) == 1
                    assert provider.requests[0].prompt == config.provider.prompts[before[0].variant]
                    assert digest(Path(before[1].staged_path)) == digest(image)
                else:
                    with pytest.raises(AppError) as caught:
                        processor._dispatch_one(event, before[0].job_id)
                    assert caught.value.code == (
                        ErrorCode.SHUTDOWN_INTERRUPTED if outcome == "cancel"
                        else ErrorCode.AUTHENTICATION_FAILED
                    )
                    unclaimed()
                    assert connection.closed and not session.started
                assert session.on_wait is previous_callback and callbacks
                assert [beat.duration_ms for beat in beats] == (
                    [15000, 30000] if outcome == "cancel" else [15000, 30000, 45000]
                )
                assert all(beat.result == "authentication" and beat.pending_count == 2 for beat in beats)
                assert all(not hasattr(beat, "attempt_no") for beat in beats)
    finally:
        quiet_logger.removeHandler(handler)
