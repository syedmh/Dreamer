from __future__ import annotations

import logging
import json
import multiprocessing
import os
import threading
import time
from dataclasses import replace
from datetime import UTC, datetime

import pytest
import yaml
from PIL import Image

from conftest import write_config, write_image, write_mpo
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode, JobStatus, StableCandidate
from tcfcomic.image_io import validate_input
from tcfcomic.logging_setup import configure_logging, log_event
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner, SubprocessAttemptRunner
from tcfcomic.scanner import FolderScanner
from tcfcomic.scheduler import SchedulerClock


class Records(logging.Handler):
    def __init__(self, logger):
        super().__init__()
        self.records = []
        logger.setLevel(logging.INFO)
        logger.addHandler(self)

    def emit(self, record):
        self.records.append(record)

    def events(self, name):
        return [r for r in self.records if getattr(r, "event", None) == name]


class Timer:
    def __init__(self):
        self.elapsed = 0
        self.waits = []

    def wait(self, event, delay):
        self.waits.append(delay)
        self.elapsed += delay
        if self.elapsed >= 31:
            event.set()
        return event.is_set()

    def clock(self):
        return SchedulerClock(
            utc=lambda: datetime(2030, 1, 1, tzinfo=UTC),
            monotonic=lambda: self.elapsed, wait=self.wait,
        )


def test_primary_mpo_jpg_is_supported(tmp_path):
    _, config = write_config(tmp_path)
    path = write_mpo(config.paths.source / "synthetic.JPG")
    assert validate_input(path, config.limits).format == "MPO"


def test_failed_watch_admission_is_visible_once(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    (config.paths.source / "broken.JPG").write_bytes(b"invalid synthetic image")
    records = Records(quiet_logger)
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())) as processor:
        processor.watch(max_cycles=4)
    assert len(records.events("watch_item_failed")) == 1


def test_idle_heartbeat_wakes_before_long_poll(tmp_path, quiet_logger):
    _, config = write_config(tmp_path, poll_interval_seconds=120)
    records = Records(quiet_logger)
    timer = Timer()
    with Processor(
        config, quiet_logger, clock=timer.clock(),
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        processor.watch()
    assert len(records.events("watch_heartbeat")) == 2
    assert timer.waits == [15, 15, 15]


def test_parent_worker_has_wait_callback():
    calls = []
    runner = SubprocessAttemptRunner(on_wait=lambda: calls.append("parent"))
    assert runner.on_wait is not None


def admit(processor, path):
    info = path.stat()
    return processor._admit(
        path, candidate=StableCandidate(path, info.st_size, info.st_mtime_ns),
        shutdown_event=threading.Event(),
    )


def test_scanner_detection_once_per_observed_version(tmp_path):
    source = tmp_path / "incoming"
    source.mkdir()
    path = write_image(source / "synthetic.JPG", "JPEG")
    detected = []
    scanner = FolderScanner(source, 3, on_observation=detected.append)
    assert scanner.observe(0) == ()
    assert scanner.observe(1) == ()
    candidate, = scanner.observe(3)
    scanner.acknowledge(candidate)
    assert scanner.observe(5) == ()
    assert len(detected) == 1
    info = path.stat()
    os.utime(path, ns=(info.st_atime_ns, info.st_mtime_ns + 1000000))
    assert scanner.observe(6) == ()
    assert len(detected) == 2
    assert len(scanner.observe(9)) == 1
    assert len(detected) == 2


def test_queue_processed_and_duplicate_counts(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    for name in ("a.jpg", "b.jpg"):
        write_mpo(config.paths.source / name)
    records = Records(quiet_logger)
    provider = FakeProvider()
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
        processor.watch(max_cycles=4)
        assert [r.pending_count for r in records.events("job_queued")] == [1, 2]
        assert [r.pending_count for r in records.events("job_processed")] == [1, 0]
        assert len(records.events("processing_started")) == 2
        processor.watch(max_cycles=3)
        assert len(records.events("job_processed")) == 2
        assert len(records.events("job_queued")) == 2
        assert len(records.events("watch_item_skipped")) == 2
        assert len(provider.requests) == 2
        assert len(records.events("watch_stopped")) == 2


def test_old_invalid_job_is_visible_without_readmission_loop(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    path = config.paths.source / "broken.JPG"
    path.write_bytes(b"synthetic corrupt input")
    records = Records(quiet_logger)
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())) as processor:
        failed = admit(processor, path)
        assert failed.status == JobStatus.FAILED and failed.attempts == 0
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())) as processor:
        before = processor.state.get_job(failed.job_id)
        processor.watch(max_cycles=4)
        assert processor.state.get_job(failed.job_id) == before
        assert processor.state.attempt_count(failed.job_id) == 0
    assert len(records.events("watch_item_failed")) == 1
    assert not records.events("job_queued")
    assert not records.events("processing_started")


@pytest.mark.parametrize("failure_point", ["before_rename", "after_rename"])
def test_processed_log_requires_published_success_including_reconciliation(tmp_path, quiet_logger, failure_point):
    _, config = write_config(tmp_path)
    records = Records(quiet_logger)

    def publication_hook(point):
        if point == failure_point:
            raise AppError(ErrorCode.PUBLICATION_FAILED, "Synthetic publication fault.")

    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider()),
        publication_hook=publication_hook,
    ) as processor:
        result = processor.process_path(write_image(config.paths.source / "synthetic.png"))
        if failure_point == "after_rename":
            assert result.status == JobStatus.SUCCEEDED
            record, = records.events("job_processed")
            assert record.output_name == result.output_path.name
            assert result.output_path.is_file()
        else:
            assert result.status == JobStatus.FAILED
            assert not records.events("job_processed")


def test_heartbeat_rate_wait_preserves_dispatch_deadlines(tmp_path, quiet_logger):
    _, config = write_config(tmp_path, poll_interval_seconds=120)
    config = replace(config, provider=replace(config.provider, requests_per_minute=2))
    records = Records(quiet_logger)
    timer = Timer()

    def wait(event, seconds):
        timer.waits.append(seconds)
        timer.elapsed += seconds
        return event.is_set()

    clock = timer.clock()
    clock.wait = wait
    provider = FakeProvider()
    with Processor(config, quiet_logger, clock=clock, runner=InProcessAttemptRunner(provider)) as processor:
        a, b = [admit(processor, write_image(config.paths.source / name)) for name in ("a.png", "b.png")]
        processor._run_until_terminal(a.job_id, threading.Event())
        assert processor.state.attempt_count(b.job_id) == 0
        result = processor._run_until_terminal(b.job_id, threading.Event())
        assert result.status == JobStatus.SUCCEEDED
        assert timer.waits == [15, 15] and timer.elapsed == 30
        assert processor.state.attempt_count(b.job_id) == 1
    heartbeat, = records.events("watch_heartbeat")
    assert heartbeat.result == "rate_wait" and heartbeat.pending_count == 1
    assert "15s" in heartbeat.safe_message


def test_heartbeat_retry_wait_and_cancel_dont_claim(tmp_path, quiet_logger):
    _, config = write_config(tmp_path, initial_delay_seconds=60, max_delay_seconds=60, poll_interval_seconds=120)
    records = Records(quiet_logger)
    timer = Timer()
    provider = FakeProvider(["retryable"])
    with Processor(config, quiet_logger, clock=timer.clock(), runner=InProcessAttemptRunner(provider)) as processor:
        item = admit(processor, write_image(config.paths.source / "a.png"))
        assert processor._dispatch_one(threading.Event(), item.job_id)
        before = processor.state.get_job(item.job_id)
        with pytest.raises(AppError):
            processor._run_until_terminal(item.job_id, threading.Event())
        assert processor.state.get_job(item.job_id) == before
        assert processor.state.attempt_count(item.job_id) == 1
    assert timer.waits == [15, 15, 15]
    assert len(records.events("watch_heartbeat")) == 2
    assert all(r.result == "queued" for r in records.events("watch_heartbeat"))


def _blocking_synthetic_child(provider, request, temp_path, result_queue):
    time.sleep(60)


def test_real_spawn_parent_heartbeats_and_cancel_no_orphan(tmp_path, quiet_logger):
    _, config = write_config(tmp_path, shutdown_timeout_seconds=0.5)
    config = replace(config, watch=replace(config.watch, heartbeat_seconds=1))
    path = write_mpo(config.paths.source / "synthetic.JPG")
    records = Records(quiet_logger)
    event = threading.Event()
    before = {child.pid for child in multiprocessing.active_children()}
    parent_pids = set()

    def on_wait():
        parent_pids.add(os.getpid())
        if len(records.events("watch_heartbeat")) >= 2:
            event.set()

    runner = SubprocessAttemptRunner(target=_blocking_synthetic_child, on_wait=on_wait)
    started = time.monotonic()
    with Processor(config, quiet_logger, runner=runner) as processor:
        result = processor.process_path(path, shutdown_event=event)
        assert result.status == JobStatus.AMBIGUOUS
        assert result.attempts == 1
        assert not list(config.paths.destination.glob("*.png"))
        assert not list(config.paths.destination.glob("*.tmp"))
    assert time.monotonic() - started < 6
    assert parent_pids == {os.getpid()}
    assert {child.pid for child in multiprocessing.active_children()} <= before
    assert len(records.events("watch_heartbeat")) == 2
    assert all(r.result == "processing" for r in records.events("watch_heartbeat"))
    assert not records.events("job_processed")
    assert runner.on_wait is on_wait


def test_processing_ticker_throttles_join_callbacks(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    records = Records(quiet_logger)
    timer = Timer()

    class WaitingRunner(SubprocessAttemptRunner):
        def run(self, provider, request, temp_path, shutdown_deadline):
            for tick in range(901):
                timer.elapsed = tick / 20
                self.on_wait()
            return InProcessAttemptRunner(FakeProvider()).run(provider, request, temp_path, shutdown_deadline)

    with Processor(config, quiet_logger, clock=timer.clock(), runner=WaitingRunner()) as processor:
        result = processor.process_path(write_mpo(config.paths.source / "synthetic.JPG"))
        assert result.status == JobStatus.SUCCEEDED
    assert [r.duration_ms for r in records.events("watch_heartbeat")] == [15000, 30000, 45000]
    assert all(r.result == "processing" for r in records.events("watch_heartbeat"))


@pytest.mark.parametrize("field,value", [
    ("console_format", None), ("console_format", "TEXT"), ("console_format", " text "),
    ("console_format", True), ("heartbeat_seconds", 0), ("heartbeat_seconds", 0.9),
    ("heartbeat_seconds", 3601), ("heartbeat_seconds", True), ("heartbeat_seconds", None),
    ("heartbeat_seconds", float("nan")), ("heartbeat_seconds", float("inf")),
])
def test_new_config_rejects_invalid_values(tmp_path, field, value):
    path, _ = write_config(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["logging" if field == "console_format" else "watch"][field] = value
    path.write_text(yaml.safe_dump(data))
    with pytest.raises(AppError):
        load_config(path)


@pytest.mark.parametrize("interval", [1, 15, 3600])
def test_config_defaults_and_heartbeat_boundaries(tmp_path, interval):
    path, config = write_config(tmp_path)
    assert config.watch.heartbeat_seconds == 15
    assert config.logging.console_format == "json"
    data = yaml.safe_load(path.read_text())
    data["watch"]["heartbeat_seconds"] = interval
    data["logging"]["console_format"] = "text"
    path.write_text(yaml.safe_dump(data))
    loaded = load_config(path)
    assert loaded.watch.heartbeat_seconds == interval
    assert loaded.logging.console_format == "text"


def test_text_console_json_file_redaction_and_level(tmp_path, capsys, monkeypatch):
    _, config = write_config(tmp_path)
    monkeypatch.setenv("OPENAI_API_KEY", "synthetic-secret-key")
    logger = configure_logging(replace(config.logging, console_format="text"), config.paths.destination)
    try:
        log_event(
            logger, logging.INFO, "processing_started",
            message="Processing synthetic.JPG synthetic-secret-key",
            source_name="synthetic.JPG", prompt="private prompt", headers="private headers",
        )
        log_event(logger, logging.DEBUG, "hidden", message="must not appear")
        for handler in logger.handlers:
            handler.flush()
        captured = capsys.readouterr()
        assert not captured.out and "INFO Processing synthetic.JPG" in captured.err
        assert not captured.err.startswith("{")
        assert "synthetic-secret-key" not in captured.err
        payload, = [
            json.loads(line) for line in
            (config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl").read_text().splitlines()
        ]
        assert payload["event"] == "processing_started"
        assert not {"prompt", "headers"} & payload.keys()
        assert "synthetic-secret-key" not in json.dumps(payload)
        assert "must not appear" not in captured.err
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()
