from __future__ import annotations

import json
import logging
import multiprocessing
import os
import threading
import time
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from types import SimpleNamespace

import pytest

from conftest import write_config, write_image
from tcfcomic import authentication as auth
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.logging_setup import configure_logging
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner, ShutdownDeadline
from tcfcomic.redaction import redact_secret
from tcfcomic.scheduler import SchedulerClock


TOKEN = "opaque-offline-heartbeat-token"


def configuration(tmp_path):
    _, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    return replace(config, provider=replace(config.provider, authentication="interactive"))


class PollingConnection:
    """Exercise the real broker polling loop with a deterministic private pipe."""

    def __init__(self, ready_after=45.05):
        self.polls = 0
        self.ready_after = ready_after
        self.commands = []
        self.closed = False
        self.check = lambda: None

    @property
    def elapsed(self):
        return self.polls / 20

    def send_bytes(self, payload):
        self.commands.append(json.loads(payload))

    def poll(self, timeout):
        assert timeout == 0.05
        self.polls += 1
        self.check()
        return self.elapsed >= self.ready_after

    def recv_bytes(self, limit):
        assert limit == auth.MAX_MESSAGE_BYTES
        return json.dumps({
            "status": "ok", "token": TOKEN, "expires_on": int(time.time()) + 3600,
        }).encode("ascii")

    def close(self):
        self.closed = True


def simulated_broker(config, connection, monkeypatch):
    monkeypatch.setattr(auth, "time", SimpleNamespace(
        monotonic=lambda: connection.elapsed, time=time.time,
    ))
    session = auth.AuthenticationBroker(config.provider, config.shutdown.timeout_seconds)
    session._started = True
    session._connection = connection
    session._process = SimpleNamespace(pid=None, is_alive=lambda: True)
    session._secrets.enter_context(redact_secret(TOKEN))
    return session


class AuthenticatedRunner(InProcessAttemptRunner):
    def run_authenticated(self, provider, request, temp_path, deadline, token):
        assert token.token == TOKEN
        return self.run(provider, request, temp_path, deadline)


def test_broker_optional_parent_callback_and_legacy_acquire(tmp_path, monkeypatch):
    config = configuration(tmp_path)
    calls = []
    with auth.AuthenticationBroker(
        config.provider, config.shutdown.timeout_seconds,
        on_wait=lambda: calls.append(os.getpid()),
    ) as session:
        connection = PollingConnection(0.2)
        monkeypatch.setattr(auth, "time", SimpleNamespace(
            monotonic=lambda: connection.elapsed, time=time.time,
        ))
        session._started = True
        session._connection = connection
        session._process = SimpleNamespace(pid=None, is_alive=lambda: True)
        token = session.acquire(37, ShutdownDeadline(lambda: False, 1))
        assert token.token == TOKEN
        assert calls == [os.getpid()] * 3
        assert connection.commands == [{"op": "token", "minimum_lifetime": 37.0}]


@pytest.mark.parametrize("console_format", ["json", "text"])
@pytest.mark.parametrize("level", ["INFO", "WARNING"])
@pytest.mark.parametrize("outcome", ["success", "cancel", "timeout"])
def test_slow_refresh_heartbeat_before_claim(
    tmp_path, monkeypatch, capsys, console_format, level, outcome,
):
    config = configuration(tmp_path)
    config = replace(config, logging=replace(
        config.logging, console_format=console_format, level=level,
    ))
    connection = PollingConnection(45.05 if outcome == "success" else 100)
    clock = SchedulerClock(
        utc=lambda: datetime(2030, 1, 1, tzinfo=UTC) + timedelta(seconds=connection.elapsed),
        monotonic=lambda: connection.elapsed,
    )
    logger = configure_logging(config.logging, config.paths.destination)
    event = threading.Event()
    provider = FakeProvider()
    callbacks = []

    def previous_callback():
        callbacks.append((os.getpid(), connection.elapsed))
        if outcome == "cancel" and connection.elapsed >= 30:
            event.set()

    try:
        with simulated_broker(config, connection, monkeypatch) as session:
            # Assigning an existing callback also detects failure to chain/restore it.
            session.on_wait = previous_callback
            with Processor(
                config, logger, clock=clock, auth_session=session,
                runner=AuthenticatedRunner(provider),
            ) as processor:
                job = processor._admit(
                    write_image(config.paths.source / f"{TOKEN}.png"),
                    candidate=None, shutdown_event=event,
                )
                before = processor.state.get_job(job.job_id)

                def check_unclaimed():
                    assert processor.state.get_job(job.job_id) == before
                    assert processor.state.attempt_count(job.job_id) == 0
                    assert not provider.requests

                connection.check = check_unclaimed
                if outcome == "success":
                    assert processor._dispatch_one(event, job.job_id)
                    assert processor.state.get_job(job.job_id).status == JobStatus.SUCCEEDED
                    assert processor.state.attempt_count(job.job_id) == 1
                    assert len(provider.requests) == 1
                else:
                    with pytest.raises(AppError) as caught:
                        processor._dispatch_one(event, job.job_id)
                    assert caught.value.code == (
                        ErrorCode.SHUTDOWN_INTERRUPTED if outcome == "cancel"
                        else ErrorCode.AUTHENTICATION_FAILED
                    )
                    check_unclaimed()
                    assert connection.closed
                    assert not session.started
                    assert connection.elapsed == (30 if outcome == "cancel" else 60)
                assert session.on_wait is previous_callback
                assert callbacks and {pid for pid, _ in callbacks} == {os.getpid()}
                # Acquisition must not leave the subsequent ticker in the auth phase.
                if level == "INFO":
                    connection.polls += 300
                    processor._heartbeat()
        records = [
            json.loads(line) for line in
            (config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl")
            .read_text(encoding="utf-8").splitlines()
        ]
        beats = [record for record in records if record["event"] == "watch_heartbeat"]
        captured = capsys.readouterr()
        assert captured.out == ""
        assert TOKEN not in captured.err and TOKEN not in json.dumps(records)
        if level == "INFO":
            expected = [15000, 30000] if outcome == "cancel" else [15000, 30000, 45000]
            auth_beats = beats[:-1]
            assert [beat["duration_ms"] for beat in auth_beats] == expected
            assert all(beat["result"] == "authentication" for beat in auth_beats)
            assert all(beat["pending_count"] == 1 for beat in auth_beats)
            assert all("attempt_no" not in beat for beat in auth_beats)
            assert all("sign-in" in beat["message"] for beat in auth_beats)
            assert all(TOKEN not in json.dumps(beat) for beat in auth_beats)
            assert beats[-1]["result"] != "authentication"
            assert len([r for r in records if r["event"] == "authentication_wait"]) == 1
            if console_format == "json":
                assert [json.loads(line) for line in captured.err.splitlines()]
            else:
                assert " INFO " in captured.err
                assert all(beat["message"] in captured.err for beat in auth_beats)
        else:
            assert records == [] and captured.err == ""
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def _blocked_refresh_child(connection, config):
    del config
    try:
        auth._receive(connection)
        auth._send(connection, {
            "status": "ok", "token": TOKEN, "expires_on": int(time.time()) + 3600,
        })
        auth._receive(connection)
        time.sleep(60)
    finally:
        connection.close()


def test_spawned_refresh_heartbeat_cancel_cleans_child_without_attempt(tmp_path, quiet_logger):
    config = configuration(tmp_path)
    config = replace(config, watch=replace(config.watch, heartbeat_seconds=1))
    quiet_logger.setLevel(logging.INFO)
    beats = []

    class Heartbeats(logging.Handler):
        def emit(self, record):
            if getattr(record, "event", None) == "watch_heartbeat":
                beats.append(record)

    handler = Heartbeats()
    quiet_logger.addHandler(handler)
    event = threading.Event()
    before_children = {child.pid for child in multiprocessing.active_children()}
    parent_pids = set()

    def on_wait():
        parent_pids.add(os.getpid())
        if len(beats) >= 2:
            event.set()

    started = time.monotonic()
    try:
        with auth.AuthenticationBroker(
            config.provider, config.shutdown.timeout_seconds, target=_blocked_refresh_child,
        ) as session:
            session.start(37, ShutdownDeadline(event.is_set, 1))
            child_pid = session._process.pid
            session.on_wait = on_wait
            with Processor(config, quiet_logger, auth_session=session) as processor:
                job = processor._admit(
                    write_image(config.paths.source / "synthetic.png"),
                    candidate=None, shutdown_event=event,
                )
                before = processor.state.get_job(job.job_id)
                with pytest.raises(AppError) as caught:
                    processor._dispatch_one(event, job.job_id)
                assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
                assert processor.state.get_job(job.job_id) == before
                assert processor.state.attempt_count(job.job_id) == 0
                assert session.on_wait is on_wait
        assert time.monotonic() - started < 6
        assert parent_pids == {os.getpid()} and child_pid not in parent_pids
        assert len(beats) == 2 and all(beat.result == "authentication" for beat in beats)
        assert {child.pid for child in multiprocessing.active_children()} == before_children
    finally:
        quiet_logger.removeHandler(handler)
