from __future__ import annotations

import json
import os
import threading
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace

import pytest

from conftest import write_config, write_image, write_mpo
from tcfcomic.domain import AppError, ErrorCode, JobStatus, ShutdownToken, StableCandidate
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.publication import initial_output_leaf
from tcfcomic.scanner import stage_candidate


def admit(processor, source):
    info = source.stat()
    return processor._admit(
        source, candidate=StableCandidate(source, info.st_size, info.st_mtime_ns),
        shutdown_event=threading.Event(),
    )


def historical_failure(processor, source, monkeypatch):
    def old_validator(*args, **kwargs):
        raise AppError(ErrorCode.INVALID_IMAGE, "The input image format or frame count is not supported.")
    with monkeypatch.context() as patch:
        patch.setattr("tcfcomic.processor.validate_input", old_validator)
        result = admit(processor, source)
    assert result.status == JobStatus.FAILED and result.attempts == 0
    job = processor.state.get_job(result.job_id)
    assert not Path(job.staged_path).exists()
    return job


def make_processor(config, logger, provider=None, **kwargs):
    return Processor(
        config, logger, runner=InProcessAttemptRunner(provider or FakeProvider()), **kwargs,
    )


def test_failed_mpo_requeues_same_job_with_audit_then_one_attempt(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    original = source.read_bytes()
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        failure = config.paths.quarantine / f"{job.job_id}.json"
        evidence = failure.read_bytes()
        processor.config = replace(config, provider=replace(config.provider, requests_per_minute=2))
        admitted = admit(processor, source)
        assert admitted.job_id == job.job_id and admitted.status == JobStatus.READY
        current = processor.state.get_job(job.job_id)
        assert current.created_at == job.created_at
        assert (current.provider, current.model, current.prompt_hash) == (job.provider, job.model, job.prompt_hash)
        assert current.error_code is None and current.next_attempt_at is None
        assert current.sha256 == job.sha256 and current.mtime_ns == job.mtime_ns
        assert Path(current.staged_path).read_bytes() == original
        assert processor.state.attempt_count(job.job_id) == 0
        result = processor._run_until_terminal(job.job_id, threading.Event())
        assert result.status == JobStatus.SUCCEEDED and result.attempts == 1
        assert result.output_path.is_file()
        assert failure.read_bytes() == evidence
        audit = config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json"
        assert audit.read_bytes() == evidence
        assert admit(processor, source).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1
    with make_processor(config, quiet_logger, provider) as processor:
        assert admit(processor, source).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1
    assert source.read_bytes() == original
    assert audit.read_bytes() == evidence


@pytest.mark.parametrize("condition", [
    "attempt", "ambiguous", "provider_failed", "identity", "temp_reference",
    "output_reference", "output_hash", "output_artifact", "temp_artifact", "old_stage",
])
def test_recovery_never_reopens_ineligible_work(tmp_path, quiet_logger, monkeypatch, condition):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        if condition == "attempt":
            processor.state.connection.execute(
                "INSERT INTO attempts (job_id,attempt_no,state,started_at,finished_at) VALUES (?,1,'permanent',?,?)",
                (job.job_id, job.created_at, job.updated_at),
            )
        elif condition == "identity":
            processor.config = replace(config, provider=replace(config.provider, model="changed-model"))
        elif condition == "old_stage":
            Path(job.staged_path).write_bytes(b"do not delete")
        elif condition == "temp_artifact":
            (config.paths.destination / ".unowned.tmp").write_bytes(b"do not delete")
        elif condition == "output_artifact":
            snapshot = processor._snapshot_from_job(job)
            (config.paths.destination / initial_output_leaf(snapshot, job.job_id)).write_bytes(b"do not delete")
        else:
            field, value = {
                "ambiguous": ("status", "AMBIGUOUS"),
                "provider_failed": ("error_code", "PROVIDER_PERMANENT"),
                "temp_reference": ("temp_name", ".owned.tmp"),
                "output_reference": ("output_name", "owned.png"),
                "output_hash": ("output_sha256", "a" * 64),
            }[condition]
            processor.state.connection.execute(f"UPDATE jobs SET {field}=? WHERE job_id=?", (value, job.job_id))
        before = processor.state.get_job(job.job_id)
        result = admit(processor, source)
        assert result.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
        assert processor.state.get_job(job.job_id) == before
        assert not list(config.paths.quarantine.glob("*.mpo-input-failure.json"))
        assert provider.requests == []
        if condition == "old_stage":
            assert Path(job.staged_path).read_bytes() == b"do not delete"
        else:
            assert not list(processor.staging.glob("*.input"))


@pytest.mark.parametrize("condition", [
    "stage", "attempts", "job_id", "error_code", "version", "source_name",
    "first_seen_at", "malformed", "oversized", "directory", "reparse", "audit_collision",
    "duplicate_field", "deep_json",
])
def test_bad_provenance_fails_closed_without_overwrite(tmp_path, quiet_logger, monkeypatch, condition):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        failure = config.paths.quarantine / f"{job.job_id}.json"
        audit = config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json"
        data = json.loads(failure.read_bytes())
        if condition == "audit_collision":
            audit.write_bytes(b"immutable other evidence")
        elif condition == "directory":
            failure.unlink()
            failure.mkdir()
        elif condition == "reparse":
            from tcfcomic import path_safety
            real = path_safety.is_reparse_or_link
            monkeypatch.setattr(path_safety, "is_reparse_or_link", lambda p: p == failure or real(p))
        elif condition == "malformed":
            failure.write_bytes(b"not json")
        elif condition == "oversized":
            failure.write_bytes(b" " * 65537)
        elif condition == "duplicate_field":
            failure.write_text(json.dumps(data)[:-1] + ', "attempts": 0}')
        elif condition == "deep_json":
            failure.write_text("[" * 2000 + "0" + "]" * 2000)
        else:
            if condition == "version":
                data["source_version"]["sha256"] = "0" * 64
            else:
                data[condition] = {
                    "stage": "provider", "attempts": 1, "job_id": "b" * 32,
                    "error_code": "PROVIDER_PERMANENT", "source_name": "other.JPG",
                    "first_seen_at": "different",
                }[condition]
            failure.write_text(json.dumps(data))
        evidence = failure.read_bytes() if condition not in {"directory", "reparse"} else None
        with pytest.raises(AppError):
            admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert not list(processor.staging.glob("*.input"))
        assert processor.state.attempt_count(job.job_id) == 0
        if evidence is not None:
            assert failure.read_bytes() == evidence
        if condition == "audit_collision":
            assert audit.read_bytes() == b"immutable other evidence"


@pytest.mark.parametrize("field", ["normalized_path", "size", "mtime_ns", "sha256", "path"])
def test_transaction_rechecks_snapshot_version(tmp_path, quiet_logger, monkeypatch, field):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        info = source.stat()
        snapshot = stage_candidate(
            StableCandidate(source, info.st_size, info.st_mtime_ns),
            processor.staging / ("c" * 32 + ".input"), config.limits.max_input_bytes,
            ShutdownToken(lambda: False),
        )
        changed = {
            "normalized_path": str(source) + "other", "size": snapshot.size + 1,
            "mtime_ns": snapshot.mtime_ns + 1, "sha256": "0" * 64, "path": source.with_name("other.JPG"),
        }[field]
        assert not processor.state.requeue_never_sent_mpo(
            job, replace(snapshot, **{field: changed}), processor._request_identity(), lambda: True,
        )
        assert processor.state.get_job(job.job_id) == job


@pytest.mark.parametrize("mutation", ["claim", "attempt", "identity", "version", "artifact"])
def test_cas_race_rechecks_state_and_only_discards_new_stage(tmp_path, quiet_logger, monkeypatch, mutation):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        original_cas = processor.state.requeue_never_sent_mpo

        def racing_cas(expected, snapshot, identity, check):
            if mutation == "attempt":
                processor.state.connection.execute(
                    "INSERT INTO attempts (job_id,attempt_no,state,started_at) VALUES (?,1,'dispatching',?)",
                    (job.job_id, job.created_at),
                )
            elif mutation == "artifact":
                (config.paths.destination / ".raced.tmp").write_bytes(b"unowned")
            else:
                field, value = {
                    "claim": ("status", "DISPATCHING"), "identity": ("model", "changed"),
                    "version": ("sha256", "0" * 64),
                }[mutation]
                processor.state.connection.execute(f"UPDATE jobs SET {field}=? WHERE job_id=?", (value, job.job_id))
            return original_cas(expected, snapshot, identity, check)

        monkeypatch.setattr(processor.state, "requeue_never_sent_mpo", racing_cas)
        result = admit(processor, source)
        assert result.status != JobStatus.READY
        assert not list(processor.staging.glob("*.input"))
        assert (config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json").is_file()
        if mutation == "artifact":
            assert (config.paths.destination / ".raced.tmp").read_bytes() == b"unowned"


class Crash(BaseException):
    pass


@pytest.mark.parametrize("point", ["mpo_requeue_before_update", "mpo_requeue_after_update", "mpo_requeue_after_commit"])
def test_requeue_crash_boundaries_audit_reused_without_replay(tmp_path, quiet_logger, monkeypatch, point):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)

        def crash(where):
            if where == point:
                raise Crash()

        processor.state._transition_hook = crash
        with pytest.raises(Crash):
            admit(processor, source)
        current = processor.state.get_job(job.job_id)
        assert current.status == (JobStatus.READY if point.endswith("after_commit") else JobStatus.FAILED)
        audit = config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json"
        evidence = audit.read_bytes()
        assert provider.requests == []
    with make_processor(config, quiet_logger, provider) as processor:
        result = processor.process_path(source)
        assert result.status == JobStatus.SUCCEEDED and result.job_id == job.job_id and result.attempts == 1
        assert len(provider.requests) == 1
        assert audit.read_bytes() == evidence
        assert not list(processor.staging.glob("*.input"))


def test_valid_non_mpo_failure_is_not_reopened(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "ordinary.jpg", "JPEG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        assert admit(processor, source).status == JobStatus.FAILED
        assert processor.state.get_job(job.job_id) == job
        assert not list(config.paths.quarantine.glob("*.mpo-input-failure.json"))


def test_source_changed_after_staging_with_restored_metadata_blocks_requeue(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        real_cas = processor.state.requeue_never_sent_mpo

        def change_source(*args):
            info = source.stat()
            data = bytearray(source.read_bytes())
            data[-1] ^= 1
            source.write_bytes(data)
            os.utime(source, ns=(info.st_atime_ns, info.st_mtime_ns))
            return real_cas(*args)

        monkeypatch.setattr(processor.state, "requeue_never_sent_mpo", change_source)
        with pytest.raises(AppError):
            admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert not list(processor.staging.glob("*.input"))


def test_audit_temp_collision_never_removes_unowned_file(tmp_path, quiet_logger, monkeypatch):
    from tcfcomic.quarantine import preserve_mpo_failure_audit

    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        payload = (config.paths.quarantine / f"{job.job_id}.json").read_bytes()
        temp = config.paths.quarantine / f"{job.job_id}.{'f' * 32}.json.tmp"
        temp.write_bytes(b"do not delete")
        monkeypatch.setattr("tcfcomic.quarantine.uuid.uuid4", lambda: SimpleNamespace(hex="f" * 32))
        with pytest.raises(AppError):
            preserve_mpo_failure_audit(job, config.paths.quarantine, payload)
        assert temp.read_bytes() == b"do not delete"
        assert processor.state.get_job(job.job_id) == job


def test_crash_after_audit_link_recovers_owned_temp_without_replay(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)

        def crash(*args):
            raise Crash()

        with monkeypatch.context() as patch:
            patch.setattr("tcfcomic.quarantine._remove_owned_temp", crash)
            with pytest.raises(Crash):
                admit(processor, source)
        audit = config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json"
        assert audit.stat().st_nlink == 2
        evidence = audit.read_bytes()
        assert processor.state.get_job(job.job_id) == job
    with make_processor(config, quiet_logger, provider) as processor:
        assert audit.stat().st_nlink == 1
        assert not list(config.paths.quarantine.glob("*.json.tmp"))
        result = processor.process_path(source)
        assert result.status == JobStatus.SUCCEEDED and result.job_id == job.job_id
        assert result.attempts == len(provider.requests) == 1
        assert audit.read_bytes() == evidence


@pytest.mark.parametrize("provenance_valid", [False, True])
def test_restart_watch_reconsiders_only_proven_mpo_once(tmp_path, quiet_logger, monkeypatch, provenance_valid):
    import logging

    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    events = []

    class Capture(logging.Handler):
        def emit(self, record):
            events.append(record)

    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(Capture())
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        if not provenance_valid:
            (config.paths.quarantine / f"{job.job_id}.json").write_bytes(b"not provenance")
    with make_processor(config, quiet_logger, provider) as processor:
        assert processor.state.get_job(job.job_id).status == JobStatus.FAILED
        processor.watch(max_cycles=4)
        current = processor.state.get_job(job.job_id)
        assert current.status == (JobStatus.SUCCEEDED if provenance_valid else JobStatus.FAILED)
        assert len(provider.requests) == int(provenance_valid)
        assert len([e for e in events if e.event == "watch_item_failed"]) == int(not provenance_valid)
        assert len([e for e in events if e.event == "job_processed"]) == int(provenance_valid)
