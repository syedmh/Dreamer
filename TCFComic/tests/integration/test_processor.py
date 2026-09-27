from __future__ import annotations

import hashlib
import json
import os
import threading
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import JobStatus
from tcfcomic.domain import AppError, ErrorCode, RequestIdentity, ShutdownToken
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.scanner import StableCandidate, stage_candidate


def build_processor(tmp_path: Path, quiet_logger, outcomes=None, max_attempts=3):
    _, config = write_config(tmp_path, max_attempts=max_attempts)
    provider = FakeProvider(outcomes)
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
        sleep=lambda _: None,
    )
    return config, provider, processor


def admit_without_dispatch(
    processor: Processor,
    source: Path,
    shutdown_event: threading.Event,
):
    info = source.stat()
    return processor._admit(
        source,
        candidate=StableCandidate(source, info.st_size, info.st_mtime_ns),
        shutdown_event=shutdown_event,
    )


def test_fake_one_shot_valid_png_and_source_preserved(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png")
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    with processor:
        result = processor.process_path(source)
    assert result.status == JobStatus.SUCCEEDED
    assert len(provider.requests) == 1
    assert "Studio Ghibli-inspired" in provider.requests[0].prompt
    assert hashlib.sha256(source.read_bytes()).hexdigest() == before
    Image.open(result.output_path).verify()
    assert not list(config.paths.destination.glob("*.tmp"))


def test_replaced_staged_input_fails_identity_before_attempt(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png", color=(1, 2, 3))
    replacement = write_image(tmp_path / "replacement.png", color=(9, 8, 7))
    assert source.stat().st_size == replacement.stat().st_size
    event = threading.Event()

    with processor:
        admitted = admit_without_dispatch(processor, source, event)
        staged = Path(processor.state.get_job(admitted.job_id).staged_path)
        staged.write_bytes(replacement.read_bytes())

        assert processor._dispatch_one(event, admitted.job_id)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == ErrorCode.STATE_FAILED.value
    assert attempts == 0
    assert provider.requests == []
    assert not staged.exists()


def test_staged_metadata_mutation_overrides_validation_error(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "mutated.png")
    event = threading.Event()
    import tcfcomic.processor as processor_module

    with processor:
        admitted = admit_without_dispatch(processor, source, event)

        def mutate_then_reject(path: Path, limits, source_name=None) -> None:
            assert source_name == source.name
            del limits
            path.write_bytes(b"changed during validation")
            raise AppError(
                ErrorCode.INVALID_IMAGE,
                "The input does not contain a valid supported image.",
            )

        monkeypatch.setattr(
            processor_module,
            "validate_input",
            mutate_then_reject,
        )
        assert processor._dispatch_one(event, admitted.job_id)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == ErrorCode.STATE_FAILED.value
    assert attempts == 0
    assert provider.requests == []


def test_timestamp_restored_same_size_substitution_during_validation_fails_before_attempt(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "original.png", color=(1, 2, 3))
    replacement = write_image(tmp_path / "replacement.png", color=(9, 8, 7))
    replacement_bytes = replacement.read_bytes()
    assert source.stat().st_size == len(replacement_bytes)
    event = threading.Event()
    import tcfcomic.processor as processor_module

    with processor:
        admitted = admit_without_dispatch(processor, source, event)

        def substitute_and_restore_timestamps(path: Path, limits, source_name=None) -> None:
            assert source_name == source.name
            del limits
            before = path.stat()
            path.write_bytes(replacement_bytes)
            os.utime(
                path,
                ns=(before.st_atime_ns, before.st_mtime_ns),
            )

        monkeypatch.setattr(
            processor,
            "_staged_metadata",
            lambda info: processor._staged_identity(info),
        )
        monkeypatch.setattr(
            processor_module,
            "validate_input",
            substitute_and_restore_timestamps,
        )
        assert processor._dispatch_one(event, admitted.job_id)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == ErrorCode.STATE_FAILED.value
    assert attempts == 0
    assert provider.requests == []


def test_oversized_staged_substitution_is_rejected_before_open(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "oversized.png")
    event = threading.Event()

    with processor:
        admitted = admit_without_dispatch(processor, source, event)
        staged = Path(processor.state.get_job(admitted.job_id).staged_path)
        os.truncate(staged, config.limits.max_input_bytes + 1)
        real_open = Path.open

        def guarded_open(path: Path, *args, **kwargs):
            mode = args[0] if args else kwargs.get("mode", "r")
            if path == staged and "r" in mode:
                pytest.fail("oversized staged input was opened for reading")
            return real_open(path, *args, **kwargs)

        monkeypatch.setattr(Path, "open", guarded_open)
        assert processor._dispatch_one(event, admitted.job_id)
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == ErrorCode.STATE_FAILED.value
    assert attempts == 0
    assert provider.requests == []


@pytest.mark.parametrize(
    "failure_mode",
    ["missing", "nonregular", "reparse_metadata"],
)
def test_unsafe_staged_input_fails_before_attempt(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
    failure_mode: str,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / f"{failure_mode}.png")
    event = threading.Event()

    with processor:
        admitted = admit_without_dispatch(processor, source, event)
        staged = Path(processor.state.get_job(admitted.job_id).staged_path)
        if failure_mode == "missing":
            staged.unlink()
        elif failure_mode == "nonregular":
            staged.unlink()
            staged.mkdir()
        else:
            monkeypatch.setattr(
                processor,
                "_is_safe_regular_staged_stat",
                lambda info: False,
            )

        if failure_mode == "missing":
            assert processor._dispatch_one(event, admitted.job_id)
        else:
            with pytest.raises(AppError) as cleanup_error:
                processor._dispatch_one(event, admitted.job_id)
            assert cleanup_error.value.code == ErrorCode.STATE_FAILED
        durable = processor.state.get_job(admitted.job_id)
        attempts = processor.state.attempt_count(admitted.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == ErrorCode.STATE_FAILED.value
    assert attempts == 0
    assert provider.requests == []
    assert staged.exists() is (failure_mode != "missing")


def test_one_shot_dispatches_only_requested_target(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    unrelated_source = write_image(
        config.paths.source / "unrelated.png", color=(1, 1, 1)
    )
    target_source = write_image(
        config.paths.source / "target.png", color=(2, 2, 2)
    )
    event = threading.Event()

    with processor:
        unrelated = admit_without_dispatch(
            processor, unrelated_source, event
        )
        target = processor.process_path(
            target_source,
            shutdown_event=event,
        )
        unrelated_job = processor.state.get_job(unrelated.job_id)

    assert target.status == JobStatus.SUCCEEDED
    assert unrelated_job.status == JobStatus.READY
    assert [request.job_id for request in provider.requests] == [target.job_id]


def test_one_shot_retry_remains_target_only(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(
        tmp_path,
        initial_delay_seconds=60,
        max_delay_seconds=60,
    )
    provider = FakeProvider(["retryable", "success"])
    processor = Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    )
    unrelated_source = write_image(
        config.paths.source / "unrelated.png", color=(3, 3, 3)
    )
    target_source = write_image(
        config.paths.source / "target.png", color=(4, 4, 4)
    )
    waits: list[float] = []

    class AdvanceTargetRetry:
        @staticmethod
        def is_set() -> bool:
            return False

        @staticmethod
        def wait(timeout: float) -> bool:
            waits.append(timeout)
            retry = next(
                job
                for job in processor.state.list_jobs()
                if job.status == JobStatus.READY_RETRY
            )
            processor.state.connection.execute(
                "UPDATE jobs SET next_attempt_at = ? WHERE job_id = ?",
                ("2000-01-01T00:00:00+00:00", retry.job_id),
            )
            return False

    event = AdvanceTargetRetry()

    with processor:
        unrelated = admit_without_dispatch(
            processor, unrelated_source, event
        )
        target = processor.process_path(
            target_source,
            shutdown_event=event,
        )
        unrelated_job = processor.state.get_job(unrelated.job_id)

    assert target.status == JobStatus.SUCCEEDED
    assert target.attempts == 2
    assert unrelated_job.status == JobStatus.READY
    assert waits == [config.watch.poll_interval_seconds]
    assert [request.job_id for request in provider.requests] == [
        target.job_id,
        target.job_id,
    ]


def test_restart_duplicate_and_modified_source(
    tmp_path: Path, quiet_logger
) -> None:
    config_path, config = write_config(tmp_path)
    source = write_image(config.paths.source / "photo.png", color=(1, 2, 3))
    first_provider = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(first_provider)
    ) as processor:
        first = processor.process_path(source)
    assert first.status == JobStatus.SUCCEEDED

    second_provider = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(second_provider)
    ) as processor:
        duplicate = processor.process_path(source)
        write_image(source, color=(9, 8, 7))
        modified = processor.process_path(source)
    assert duplicate.status == JobStatus.SUCCEEDED
    assert second_provider.requests == [modified_request := second_provider.requests[0]]
    assert modified.status == JobStatus.SUCCEEDED
    assert modified_request.source.sha256 != first_provider.requests[0].source.sha256
    assert len(list(config.paths.destination.glob("*.png"))) == 2


def test_two_retries_then_success(tmp_path: Path, quiet_logger) -> None:
    config, provider, processor = build_processor(
        tmp_path, quiet_logger, ["retryable", "retryable", "success"]
    )
    source = write_image(config.paths.source / "photo.png")
    with processor:
        result = processor.process_path(source)
    assert result.status == JobStatus.SUCCEEDED
    assert result.attempts == 3
    assert len(provider.requests) == 3


def test_retry_exhaustion_and_permanent_failure(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(
        tmp_path / "retry", quiet_logger, ["retryable"] * 3
    )
    source = write_image(config.paths.source / "photo.png")
    with processor:
        exhausted = processor.process_path(source)
    assert exhausted.status == JobStatus.FAILED
    assert exhausted.output_path is None
    assert len(provider.requests) == 3
    record = json.loads(next(config.paths.quarantine.glob("*.json")).read_text())
    assert record["attempts"] == 3
    assert record["error_code"] == "PROVIDER_RETRYABLE"

    config, provider, processor = build_processor(
        tmp_path / "permanent", quiet_logger, ["permanent"]
    )
    source = write_image(config.paths.source / "photo.png")
    with processor:
        permanent = processor.process_path(source)
    assert permanent.status == JobStatus.FAILED
    assert len(provider.requests) == 1


def test_corrupt_input_and_corrupt_output_are_quarantined(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(tmp_path / "input", quiet_logger)
    source = config.paths.source / "bad.png"
    source.write_bytes(b"corrupt")
    with processor:
        result = processor.process_path(source)
    assert result.status == JobStatus.FAILED
    assert provider.requests == []
    assert "INVALID_IMAGE" in next(config.paths.quarantine.glob("*.json")).read_text()

    config, provider, processor = build_processor(
        tmp_path / "output", quiet_logger, ["corrupt"]
    )
    source = write_image(config.paths.source / "good.png")
    with processor:
        result = processor.process_path(source)
        staged = provider.requests[0].source.staged_path
    assert result.status == JobStatus.FAILED
    assert result.attempts == 1
    assert "OUTPUT_INVALID" in next(config.paths.quarantine.glob("*.json")).read_text()
    assert not list(config.paths.destination.glob("*.tmp"))
    assert not staged.exists()


def test_watch_continues_after_bad_item(tmp_path: Path, quiet_logger) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    (config.paths.source / "bad.png").write_bytes(b"bad")
    write_image(config.paths.source / "good.webp", "WEBP")
    with processor:
        processor.watch(max_cycles=3)
    assert len(provider.requests) == 1
    assert len(list(config.paths.destination.glob("*.png"))) == 1
    assert len(list(config.paths.quarantine.glob("*.json"))) == 1


class InterruptingRunner:
    def run(self, provider, request, temp_path, shutdown_deadline):
        raise KeyboardInterrupt


def test_interruption_marks_ambiguous_without_partial_output(
    tmp_path: Path, quiet_logger
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "photo.png")
    processor = Processor(config, quiet_logger, runner=InterruptingRunner())
    with pytest.raises(KeyboardInterrupt):
        processor.process_path(source)
    jobs = processor.state.list_jobs()
    processor.close()
    assert jobs[0].status == JobStatus.AMBIGUOUS
    assert not list(config.paths.destination.glob("*.png"))
    assert len(list(config.paths.quarantine.glob("*.json"))) == 1


def test_watch_shutdown_seam_leaves_unstarted_file_eligible(
    tmp_path: Path, quiet_logger
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "queued.png")
    event = threading.Event()
    event.set()
    with processor:
        processor.watch(shutdown_event=event)
    assert provider.requests == []
    second = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(second)
    ) as restarted:
        result = restarted.process_path(source)
    assert result.status == JobStatus.SUCCEEDED
    assert len(second.requests) == 1


def test_ready_job_resumes_after_restart(tmp_path: Path, quiet_logger) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "pending.png")
    first = Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    )
    info = source.stat()
    staged = first.staging / "pendingjob.input"
    snap = stage_candidate(
        StableCandidate(source, info.st_size, info.st_mtime_ns),
        staged,
        config.limits.max_input_bytes,
        ShutdownToken(lambda: False),
    )
    admission = first.state.admit(
        snap, first._request_identity()
    )
    first.close()
    assert admission.admitted

    provider = FakeProvider()
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(provider)
    ) as restarted:
        result = restarted.process_path(source)
    assert result.status == JobStatus.SUCCEEDED
    assert len(provider.requests) == 1


def test_published_state_is_reconciled_after_restart(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png")
    result = processor.process_path(source)
    processor.state.connection.execute(
        "UPDATE jobs SET status = ? WHERE job_id = ?",
        (JobStatus.PUBLISHED.value, result.job_id),
    )
    processor.close()

    def forbid_read_bytes(path: Path) -> bytes:
        raise AssertionError(f"read_bytes must not be used for recovery digest: {path}")

    monkeypatch.setattr(Path, "read_bytes", forbid_read_bytes)
    with Processor(
        config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())
    ) as restarted:
        assert restarted.state.get_job(result.job_id).status == JobStatus.SUCCEEDED


@pytest.mark.parametrize(
    ("mutation", "expected_error"),
    [
        ("deleted", ErrorCode.STATE_FAILED),
        ("corrupt", ErrorCode.OUTPUT_INVALID),
        ("digest_mismatch", ErrorCode.OUTPUT_INVALID),
    ],
)
def test_same_process_persisted_succeeded_output_is_failed_without_provider_replay(
    tmp_path: Path,
    quiet_logger,
    mutation: str,
    expected_error: ErrorCode,
) -> None:
    config, provider, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png")
    with processor:
        result = processor.process_path(source)
        output_path = result.output_path
        assert output_path is not None

        if mutation == "deleted":
            output_path.unlink()
        elif mutation == "corrupt":
            output_path.write_bytes(b"not a png")
        else:
            write_image(output_path, color=(1, 2, 3))

        duplicate = processor.process_path(source)
        durable = processor.state.get_job(result.job_id)

    assert durable.status == JobStatus.FAILED
    assert durable.error_code == expected_error.value
    assert duplicate.status == JobStatus.FAILED
    assert duplicate.output_path is None
    assert duplicate.error_code == expected_error
    assert len(provider.requests) == 1
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    assert expected_error.value in records[0].read_text(encoding="utf-8")


@pytest.mark.parametrize(
    ("mutation", "expected_error"),
    [
        ("deleted", ErrorCode.STATE_FAILED),
        ("corrupt", ErrorCode.OUTPUT_INVALID),
        ("digest_mismatch", ErrorCode.OUTPUT_INVALID),
    ],
)
def test_persisted_succeeded_output_is_failed_without_provider_replay(
    tmp_path: Path,
    quiet_logger,
    mutation: str,
    expected_error: ErrorCode,
) -> None:
    config, _, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png")
    with processor:
        result = processor.process_path(source)
    output_path = result.output_path
    assert output_path is not None

    if mutation == "deleted":
        output_path.unlink()
    elif mutation == "corrupt":
        output_path.write_bytes(b"not a png")
    else:
        write_image(output_path, color=(1, 2, 3))

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        recovered = restarted.state.get_job(result.job_id)
        duplicate = restarted.process_path(source)

    assert recovered.status == JobStatus.FAILED
    assert recovered.error_code == expected_error.value
    assert duplicate.status == JobStatus.FAILED
    assert duplicate.output_path is None
    assert duplicate.error_code == expected_error
    assert replay_guard.requests == []
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    assert expected_error.value in records[0].read_text(encoding="utf-8")


def test_failed_recovery_for_bad_succeeded_output_is_not_rewritten_on_restart(
    tmp_path: Path,
    quiet_logger,
) -> None:
    config, _, processor = build_processor(tmp_path, quiet_logger)
    source = write_image(config.paths.source / "photo.png")
    with processor:
        result = processor.process_path(source)
    assert result.output_path is not None
    result.output_path.write_bytes(b"not a png")

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as restarted:
        assert restarted.state.get_job(result.job_id).status == JobStatus.FAILED

    record = next(config.paths.quarantine.glob("*.json"))
    record.write_text("sentinel", encoding="utf-8")

    replay_guard = FakeProvider()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(replay_guard),
    ) as restarted:
        assert restarted.state.get_job(result.job_id).status == JobStatus.FAILED

    assert replay_guard.requests == []
    assert record.read_text(encoding="utf-8") == "sentinel"


def test_process_path_shutdown_interrupts_stability_wait_promptly(
    tmp_path: Path,
    quiet_logger,
) -> None:
    _, config = write_config(
        tmp_path,
        stable_seconds=5,
        poll_interval_seconds=3_600,
    )
    source = write_image(config.paths.source / "photo.png")
    provider = FakeProvider()
    waits: list[float] = []

    class InterruptingEvent:
        def __init__(self) -> None:
            self._set = False

        def is_set(self) -> bool:
            return self._set

        def wait(self, timeout: float) -> bool:
            waits.append(timeout)
            self._set = True
            return True

    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        with pytest.raises(AppError) as caught:
            processor.process_path(source, shutdown_event=InterruptingEvent())

    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert waits == [5]
    assert provider.requests == []


def test_process_path_honors_shutdown_during_staging_copy(
    tmp_path: Path,
    quiet_logger,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "photo.png")
    shutdown_event = threading.Event()
    provider = FakeProvider()
    original_open = Path.open

    class InterruptingReader:
        def __init__(self, stream) -> None:
            self._stream = stream
            self._triggered = False

        def __enter__(self):
            self._stream.__enter__()
            return self

        def __exit__(self, exc_type, exc, traceback):
            return self._stream.__exit__(exc_type, exc, traceback)

        def read(self, size: int = -1) -> bytes:
            if not self._triggered:
                limit = 8 if size < 0 else min(size, 8)
                chunk = self._stream.read(limit)
                if chunk:
                    self._triggered = True
                    shutdown_event.set()
                return chunk
            return self._stream.read(size)

        def __getattr__(self, name: str):
            return getattr(self._stream, name)

    def patched_open(self: Path, *args, **kwargs):
        stream = original_open(self, *args, **kwargs)
        mode = args[0] if args else kwargs.get("mode", "r")
        if self == source and mode == "rb":
            return InterruptingReader(stream)
        return stream

    monkeypatch.setattr(Path, "open", patched_open)
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(provider),
    ) as processor:
        with pytest.raises(AppError) as caught:
            processor.process_path(source, shutdown_event=shutdown_event)
        assert processor.state.list_jobs() == []

    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert provider.requests == []
    assert not list((config.paths.destination / ".tcfcomic" / "staging").glob("*.input"))


def test_runtime_lock_rejects_second_instance(tmp_path: Path, quiet_logger) -> None:
    _, config = write_config(tmp_path)
    first = Processor(config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider()))
    try:
        with pytest.raises(AppError) as caught:
            Processor(config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider()))
        assert caught.value.code == ErrorCode.STATE_FAILED
    finally:
        first.close()
