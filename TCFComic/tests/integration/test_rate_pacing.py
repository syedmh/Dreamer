from __future__ import annotations

import sqlite3
import threading
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import AppError, ErrorCode, JobStatus, RetryableProviderError, StableCandidate
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.scheduler import SchedulerClock
from tcfcomic.state import StateStore


class ManualTime:
    def __init__(self):
        self.wall = datetime(2030, 1, 1, tzinfo=UTC)
        self.elapsed = 0.0
        self.waits = []
        self.on_wait = None

    def advance(self, seconds):
        self.elapsed += seconds
        self.wall += timedelta(seconds=seconds)

    def wait(self, event, seconds):
        self.waits.append(seconds)
        assert seconds > 0
        if self.on_wait:
            self.on_wait(event, seconds)
        if not event.is_set():
            self.advance(seconds)
        return event.is_set()

    def clock(self):
        return SchedulerClock(
            utc=lambda: self.wall,
            monotonic=lambda: self.elapsed,
            wait=self.wait,
        )


class TimedProvider:
    def __init__(self, timer, *, duration=0, outcomes=()):
        self.timer = timer
        self.duration = duration
        self.outcomes = list(outcomes)
        self.starts = []
        self.finishes = []
        self.jobs = []

    def transform(self, request, output):
        self.starts.append(self.timer.elapsed)
        self.jobs.append(request.job_id)
        self.timer.advance(self.duration)
        self.finishes.append(self.timer.elapsed)
        outcome = self.outcomes.pop(0) if self.outcomes else "success"
        if isinstance(outcome, AppError):
            raise outcome
        if type(outcome) in (int, float):
            raise RetryableProviderError(
                ErrorCode.PROVIDER_RETRYABLE, "HTTP 429.",
                retry_after_seconds=outcome,
            )
        return FakeProvider([outcome]).transform(request, output)


def setup(tmp_path, *, rpm=2, **kwargs):
    _, config = write_config(tmp_path, poll_interval_seconds=1, **kwargs)
    return replace(config, provider=replace(config.provider, requests_per_minute=rpm))


def admit(processor, name):
    path = write_image(processor.config.paths.source / name)
    info = path.stat()
    return processor._admit(
        path, candidate=StableCandidate(path, info.st_size, info.st_mtime_ns),
        shutdown_event=threading.Event(),
    ).job_id


def processor_for(config, logger, timer, provider, **kwargs):
    return Processor(
        config, logger, clock=timer.clock(),
        runner=InProcessAttemptRunner(provider), **kwargs,
    )


@pytest.mark.parametrize("duration,expected", [
    (0, [0, 30, 60, 90, 120]),
    (7, [0, 37, 74, 111, 148]),
])
def test_five_images_actual_dispatch_thresholds_and_outputs(
    tmp_path, quiet_logger, duration, expected,
):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, duration=duration)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        jobs = [admit(processor, f"{i}.png") for i in range(5)]
        for job in jobs:
            result = processor._run_until_terminal(job, threading.Event())
            assert result.status == JobStatus.SUCCEEDED
            assert result.attempts == 1
            with Image.open(result.output_path) as image:
                image.verify()
        rows = processor.state.connection.execute(
            "SELECT started_at, finished_at FROM attempts ORDER BY started_at"
        ).fetchall()
        epoch = datetime(2030, 1, 1, tzinfo=UTC)
        assert [(datetime.fromisoformat(r["started_at"]) - epoch).total_seconds()
                for r in rows] == expected
        assert [(datetime.fromisoformat(r["finished_at"]) - epoch).total_seconds()
                for r in rows] == provider.finishes
    assert provider.starts == expected
    assert all(start - finish >= 30 for start, finish in zip(provider.starts[1:], provider.finishes))
    assert len(list(config.paths.destination.glob("*.png"))) == 5


@pytest.mark.parametrize("outcome", ["success", "retryable", "permanent", "ambiguous"])
def test_all_attempt_outcomes_count_and_gate_before_claim(tmp_path, quiet_logger, outcome):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[outcome])
    event = threading.Event()
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        assert processor._dispatch_one(event, first)
        before = processor.state.get_job(second)
        timer.advance(29.999)
        assert not processor._dispatch_one(event, second)
        assert processor.state.get_job(second) == before
        assert processor.state.attempt_count(second) == 0
        timer.advance(0.001)
        assert processor._dispatch_one(event, second)
        assert processor.state.get_job(second).status == JobStatus.SUCCEEDED
    assert provider.starts == [0, 30]


@pytest.mark.parametrize("rpm", [None, 2])
@pytest.mark.parametrize("max_attempts", [1, 3])
@pytest.mark.parametrize("server,backoff,expected", [(75, 8, 75), (0, 90, 90), (2, 8, 8)])
def test_429_cooldown_blocks_other_jobs_and_exhausted_job(
    tmp_path, quiet_logger, rpm, max_attempts, server, backoff, expected,
):
    config = setup(
        tmp_path, rpm=rpm, max_attempts=max_attempts,
        initial_delay_seconds=backoff, max_delay_seconds=backoff,
    )
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[server])
    event = threading.Event()
    threshold = max(expected, 30 if rpm else 0)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        assert processor._dispatch_one(event, first)
        assert processor.state.get_job(first).status == (
            JobStatus.FAILED if max_attempts == 1 else JobStatus.READY_RETRY
        )
        assert processor.state.connection.execute(
            "SELECT retry_after_seconds FROM attempts"
        ).fetchone()[0] == expected
        timer.advance(threshold - 0.001)
        assert not processor._dispatch_one(event, second)
        timer.advance(0.001)
        assert processor._dispatch_one(event, second)
    assert provider.starts == [0, threshold]


def test_429_retries_are_bounded_and_final_backoff_is_global(tmp_path, quiet_logger):
    config = setup(tmp_path, initial_delay_seconds=20, max_delay_seconds=80)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[0, 0, 0])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        target, unrelated = admit(processor, "a.png"), admit(processor, "b.png")
        result = processor._run_until_terminal(target, threading.Event())
        assert result.status == JobStatus.FAILED
        assert result.attempts == 3
        assert provider.starts == [0, 30, 70]
        assert processor.state.attempt_count(unrelated) == 0
        assert processor._dispatch_delay(unrelated) == 80
        processor._run_until_terminal(unrelated, threading.Event())
        assert provider.starts == [0, 30, 70, 150]


@pytest.mark.parametrize("outcome,rpm,threshold", [
    ("success", 2, 30), (120, 2, 120), (120, None, 120),
])
def test_restart_reconstructs_rate_and_terminal_429(tmp_path, quiet_logger, outcome, rpm, threshold):
    config = setup(tmp_path, rpm=rpm, max_attempts=1)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[outcome])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
    timer.advance(10)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert processor._dispatch_delay(second) == threshold - 10
        assert not processor._dispatch_one(threading.Event(), second)
        processor._run_until_terminal(second, threading.Event())
    assert provider.starts == [0, threshold]


def test_omitted_pacing_has_no_artificial_delay_or_global_nonhttp_retry(tmp_path, quiet_logger):
    config = setup(tmp_path, rpm=None, initial_delay_seconds=80, max_delay_seconds=80)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=["retryable"])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        processor._run_until_terminal(second, threading.Event())
        assert processor.state.attempt_count(first) == 1
    assert provider.starts == [0, 0]
    assert timer.waits == []


def test_one_shot_combines_due_rate_and_cooldown_without_claiming_others(tmp_path, quiet_logger):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[50])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        due = (processor.clock.now() + timedelta(seconds=80)).isoformat()
        processor.state.connection.execute(
            "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?", (due, first),
        )
        result = processor._run_until_terminal(first, threading.Event())
        assert result.status == JobStatus.SUCCEEDED
        assert processor.state.get_job(second).status == JobStatus.READY
        assert processor.state.attempt_count(second) == 0
        assert timer.waits == [80]
    assert provider.starts == [0, 80]


@pytest.mark.parametrize("jump", [3600, -3600])
@pytest.mark.parametrize("outcome,threshold", [("success", 30), (80, 80)])
def test_wall_clock_jump_cannot_release_wait_early(tmp_path, quiet_logger, jump, outcome, threshold):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[outcome])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        timer.wall += timedelta(seconds=jump)
        timer.advance(threshold - 1)
        assert not processor._dispatch_one(threading.Event(), second)
        timer.advance(1)
        assert processor._dispatch_one(threading.Event(), second)
    assert provider.starts == [0, threshold]


def test_watch_admits_arrival_while_terminal_cooldown_and_backlog(tmp_path, quiet_logger):
    config = setup(tmp_path, max_attempts=1)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[90])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        for i in range(5):
            admit(processor, f"{i}.png")
        def arrival(event, seconds):
            write_image(config.paths.source / "late.png")
            timer.on_wait = None
        timer.on_wait = arrival
        processor.watch(max_cycles=4)
        jobs = processor.state.list_jobs()
        assert len(jobs) == 6
        late = next(job for job in jobs if job.source_name == "late.png")
        assert late.status == JobStatus.READY
        assert processor.state.attempt_count(late.job_id) == 0
        assert provider.starts == [0]
        for job in jobs:
            if job.status == JobStatus.READY:
                processor._run_until_terminal(job.job_id, threading.Event())
        assert len(list(config.paths.destination.glob("*.png"))) == 5


def test_paced_watch_dispatches_at_most_once_per_scan(tmp_path, quiet_logger, monkeypatch):
    config = setup(tmp_path, rpm=60_000)
    timer = ManualTime()
    provider = TimedProvider(timer, duration=1)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        for i in range(5):
            admit(processor, f"{i}.png")
        dispatch = processor._dispatch_one
        def dispatch_with_bookkeeping(*args, **kwargs):
            result = dispatch(*args, **kwargs)
            # Enough post-attempt bookkeeping to pass the 1ms gate without a wait.
            timer.advance(0.01)
            return result
        monkeypatch.setattr(processor, "_dispatch_one", dispatch_with_bookkeeping)
        processor.watch(max_cycles=1)
        assert len(provider.starts) == 1


class OfflineSession:
    started = True

    def __init__(self, timer):
        self.timer = timer
        self.acquisitions = []
        self.fail = False

    def acquire(self, *args):
        self.acquisitions.append(self.timer.elapsed)
        if self.fail:
            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "Offline refresh failure.")
        return self

    def require_valid(self, *args):
        pass


class AuthenticatedRunner(InProcessAttemptRunner):
    def run_authenticated(self, provider, request, temp_path, deadline, access_token):
        return self.run(provider, request, temp_path, deadline)


@pytest.mark.parametrize("retry", [False, True])
def test_cancel_rate_wait_preserves_state_and_does_not_acquire_token(tmp_path, quiet_logger, retry):
    config = setup(tmp_path)
    config = replace(config, provider=replace(
        config.provider, name="azure_openai", endpoint="https://offline.openai.azure.com",
        authentication="interactive",
    ))
    timer = ManualTime()
    from fresh_response import capture_response
    error = capture_response(write_image(tmp_path / "fixture.png"), 429) if retry else "success"
    provider = TimedProvider(timer, outcomes=[error])
    session = OfflineSession(timer)
    with Processor(
        config, quiet_logger, clock=timer.clock(), auth_session=session,
        runner=AuthenticatedRunner(provider),
    ) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        target = first if retry else second
        before = processor.state.get_job(target)
        attempts = processor.state.attempt_count(target)
        timer.on_wait = lambda event, seconds: event.set()
        with pytest.raises(AppError) as caught:
            processor._run_until_terminal(target, threading.Event())
        assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
        assert processor.state.get_job(target) == before
        assert processor.state.attempt_count(target) == attempts
        assert session.acquisitions == [0]
        assert timer.waits == [30]
        timer.on_wait = None
        processor._run_until_terminal(target, threading.Event())
        assert session.acquisitions == [0, 30]


def test_failed_refresh_and_invalid_input_before_attempt_do_not_take_slot(tmp_path, quiet_logger):
    config = setup(tmp_path)
    config = replace(config, provider=replace(
        config.provider, name="azure_openai", endpoint="https://offline.openai.azure.com",
        authentication="interactive",
    ))
    timer = ManualTime()
    provider = TimedProvider(timer)
    session = OfflineSession(timer)
    with Processor(
        config, quiet_logger, clock=timer.clock(), auth_session=session,
        runner=AuthenticatedRunner(provider),
    ) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        before = processor.state.get_job(first)
        session.fail = True
        with pytest.raises(AppError):
            processor._dispatch_one(threading.Event(), first)
        assert processor.state.get_job(first) == before
        assert processor.state.attempt_count(first) == 0
        session.fail = False
        Path(before.staged_path).write_bytes(b"invalid")
        assert processor._dispatch_one(threading.Event(), first)
        assert processor.state.attempt_count(first) == 0
        assert processor._dispatch_delay(second) == 0
        processor._run_until_terminal(second, threading.Event())
    assert provider.starts == [0]


def test_restart_unfinished_attempt_is_ambiguous_and_spaced_from_recovery(tmp_path, quiet_logger):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor.state.claim_ready(first, processor.clock.now())
        processor.state.begin_attempt(first)
    timer.advance(100)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert processor.state.get_job(first).status == JobStatus.AMBIGUOUS
        assert processor._dispatch_delay(second) == 30
        processor._run_until_terminal(second, threading.Event())
    assert provider.starts == [130]


def test_legacy_migration_repeatable_preserves_rows_and_pacing(tmp_path, quiet_logger):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job = admit(processor, "a.png")
        processor._dispatch_one(threading.Event(), job)
        path = processor.state.path
    # Only this temporary database is converted to the exact pre-migration schema.
    with sqlite3.connect(path) as db:
        db.execute("ALTER TABLE attempts DROP COLUMN retry_after_seconds")
        before = db.execute("SELECT * FROM attempts").fetchall()
    store = StateStore(path, now=timer.clock().timestamp)
    try:
        store.initialize()
        store.initialize()
        rows = store.connection.execute("SELECT * FROM attempts").fetchall()
        assert [tuple(row)[:-1] for row in rows] == before
        assert rows[0]["retry_after_seconds"] is None
        assert store.provider_not_before(2) == timer.wall + timedelta(seconds=30)
        assert store.provider_not_before(None) is None
        assert [row["name"] for row in store.connection.execute(
            "PRAGMA table_info(attempts)"
        )].count("retry_after_seconds") == 1
    finally:
        store.close()


@pytest.mark.parametrize("terminal", [False, True])
def test_cooldown_and_attempt_completion_rollback_atomically(tmp_path, quiet_logger, terminal):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job = admit(processor, "a.png")
        processor.state.claim_ready(job, processor.clock.now())
        attempt = processor.state.begin_attempt(job)
        def crash(point):
            if point in {"mark_retryable_after_attempt", "mark_failed_after_attempt"}:
                raise RuntimeError("transaction fault")
        processor.state._transition_hook = crash
        with pytest.raises(RuntimeError, match="transaction fault"):
            if terminal:
                processor.state.mark_failed(
                    job, ErrorCode.PROVIDER_RETRYABLE, attempt_no=attempt.attempt_no,
                    retry_after_seconds=90,
                )
            else:
                processor.state.mark_retryable(
                    job, attempt.attempt_no, 0, timer.wall.isoformat(),
                    ErrorCode.PROVIDER_RETRYABLE, "HTTP 429.", retry_after_seconds=90,
                )
        row = processor.state.connection.execute("SELECT * FROM attempts").fetchone()
        assert row["finished_at"] is None
        assert row["retry_after_seconds"] is None
        processor.state._transition_hook = None
        processor.state.mark_retryable(
            job, attempt.attempt_no, 0, timer.wall.isoformat(),
            ErrorCode.PROVIDER_RETRYABLE, "HTTP 429.", retry_after_seconds=90,
        )
        row = processor.state.connection.execute("SELECT * FROM attempts").fetchone()
        assert row["finished_at"] == timer.wall.isoformat()
        assert row["retry_after_seconds"] == 90
        assert processor._dispatch_delay(job) == 90


def test_migration_failure_rolls_back_and_can_retry(tmp_path):
    path = tmp_path / "legacy.db"
    # Minimal genuine legacy attempts schema; initialize creates only missing tables.
    with sqlite3.connect(path) as db:
        db.execute("""
            CREATE TABLE attempts (
                job_id TEXT NOT NULL, attempt_no INTEGER NOT NULL, state TEXT NOT NULL,
                started_at TEXT NOT NULL, finished_at TEXT, duration_ms INTEGER,
                error_code TEXT, safe_message TEXT, provider_request_id TEXT,
                PRIMARY KEY(job_id, attempt_no)
            )
        """)
        db.execute(
            "INSERT INTO attempts VALUES (?, 1, 'ambiguous', ?, NULL, 0, NULL, NULL, NULL)",
            ("a" * 32, "2030-01-01T00:00:00+00:00"),
        )
    def crash(point):
        if point == "initialize_after_rate_migration":
            raise RuntimeError("migration fault")
    store = StateStore(path, transition_hook=crash)
    try:
        with pytest.raises(RuntimeError, match="migration fault"):
            store.initialize()
        assert "retry_after_seconds" not in {
            row["name"] for row in store.connection.execute("PRAGMA table_info(attempts)")
        }
        assert store.connection.execute("SELECT COUNT(*) FROM attempts").fetchone()[0] == 1
        store._transition_hook = None
        store.initialize()
        store.initialize()
        assert store.connection.execute("SELECT COUNT(*) FROM attempts").fetchone()[0] == 1
    finally:
        store.close()


def test_legacy_unfinished_row_uses_restart_as_conservative_finish(tmp_path, quiet_logger):
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=["permanent"])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        processor.state.connection.execute("UPDATE attempts SET finished_at = NULL")
    timer.advance(100)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        processor._run_until_terminal(second, threading.Event())
        assert processor.state.attempt_count(first) == 1
    assert provider.starts == [0, 130]


def test_quarantine_failure_cannot_discard_final_429_cooldown(tmp_path, quiet_logger, monkeypatch):
    config = setup(tmp_path, max_attempts=1)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[90])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        def failure(*args):
            assert processor.state.get_job(first).status == JobStatus.FAILED
            raise OSError("offline quarantine failure")
        monkeypatch.setattr(processor, "_quarantine", failure)
        with pytest.raises(OSError, match="offline quarantine"):
            processor._dispatch_one(threading.Event(), first)
        assert processor.state.get_job(first).status == JobStatus.FAILED
        assert processor.state.attempt_count(first) == 1
        assert processor._dispatch_delay(second) == 90
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert not processor._dispatch_one(threading.Event(), second)
        result = processor._run_until_terminal(first, threading.Event())
        assert result.status == JobStatus.FAILED
        assert result.attempts == 1
        processor._run_until_terminal(second, threading.Event())
    assert provider.starts == [0, 90]


def test_pending_jobs_allow_pacing_and_retry_config_update(tmp_path, quiet_logger):
    config = setup(tmp_path, rpm=None, provider="azure_openai", endpoint="https://offline.openai.azure.com")
    timer = ManualTime()
    provider = TimedProvider(timer)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        identity = processor._request_identity()
        processor._dispatch_one(threading.Event(), first)
    changed = replace(
        config, provider=replace(config.provider, requests_per_minute=2),
        retry=replace(config.retry, initial_delay_seconds=5, max_delay_seconds=10),
    )
    with processor_for(changed, quiet_logger, timer, provider) as processor:
        assert processor._request_identity() == identity
        assert processor._run_until_terminal(second, threading.Event()).status == JobStatus.SUCCEEDED
    assert provider.starts == [0, 30]


def test_watch_backpressure_preserves_unacked_arrivals_until_capacity(tmp_path, quiet_logger, monkeypatch):
    import tcfcomic.processor as processor_module
    monkeypatch.setattr(processor_module, "_MAX_STAGED_JOBS", 2)
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer)
    for i in range(5):
        write_image(config.paths.source / f"{i}.png")
    with processor_for(config, quiet_logger, timer, provider) as processor:
        processor.watch(max_cycles=125)
        jobs = processor.state.list_jobs()
        assert len(jobs) == 5
        assert all(job.status == JobStatus.SUCCEEDED for job in jobs)
        assert all(processor.state.attempt_count(job.job_id) == 1 for job in jobs)
    assert provider.starts == [1, 31, 61, 91, 121]
    assert len(list(config.paths.destination.glob("*.png"))) == 5
    assert len(list(config.paths.source.glob("*.png"))) == 5


def test_rate_wait_log_emitted_once_per_deadline(tmp_path, quiet_logger, monkeypatch):
    import tcfcomic.processor as processor_module
    config = setup(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer)
    logs = []
    monkeypatch.setattr(
        processor_module, "log_event",
        lambda logger, level, event, **fields: logs.append((event, fields)),
    )
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        for _ in range(29):
            assert not processor._dispatch_one(threading.Event(), second)
            timer.advance(1)
    waits = [fields for event, fields in logs if event == "rate_limit_wait"]
    assert len(waits) == 1
    assert waits[0]["duration_ms"] == 30000
    assert waits[0]["result"] == "queued"
    assert set(waits[0]) == {"job_id", "result", "duration_ms", "message"}


def test_fractional_interval_rounds_up_not_below_limit(tmp_path, quiet_logger):
    config = setup(tmp_path, rpm=11)
    timer = ManualTime()
    provider = TimedProvider(timer)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        first, second = admit(processor, "a.png"), admit(processor, "b.png")
        processor._dispatch_one(threading.Event(), first)
        processor._run_until_terminal(second, threading.Event())
    assert provider.starts[1] >= 60 / 11
    assert provider.starts[1] == pytest.approx(5.454546)
