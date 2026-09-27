"""Synthetic durable retry fixtures shared ONLY by the new independent modules."""
from __future__ import annotations

import hashlib
import importlib.util
import sys
import threading
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from pathlib import Path
from types import SimpleNamespace

import pytest

from conftest import write_config, write_image
from fresh_response import capture_response
# Pytest's prepend import mode does not add tests/unit before integration
# collection. Load ONLY this owned helper by its exact path, without mutating
# sys.path or shared conftest and without depending on test selection order.
if "test_retry_cda7_classifier" not in sys.modules:
    _spec = importlib.util.spec_from_file_location(
        "test_retry_cda7_classifier", Path(__file__).resolve().parents[1] / "unit/test_retry_cda7_classifier.py")
    _helper = importlib.util.module_from_spec(_spec)
    sys.modules[_spec.name] = _helper
    _spec.loader.exec_module(_helper)
from test_retry_cda7_classifier import tmp_path, sdk, ENDPOINT
from tcfcomic.domain import (
    AppError, ErrorCode, JobStatus, PermanentProviderError, RetryableProviderError,
    ShutdownToken, StableCandidate,
)
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.scanner import stage_candidate
from tcfcomic.scheduler import SchedulerClock

class Clock:
    def __init__(self):
        self.wall = datetime(2030, 1, 1, tzinfo=UTC)
        self.elapsed = 0
        self.waits = []
        self.on_wait = None

    def advance(self, seconds):
        # Manual clock uses microsecond precision, matching datetime, not binary drift.
        self.elapsed = round(self.elapsed + seconds, 6)
        self.wall += timedelta(seconds=seconds)

    def wait(self, event, seconds):
        self.waits.append(seconds)
        if self.on_wait:
            self.on_wait(event, seconds)
        if not event.is_set():
            self.advance(seconds)
        return event.is_set()

    def clock(self):
        return SchedulerClock(utc=lambda: self.wall, monotonic=lambda: self.elapsed, wait=self.wait)


class Scripted:
    def __init__(self, timer, outcomes=(), duration=0):
        self.timer, self.outcomes, self.duration = timer, list(outcomes), duration
        self.requests, self.starts, self.inputs = [], [], []

    def transform(self, request, output):
        self.requests.append(request)
        self.starts.append(self.timer.elapsed)
        self.inputs.append(hashlib.sha256(request.source.staged_path.read_bytes()).hexdigest())
        self.timer.advance(self.duration)
        action = self.outcomes.pop(0) if self.outcomes else None
        if isinstance(action, Exception):
            raise action
        return FakeProvider().transform(request, output)


def config_for(root, *, attempts=4, rpm=2, named=False, enabled=True):
    path, config = write_config(root, provider="azure_openai", endpoint=ENDPOINT,
                               max_attempts=attempts, initial_delay_seconds=10,
                               max_delay_seconds=120, poll_interval_seconds=1,
                               source_name="i", destination_name="o")
    config = replace(config, retry=replace(config.retry, azure_response_retries=enabled),
                     provider=replace(config.provider, requests_per_minute=rpm,
                                      prompts={"one": "original one", "two": "original two"} if named else None))
    return config


def processor_for(config, logger, timer, provider, **kwargs):
    class OfflineRunner(InProcessAttemptRunner):
        def run_authenticated(self, config, request, temp_path, deadline, token):
            return self.run(config, request, temp_path, deadline)
    return Processor(config, logger, clock=timer.clock(), runner=OfflineRunner(provider), **kwargs)


def admit(processor, source, **kwargs):
    info = source.stat()
    return processor._admit(source, candidate=StableCandidate(source, info.st_size, info.st_mtime_ns),
                            shutdown_event=threading.Event(), **kwargs)


def rows(processor):
    return tuple(tuple(row) for row in processor.state.connection.execute(
        "SELECT * FROM attempts ORDER BY job_id, attempt_no"))


@pytest.fixture
def failed(tmp_path, quiet_logger):
    config = config_for(tmp_path, attempts=1)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [capture_response(source)])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        job = processor.state.get_job(result.job_id)
        assert job.status == JobStatus.FAILED
        assert processor.state.attempt_count(job.job_id) == len(provider.requests) == 1
        assert not Path(job.staged_path).exists()
        processor.config = replace(
            config, retry=replace(config.retry, max_attempts=4),
            provider=replace(config.provider, prompt="changed synthetic private prompt"),
        )
        yield SimpleNamespace(config=processor.config, source=source, timer=timer,
                              provider=provider, processor=processor, job=job,
                              attempts=rows(processor), logger=quiet_logger)


def fresh_snapshot(failed):
    processor = failed.processor
    source = failed.source
    info = source.stat()
    # Owned stage naming follows the existing job-bound staging contract.
    import uuid
    stage = processor.staging / f"{failed.job.job_id}.{uuid.uuid4().hex}.input"
    return stage_candidate(StableCandidate(source, info.st_size, info.st_mtime_ns),
                           stage, processor.config.limits.max_input_bytes,
                           ShutdownToken(lambda: False, lambda seconds: False))


def retry_error(source, hint=None):
    return capture_response(source, 503, enabled=True,
                            headers={} if hint is None else {"retry-after": str(hint)})


@pytest.mark.parametrize("rpm,expected", [(None, [0, 10, 30, 70]), (2, [0, 30, 60, 100])])
def test_four_lifetime_attempts_across_restart_and_explicit_recovery(tmp_path, quiet_logger, rpm, expected):
    config = config_for(tmp_path, attempts=2, rpm=rpm)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [retry_error(source) for _ in range(4)])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        original = processor.state.get_job(result.job_id)
        old_attempts = rows(processor)
        assert result.attempts == 2 and result.status == JobStatus.FAILED
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert processor.process_path(source).attempts == 2
        result = processor.process_path(source, retry_failed_variants=True)
        assert result.job_id == original.job_id
        assert result.attempts == 4 and result.status == JobStatus.FAILED
        assert rows(processor)[:2] == old_attempts
        assert [row["attempt_no"] for row in processor.state.connection.execute(
            "SELECT attempt_no FROM attempts ORDER BY attempt_no")] == [1, 2, 3, 4]
        assert processor.state.completed_attempts(result.job_id)[-1].state.value == "retryable"
        for _ in range(2):
            assert processor.process_path(source, retry_failed_variants=True).attempts == 4
    assert provider.starts == expected
    assert len(provider.requests) == 4


@pytest.mark.parametrize("hint,expected", [(None, 30), (75, 75), (0, 30)])
def test_shared_gate_before_claim_and_other_variant(tmp_path, quiet_logger, hint, expected):
    config = config_for(tmp_path, named=True)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [retry_error(source, hint)], duration=7)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        one = admit(processor, source, variant="one").job_id
        two = admit(processor, source, variant="two").job_id
        assert processor._dispatch_one(threading.Event(), one)
        before = processor.state.get_job(two)
        timer.advance(expected - .001)
        assert not processor._dispatch_one(threading.Event(), two)
        assert processor.state.get_job(two) == before
        assert processor.state.attempt_count(two) == 0
        timer.advance(.001)
        assert processor._dispatch_one(threading.Event(), two)
        assert all(request.azure_response_retries for request in provider.requests)
    assert provider.starts == [0, 7 + expected]


@pytest.mark.parametrize("jump", [-3600, 3600])
def test_wall_jump_cannot_shorten_response_cooldown(tmp_path, quiet_logger, jump):
    config = config_for(tmp_path)
    timer = Clock()
    provider = Scripted(timer, [retry_error(write_image(tmp_path / "fixture.png"), 75)])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        one = admit(processor, write_image(config.paths.source / "a.png")).job_id
        two = admit(processor, write_image(config.paths.source / "b.png")).job_id
        processor._dispatch_one(threading.Event(), one)
        timer.wall += timedelta(seconds=jump)
        timer.advance(74)
        assert not processor._dispatch_one(threading.Event(), two)
        timer.advance(1)
        assert processor._dispatch_one(threading.Event(), two)
    assert provider.starts == [0, 75]


def test_preclaim_auth_failure_consumes_neither_attempt_nor_due_time(tmp_path, quiet_logger):
    config = config_for(tmp_path)
    config = replace(config, provider=replace(config.provider, authentication="interactive"))
    timer = Clock()
    provider = Scripted(timer)
    class Auth:
        started = True
        calls = 0
        def acquire(self, *args):
            self.calls += 1
            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "Synthetic token failure.")
    auth = Auth()
    with processor_for(config, quiet_logger, timer, provider, auth_session=auth) as processor:
        job_id = admit(processor, write_image(config.paths.source / "x.png")).job_id
        due = (timer.wall + timedelta(seconds=30)).isoformat()
        processor.state.connection.execute(
            "UPDATE jobs SET status='READY_RETRY', next_attempt_at=? WHERE job_id=?", (due, job_id))
        before = processor.state.get_job(job_id)
        assert not processor._dispatch_one(threading.Event(), job_id)
        assert auth.calls == 0
        timer.advance(30)
        with pytest.raises(AppError) as error:
            processor._dispatch_one(threading.Event(), job_id)
        assert error.value.code == ErrorCode.AUTHENTICATION_FAILED
        assert auth.calls == 1
        assert processor.state.get_job(job_id) == before
        assert processor.state.attempt_count(job_id) == 0
        assert not provider.requests


def test_final_retry_cooldown_survives_quarantine_failure(tmp_path, quiet_logger, monkeypatch):
    config = config_for(tmp_path, attempts=1)
    timer = Clock()
    provider = Scripted(timer, [retry_error(write_image(tmp_path / "fixture.png"), 75)])
    hits = []
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job_id = admit(processor, write_image(config.paths.source / "x.png")).job_id
        def fail(*args):
            hits.append(True)
            raise AppError(ErrorCode.STATE_FAILED, "Synthetic quarantine failure.")
        monkeypatch.setattr(processor, "_quarantine", fail)
        with pytest.raises(AppError):
            processor._dispatch_one(threading.Event(), job_id)
        assert hits == [True]
        attempt = processor.state.completed_attempts(job_id)[0]
        assert attempt.retry_after_seconds == 75
        assert processor.state.provider_not_before(2) == timer.wall + timedelta(seconds=75)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        other = admit(processor, write_image(config.paths.source / "b.png")).job_id
        assert not processor._dispatch_one(threading.Event(), other)
        assert len(provider.requests) == 1


def test_timing_policy_does_not_change_request_identity(failed):
    p = failed.processor
    before = p._request_identity()
    p.config = replace(p.config, retry=replace(p.config.retry, azure_response_retries=False,
                                             initial_delay_seconds=20, max_delay_seconds=120),
                       provider=replace(p.config.provider, requests_per_minute=None))
    assert p._request_identity() == before


def test_exponential_cap_expression_without_extra_dispatch(failed):
    """Exercise actual delay expression beyond reachable four-attempt policy safely."""
    import ast
    import inspect
    import textwrap
    source = ast.parse(textwrap.dedent(inspect.getsource(Processor._dispatch_one_impl)))
    expressions = [node.value for node in ast.walk(source)
                   if isinstance(node, ast.Assign)
                   and any(isinstance(target, ast.Name) and target.id == "delay" for target in node.targets)]
    assert len(expressions) == 1
    expression = compile(ast.Expression(expressions[0]), "<production-backoff-expression>", "eval")
    values = [eval(expression, {"self": failed.processor, "attempt": SimpleNamespace(attempt_no=n)})
              for n in range(1, 7)]
    assert values == [10, 20, 40, 80, 120, 120]
    assert len(failed.provider.requests) == 1
