"""Production replay controls with newly captured, offline adapter evidence."""
from dataclasses import replace
import io
import threading

import httpx
import pytest

from conftest import write_image
from test_retry_cda7_pacing import (
    Clock, Scripted, admit, config_for, processor_for, rows, sdk, tmp_path,
)
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.providers.openai import recorded_retryable_response


def captured_error(sdk, status=503, body=None, **kwargs):
    provider, request = sdk.install(status, {} if body is None else body, **kwargs)
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    return caught.value


@pytest.mark.parametrize("rpm,expected", [(None, [0, 10, 30, 70]), (2, [0, 30, 60, 100])])
def test_versioned_four_lifetime_attempts_restart_and_recovery(
    sdk, tmp_path, quiet_logger, rpm, expected,
):
    error = captured_error(sdk)
    assert recorded_retryable_response(error.safe_message, error.code) == 503
    config = config_for(tmp_path / "run", attempts=2, rpm=rpm)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [error] * 4)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        assert result.status == JobStatus.FAILED and result.attempts == 2
        history = rows(processor)
        failure = (config.paths.quarantine / f"{result.job_id}.json").read_bytes()
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert processor.process_path(source).attempts == 2
        result = processor.process_path(source, retry_failed_variants=True)
        assert result.status == JobStatus.FAILED and result.attempts == 4
        assert rows(processor)[:2] == history
        audits = list(config.paths.quarantine.glob("*.response-*.json"))
        assert any(path.read_bytes() == failure for path in audits)
        assert processor.process_path(source, retry_failed_variants=True).attempts == 4
    assert len(provider.requests) == 4 and provider.starts == expected


@pytest.mark.parametrize("cause", [httpx.ConnectError, httpx.ConnectTimeout, httpx.PoolTimeout])
def test_genuine_not_sent_restart_continues_without_response_recovery(
    sdk, tmp_path, quiet_logger, cause,
):
    def not_sent(request):
        raise cause("offline", request=request)
    error = captured_error(sdk, handler=not_sent)
    assert error.code == ErrorCode.PROVIDER_RETRYABLE
    assert error.safe_message.endswith("Dispatch evidence v1 not-sent.")
    assert recorded_retryable_response(error.safe_message, error.code) is None
    config = config_for(tmp_path / "run", enabled=False)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [error])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job = admit(processor, source).job_id
        assert processor._dispatch_one(threading.Event(), job)
        assert processor.state.get_job(job).status == JobStatus.READY_RETRY
        history = rows(processor)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        assert result.status == JobStatus.SUCCEEDED and result.attempts == 2
        assert rows(processor)[:1] == history
    assert provider.starts == [0, 30]


@pytest.mark.parametrize("mutation", ["history", "job", "zero-attempt-job"])
@pytest.mark.parametrize("boundary", ["after-stage", "inside-begin"])
def test_fresh_atomic_recheck_inserts_nothing_on_mutation(
    sdk, tmp_path, quiet_logger, monkeypatch, mutation, boundary,
):
    config = config_for(tmp_path / "run")
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [captured_error(sdk)])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job = admit(processor, source).job_id
        if mutation != "zero-attempt-job":
            processor._dispatch_one(threading.Event(), job)
            timer.advance(30)
        before_count = processor.state.attempt_count(job)
        before_calls = len(provider.requests)

        def mutate():
            if mutation == "history":
                processor.state.connection.execute(
                    "UPDATE attempts SET safe_message='unverified' WHERE job_id=?", (job,))
            else:
                processor.state.connection.execute(
                    "UPDATE jobs SET model='competing-model' WHERE job_id=?", (job,))

        if boundary == "after-stage":
            original = processor._verify_staged_input
            def verify(claimed):
                result = original(claimed)
                mutate()
                return result
            monkeypatch.setattr(processor, "_verify_staged_input", verify)
        else:
            original = processor.state._fault
            def fault(point):
                if point == "begin_attempt_before_check":
                    mutate()
                original(point)
            monkeypatch.setattr(processor.state, "_fault", fault)
        with pytest.raises(AppError) as caught:
            processor._dispatch_one(threading.Event(), job)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert processor.state.attempt_count(job) == before_count
        assert len(provider.requests) == before_calls


@pytest.mark.parametrize("enabled", [False, True])
@pytest.mark.parametrize("earlier", ["moderation", "unknown", "legacy"])
def test_queued_mixed_history_denial_preserves_rows_and_allows_next_slot(
    sdk, tmp_path, quiet_logger, enabled, earlier,
):
    last = captured_error(sdk, 429)
    previous = captured_error(sdk, 429, {"type": (
        "moderation_blocked" if earlier == "moderation" else "unknown")})
    config = config_for(tmp_path / "run", named=True, enabled=enabled)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [last])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        one = admit(processor, source, variant="one").job_id
        two = admit(processor, source, variant="two").job_id
        processor._dispatch_one(threading.Event(), one)
        processor.state.connection.execute(
            """INSERT INTO attempts SELECT job_id, 2, state, started_at, finished_at,
               duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
               FROM attempts WHERE job_id=?""", (one,))
        processor.state.connection.execute(
            """UPDATE attempts SET state='permanent', error_code=?, safe_message=?
               WHERE job_id=? AND attempt_no=1""",
            (ErrorCode.PROVIDER_PERMANENT.value,
             "The provider rejected the request. HTTP 400." if earlier == "legacy"
             else previous.safe_message, one))
        history = rows(processor)
        due = processor.state.get_job(one).next_attempt_at
        gate = processor.state.provider_not_before(config.provider.requests_per_minute)
        timer.advance(30)
        assert processor._dispatch_one(threading.Event(), one)
        denied = processor.state.get_job(one)
        assert denied.status == JobStatus.FAILED and denied.error_code == "STATE_FAILED"
        assert denied.next_attempt_at == due and rows(processor) == history
        assert processor.state.provider_not_before(config.provider.requests_per_minute) == gate
        assert len(provider.requests) == 1
        assert not processor._dispatch_one(threading.Event(), one)
        assert processor._dispatch_one(threading.Event(), two)
        assert processor.state.get_job(two).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 2


def test_terminal_429_persists_cooldown_before_quarantine_failure(
    sdk, tmp_path, quiet_logger, monkeypatch,
):
    error = captured_error(sdk, handler=lambda request: httpx.Response(
        429, json={"type": "moderation_blocked"}, headers={"retry-after": "75"}))
    config = config_for(tmp_path / "run")
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = Scripted(timer, [error])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job = admit(processor, source).job_id
        def fail_quarantine(*args):
            assert processor.state.get_job(job).status == JobStatus.FAILED
            raise AppError(ErrorCode.STATE_FAILED, "Synthetic quarantine failure.")
        monkeypatch.setattr(processor, "_quarantine", fail_quarantine)
        with pytest.raises(AppError, match="Synthetic quarantine failure"):
            processor._dispatch_one(threading.Event(), job)
        attempt = processor.state.completed_attempts(job)[0]
        assert attempt.retry_after_seconds == 75
        assert (processor.state.provider_not_before(None) - timer.wall).total_seconds() == 75
        history = rows(processor)
    with processor_for(config, quiet_logger, timer, provider) as processor:
        assert processor.state.get_job(job).status == JobStatus.FAILED
        assert rows(processor) == history
        assert (processor.state.provider_not_before(None) - timer.wall).total_seconds() == 75
        assert not processor._dispatch_one(threading.Event(), job)
    assert len(provider.requests) == 1


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("status", [409, 429])
def test_default_complete_safe_response_still_retries(
    sdk, tmp_path, quiet_logger, azure, status,
):
    error = captured_error(sdk, status, enabled=False, azure=azure)
    config = config_for(tmp_path / "run", enabled=False)
    if not azure:
        config = replace(config, provider=replace(config.provider, name="openai", endpoint=None))
    timer = Clock()
    provider = Scripted(timer, [error])
    source = write_image(config.paths.source / "x.png")
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        assert result.status == JobStatus.SUCCEEDED and result.attempts == 2
    assert provider.starts == [0, 30]


def test_safe_history_checks_rate_then_auth_without_consuming_attempt(
    sdk, tmp_path, quiet_logger,
):
    error = captured_error(sdk, 429, enabled=False)
    config = config_for(tmp_path / "run", enabled=False)
    timer = Clock()
    provider = Scripted(timer, [error])
    source = write_image(config.paths.source / "x.png")
    class Auth:
        started = True
        calls = 0
        def acquire(self, *args):
            self.calls += 1
            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "Synthetic refresh failure.")
    auth = Auth()
    with processor_for(config, quiet_logger, timer, provider, auth_session=auth) as processor:
        job = admit(processor, source).job_id
        processor._dispatch_one(threading.Event(), job)
        processor.config = replace(config, provider=replace(config.provider, authentication="interactive"))
        before = processor.state.get_job(job)
        history = rows(processor)
        assert not processor._dispatch_one(threading.Event(), job)
        assert auth.calls == 0
        timer.advance(30)
        with pytest.raises(AppError) as caught:
            processor._dispatch_one(threading.Event(), job)
        assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED and auth.calls == 1
        assert processor.state.get_job(job) == before and rows(processor) == history
        assert len(provider.requests) == 1


def test_explicit_recovery_cannot_use_last_not_sent_proof(
    sdk, tmp_path, quiet_logger,
):
    def not_sent(request):
        raise httpx.ConnectError("offline", request=request)
    error = captured_error(sdk, handler=not_sent)
    config = config_for(tmp_path / "run", attempts=1)
    timer = Clock()
    provider = Scripted(timer, [error])
    source = write_image(config.paths.source / "x.png")
    with processor_for(config, quiet_logger, timer, provider) as processor:
        failed = processor.process_path(source)
        assert failed.status == JobStatus.FAILED
        history = rows(processor)
        job = processor.state.get_job(failed.job_id)
        failure_path = config.paths.quarantine / f"{failed.job_id}.json"
        failure_bytes = failure_path.read_bytes()
        processor.config = replace(config, retry=replace(config.retry, max_attempts=4))
        with pytest.raises(AppError) as caught:
            processor.process_path(source, retry_failed_variants=True)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert processor.state.get_job(failed.job_id) == job and rows(processor) == history
        assert failure_path.read_bytes() == failure_bytes and len(provider.requests) == 1
