"""Independent RED regressions for review preservation defects, not exhaustion.

All state is synthetic. Current positive evidence comes only from the real
offline adapter helper; subsequent message changes are negative legacy fixtures.
"""
import json
import threading
from dataclasses import replace
from datetime import timedelta
from pathlib import Path

import pytest

from conftest import write_image
from fresh_response import capture_response
from test_retry_cda7_pacing import (
    Clock, Scripted, admit, config_for, processor_for, rows, tmp_path,
)
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.providers.openai import recorded_retryable_response
from tcfcomic.quarantine import preserve_response_retry_audits


def _seed_queued(p, source, provider, timer, *, state="READY_RETRY", history="legacy"):
    """Leave one completed attempt, original failure, two inert audits and stage."""
    job_id = admit(p, source).job_id
    assert p._dispatch_one(threading.Event(), job_id)
    assert p.state.get_job(job_id).status == JobStatus.READY_RETRY
    assert len(provider.requests) == p.state.attempt_count(job_id) == 1
    message = (
        "The provider is temporarily unavailable. HTTP 429."
        if history == "legacy" else "Unverifiable synthetic historical response."
    )
    assert recorded_retryable_response(message, ErrorCode.PROVIDER_RETRYABLE) is None
    p.state.connection.execute(
        "UPDATE attempts SET safe_message=? WHERE job_id=?", (message, job_id),
    )
    p.state.connection.execute(
        "UPDATE jobs SET status=? WHERE job_id=?", (state, job_id),
    )
    job = p.state.get_job(job_id)
    failure = p._quarantine(
        job, "provider", AppError(ErrorCode.PROVIDER_RETRYABLE, message), 1,
    )
    # Prepared audits are deliberately inert, not positive replay authorization.
    audits = preserve_response_retry_audits(
        job, p.config.paths.quarantine, p._request_identity(""),
        failure.read_bytes(), p.state.completed_attempts(job_id), 4,
    )
    assert len(audits) == 2 and all(path.is_file() for path in audits)
    assert p.state.provider_not_before(None) == timer.wall + timedelta(seconds=75)
    assert Path(job.staged_path).is_file()
    return job_id


def _snapshot(p, job_id, source, provider):
    job = p.state.get_job(job_id)
    return {
        # Only status/error/updated_at may change on a history denial.
        "job_identity_and_due": replace(
            job, status=JobStatus.FAILED, error_code="STATE_FAILED", updated_at="",
        ),
        "attempt_values": rows(p),
        "attempt_message_storage_bytes": tuple(
            tuple(row) for row in p.state.connection.execute(
                """SELECT attempt_no, typeof(safe_message), hex(CAST(safe_message AS BLOB))
                   FROM attempts WHERE job_id=? ORDER BY attempt_no""", (job_id,),
            )
        ),
        "attempt_count": p.state.attempt_count(job_id),
        "source_bytes": source.read_bytes(),
        "staged_path": job.staged_path,
        "staging_artifacts": {
            str(path): path.read_bytes() for path in p.staging.glob("*.input")
        },
        "failure_and_audit_artifacts": {
            str(path): path.read_bytes()
            for path in p.config.paths.quarantine.glob(f"{job_id}*.json")
        },
        "cooldown": p.state.provider_not_before(None),
        "rate_gate": p.state.provider_not_before(p.config.provider.requests_per_minute),
        "provider_calls": len(provider.requests),
    }


def _check_preservation(p, job_id, source, provider, before, phase):
    """Collect every invariant before asserting, so a missing stage hides nothing."""
    after = _snapshot(p, job_id, source, provider)
    changed = [name for name in before if after[name] != before[name]]
    job = p.state.get_job(job_id)
    if (job.status, job.error_code) != (JobStatus.FAILED, "STATE_FAILED"):
        changed.append("FAILED/STATE_FAILED denial")
    print(json.dumps({
        "phase": phase, "changed": changed, "status": job.status.value,
        "error_code": job.error_code, "attempts": after["attempt_count"],
        "new_provider_calls": after["provider_calls"] - before["provider_calls"],
        "original_stage_exists": Path(before["staged_path"]).exists(),
        "same_stage_path": after["staged_path"] == before["staged_path"],
    }, sort_keys=True))
    return [f"{phase}: {name}" for name in changed]


@pytest.mark.parametrize("history", ["legacy", "unverifiable"])
@pytest.mark.parametrize("damage", ["reduced-limits", "corrupt-stage", "changed-stage"])
def test_duplicate_admission_denies_before_repair_or_failure_replacement(
    tmp_path, quiet_logger, monkeypatch, history, damage,
):
    """REVIEW-PRESERVE-01: duplicate process_path must not destroy denied evidence."""
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "x.png")
    response = capture_response(source, 429, enabled=True, headers={"retry-after": "75"})
    timer = Clock()
    provider = Scripted(timer, [response])
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = _seed_queued(p, source, provider, timer, history=history)
        stage = Path(p.state.get_job(job_id).staged_path)
        if damage == "reduced-limits":
            p.config = replace(config, limits=replace(config.limits, max_width=1))
        elif damage == "corrupt-stage":
            stage.write_bytes(b"\x00corrupt synthetic staged evidence\xff")
        else:
            write_image(stage, color=(90, 20, 10))
            assert stage.read_bytes() != source.read_bytes()
        before = _snapshot(p, job_id, source, provider)
        validations = []
        original_verify = p._verify_staged_input

        def observe_verify(job, *args, **kwargs):
            if job.job_id == job_id:
                validations.append(p.state.get_job(job_id).status.value)
            return original_verify(job, *args, **kwargs)

        monkeypatch.setattr(p, "_verify_staged_input", observe_verify)
        timer.advance(75)
        errors = []
        for invocation in range(2):
            result = p.process_path(source)
            errors += _check_preservation(
                p, job_id, source, provider, before, f"duplicate-{invocation}",
            )
            if (result.status, result.error_code, result.attempts) != (
                JobStatus.FAILED, ErrorCode.STATE_FAILED, 1,
            ):
                errors.append(f"duplicate-{invocation}: returned denial result")
        if validations:
            errors.append(f"validated denied historical input before denial: {validations}")
        assert not errors, "\n".join(errors)


@pytest.mark.parametrize("enabled", [False, True])
@pytest.mark.parametrize("state", ["READY", "READY_RETRY", "DISPATCHING"])
def test_denied_queue_keeps_exact_artifacts_across_restarts_and_invocations(
    tmp_path, quiet_logger, state, enabled,
):
    """REVIEW-PRESERVE-02: startup cleanup must not erase history-denied staging."""
    config = config_for(tmp_path, enabled=enabled)
    source = write_image(config.paths.source / "x.png")
    response = capture_response(
        source, 429, enabled=enabled, headers={"retry-after": "75"},
    )
    timer = Clock()
    provider = Scripted(timer, [response])
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = _seed_queued(p, source, provider, timer, state=state)
        # DISPATCHING here is an unsubmitted claim: no active attempt exists.
        assert p.state.completed_attempts(job_id)[0].finished_at is not None
        before = _snapshot(p, job_id, source, provider)
    timer.advance(75)
    errors = []
    for restart in range(3):
        with processor_for(config, quiet_logger, timer, provider) as p:
            if restart:
                errors += _check_preservation(
                    p, job_id, source, provider, before, f"restart-{restart}-before-call",
                )
            for invocation in range(2):
                result = p.process_path(source)
                errors += _check_preservation(
                    p, job_id, source, provider, before,
                    f"restart-{restart}-invocation-{invocation}",
                )
                assert (result.status, result.error_code, result.attempts) == (
                    JobStatus.FAILED, ErrorCode.STATE_FAILED, 1,
                )
        # Distinguish close-time cleanup from next-construction cleanup.
        if restart == 0:
            assert Path(before["staged_path"]).read_bytes() == next(
                iter(before["staging_artifacts"].values()),
            )
    assert not errors, "\n".join(errors)


def test_unrelated_terminal_staging_still_gets_startup_cleanup(tmp_path, quiet_logger):
    """Control: fixing retention must not disable all terminal scratch cleanup."""
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "terminal.png")
    source_bytes = source.read_bytes()
    timer = Clock()
    provider = Scripted(timer)
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = admit(p, source).job_id
        job = p.state.get_job(job_id)
        stage = Path(job.staged_path)
        failure = p._quarantine(
            job, "input", AppError(ErrorCode.INVALID_IMAGE, "Synthetic invalid input."), 0,
        )
        failure_bytes = failure.read_bytes()
        # Model a crash between normal terminalization and scratch cleanup.
        p.state.mark_failed(job_id, ErrorCode.INVALID_IMAGE)
        assert stage.is_file()
    for _ in range(2):
        with processor_for(config, quiet_logger, timer, provider) as p:
            assert not stage.exists()
            assert p.state.get_job(job_id).error_code == ErrorCode.INVALID_IMAGE.value
            assert failure.read_bytes() == failure_bytes
            assert source.read_bytes() == source_bytes
            assert not rows(p) and not provider.requests
