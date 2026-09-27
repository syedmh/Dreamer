"""Synthetic restart/recovery security regressions; never open real user state."""
from __future__ import annotations

import base64
import io
import json
import threading
from dataclasses import replace

import httpx
import pytest

from conftest import write_image
from test_retry_cda7_pacing import (
    Clock, Scripted, config_for, processor_for, rows, admit, sdk, tmp_path,
)
from tcfcomic.domain import (
    AppError, ErrorCode, JobStatus, PermanentProviderError, RetryableProviderError,
)
from tcfcomic.providers.openai import recorded_retryable_response

LEGACY_400 = (
    "The provider rejected the request. HTTP 400. Check request parameters "
    "and model support for the image-edit API."
)


def current_error(sdk, body, *, status=400, enabled=True):
    """Obtain current writer evidence without assuming its marker spelling."""
    provider, request = sdk.install(status, body, enabled=enabled)
    with pytest.raises(AppError) as raised:
        provider.transform(request, io.BytesIO())
    return raised.value


def assert_preserved(processor, history, failure_path, failure_bytes):
    # Check preservation BEFORE the replay assertion, so RED still proves it.
    assert rows(processor)[:len(history)] == history
    assert failure_path.read_bytes() == failure_bytes


def process_or_safe_refusal(processor, source, **kwargs):
    """A safe recovery refusal may be returned or raised; dispatch is the oracle."""
    try:
        return processor.process_path(source, **kwargs)
    except AppError as error:
        assert error.code == ErrorCode.STATE_FAILED
        return None


@pytest.mark.parametrize("lost_evidence", ["moderation-type", "uninspectable-body"])
def test_legacy_failed_history_explicit_recovery_makes_zero_new_calls(
    tmp_path, quiet_logger, lost_evidence,
):
    # Prior writers collapsed both causes to the same bytes. This fixture
    # models that information loss; it does not claim to run an old binary.
    config = config_for(tmp_path, attempts=1, named=True, enabled=False)
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    provider = Scripted(timer, [PermanentProviderError(
        ErrorCode.PROVIDER_PERMANENT, LEGACY_400)])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        failed = processor.process_path(source, variant="one")
        assert failed.status == JobStatus.FAILED and failed.attempts == 1
        history = rows(processor)
        failure_path = config.paths.quarantine / f"{failed.job_id}.json"
        failure_bytes = failure_path.read_bytes()
    config = replace(config, retry=replace(
        config.retry, max_attempts=4, azure_response_retries=True))
    with processor_for(config, quiet_logger, timer, provider) as processor:
        implicit = processor.process_path(source, variant="one")
        assert implicit.status == JobStatus.FAILED
        assert len(provider.requests) == 1
        before = len(provider.requests)
        result = process_or_safe_refusal(
            processor, source, variant="one", retry_failed_variants=True)
        assert_preserved(processor, history, failure_path, failure_bytes)
        additional = len(provider.requests) - before
        print("RED_OBSERVATION " + json.dumps({
            "case": "legacy-explicit", "lost_evidence": lost_evidence,
            "requests_before": before, "requests_after": len(provider.requests),
            "additional_calls": additional,
            "attempts": processor.state.attempt_count(failed.job_id),
            "history_unchanged": True, "failure_bytes_unchanged": True,
        }, sort_keys=True))
        assert additional == 0, "Unattested legacy history must not authorize any new submission"
        assert result is None or result.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
        assert processor.state.get_job(failed.job_id).status in {
            JobStatus.FAILED, JobStatus.AMBIGUOUS}
        assert rows(processor) == history


@pytest.mark.parametrize("status,tail", [
    (400, " Check request parameters and model support for the image-edit API."),
    (503, ""),
])
def test_legacy_ready_retry_restart_makes_zero_new_calls(
    tmp_path, quiet_logger, status, tail,
):
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    provider = Scripted(timer, [RetryableProviderError(
        ErrorCode.PROVIDER_RETRYABLE,
        f"The provider is temporarily unavailable. HTTP {status}.{tail}",
    )])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        job_id = admit(processor, source).job_id
        assert processor._dispatch_one(threading.Event(), job_id)
        assert processor.state.get_job(job_id).status == JobStatus.READY_RETRY
        history = rows(processor)
        assert len(provider.requests) == 1
    with processor_for(config, quiet_logger, timer, provider) as processor:
        before = len(provider.requests)
        process_or_safe_refusal(processor, source)  # No recovery flag.
        assert rows(processor)[:len(history)] == history
        additional = len(provider.requests) - before
        print("RED_OBSERVATION " + json.dumps({
            "case": "legacy-ready-retry", "http_status": status,
            "requests_before": before, "requests_after": len(provider.requests),
            "additional_calls": additional,
            "attempts": processor.state.attempt_count(job_id),
            "history_unchanged": True,
        }, sort_keys=True))
        assert additional == 0, "Restart must not trust incomplete queued response evidence"
        assert rows(processor) == history


@pytest.mark.parametrize("earlier", [
    "moderation-code", "moderation-type", "unknown-classification",
    "no-response", "unversioned",
])
def test_earlier_unsafe_attempt_cannot_be_erased_by_last_eligible_400(
    sdk, tmp_path, quiet_logger, earlier,
):
    # Real adapter-generated last evidence ensures this still exercises the
    # all-history gate after a new completeness protocol is implemented.
    last = current_error(sdk, {})
    assert recorded_retryable_response(last.safe_message, last.code) == 400
    if earlier.startswith("moderation"):
        field = earlier.split("-")[1]
        previous = current_error(sdk, {"error": {field: "moderation_blocked"}})
        previous_state = "permanent"
    elif earlier == "unknown-classification":
        previous = current_error(sdk, {"type": "synthetic_unknown_classification"})
        previous_state = "permanent"
    elif earlier == "unversioned":
        previous = PermanentProviderError(ErrorCode.PROVIDER_PERMANENT, LEGACY_400)
        previous_state = "permanent"
    else:
        previous = AppError(
            ErrorCode.PROVIDER_AMBIGUOUS,
            "The provider request ended with an uncertain dispatch result.",
        )
        previous_state = "ambiguous"
    config = config_for(tmp_path / "history", attempts=1)
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    provider = Scripted(timer, [last])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        failed = processor.process_path(source)
        assert failed.status == JobStatus.FAILED
        # Model an imported, completed two-attempt history entirely in the
        # synthetic DB. Do not force the current scheduler to replay moderation.
        processor.state.connection.execute(
            """INSERT INTO attempts SELECT job_id, 2, state, started_at, finished_at,
               duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
               FROM attempts WHERE job_id=? AND attempt_no=1""", (failed.job_id,))
        processor.state.connection.execute(
            """UPDATE attempts SET state=?,error_code=?,safe_message=?
               WHERE job_id=? AND attempt_no=1""",
            (previous_state, previous.code.value, previous.safe_message, failed.job_id),
        )
        failure_path = config.paths.quarantine / f"{failed.job_id}.json"
        data = json.loads(failure_path.read_text(encoding="utf-8"))
        data["attempts"] = 2
        failure_path.write_text(json.dumps(data), encoding="utf-8")
        history = rows(processor)
        failure_bytes = failure_path.read_bytes()
        completed = processor.state.completed_attempts(failed.job_id)
        assert len(completed) == 2
        assert recorded_retryable_response(
            completed[-1].safe_message, completed[-1].error_code) == 400
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as processor:
        before = len(provider.requests)
        result = process_or_safe_refusal(processor, source, retry_failed_variants=True)
        assert_preserved(processor, history, failure_path, failure_bytes)
        additional = len(provider.requests) - before
        print("RED_OBSERVATION " + json.dumps({
            "case": "earlier-unsafe", "earlier": earlier, "additional_calls": additional,
            "attempts_before": len(history),
            "attempts_after": processor.state.attempt_count(failed.job_id),
            "history_unchanged": True, "failure_bytes_unchanged": True,
        }, sort_keys=True))
        assert additional == 0, "A later eligible response cannot erase an unsafe earlier attempt"
        assert rows(processor) == history
        assert result is None or result.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
        assert processor.state.get_job(failed.job_id).status in {
            JobStatus.FAILED, JobStatus.AMBIGUOUS}


def test_current_complete_history_recovery_control(sdk, tmp_path, quiet_logger):
    last = current_error(sdk, {})
    config = config_for(tmp_path / "control", attempts=1)
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    provider = Scripted(timer, [last])
    with processor_for(config, quiet_logger, timer, provider) as processor:
        failed = processor.process_path(source)
        assert failed.status == JobStatus.FAILED
        history = rows(processor)
        failure_path = config.paths.quarantine / f"{failed.job_id}.json"
        failure_bytes = failure_path.read_bytes()
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source, retry_failed_variants=True)
        assert_preserved(processor, history, failure_path, failure_bytes)
        assert result.status == JobStatus.SUCCEEDED
        assert result.attempts == len(provider.requests) == 2


@pytest.mark.parametrize("azure", [False, True], ids=["public", "azure"])
def test_default_moderation_never_schedules_a_second_dispatch(
    sdk, tmp_path, quiet_logger, azure,
):
    provider, _ = sdk.install(
        429, {"error": {"type": "moderation_blocked"}}, enabled=False, azure=azure)
    config = config_for(tmp_path / "default", enabled=False)
    if not azure:
        config = replace(config, provider=replace(
            config.provider, name="openai", endpoint=None))
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        print("RED_OBSERVATION " + json.dumps({
            "case": "default-moderation", "azure": azure,
            "calls": len(sdk.calls), "attempts": result.attempts,
        }, sort_keys=True))
        assert result.status == JobStatus.FAILED
        assert len(sdk.calls) == result.attempts == 1


@pytest.mark.parametrize("identifier,expected", [
    ("SYNTHETIC PRIVATE RESPONSE PROSE", None),
    ("synthetic-person@example.invalid", None),
    ("a" * 32, "a" * 32),
    ("01234567-89ab-cdef-0123-456789abcdef", "01234567-89ab-cdef-0123-456789abcdef"),
    ("req_" + "b" * 32, "req_" + "b" * 32),
], ids=["private-prose", "email", "hex-control", "uuid-control", "request-prefix-control"])
def test_success_identifier_privacy_through_durable_ledger(
    sdk, tmp_path, quiet_logger, identifier, expected,
):
    provider, _ = sdk.install(handler=lambda req: httpx.Response(200, json={
        "id": identifier,
        "data": [{"b64_json": base64.b64encode(sdk.source.read_bytes()).decode("ascii")}],
    }))
    config = config_for(tmp_path / "ledger")
    source = write_image(config.paths.source / "synthetic.png")
    timer = Clock()
    with processor_for(config, quiet_logger, timer, provider) as processor:
        result = processor.process_path(source)
        assert result.status == JobStatus.SUCCEEDED
        assert len(sdk.calls) == result.attempts == 1
    with processor_for(config, quiet_logger, timer, provider) as processor:
        value = processor.state.connection.execute(
            "SELECT provider_request_id FROM attempts WHERE job_id=?",
            (result.job_id,),
        ).fetchone()[0]
        assert value == expected
