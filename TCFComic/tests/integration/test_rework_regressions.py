from __future__ import annotations

import hashlib
import json
import logging
import multiprocessing
import os
import socket
import sqlite3
import threading
import time
import types
from dataclasses import replace
from datetime import UTC, datetime
from io import BytesIO
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.config import load_config
from tcfcomic.domain import (
    AppError,
    ErrorCode,
    FailureRecord,
    JobStatus,
    SourceSnapshot,
    TransformRequest,
)
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import (
    InProcessAttemptRunner,
    ShutdownDeadline,
    SubprocessAttemptRunner,
)
from tcfcomic.quarantine import write_failure_record


def _png_bytes() -> bytes:
    output = BytesIO()
    Image.new("RGB", (2, 2), (1, 2, 3)).save(output, format="PNG")
    return output.getvalue()


def _slow_child(provider, request, temp_path, result_queue) -> None:
    del provider, request, result_queue
    with temp_path.open("xb") as output:
        output.write(b"partial")
        output.flush()
        os.fsync(output.fileno())
    time.sleep(30)


def _silent_partial_child(provider, request, temp_path, result_queue) -> None:
    del provider, request, result_queue
    with temp_path.open("xb") as output:
        output.write(b"partial")
        output.flush()
        os.fsync(output.fileno())


def _admit(processor: Processor, source: Path, event: threading.Event):
    info = source.stat()
    from tcfcomic.domain import StableCandidate

    return processor._admit(
        source,
        candidate=StableCandidate(source, info.st_size, info.st_mtime_ns),
        shutdown_event=event,
    )


def test_retry_recovery_across_restart_honors_due_time_and_total_attempts(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        initial_delay_seconds=60,
        max_delay_seconds=60,
        max_attempts=3,
    )
    source = write_image(config.paths.source / "retry.png")
    event = threading.Event()
    first_provider = FakeProvider(["retryable"])
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(first_provider)
    )
    admitted = _admit(first, source, event)
    assert admitted.status == JobStatus.READY
    assert first._dispatch_one(event)
    job_id = admitted.job_id
    assert first.state.get_job(job_id).status == JobStatus.READY_RETRY
    first.close()

    second_provider = FakeProvider(["success"])
    second = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(second_provider)
    )
    assert second._dispatch_one(event) is False
    assert second_provider.requests == []
    second.state.connection.execute(
        "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?",
        ("2000-01-01T00:00:00+00:00", job_id),
    )
    assert second._dispatch_one(event)
    assert second.state.get_job(job_id).status == JobStatus.SUCCEEDED
    assert second.state.attempt_count(job_id) == 2
    second.close()


def test_retry_scheduler_progresses_unrelated_ready_job(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        initial_delay_seconds=60,
        max_delay_seconds=60,
    )
    first_source = write_image(config.paths.source / "first.png", color=(1, 1, 1))
    second_source = write_image(config.paths.source / "second.png", color=(2, 2, 2))
    provider = FakeProvider(["retryable", "success"])
    event = threading.Event()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as processor:
        first = _admit(processor, first_source, event)
        second = _admit(processor, second_source, event)
        assert processor._dispatch_one(event)
        assert processor.state.get_job(first.job_id).status == JobStatus.READY_RETRY
        assert processor._dispatch_one(event)
        assert processor.state.get_job(second.job_id).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 2


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("provider", "openai"),
        ("model", "changed-model"),
        ("prompt", "changed transformation prompt"),
    ],
)
def test_restart_request_identity_drift_fails_only_claimed_target(
    tmp_path: Path,
    quiet_logger,
    field: str,
    value: str,
) -> None:
    _, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / "target.png", color=(1, 2, 3)
    )
    unrelated_source = write_image(
        config.paths.source / "unrelated.png", color=(4, 5, 6)
    )
    event = threading.Event()
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    )
    target = _admit(first, target_source, event)
    unrelated = _admit(first, unrelated_source, event)
    original = first.state.get_job(target.job_id)
    target_stage = Path(original.staged_path)
    unrelated_stage = Path(first.state.get_job(unrelated.job_id).staged_path)
    first.close()

    config_field = "name" if field == "provider" else field
    provider_config = replace(config.provider, **{config_field: value})
    drifted = replace(config, provider=provider_config)
    replay_guard = FakeProvider()
    with Processor(
        drifted,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        assert restarted._dispatch_one(event, target.job_id)
        failed = restarted.state.get_job(target.job_id)
        untouched = restarted.state.get_job(unrelated.job_id)
        attempts = restarted.state.attempt_count(target.job_id)

    assert failed.status == JobStatus.FAILED
    assert failed.error_code == ErrorCode.STATE_FAILED.value
    assert (failed.provider, failed.model, failed.prompt_hash) == (
        original.provider,
        original.model,
        original.prompt_hash,
    )
    assert untouched.status == JobStatus.READY
    assert attempts == 0
    assert replay_guard.requests == []
    assert not target_stage.exists()
    assert unrelated_stage.exists()
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    record = json.loads(records[0].read_text(encoding="utf-8"))
    assert record["stage"] == "state"
    assert record["error_code"] == ErrorCode.STATE_FAILED.value


def test_admission_preserves_exact_request_identity_through_first_claim(
    tmp_path: Path, quiet_logger
) -> None:
    config_path, _ = write_config(tmp_path)
    exact_model = f"api_key=ordinary-model-{'x' * 160}"
    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "  model: gpt-image-2",
            f"  model: {exact_model}",
        ),
        encoding="utf-8",
    )
    config = load_config(config_path)
    source = write_image(config.paths.source / "identity-round-trip.png")
    event = threading.Event()
    provider = FakeProvider()

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        admitted = _admit(processor, source, event)
        expected = processor._request_identity()
        ready = processor.state.get_job(admitted.job_id)
        assert (ready.provider, ready.model, ready.prompt_hash) == (
            expected.provider,
            expected.model,
            expected.prompt_hash,
        )
        assert ready.model == exact_model

        assert processor._dispatch_one(event, admitted.job_id)
        succeeded = processor.state.get_job(admitted.job_id)

    assert succeeded.status == JobStatus.SUCCEEDED
    assert (succeeded.provider, succeeded.model, succeeded.prompt_hash) == (
        expected.provider,
        expected.model,
        expected.prompt_hash,
    )
    assert len(provider.requests) == 1
    assert provider.requests[0].model == exact_model


def test_watch_continues_after_request_identity_mismatch(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    stale_source = write_image(
        config.paths.source / "stale.png", color=(1, 1, 1)
    )
    event = threading.Event()
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    )
    stale = _admit(first, stale_source, event)
    first.close()

    fresh_source = write_image(
        config.paths.source / "fresh.png", color=(2, 2, 2)
    )
    drifted = replace(
        config,
        provider=replace(config.provider, prompt="changed prompt"),
    )
    provider = FakeProvider()
    with Processor(
        drifted,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as restarted:
        restarted.watch(max_cycles=2)
        jobs = restarted.state.list_jobs()
        stale_job = restarted.state.get_job(stale.job_id)

    fresh_job = next(
        job for job in jobs if job.job_id != stale.job_id
    )
    assert stale_job.status == JobStatus.FAILED
    assert stale_job.error_code == ErrorCode.STATE_FAILED.value
    assert fresh_job.status == JobStatus.SUCCEEDED
    assert [request.job_id for request in provider.requests] == [
        fresh_job.job_id
    ]
    assert fresh_source.exists()


def test_reduced_input_limit_after_restart_is_input_failure_before_read(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / "target-limit.png", color=(3, 4, 5)
    )
    unrelated_source = write_image(
        config.paths.source / "unrelated-limit.png", color=(6, 7, 8)
    )
    event = threading.Event()
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    )
    target = _admit(first, target_source, event)
    unrelated = _admit(first, unrelated_source, event)
    target_stage = Path(first.state.get_job(target.job_id).staged_path)
    unrelated_stage = Path(first.state.get_job(unrelated.job_id).staged_path)
    durable_size = target_stage.stat().st_size
    first.close()

    reduced = replace(
        config,
        limits=replace(
            config.limits,
            max_input_bytes=durable_size - 1,
        ),
    )
    replay_guard = FakeProvider()
    with Processor(
        reduced,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        real_open = Path.open

        def guarded_open(path: Path, *args, **kwargs):
            mode = args[0] if args else kwargs.get("mode", "r")
            if path == target_stage and "r" in mode:
                pytest.fail("over-limit durable staged input was read")
            return real_open(path, *args, **kwargs)

        monkeypatch.setattr(Path, "open", guarded_open)
        assert restarted._dispatch_one(event, target.job_id)
        failed = restarted.state.get_job(target.job_id)
        untouched = restarted.state.get_job(unrelated.job_id)
        attempts = restarted.state.attempt_count(target.job_id)

    assert failed.status == JobStatus.FAILED
    assert failed.error_code == ErrorCode.IMAGE_LIMIT_EXCEEDED.value
    assert untouched.status == JobStatus.READY
    assert attempts == 0
    assert replay_guard.requests == []
    assert not target_stage.exists()
    assert unrelated_stage.exists()


@pytest.mark.parametrize(
    ("failure_mode", "expected_error", "expected_attempts"),
    [
        ("identity", ErrorCode.STATE_FAILED, 0),
        ("reduced-input-limit", ErrorCode.IMAGE_LIMIT_EXCEEDED, 0),
        ("exhausted-at-claim", ErrorCode.PROVIDER_RETRYABLE, 1),
    ],
)
def test_preattempt_terminal_cleanup_failure_preserves_state_and_recovers(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    failure_mode: str,
    expected_error: ErrorCode,
    expected_attempts: int,
) -> None:
    _, config = write_config(
        tmp_path,
        max_attempts=2,
        initial_delay_seconds=60,
        max_delay_seconds=60,
    )
    source = write_image(config.paths.source / f"{failure_mode}.png")
    event = threading.Event()
    first_provider = FakeProvider(
        ["retryable"] if failure_mode == "exhausted-at-claim" else None
    )
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(first_provider),
    )
    admitted = _admit(first, source, event)
    staged = Path(first.state.get_job(admitted.job_id).staged_path)
    staged_size = staged.stat().st_size
    if failure_mode == "exhausted-at-claim":
        assert first._dispatch_one(event, admitted.job_id)
        first.state.connection.execute(
            "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?",
            ("2000-01-01T00:00:00+00:00", admitted.job_id),
        )
    first.close()

    if failure_mode == "identity":
        current = replace(
            config,
            provider=replace(config.provider, model="changed-model"),
        )
    elif failure_mode == "reduced-input-limit":
        current = replace(
            config,
            limits=replace(
                config.limits,
                max_input_bytes=staged_size - 1,
            ),
        )
    else:
        current = replace(
            config,
            retry=replace(config.retry, max_attempts=1),
        )

    replay_guard = FakeProvider()
    restarted = Processor(
        current,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    )
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged:
            raise PermissionError("synthetic terminal cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    with pytest.raises(AppError) as caught:
        restarted._dispatch_one(event, admitted.job_id)
    terminal = restarted.state.get_job(admitted.job_id)
    usage = restarted._staging_usage()
    attempts = restarted.state.attempt_count(admitted.job_id)
    restarted.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert terminal.status == JobStatus.FAILED
    assert terminal.error_code == expected_error.value
    assert attempts == expected_attempts
    assert usage == (1, staged_size)
    assert staged.exists()
    assert replay_guard.requests == []
    expected_initial_calls = 1 if failure_mode == "exhausted-at-claim" else 0
    assert len(first_provider.requests) == expected_initial_calls

    with pytest.raises(AppError) as restart_error:
        Processor(
            current,
            quiet_logger,
            runner=InProcessAttemptRunner(FakeProvider()),
        )
    assert restart_error.value.code == ErrorCode.STATE_FAILED
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        persisted = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (admitted.job_id,),
        ).fetchone()
    finally:
        connection.close()
    assert persisted == (JobStatus.FAILED.value, expected_error.value)

    monkeypatch.setattr(Path, "unlink", real_unlink)
    final_replay_guard = FakeProvider()
    with Processor(
        current,
        quiet_logger,
        runner=InProcessAttemptRunner(final_replay_guard),
    ) as recovered:
        final = recovered.state.get_job(admitted.job_id)
        assert recovered.state.attempt_count(admitted.job_id) == expected_attempts
    assert final.status == JobStatus.FAILED
    assert final.error_code == expected_error.value
    assert not staged.exists()
    assert final_replay_guard.requests == []


@pytest.mark.parametrize("existing_damage", ["missing", "tampered"])
def test_duplicate_queued_stage_repairs_from_fresh_and_processes_once(
    tmp_path: Path,
    quiet_logger,
    existing_damage: str,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "duplicate.png", color=(1, 2, 3))
    provider = FakeProvider()
    event = threading.Event()

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        admitted = _admit(processor, source, event)
        old_staged = Path(
            processor.state.get_job(admitted.job_id).staged_path
        )
        if existing_damage == "missing":
            old_staged.unlink()
        else:
            write_image(old_staged, color=(9, 8, 7))

        result = processor.process_path(source)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert result.status == JobStatus.SUCCEEDED
    assert durable.status == JobStatus.SUCCEEDED
    assert attempts == 1
    assert len(provider.requests) == 1
    assert provider.requests[0].job_id == admitted.job_id
    assert provider.requests[0].source.staged_path != old_staged
    assert not old_staged.exists()


@pytest.mark.parametrize("owner_status", [JobStatus.READY, JobStatus.READY_RETRY])
def test_duplicate_stage_repeat_repair_processes_latest_replacement_once(
    tmp_path: Path,
    quiet_logger,
    owner_status: JobStatus,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(
        config.paths.source / f"repeat-repair-{owner_status.value.lower()}.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / f"unrelated-{owner_status.value.lower()}.png",
        color=(4, 5, 6),
    )
    provider = FakeProvider()
    event = threading.Event()
    retry_due = (
        "2000-01-01T00:00:00+00:00"
        if owner_status == JobStatus.READY_RETRY
        else None
    )

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        target = _admit(processor, source, event)
        unrelated = _admit(processor, unrelated_source, event)
        if retry_due is not None:
            processor.state.connection.execute(
                """
                UPDATE jobs SET status = ?, next_attempt_at = ?
                 WHERE job_id = ?
                """,
                (owner_status.value, retry_due, target.job_id),
            )
        unrelated_before = processor.state.get_job(unrelated.job_id)
        first_stage = Path(
            processor.state.get_job(target.job_id).staged_path
        )
        first_stage.unlink()

        first_repair = _admit(processor, source, event)
        repaired_once = processor.state.get_job(target.job_id)
        second_stage = Path(repaired_once.staged_path)
        assert first_repair.status == owner_status
        assert repaired_once.status == owner_status
        assert repaired_once.next_attempt_at == retry_due
        assert second_stage != first_stage
        assert not first_stage.exists()
        assert second_stage.exists()

        write_image(second_stage, color=(9, 8, 7))
        result = processor.process_path(source)
        durable = processor.state.get_job(target.job_id)
        final_stage = Path(durable.staged_path)
        unrelated_after = processor.state.get_job(unrelated.job_id)
        attempts = processor.state.attempt_count(target.job_id)

    assert result.status == JobStatus.SUCCEEDED
    assert durable.status == JobStatus.SUCCEEDED
    assert durable.next_attempt_at is None
    assert final_stage != second_stage
    assert not second_stage.exists()
    assert not final_stage.exists()
    assert attempts == 1
    assert len(provider.requests) == 1
    assert provider.requests[0].job_id == target.job_id
    assert provider.requests[0].source.staged_path == final_stage
    assert result.output_path is not None
    Image.open(result.output_path).verify()
    assert unrelated_after == unrelated_before


@pytest.mark.parametrize("owner_status", [JobStatus.READY, JobStatus.READY_RETRY])
def test_second_duplicate_stage_repair_cleanup_failure_restarts_replacement(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    owner_status: JobStatus,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(
        config.paths.source / f"cleanup-restart-{owner_status.value.lower()}.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source
        / f"cleanup-unrelated-{owner_status.value.lower()}.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    first_provider = FakeProvider()
    retry_due = (
        "2000-01-01T00:00:00+00:00"
        if owner_status == JobStatus.READY_RETRY
        else None
    )
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(first_provider),
    )
    target = _admit(processor, source, event)
    unrelated = _admit(processor, unrelated_source, event)
    if retry_due is not None:
        processor.state.connection.execute(
            """
            UPDATE jobs SET status = ?, next_attempt_at = ?
             WHERE job_id = ?
            """,
            (owner_status.value, retry_due, target.job_id),
        )
    unrelated_before = processor.state.get_job(unrelated.job_id)
    first_stage = Path(processor.state.get_job(target.job_id).staged_path)
    first_stage.unlink()
    first_repair = _admit(processor, source, event)
    assert first_repair.status == owner_status
    superseded_stage = Path(
        processor.state.get_job(target.job_id).staged_path
    )
    superseded_stage.write_bytes(b"tampered durable stage")
    real_unlink = Path.unlink
    superseded_unlink_attempts: list[Path] = []

    def fail_superseded_unlink(path: Path, *args, **kwargs):
        if path == superseded_stage:
            superseded_unlink_attempts.append(path)
            raise PermissionError("synthetic superseded cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_superseded_unlink)
    with pytest.raises(AppError) as caught:
        processor.process_path(source)
    committed = processor.state.get_job(target.job_id)
    replacement_stage = Path(committed.staged_path)
    unrelated_after_failure = processor.state.get_job(unrelated.job_id)
    attempts_after_failure = processor.state.attempt_count(target.job_id)
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert committed.status == owner_status
    assert committed.next_attempt_at == retry_due
    assert replacement_stage != superseded_stage
    assert replacement_stage.exists()
    assert superseded_stage.exists()
    assert superseded_unlink_attempts == [superseded_stage]
    assert attempts_after_failure == 0
    assert first_provider.requests == []
    assert unrelated_after_failure == unrelated_before

    monkeypatch.setattr(Path, "unlink", real_unlink)
    restart_provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(restart_provider),
    ) as restarted:
        recovered = restarted.state.get_job(target.job_id)
        assert recovered.status == owner_status
        assert recovered.next_attempt_at == retry_due
        assert Path(recovered.staged_path) == replacement_stage
        assert replacement_stage.exists()
        assert not superseded_stage.exists()

        def reject_extra_repair(*args, **kwargs):
            pytest.fail("valid committed replacement was repaired again")

        monkeypatch.setattr(
            restarted.state,
            "compare_and_swap_staged_path",
            reject_extra_repair,
        )
        result = restarted.process_path(source)
        durable = restarted.state.get_job(target.job_id)
        unrelated_after_restart = restarted.state.get_job(unrelated.job_id)
        attempts = restarted.state.attempt_count(target.job_id)

    assert result.status == JobStatus.SUCCEEDED
    assert durable.status == JobStatus.SUCCEEDED
    assert Path(durable.staged_path) == replacement_stage
    assert durable.next_attempt_at is None
    assert attempts == 1
    assert len(restart_provider.requests) == 1
    assert restart_provider.requests[0].job_id == target.job_id
    assert restart_provider.requests[0].source.staged_path == replacement_stage
    assert not replacement_stage.exists()
    assert result.output_path is not None
    Image.open(result.output_path).verify()
    assert unrelated_after_restart == unrelated_before


def test_duplicate_fresh_repair_failure_removes_terminal_stages_without_dispatch(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "fresh-failure.png")
    provider = FakeProvider()
    event = threading.Event()
    import tcfcomic.processor as processor_module

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        admitted = _admit(processor, source, event)
        old_staged = Path(
            processor.state.get_job(admitted.job_id).staged_path
        )
        old_staged.write_bytes(b"tampered durable stage")
        real_stage_candidate = processor_module.stage_candidate
        fresh_paths: list[Path] = []

        def corrupt_fresh(*args, **kwargs):
            fresh = real_stage_candidate(*args, **kwargs)
            fresh_paths.append(fresh.staged_path)
            fresh.staged_path.write_bytes(b"tampered fresh stage")
            return fresh

        monkeypatch.setattr(
            processor_module,
            "stage_candidate",
            corrupt_fresh,
        )
        result = processor.process_path(source)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert result.status == JobStatus.FAILED
    assert result.error_code == ErrorCode.STATE_FAILED
    assert durable.status == JobStatus.FAILED
    assert Path(durable.staged_path) == old_staged
    assert not old_staged.exists()
    assert fresh_paths and not fresh_paths[0].exists()
    assert attempts == 0
    assert provider.requests == []


def test_duplicate_stage_repair_cas_loss_deletes_only_fresh_without_dispatch(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "cas-loss.png")
    provider = FakeProvider()
    event = threading.Event()

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        admitted = _admit(processor, source, event)
        old_staged = Path(
            processor.state.get_job(admitted.job_id).staged_path
        )
        old_staged.write_bytes(b"tampered durable stage")
        replacements: list[Path] = []

        def lose_compare_and_swap(
            job_id: str,
            expected_staged_path: Path,
            replacement_staged_path: Path,
        ) -> bool:
            assert job_id == admitted.job_id
            assert expected_staged_path == old_staged
            replacements.append(replacement_staged_path)
            return False

        monkeypatch.setattr(
            processor.state,
            "compare_and_swap_staged_path",
            lose_compare_and_swap,
        )
        result = _admit(processor, source, event)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert result.status == JobStatus.READY
    assert durable.status == JobStatus.READY
    assert Path(durable.staged_path) == old_staged
    assert old_staged.read_bytes() == b"tampered durable stage"
    assert replacements and not replacements[0].exists()
    assert attempts == 0
    assert provider.requests == []


def test_permanent_provider_failure_does_not_stop_later_job(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    first_source = write_image(config.paths.source / "rejected.png", color=(3, 3, 3))
    second_source = write_image(config.paths.source / "accepted.png", color=(4, 4, 4))
    provider = FakeProvider(["permanent", "success"])
    event = threading.Event()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as processor:
        first = _admit(processor, first_source, event)
        second = _admit(processor, second_source, event)
        assert processor._dispatch_one(event)
        assert processor.state.get_job(first.job_id).status == JobStatus.FAILED
        assert processor._dispatch_one(event)
        assert processor.state.get_job(second.job_id).status == JobStatus.SUCCEEDED
    assert len(provider.requests) == 2


def test_total_attempt_limit_remains_bounded_across_restarts(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path, max_attempts=3)
    source = write_image(config.paths.source / "bounded.png")
    event = threading.Event()
    providers: list[FakeProvider] = []
    job_id = ""
    for index in range(3):
        provider = FakeProvider(["retryable"])
        providers.append(provider)
        with Processor(
            config, quiet_logger, runner=InProcessAttemptRunner(provider)
        ) as processor:
            if index == 0:
                job_id = _admit(processor, source, event).job_id
            assert processor._dispatch_one(event)
            expected = JobStatus.FAILED if index == 2 else JobStatus.READY_RETRY
            assert processor.state.get_job(job_id).status == expected
    assert sum(len(provider.requests) for provider in providers) == 3


def test_reduced_attempt_limit_fails_at_claim_and_removes_stage(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        max_attempts=2,
        initial_delay_seconds=60,
        max_delay_seconds=60,
    )
    source = write_image(config.paths.source / "reduced-attempt-limit.png")
    event = threading.Event()
    first_provider = FakeProvider(["retryable"])
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(first_provider),
    )
    admitted = _admit(first, source, event)
    staged = Path(first.state.get_job(admitted.job_id).staged_path)
    assert first._dispatch_one(event, admitted.job_id)
    first.state.connection.execute(
        "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?",
        ("2000-01-01T00:00:00+00:00", admitted.job_id),
    )
    first.close()

    reduced = replace(
        config,
        retry=replace(config.retry, max_attempts=1),
    )
    replay_guard = FakeProvider()
    with Processor(
        reduced,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        assert restarted._dispatch_one(event, admitted.job_id)
        failed = restarted.state.get_job(admitted.job_id)
        attempts = restarted.state.attempt_count(admitted.job_id)

    assert failed.status == JobStatus.FAILED
    assert failed.error_code == ErrorCode.PROVIDER_RETRYABLE.value
    assert attempts == 1
    assert len(first_provider.requests) == 1
    assert replay_guard.requests == []
    assert not staged.exists()


def test_post_rename_crash_recovers_without_duplicate_provider_call(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "publish.png")
    provider = FakeProvider()

    def crash(point: str) -> None:
        if point == "after_rename":
            raise RuntimeError("simulated process loss")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        publication_hook=crash,
    )
    with pytest.raises(RuntimeError, match="simulated process loss"):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    assert job.status == JobStatus.OUTPUT_VERIFIED
    assert job.output_name and job.output_sha256
    assert (config.paths.destination / job.output_name).exists()
    processor.close()

    restarted_provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(restarted_provider),
    ) as restarted:
        recovered = restarted.state.get_job(job.job_id)
        assert recovered.status == JobStatus.SUCCEEDED
    assert restarted_provider.requests == []
    assert len(list(config.paths.destination.glob("*.png"))) == 1


@pytest.mark.parametrize(
    "recovery_status",
    [JobStatus.PUBLISHED, JobStatus.SUCCEEDED],
)
def test_completed_recovery_rejects_cross_job_staged_cleanup_and_keeps_owner_usable(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    recovery_status: JobStatus,
) -> None:
    _, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / f"target-{recovery_status.value.lower()}.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / f"unrelated-{recovery_status.value.lower()}.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    first_provider = FakeProvider()
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(first_provider),
    )
    result = processor.process_path(target_source)
    target = processor.state.get_job(result.job_id)
    unrelated = _admit(processor, unrelated_source, event)
    unrelated_stage = Path(
        processor.state.get_job(unrelated.job_id).staged_path
    )
    assert result.output_path is not None
    target_output = result.output_path
    target_output_bytes = target_output.read_bytes()
    target_output_mtime_ns = target_output.stat().st_mtime_ns
    target_attempts_before = processor.state.attempt_count(target.job_id)
    processor.state.connection.execute(
        "UPDATE jobs SET status = ?, staged_path = ? WHERE job_id = ?",
        (recovery_status.value, str(unrelated_stage), target.job_id),
    )
    processor.close()

    import tcfcomic.processor as processor_module

    promotion_targets: list[Path] = []
    real_promote = processor_module.promote_planned_output

    def track_promotion(temp_path: Path, final_path: Path, digest: str) -> None:
        promotion_targets.append(final_path)
        real_promote(temp_path, final_path, digest)

    monkeypatch.setattr(
        processor_module,
        "promote_planned_output",
        track_promotion,
    )
    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        failed = restarted.state.get_job(target.job_id)
        ready = restarted.state.get_job(unrelated.job_id)
        target_attempts_after = restarted.state.attempt_count(target.job_id)
        assert failed.status == JobStatus.FAILED
        assert failed.error_code == ErrorCode.STATE_FAILED.value
        assert ready.status == JobStatus.READY
        assert target_attempts_after == target_attempts_before
        assert replay_guard.requests == []
        assert promotion_targets == []
        assert unrelated_stage.exists()
        assert target_output.read_bytes() == target_output_bytes
        assert target_output.stat().st_mtime_ns == target_output_mtime_ns
        assert restarted._dispatch_one(event, unrelated.job_id)
        processed = restarted.state.get_job(unrelated.job_id)

    assert processed.status == JobStatus.SUCCEEDED
    assert [request.job_id for request in replay_guard.requests] == [
        unrelated.job_id
    ]
    assert len(promotion_targets) == 1
    assert not unrelated_stage.exists()
    assert target_output.read_bytes() == target_output_bytes
    assert target_output.stat().st_mtime_ns == target_output_mtime_ns
    record = json.loads(
        (config.paths.quarantine / f"{target.job_id}.json").read_text(
            encoding="utf-8"
        )
    )
    assert record["error_code"] == ErrorCode.STATE_FAILED.value


def test_terminal_reconciliation_rejects_cross_job_staged_cleanup_and_keeps_owner_usable(
    tmp_path: Path,
    quiet_logger,
) -> None:
    _, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / "failed-cleanup-target.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / "failed-cleanup-owner.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    )
    target = _admit(processor, target_source, event)
    unrelated = _admit(processor, unrelated_source, event)
    target_stage = Path(processor.state.get_job(target.job_id).staged_path)
    unrelated_stage = Path(
        processor.state.get_job(unrelated.job_id).staged_path
    )
    processor.state.mark_failed(target.job_id, ErrorCode.PROVIDER_PERMANENT)
    processor.state.connection.execute(
        "UPDATE jobs SET staged_path = ? WHERE job_id = ?",
        (str(unrelated_stage), target.job_id),
    )
    processor.close()

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        failed = restarted.state.get_job(target.job_id)
        ready = restarted.state.get_job(unrelated.job_id)
        assert failed.status == JobStatus.FAILED
        assert failed.error_code == ErrorCode.STATE_FAILED.value
        assert ready.status == JobStatus.READY
        assert restarted.state.attempt_count(target.job_id) == 0
        assert replay_guard.requests == []
        assert unrelated_stage.exists()
        assert restarted._dispatch_one(event, unrelated.job_id)
        processed = restarted.state.get_job(unrelated.job_id)

    assert processed.status == JobStatus.SUCCEEDED
    assert [request.job_id for request in replay_guard.requests] == [
        unrelated.job_id
    ]
    assert not target_stage.exists()
    assert not unrelated_stage.exists()
    record = json.loads(
        (config.paths.quarantine / f"{target.job_id}.json").read_text(
            encoding="utf-8"
        )
    )
    assert record["error_code"] == ErrorCode.STATE_FAILED.value


@pytest.mark.parametrize(
    "reduced_limit",
    ["bytes", "width", "pixels"],
)
def test_recovered_verified_temp_is_revalidated_before_promotion(
    tmp_path: Path,
    quiet_logger,
    reduced_limit: str,
) -> None:
    _, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / f"reduced-output-{reduced_limit}.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / f"unrelated-{reduced_limit}.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    provider = FakeProvider()

    def crash(point: str) -> None:
        if point == "before_rename":
            raise RuntimeError("simulated loss before rename")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        publication_hook=crash,
    )
    target = _admit(processor, target_source, event)
    unrelated = _admit(processor, unrelated_source, event)
    unrelated_stage = Path(
        processor.state.get_job(unrelated.job_id).staged_path
    )
    with pytest.raises(RuntimeError, match="before rename"):
        processor._dispatch_one(event, target.job_id)
    interrupted = processor.state.get_job(target.job_id)
    assert interrupted.status == JobStatus.OUTPUT_VERIFIED
    assert interrupted.temp_name is not None
    opaque_token = interrupted.temp_name[1:-4]
    assert len(opaque_token) == 32
    assert all(character in "0123456789abcdef" for character in opaque_token)
    assert interrupted.job_id not in interrupted.temp_name
    temp_path = config.paths.destination / interrupted.temp_name
    target_stage = Path(interrupted.staged_path)
    assert temp_path.is_file()
    assert interrupted.output_name is not None
    assert not (config.paths.destination / interrupted.output_name).exists()
    processor.close()

    if reduced_limit == "bytes":
        limits = replace(
            config.limits,
            max_output_bytes=temp_path.stat().st_size - 1,
        )
    elif reduced_limit == "width":
        limits = replace(config.limits, max_width=31)
    else:
        limits = replace(config.limits, max_pixels=1023)
    reduced = replace(config, limits=limits)
    replay_guard = FakeProvider()

    with Processor(
        reduced,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        failed = restarted.state.get_job(target.job_id)
        untouched = restarted.state.get_job(unrelated.job_id)
        attempts = restarted.state.attempt_count(target.job_id)

    assert failed.status == JobStatus.FAILED
    assert failed.error_code == ErrorCode.OUTPUT_LIMIT_EXCEEDED.value
    assert untouched.status == JobStatus.READY
    assert attempts == 1
    assert replay_guard.requests == []
    assert len(provider.requests) == 1
    assert not target_stage.exists()
    assert not temp_path.exists()
    assert unrelated_stage.exists()
    assert not list(config.paths.destination.glob("*.png"))


def test_recovered_output_limit_failure_is_durable_before_staged_cleanup(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "recovery-cleanup-debt.png")
    provider = FakeProvider()

    def crash(point: str) -> None:
        if point == "before_rename":
            raise RuntimeError("simulated loss before rename")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        publication_hook=crash,
    )
    with pytest.raises(RuntimeError, match="before rename"):
        processor.process_path(source)
    interrupted = processor.state.list_jobs()[0]
    assert interrupted.temp_name is not None
    temp_path = config.paths.destination / interrupted.temp_name
    staged_path = Path(interrupted.staged_path)
    assert temp_path.is_file()
    assert interrupted.output_name is not None
    processor.close()

    reduced = replace(
        config,
        limits=replace(
            config.limits,
            max_output_bytes=temp_path.stat().st_size - 1,
        ),
    )
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged_path:
            raise PermissionError("synthetic recovery cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    replay_guard = FakeProvider()
    with pytest.raises(AppError) as caught:
        Processor(
            reduced,
            quiet_logger,
            runner=InProcessAttemptRunner(replay_guard),
        )

    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        persisted = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (interrupted.job_id,),
        ).fetchone()
    finally:
        connection.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert persisted == (
        JobStatus.FAILED.value,
        ErrorCode.OUTPUT_LIMIT_EXCEEDED.value,
    )
    assert replay_guard.requests == []
    assert staged_path.exists()
    assert temp_path.exists()
    assert not (config.paths.destination / interrupted.output_name).exists()

    monkeypatch.setattr(Path, "unlink", real_unlink)
    final_replay_guard = FakeProvider()
    with Processor(
        reduced,
        quiet_logger,
        runner=InProcessAttemptRunner(final_replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(interrupted.job_id)
        attempts = restarted.state.attempt_count(interrupted.job_id)

    assert recovered.status == JobStatus.FAILED
    assert recovered.error_code == ErrorCode.OUTPUT_LIMIT_EXCEEDED.value
    assert attempts == 1
    assert final_replay_guard.requests == []
    assert not staged_path.exists()
    assert not temp_path.exists()
    assert not list(config.paths.destination.glob("*.png"))


def test_after_rename_app_error_reconciles_success_without_duplicate_provider_call(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "publish.png")
    provider = FakeProvider()

    def fail(point: str) -> None:
        if point == "after_rename":
            raise AppError(
                ErrorCode.PUBLICATION_FAILED,
                "simulated publication failure after rename",
            )

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        publication_hook=fail,
    ) as processor:
        result = processor.process_path(source)
        job = processor.state.get_job(result.job_id)

    assert result.status == JobStatus.SUCCEEDED
    assert job.status == JobStatus.SUCCEEDED
    assert result.output_path and result.output_path.is_file()
    assert len(list(config.paths.destination.glob("*.png"))) == 1
    assert len(provider.requests) == 1


def test_restart_replans_when_recorded_final_conflicts_before_rename(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "publish.png")
    original_source = source.read_bytes()
    conflict = b"preexisting-conflicting-bytes"
    processor: Processor

    def crash(point: str) -> None:
        if point != "before_rename":
            return
        planned = processor.state.list_jobs()[0]
        assert planned.output_name
        (config.paths.destination / planned.output_name).write_bytes(conflict)
        raise RuntimeError("simulated loss before rename")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
        publication_hook=crash,
    )
    with pytest.raises(RuntimeError, match="before rename"):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    old_final = config.paths.destination / job.output_name
    assert old_final.read_bytes() == conflict
    assert job.temp_name and (config.paths.destination / job.temp_name).exists()
    processor.close()

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(job.job_id)
        assert recovered.status == JobStatus.SUCCEEDED
        assert recovered.output_name != job.output_name
        new_final = config.paths.destination / recovered.output_name
        Image.open(new_final).verify()
    assert replay_guard.requests == []
    assert old_final.read_bytes() == conflict
    assert source.read_bytes() == original_source


def test_recovered_dispatching_is_quarantined_and_artifacts_cleaned(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "active.png")
    source_digest = hashlib.sha256(source.read_bytes()).hexdigest()
    event = threading.Event()
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    admitted = _admit(first, source, event)
    claimed = first.state.claim_next_ready(
        datetime.now(UTC)
    )
    assert claimed and claimed.job_id == admitted.job_id
    first.state.begin_attempt(admitted.job_id)
    temp_name = f".{'d' * 32}.tmp"
    first.state.set_current_temp_name(admitted.job_id, temp_name)
    staged = Path(first.state.get_job(admitted.job_id).staged_path)
    recorded_temp = config.paths.destination / temp_name
    recorded_temp.write_bytes(b"partial")
    first.close()

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)
        assert recovered.status == JobStatus.AMBIGUOUS
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    assert json.loads(records[0].read_text())["stage"] == "recovery"
    assert not staged.exists()
    assert not recorded_temp.exists()
    assert recovered.temp_name is None
    assert replay_guard.requests == []
    assert hashlib.sha256(source.read_bytes()).hexdigest() == source_digest


def test_restart_rearms_initial_claim_without_attempt(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "claimed-ready.png")
    source_digest = hashlib.sha256(source.read_bytes()).hexdigest()
    event = threading.Event()
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    admitted = _admit(first, source, event)
    claimed = first.state.claim_next_ready(datetime.now(UTC))
    assert claimed and claimed.job_id == admitted.job_id
    assert first.state.attempt_count(admitted.job_id) == 0
    staged = Path(first.state.get_job(admitted.job_id).staged_path)
    first.close()

    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)
        assert recovered.status == JobStatus.READY
        assert recovered.next_attempt_at is None
        result = restarted.process_path(source)
        assert result.status == JobStatus.SUCCEEDED
        assert restarted.state.attempt_count(admitted.job_id) == 1

    assert len(provider.requests) == 1
    assert not list(config.paths.quarantine.glob("*.json"))
    assert not staged.exists()
    assert hashlib.sha256(source.read_bytes()).hexdigest() == source_digest


def test_restart_rearms_retry_claim_without_new_attempt(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        initial_delay_seconds=60,
        max_delay_seconds=60,
        max_attempts=3,
    )
    source = write_image(config.paths.source / "claimed-retry.png")
    source_digest = hashlib.sha256(source.read_bytes()).hexdigest()
    event = threading.Event()
    first_provider = FakeProvider(["retryable"])
    first = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(first_provider),
    )
    admitted = _admit(first, source, event)
    assert first._dispatch_one(event)
    first.state.connection.execute(
        "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?",
        ("2000-01-01T00:00:00+00:00", admitted.job_id),
    )
    claimed = first.state.claim_next_ready(datetime.now(UTC))
    assert claimed and claimed.job_id == admitted.job_id
    assert first.state.attempt_count(admitted.job_id) == 1
    prior_due = claimed.next_attempt_at
    first.close()

    provider = FakeProvider(["success"])
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)
        assert recovered.status == JobStatus.READY_RETRY
        assert recovered.next_attempt_at == prior_due
        result = restarted.process_path(source)
        assert result.status == JobStatus.SUCCEEDED
        assert restarted.state.attempt_count(admitted.job_id) == 2

    assert len(first_provider.requests) == 1
    assert len(provider.requests) == 1
    assert not list(config.paths.quarantine.glob("*.json"))
    assert hashlib.sha256(source.read_bytes()).hexdigest() == source_digest


def test_startup_removes_unreferenced_staging_but_preserves_unrecorded_temps(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "ready.png")
    event = threading.Event()
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    admitted = _admit(first, source, event)
    referenced = Path(first.state.get_job(admitted.job_id).staged_path)
    orphan_stage = first.staging / f"{'b' * 32}.input"
    orphan_stage.write_bytes(b"orphan")
    unrecorded_provider_shaped_temp = (
        config.paths.destination / f".{'c' * 32}.1.{'d' * 32}.tmp"
    )
    operator_temp = config.paths.destination / "operator-notes.tmp"
    unrecorded_provider_shaped_temp.write_bytes(b"unrecorded")
    operator_temp.write_bytes(b"operator-owned")
    first.close()

    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ):
        assert referenced.exists()
        assert not orphan_stage.exists()
        assert (
            unrecorded_provider_shaped_temp.read_bytes()
            == b"unrecorded"
        )
        assert operator_temp.read_bytes() == b"operator-owned"


def test_startup_rejects_duplicate_durable_temp_references_without_deletion(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    first_source = write_image(config.paths.source / "first.png")
    second_source = write_image(config.paths.source / "second.png")
    event = threading.Event()
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    first_job = _admit(first, first_source, event)
    second_job = _admit(first, second_source, event)
    temp_name = f".{'e' * 32}.tmp"
    shared_temp = config.paths.destination / temp_name
    shared_temp.write_bytes(b"shared")
    first.state.connection.execute(
        "UPDATE jobs SET temp_name = ? WHERE job_id IN (?, ?)",
        (temp_name, first_job.job_id, second_job.job_id),
    )
    first.close()

    with pytest.raises(AppError) as caught:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(FakeProvider()),
        )

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert shared_temp.read_bytes() == b"shared"


def test_tampered_recovery_output_path_is_rejected_without_escape(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "tampered.png")

    def crash(point: str) -> None:
        raise RuntimeError(point)

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
        publication_hook=crash,
    )
    with pytest.raises(RuntimeError):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    escaped = tmp_path / "escaped.png"
    processor.state.connection.execute(
        "UPDATE jobs SET output_name = ? WHERE job_id = ?",
        (str(escaped), job.job_id),
    )
    processor.close()

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as restarted:
        assert restarted.state.get_job(job.job_id).status == JobStatus.FAILED
    assert not escaped.exists()


def test_locked_candidate_is_rearmed_and_watch_continues(
    tmp_path: Path, quiet_logger, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "locked.png")
    provider = FakeProvider()
    import tcfcomic.processor as processor_module

    real_stage = processor_module.stage_candidate
    calls = 0

    def fail_once(*args, **kwargs):
        nonlocal calls
        calls += 1
        if calls == 1:
            raise PermissionError("sharing violation")
        return real_stage(*args, **kwargs)

    monkeypatch.setattr(processor_module, "stage_candidate", fail_once)
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as processor:
        processor.watch(max_cycles=3)
    assert calls >= 2
    assert len(provider.requests) == 1
    assert source.exists()


def test_watch_acknowledges_unchanged_oversized_candidate_until_signature_changes(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = config.paths.source / "oversized.png"
    original = b"x" * (config.limits.max_input_bytes + 1)
    source.write_bytes(original)
    provider = FakeProvider()
    events: list[dict[str, object]] = []
    import tcfcomic.processor as processor_module

    def capture_event(logger, level, event, **fields) -> None:
        del logger, level
        if event == "watch_item_failed":
            events.append(fields)

    class CyclingEvent:
        def __init__(self) -> None:
            self.waits = 0

        def is_set(self) -> bool:
            return False

        def wait(self, timeout: float) -> bool:
            del timeout
            self.waits += 1
            if self.waits == 3:
                assert source.read_bytes() == original
                source.write_bytes(original + b"x")
            return False

    monkeypatch.setattr(processor_module, "log_event", capture_event)
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        processor.watch(shutdown_event=CyclingEvent(), max_cycles=6)
        assert processor.state.list_jobs() == []

    assert [event["error_code"] for event in events] == [
        ErrorCode.IMAGE_LIMIT_EXCEEDED.value,
        ErrorCode.IMAGE_LIMIT_EXCEEDED.value,
    ]
    assert provider.requests == []
    assert source.read_bytes() == original + b"x"


def test_real_stability_watch_dispatch_and_detection_latency(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path, stable_seconds=0.08, poll_interval_seconds=0.01
    )
    provider = FakeProvider()
    errors: list[BaseException] = []

    def run_watch() -> None:
        try:
            with Processor(
                config, quiet_logger, runner=InProcessAttemptRunner(provider)
            ) as processor:
                processor.watch(shutdown_event=stop)
        except BaseException as exc:
            errors.append(exc)

    stop = threading.Event()
    thread = threading.Thread(target=run_watch, daemon=False)
    thread.start()
    payload = _png_bytes()
    source = config.paths.source / "slow.png"
    with source.open("wb") as stream:
        for offset in range(0, len(payload), 16):
            stream.write(payload[offset : offset + 16])
            stream.flush()
            os.fsync(stream.fileno())
            time.sleep(0.025)
            assert provider.requests == []
    stable_at = time.monotonic() + config.watch.stable_seconds
    deadline = time.monotonic() + 2
    while not provider.requests and time.monotonic() < deadline:
        time.sleep(0.01)
    detected_at = time.monotonic()
    stop.set()
    thread.join(timeout=10)
    assert not thread.is_alive()
    assert errors == []
    assert len(provider.requests) == 1
    assert detected_at - stable_at < 1.0


def test_idle_watch_resource_measurement_seam(tmp_path: Path, quiet_logger) -> None:
    _, config = write_config(tmp_path, poll_interval_seconds=0.005)
    provider = FakeProvider()
    started_cpu = time.process_time()
    started_wall = time.monotonic()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as processor:
        processor.watch(max_cycles=20)
    cpu_seconds = time.process_time() - started_cpu
    wall_seconds = time.monotonic() - started_wall
    assert provider.requests == []
    assert wall_seconds >= 0.05
    assert cpu_seconds < 0.5


def test_active_subprocess_shutdown_uses_deadline_and_cleans_child(
    tmp_path: Path,
) -> None:
    source = write_image(tmp_path / "source.png")
    info = source.stat()
    request = TransformRequest(
        "job",
        SourceSnapshot(
            source,
            str(source).lower(),
            info.st_size,
            info.st_mtime_ns,
            hashlib.sha256(source.read_bytes()).hexdigest(),
            source,
        ),
        "prompt",
        "gpt-image-2",
    )
    _, config = write_config(
        tmp_path / "config", shutdown_timeout_seconds=0.25
    )
    event = threading.Event()
    dispatch = multiprocessing.get_context("spawn").Event()

    def request_shutdown() -> None:
        assert dispatch.wait(5)
        event.set()

    requester = threading.Thread(target=request_shutdown)
    requester.start()
    started = time.monotonic()
    result = SubprocessAttemptRunner(
        target=_slow_child, dispatch_event=dispatch
    ).run(
        config.provider,
        request,
        tmp_path / "attempt.tmp",
        ShutdownDeadline(event.is_set, config.shutdown.timeout_seconds),
    )
    elapsed = time.monotonic() - started
    requester.join(timeout=5)
    assert not requester.is_alive()
    assert result.outcome == "ambiguous"
    assert elapsed < 2.0
    assert not (tmp_path / "attempt.tmp").exists()
    assert multiprocessing.active_children() == []


def test_subprocess_runner_removes_scratch_when_child_has_no_result(
    tmp_path: Path,
) -> None:
    source = write_image(tmp_path / "source.png")
    info = source.stat()
    request = TransformRequest(
        "job",
        SourceSnapshot(
            source,
            str(source).lower(),
            info.st_size,
            info.st_mtime_ns,
            hashlib.sha256(source.read_bytes()).hexdigest(),
            source,
        ),
        "prompt",
        "gpt-image-2",
    )
    _, config = write_config(tmp_path / "config")
    temp = tmp_path / "attempt.tmp"

    result = SubprocessAttemptRunner(target=_silent_partial_child).run(
        config.provider,
        request,
        temp,
        None,
    )

    assert result.outcome == "ambiguous"
    assert result.error_code == ErrorCode.PROVIDER_AMBIGUOUS
    assert not temp.exists()
    assert multiprocessing.active_children() == []


def test_active_provider_shutdown_is_ambiguous_and_not_replayed_on_restart(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path, shutdown_timeout_seconds=0.25
    )
    source = write_image(config.paths.source / "active.png")
    event = threading.Event()
    dispatch = multiprocessing.get_context("spawn").Event()

    def request_shutdown() -> None:
        assert dispatch.wait(5)
        event.set()

    requester = threading.Thread(target=request_shutdown)
    requester.start()
    started = time.monotonic()
    with Processor(
        config,
        quiet_logger,
        runner=SubprocessAttemptRunner(
            target=_slow_child, dispatch_event=dispatch
        ),
    ) as processor:
        result = processor.process_path(source, shutdown_event=event)
    elapsed = time.monotonic() - started
    requester.join(timeout=5)
    assert not requester.is_alive()
    assert result.status == JobStatus.AMBIGUOUS
    assert elapsed < 2.0
    assert not list(config.paths.destination.glob("*.tmp"))

    provider = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as restarted:
        duplicate = restarted.process_path(source)
    assert duplicate.status == JobStatus.AMBIGUOUS
    assert provider.requests == []


def test_interruption_commits_ambiguous_before_scratch_cleanup_failure(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "interrupted-scratch.png")
    event = threading.Event()

    class PartialInterruptingRunner:
        def run(self, provider, request, temp_path, shutdown_deadline):
            del provider, request, shutdown_deadline
            temp_path.write_bytes(b"partial")
            raise KeyboardInterrupt

    processor = Processor(
        config,
        quiet_logger,
        runner=PartialInterruptingRunner(),
    )
    admitted = _admit(processor, source, event)
    staged_path = Path(processor.state.get_job(admitted.job_id).staged_path)
    real_unlink = Path.unlink

    def fail_scratch_unlink(path: Path, *args, **kwargs):
        if path.parent == config.paths.destination and path.suffix == ".tmp":
            raise PermissionError("synthetic scratch cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_scratch_unlink)
    with pytest.raises(AppError) as caught:
        processor._dispatch_one(event, admitted.job_id)
    terminal = processor.state.get_job(admitted.job_id)
    attempts = processor.state.attempt_count(admitted.job_id)
    scratch = list(config.paths.destination.glob("*.tmp"))
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert terminal.status == JobStatus.AMBIGUOUS
    assert terminal.error_code == ErrorCode.PROVIDER_AMBIGUOUS.value
    assert attempts == 1
    assert len(scratch) == 1
    assert terminal.temp_name == scratch[0].name
    assert scratch[0].read_bytes() == b"partial"
    assert staged_path.exists()

    replay_guard = FakeProvider()
    with pytest.raises(AppError) as restart_error:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(replay_guard),
        )
    assert restart_error.value.code == ErrorCode.STATE_FAILED
    assert replay_guard.requests == []
    assert scratch[0].exists()

    monkeypatch.setattr(Path, "unlink", real_unlink)
    final_replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(final_replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)

    assert recovered.status == JobStatus.AMBIGUOUS
    assert recovered.error_code == ErrorCode.PROVIDER_AMBIGUOUS.value
    assert recovered.temp_name is None
    assert final_replay_guard.requests == []
    assert not staged_path.exists()
    assert not scratch[0].exists()


@pytest.mark.parametrize(
    "fault_point",
    ["plan_publication_after_update", "mark_published_after_update"],
)
def test_publication_state_faults_recover_without_invalid_or_duplicate_output(
    tmp_path: Path, quiet_logger, fault_point: str
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / f"{fault_point}.png")
    provider = FakeProvider()

    def fail(point: str) -> None:
        if point == fault_point:
            raise RuntimeError(f"fault at {point}")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        state_transition_hook=fail,
    )
    with pytest.raises(RuntimeError, match="fault at"):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    processor.close()

    finals = list(config.paths.destination.glob("*.png"))
    assert all(_is_valid_png(path) for path in finals)
    restarted_provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(restarted_provider),
    ) as restarted:
        recovered = restarted.state.get_job(job.job_id)
        assert recovered.status == JobStatus.SUCCEEDED
    finals = list(config.paths.destination.glob("*.png"))
    assert len(finals) == 1
    assert _is_valid_png(finals[0])
    assert restarted_provider.requests == []


def _is_valid_png(path: Path) -> bool:
    try:
        with Image.open(path) as image:
            image.verify()
            return image.format == "PNG"
    except Exception:
        return False


def test_published_cleanup_failure_is_durable_and_restart_recoverable(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "cleanup.png")
    event = threading.Event()
    provider = FakeProvider()
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    )
    admitted = _admit(processor, source, event)
    staged = Path(processor.state.get_job(admitted.job_id).staged_path)
    staged_size = staged.stat().st_size
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged:
            raise PermissionError("synthetic staged cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    with pytest.raises(AppError) as caught:
        processor._dispatch_one(event, admitted.job_id)
    published = processor.state.get_job(admitted.job_id)
    assert published.output_name is not None
    output = config.paths.destination / str(published.output_name)
    usage = processor._staging_usage()
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert published.status == JobStatus.PUBLISHED
    assert output.is_file()
    assert _is_valid_png(output)
    assert staged.exists()
    assert usage == (1, staged_size)
    assert len(provider.requests) == 1

    with pytest.raises(AppError) as restarted_error:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(FakeProvider()),
        )
    assert restarted_error.value.code == ErrorCode.STATE_FAILED
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        status = connection.execute(
            "SELECT status FROM jobs WHERE job_id = ?",
            (admitted.job_id,),
        ).fetchone()[0]
    finally:
        connection.close()
    assert status == JobStatus.PUBLISHED.value

    monkeypatch.setattr(Path, "unlink", real_unlink)
    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)
    assert recovered.status == JobStatus.SUCCEEDED
    assert recovered.temp_name is None
    assert not staged.exists()
    assert replay_guard.requests == []


def test_succeeded_legacy_stage_cleanup_failure_does_not_downgrade_success(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "legacy-stage.png")
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        result = processor.process_path(source)
        staged = provider.requests[0].source.staged_path
    staged.write_bytes(source.read_bytes())
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged:
            raise PermissionError("persistent legacy cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    with pytest.raises(AppError) as caught:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(FakeProvider()),
        )
    assert caught.value.code == ErrorCode.STATE_FAILED
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        status = connection.execute(
            "SELECT status FROM jobs WHERE job_id = ?",
            (result.job_id,),
        ).fetchone()[0]
    finally:
        connection.close()
    assert status == JobStatus.SUCCEEDED.value
    assert result.output_path is not None and _is_valid_png(result.output_path)
    assert staged.exists()

    monkeypatch.setattr(Path, "unlink", real_unlink)
    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        assert restarted.state.get_job(result.job_id).status == JobStatus.SUCCEEDED
    assert not staged.exists()
    assert replay_guard.requests == []


def test_watch_continues_after_published_cleanup_failure(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    first_source = write_image(
        config.paths.source / "first-cleanup.png", color=(1, 2, 3)
    )
    second_source = write_image(
        config.paths.source / "second-cleanup.png", color=(4, 5, 6)
    )
    event = threading.Event()
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        first = _admit(processor, first_source, event)
        second = _admit(processor, second_source, event)
        first_stage = Path(processor.state.get_job(first.job_id).staged_path)
        real_remove = processor._remove_staged_file

        def fail_first_cleanup(path: Path) -> None:
            if path == first_stage:
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "The staged input could not be removed.",
                )
            real_remove(path)

        monkeypatch.setattr(
            processor,
            "_remove_staged_file",
            fail_first_cleanup,
        )
        processor.watch(max_cycles=2)
        first_job = processor.state.get_job(first.job_id)
        second_job = processor.state.get_job(second.job_id)

    assert first_job.status == JobStatus.PUBLISHED
    assert second_job.status == JobStatus.SUCCEEDED
    assert len(provider.requests) == 2
    assert first_stage.exists()


@pytest.mark.parametrize(
    ("outcome", "expected_status"),
    [
        ("permanent", JobStatus.FAILED),
        ("ambiguous", JobStatus.AMBIGUOUS),
    ],
)
def test_terminal_cleanup_failure_remains_counted_and_recovers_on_restart(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    outcome: str,
    expected_status: JobStatus,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / f"{outcome}-cleanup.png")
    event = threading.Event()
    provider = FakeProvider([outcome])
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    )
    admitted = _admit(processor, source, event)
    staged = Path(processor.state.get_job(admitted.job_id).staged_path)
    staged_size = staged.stat().st_size
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged:
            raise PermissionError("terminal cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    with pytest.raises(AppError) as caught:
        processor._dispatch_one(event, admitted.job_id)
    terminal = processor.state.get_job(admitted.job_id)
    usage = processor._staging_usage()
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert terminal.status == expected_status
    assert usage == (1, staged_size)
    assert staged.exists()
    assert len(provider.requests) == 1

    monkeypatch.setattr(Path, "unlink", real_unlink)
    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)
    assert recovered.status == expected_status
    assert not staged.exists()
    assert replay_guard.requests == []


def test_output_failure_commits_before_scratch_cleanup_failure(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "invalid-output-scratch.png")
    event = threading.Event()
    provider = FakeProvider(["corrupt"])
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    )
    admitted = _admit(processor, source, event)
    staged_path = Path(processor.state.get_job(admitted.job_id).staged_path)
    real_unlink = Path.unlink

    def fail_scratch_unlink(path: Path, *args, **kwargs):
        if path.parent == config.paths.destination and path.suffix == ".tmp":
            raise PermissionError("synthetic scratch cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_scratch_unlink)
    with pytest.raises(AppError) as caught:
        processor._dispatch_one(event, admitted.job_id)
    terminal = processor.state.get_job(admitted.job_id)
    attempts = processor.state.attempt_count(admitted.job_id)
    scratch = list(config.paths.destination.glob("*.tmp"))
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert terminal.status == JobStatus.FAILED
    assert terminal.error_code == ErrorCode.OUTPUT_INVALID.value
    assert attempts == 1
    assert len(provider.requests) == 1
    assert len(scratch) == 1
    assert terminal.temp_name == scratch[0].name
    assert scratch[0].read_bytes() == b"not-a-png"
    assert staged_path.exists()

    replay_guard = FakeProvider()
    with pytest.raises(AppError) as restart_error:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(replay_guard),
        )
    assert restart_error.value.code == ErrorCode.STATE_FAILED
    assert replay_guard.requests == []
    assert scratch[0].exists()

    monkeypatch.setattr(Path, "unlink", real_unlink)
    final_replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(final_replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(admitted.job_id)

    assert recovered.status == JobStatus.FAILED
    assert recovered.error_code == ErrorCode.OUTPUT_INVALID.value
    assert recovered.temp_name is None
    assert final_replay_guard.requests == []
    assert not staged_path.exists()
    assert not scratch[0].exists()


@pytest.mark.parametrize(
    ("failure_mode", "expected_error"),
    [
        ("invalid-output", ErrorCode.OUTPUT_INVALID),
        ("publication-failure", ErrorCode.PUBLICATION_FAILED),
    ],
)
def test_output_terminal_cleanup_failure_preserves_state_and_recovers(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    failure_mode: str,
    expected_error: ErrorCode,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / f"{failure_mode}.png")
    event = threading.Event()
    provider = FakeProvider(
        ["corrupt"] if failure_mode == "invalid-output" else None
    )
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    )
    admitted = _admit(processor, source, event)
    staged = Path(processor.state.get_job(admitted.job_id).staged_path)
    staged_size = staged.stat().st_size
    if failure_mode == "publication-failure":
        import tcfcomic.processor as processor_module

        def fail_publication(*args, **kwargs):
            del args, kwargs
            raise AppError(
                ErrorCode.PUBLICATION_FAILED,
                "The validated output could not be published.",
            )

        monkeypatch.setattr(
            processor_module,
            "promote_planned_output",
            fail_publication,
        )
    real_unlink = Path.unlink

    def fail_staged_unlink(path: Path, *args, **kwargs):
        if path == staged:
            raise PermissionError("synthetic output cleanup failure")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_staged_unlink)
    with pytest.raises(AppError) as caught:
        processor._dispatch_one(event, admitted.job_id)
    terminal = processor.state.get_job(admitted.job_id)
    usage = processor._staging_usage()
    attempts = processor.state.attempt_count(admitted.job_id)
    processor.close()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert terminal.status == JobStatus.FAILED
    assert terminal.error_code == expected_error.value
    assert attempts == 1
    assert usage == (1, staged_size)
    assert staged.exists()
    assert len(provider.requests) == 1
    assert not list(config.paths.destination.glob("*.tmp"))

    with pytest.raises(AppError) as restart_error:
        Processor(
            config,
            quiet_logger,
            runner=InProcessAttemptRunner(FakeProvider()),
        )
    assert restart_error.value.code == ErrorCode.STATE_FAILED
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        persisted = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (admitted.job_id,),
        ).fetchone()
    finally:
        connection.close()
    assert persisted == (JobStatus.FAILED.value, expected_error.value)

    monkeypatch.setattr(Path, "unlink", real_unlink)
    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as recovered:
        final = recovered.state.get_job(admitted.job_id)
        assert recovered.state.attempt_count(admitted.job_id) == 1
    assert final.status == JobStatus.FAILED
    assert final.error_code == expected_error.value
    assert not staged.exists()
    assert replay_guard.requests == []


def test_publication_rename_failure_never_leaves_final(
    tmp_path: Path, quiet_logger, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "rename.png")
    import tcfcomic.processor as processor_module

    def fail_rename(*args, **kwargs):
        del args, kwargs
        from tcfcomic.domain import AppError

        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The validated output could not be published.",
        )

    monkeypatch.setattr(processor_module, "promote_planned_output", fail_rename)
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        result = processor.process_path(source)
        staged = provider.requests[0].source.staged_path
    assert result.status == JobStatus.FAILED
    assert result.error_code == ErrorCode.PUBLICATION_FAILED
    assert result.attempts == 1
    assert not staged.exists()
    assert not list(config.paths.destination.glob("*.png"))
    assert not list(config.paths.destination.glob("*.tmp"))


def test_malicious_recovered_job_id_is_rejected_without_escape_or_lock_leak(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ):
        pass
    state_path = config.paths.destination / ".tcfcomic" / "state.db"
    connection = sqlite3.connect(state_path)
    try:
        connection.execute(
            """
            INSERT INTO jobs (
                job_id, source_path, source_path_key, source_name, size, mtime_ns,
                sha256, status, provider, model, prompt_hash, staged_path,
                created_at, updated_at
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                "..\\escaped",
                "safe",
                "key",
                "safe.png",
                1,
                1,
                "a" * 64,
                JobStatus.DISPATCHING.value,
                "fake",
                "gpt-image-2",
                "b" * 64,
                str(config.paths.destination / "outside.input"),
                "2026-08-30T00:00:00+00:00",
                "2026-08-30T00:00:00+00:00",
            ),
        )
        connection.commit()
    finally:
        connection.close()
    with pytest.raises(AppError):
        Processor(
            config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
        )
    assert not (tmp_path / "escaped.json").exists()
    connection = sqlite3.connect(state_path)
    try:
        connection.execute("DELETE FROM jobs WHERE job_id = ?", ("..\\escaped",))
        connection.commit()
    finally:
        connection.close()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ):
        pass


def test_quarantine_write_failure_is_reconciled_before_terminal_state(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "quarantine-write-failure.png")
    import tcfcomic.processor as processor_module

    real_write = processor_module.write_failure_record
    calls = 0

    def fail_once(*args, **kwargs):
        nonlocal calls
        calls += 1
        if calls == 1:
            raise OSError("simulated quarantine write failure")
        return real_write(*args, **kwargs)

    monkeypatch.setattr(processor_module, "write_failure_record", fail_once)
    processor = Processor(
        config,
        quiet_logger,
        # Unanswered outcomes retain quarantine-before-ambiguous ordering.
        # Definitive terminal outcomes now commit first under the safety contract.
        runner=InProcessAttemptRunner(FakeProvider(["ambiguous"])),
    )
    with pytest.raises(OSError, match="simulated quarantine"):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    assert job.status == JobStatus.DISPATCHING
    assert processor.state.active_attempt(job.job_id).attempt_no == 1
    assert not list(config.paths.quarantine.glob("*.json"))
    processor.close()

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(job.job_id)
        assert recovered.status == JobStatus.AMBIGUOUS
    assert replay_guard.requests == []
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    assert json.loads(records[0].read_text(encoding="utf-8"))["error_code"] == (
        ErrorCode.PROVIDER_AMBIGUOUS.value
    )


def test_quarantine_baseexception_residue_is_removed_before_state_recovery(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    record = FailureRecord(
        job_id="a" * 32,
        source_name="photo.png",
        size=3,
        mtime_ns=4,
        sha256="b" * 64,
        stage="provider",
        error_code=ErrorCode.PROVIDER_PERMANENT,
        safe_message="safe",
        attempts=1,
        first_seen_at="2026-08-30T00:00:00+00:00",
        failed_at="2026-08-30T00:00:01+00:00",
    )
    import tcfcomic.processor as processor_module
    import tcfcomic.quarantine as quarantine_module

    class SimulatedCrash(BaseException):
        pass

    real_replace = quarantine_module.os.replace

    def crash_replace(source, destination):
        del source, destination
        raise SimulatedCrash()

    monkeypatch.setattr(quarantine_module.os, "replace", crash_replace)
    with pytest.raises(SimulatedCrash):
        write_failure_record(record, config.paths.quarantine)
    owned = list(config.paths.quarantine.glob("*.json.tmp"))
    assert len(owned) == 1
    monkeypatch.setattr(quarantine_module.os, "replace", real_replace)

    real_recover = processor_module.StateStore.recover

    def assert_reconciled_before_recover(state):
        assert not owned[0].exists()
        return real_recover(state)

    monkeypatch.setattr(
        processor_module.StateStore,
        "recover",
        assert_reconciled_before_recover,
    )
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ):
        pass
    assert not owned[0].exists()


def test_startup_quarantine_reconciliation_leaves_operator_artifacts(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    quarantine = config.paths.quarantine
    quarantine.mkdir(parents=True, exist_ok=True)
    owned = quarantine / f"{'a' * 32}.{'b' * 32}.json.tmp"
    owned.write_text("crash residue", encoding="utf-8")
    operator_artifacts = [
        quarantine / f"{'A' * 32}.{'c' * 32}.json.tmp",
        quarantine / f"{'a' * 32}.{'b' * 31}.json.tmp",
        quarantine / f"{'a' * 32}.{'b' * 32}.json.tmp.keep",
        quarantine / "operator.json.tmp",
    ]
    for artifact in operator_artifacts:
        artifact.write_text("operator", encoding="utf-8")

    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ):
        pass

    assert not owned.exists()
    assert all(artifact.read_text(encoding="utf-8") == "operator" for artifact in operator_artifacts)


@pytest.mark.parametrize("artifact_kind", ["directory", "symlink", "reparse"])
def test_startup_rejects_unsafe_canonical_quarantine_temp_without_touching_it(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    artifact_kind: str,
) -> None:
    _, config = write_config(tmp_path)
    quarantine = config.paths.quarantine
    quarantine.mkdir(parents=True, exist_ok=True)
    artifact = quarantine / f"{'a' * 32}.{'b' * 32}.json.tmp"

    if artifact_kind == "directory":
        artifact.mkdir()
    elif artifact_kind == "symlink":
        artifact.write_text("operator", encoding="utf-8")
        import tcfcomic.quarantine as quarantine_module

        actual = artifact.stat()

        class SymlinkEntry:
            name = artifact.name
            path = str(artifact)

            @staticmethod
            def stat(*, follow_symlinks: bool):
                assert follow_symlinks is False
                return actual

            @staticmethod
            def is_symlink():
                return True

        monkeypatch.setattr(
            quarantine_module.os, "scandir", lambda path: [SymlinkEntry()]
        )
    else:
        artifact.write_text("operator", encoding="utf-8")
        import tcfcomic.quarantine as quarantine_module

        actual = artifact.stat()
        reparse_flag = 1024
        monkeypatch.setattr(
            quarantine_module.stat,
            "FILE_ATTRIBUTE_REPARSE_POINT",
            reparse_flag,
            raising=False,
        )

        class ReparseEntry:
            name = artifact.name
            path = str(artifact)

            @staticmethod
            def stat(*, follow_symlinks: bool):
                assert follow_symlinks is False
                return types.SimpleNamespace(
                    st_mode=actual.st_mode,
                    st_file_attributes=reparse_flag,
                )

            @staticmethod
            def is_symlink():
                return False

        monkeypatch.setattr(
            quarantine_module.os, "scandir", lambda path: [ReparseEntry()]
        )

    with pytest.raises(AppError) as caught:
        Processor(
            config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
        )
    assert caught.value.code == ErrorCode.STATE_FAILED
    assert os.path.lexists(artifact)
    if artifact_kind == "directory":
        assert artifact.is_dir()
    elif artifact_kind == "symlink":
        assert artifact.read_text(encoding="utf-8") == "operator"
    else:
        assert artifact.read_text(encoding="utf-8") == "operator"


def test_crash_after_quarantine_before_terminal_commit_reconciles_on_restart(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "quarantine-before-state.png")
    armed = False

    def crash(point: str) -> None:
        if armed and point == "mark_ambiguous_after_attempt":
            raise RuntimeError("simulated crash after quarantine")

    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider(["ambiguous"])),
        state_transition_hook=crash,
    )
    armed = True
    with pytest.raises(RuntimeError, match="after quarantine"):
        processor.process_path(source)
    job = processor.state.list_jobs()[0]
    assert job.status == JobStatus.DISPATCHING
    assert len(list(config.paths.quarantine.glob("*.json"))) == 1
    processor.close()

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        assert restarted.state.get_job(job.job_id).status == JobStatus.AMBIGUOUS
    assert replay_guard.requests == []


def test_staging_backpressure_and_free_space_reserve_are_actionable(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "backpressure.png")
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        monkeypatch.setattr(processor, "_staging_usage", lambda: (1000, 0))
        with pytest.raises(AppError) as caught:
            processor.process_path(source)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert "backpressure" in caught.value.safe_message.lower()
        assert provider.requests == []
        assert not list(processor.staging.glob("*.input"))

    _, second_config = write_config(tmp_path / "free-space")
    second_source = write_image(second_config.paths.source / "reserve.png")
    import tcfcomic.processor as processor_module

    required = (
        second_source.stat().st_size
        + second_config.limits.max_output_bytes
        + processor_module._MIN_FREE_SPACE_RESERVE
    )
    available = {"free": required - 1}
    monkeypatch.setattr(
        processor_module.shutil,
        "disk_usage",
        lambda path: types.SimpleNamespace(
            total=required * 2,
            used=required,
            free=available["free"],
        ),
    )
    second_provider = FakeProvider()
    with Processor(
        second_config,
        quiet_logger,
        runner=InProcessAttemptRunner(second_provider),
    ) as processor:
        with pytest.raises(AppError) as caught:
            processor.process_path(second_source)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert "free space" in caught.value.safe_message.lower()
        assert not list(processor.staging.glob("*.input"))
        available["free"] = required
        recovered = processor.process_path(second_source)
        assert recovered.status == JobStatus.SUCCEEDED
        assert len(second_provider.requests) == 1


def test_physical_staging_usage_counts_terminal_and_unreferenced_artifacts(
    tmp_path: Path,
    quiet_logger,
) -> None:
    _, config = write_config(tmp_path)
    terminal_source = write_image(
        config.paths.source / "terminal-leftover.png"
    )
    blocked_source = write_image(
        config.paths.source / "blocked-by-physical-usage.png"
    )
    event = threading.Event()
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        terminal = _admit(processor, terminal_source, event)
        terminal_stage = Path(
            processor.state.get_job(terminal.job_id).staged_path
        )
        terminal_size = terminal_stage.stat().st_size
        processor.state.mark_failed(terminal.job_id, ErrorCode.STATE_FAILED)
        for index in range(999):
            (processor.staging / f"draft-{index:04d}.input").write_bytes(b"x")

        assert processor._staging_usage() == (1000, terminal_size + 999)
        with pytest.raises(AppError) as caught:
            processor.process_path(blocked_source)
        jobs = processor.state.list_jobs()

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert "backpressure" in caught.value.safe_message.lower()
    assert len(jobs) == 1
    assert jobs[0].status == JobStatus.FAILED
    assert provider.requests == []


def test_physical_staging_usage_fails_closed_for_unsafe_artifact(
    tmp_path: Path,
    quiet_logger,
) -> None:
    _, config = write_config(tmp_path)
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        (processor.staging / "unsafe.input").mkdir()
        with pytest.raises(AppError) as caught:
            processor._staging_usage()
    assert caught.value.code == ErrorCode.STATE_FAILED


def test_remove_staged_file_is_idempotent_when_missing(
    tmp_path: Path,
    quiet_logger,
) -> None:
    _, config = write_config(tmp_path)
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        missing = processor.staging / "missing.input"
        processor._remove_staged_file(missing)
        assert not missing.exists()


@pytest.mark.parametrize(
    "failure_mode",
    ["outside", "nonregular", "reparse", "stat", "unsafe-metadata"],
)
def test_remove_staged_file_fails_closed_for_unsafe_cleanup(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    failure_mode: str,
) -> None:
    _, config = write_config(tmp_path)
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        if failure_mode == "outside":
            artifact = tmp_path / "outside.input"
            artifact.write_bytes(b"outside")
        elif failure_mode == "nonregular":
            artifact = processor.staging / "directory.input"
            artifact.mkdir()
        else:
            artifact = processor.staging / f"{failure_mode}.input"
            artifact.write_bytes(b"unsafe")

        if failure_mode == "reparse":
            import tcfcomic.path_safety as path_safety_module

            real_is_reparse = path_safety_module.is_reparse_or_link

            def report_artifact_as_reparse(path: Path) -> bool:
                return path == artifact or real_is_reparse(path)

            monkeypatch.setattr(
                path_safety_module,
                "is_reparse_or_link",
                report_artifact_as_reparse,
            )
        elif failure_mode == "stat":
            import tcfcomic.processor as processor_module

            real_stat = processor_module.os.stat

            def fail_artifact_stat(path, *args, **kwargs):
                if Path(path) == artifact:
                    raise PermissionError("synthetic stat failure")
                return real_stat(path, *args, **kwargs)

            monkeypatch.setattr(
                processor_module.os,
                "stat",
                fail_artifact_stat,
            )
        elif failure_mode == "unsafe-metadata":
            monkeypatch.setattr(
                processor,
                "_is_safe_regular_staged_stat",
                lambda info: False,
            )

        with pytest.raises(AppError) as caught:
            processor._remove_staged_file(artifact)

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert os.path.lexists(artifact)


@pytest.mark.parametrize("capacity_mode", ["job-count", "free-space"])
def test_watch_rearms_after_admission_capacity_recovers(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    capacity_mode: str,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "capacity-recovers.png")
    provider = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        if capacity_mode == "job-count":
            real_usage = processor._staging_usage
            calls = 0

            def staged_usage():
                nonlocal calls
                calls += 1
                return (1000, 0) if calls == 1 else real_usage()

            monkeypatch.setattr(processor, "_staging_usage", staged_usage)
        else:
            import tcfcomic.processor as processor_module

            real_disk_usage = processor_module.shutil.disk_usage
            calls = 0

            def disk_usage(path):
                nonlocal calls
                calls += 1
                if calls == 1:
                    return types.SimpleNamespace(total=1, used=1, free=0)
                return real_disk_usage(path)

            monkeypatch.setattr(processor_module.shutil, "disk_usage", disk_usage)

        processor.watch(max_cycles=3)
        jobs = processor.state.list_jobs()

    assert source.exists()
    assert len(jobs) == 1
    assert jobs[0].status == JobStatus.SUCCEEDED
    assert len(provider.requests) == 1


def test_recovered_paths_cannot_escape_staging_or_destination(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "path.png")
    event = threading.Event()
    processor = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    admitted = _admit(processor, source, event)
    outside = tmp_path / "outside.input"
    outside.write_bytes(source.read_bytes())
    processor.state.connection.execute(
        "UPDATE jobs SET staged_path = ? WHERE job_id = ?",
        (str(outside), admitted.job_id),
    )
    processor.close()

    replay_guard = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(replay_guard)
    ) as restarted:
        with pytest.raises(AppError) as cleanup_error:
            restarted._dispatch_one(event)
        failed = restarted.state.get_job(admitted.job_id)
    assert cleanup_error.value.code == ErrorCode.STATE_FAILED
    assert failed.status == JobStatus.FAILED
    assert failed.error_code == ErrorCode.STATE_FAILED.value
    assert replay_guard.requests == []
    assert outside.exists()

    final_replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(final_replay_guard),
    ) as recovered:
        assert recovered.state.get_job(admitted.job_id).status == JobStatus.FAILED
    assert outside.exists()
    assert final_replay_guard.requests == []


def test_fake_provider_journey_remains_offline(
    tmp_path: Path, quiet_logger, monkeypatch: pytest.MonkeyPatch
) -> None:
    def deny_network(*args, **kwargs):
        del args, kwargs
        raise AssertionError("network access is forbidden")

    monkeypatch.setattr(socket.socket, "connect", deny_network)
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "offline.png")
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        assert processor.process_path(source).status == JobStatus.SUCCEEDED


@pytest.mark.performance
def test_1000_file_discovery_admission_dispatch_and_publication(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path, poll_interval_seconds=0.001)
    payload = _png_bytes()
    for index in range(1000):
        (config.paths.source / f"image-{index:04d}.png").write_bytes(payload)
    class TrackingProvider(FakeProvider):
        def __init__(self) -> None:
            super().__init__()
            self.max_staged = 0

        def transform(self, request, output):
            self.max_staged = max(
                self.max_staged,
                len(list((config.paths.destination / ".tcfcomic" / "staging").glob("*.input"))),
            )
            return super().transform(request, output)

    provider = TrackingProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        processor.watch(max_cycles=2)
        jobs = processor.state.list_jobs()
    assert len(jobs) == 1000
    assert all(job.status == JobStatus.SUCCEEDED for job in jobs)
    assert len(provider.requests) == 1000
    assert provider.max_staged == 1000
    assert len({request.job_id for request in provider.requests}) == 1000
    assert len(list(config.paths.destination.glob("*.png"))) == 1000
    assert not list(config.paths.destination.glob("*.tmp"))
